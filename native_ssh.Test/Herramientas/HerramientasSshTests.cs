using FluentAssertions;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Sesion.Falsos;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Herramientas
{
    public sealed class HerramientasSshTests : IAsyncDisposable
    {
        private readonly FabricaFalsa _fabrica = new(Guion);
        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;

        public HerramientasSshTests()
        {
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana", MaxBytesPorRespuesta = 16, EsperaTrasCtrlC = TimeSpan.FromMilliseconds(300) };
            _gestor = new GestorSesiones(_fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            _herramientas = new HerramientasSsh(_gestor, opciones);
        }

        private static RespuestaFalsa Guion(string comando) =>
            comando.Contains("error.txt") ? new("Error: el archivo no existe: error.txt\n", 1) :
            comando.Contains("sed -n") ? SimularLectura(comando) :
            comando.Contains("base64 -d") ? SimularEscritura(comando) :
            comando.Contains("grep -rnI") ? SimularBusqueda(comando) :
            comando switch
            {
                "colgado" => new("empezando\n", 0, ComportamientoFalsoEnum.Colgado),
                "pregunta" => new("¿Nombre? ", 0, ComportamientoFalsoEnum.PideEntrada),
                "largo" => new(string.Concat(Enumerable.Range(0, 10).Select(i => $"linea {i}\n"))),
                _ => new($"eco:{comando}\n", 2)
            };

        private static RespuestaFalsa SimularBusqueda(string comando)
        {
            if (!comando.Contains("-E -e"))
                return new("grep: error falta flag -e\n", 2);

            if (comando.Contains("ruta_inexistente"))
                return new("Error: la ruta de búsqueda no existe: ruta_inexistente\n", 1);

            if (comando.Contains("error_fatal"))
                return new("grep: error de sintaxis en expresión regular\n", 2);

            if (comando.Contains("sin_coincidencias"))
                return new("", 1);

            if (comando.Contains("muchas_coincidencias"))
            {
                string muchas = string.Concat(Enumerable.Range(1, 5).Select(i => $"archivo.txt:{i}:coincidencia {i}\n"));
                return new(muchas, 0);
            }

            return new("archivo.txt:1:coincidencia encontrada\n", 0);
        }

        private static RespuestaFalsa SimularEscritura(string comando) =>
            comando.Contains("ya_existe.txt") && comando.Contains("el archivo ya existe")
                ? new("Error: el archivo ya existe: ya_existe.txt. Especifica overwrite=true para sobrescribirlo o append=true para anexar.\n", 1)
                : new("", 0);

        private static RespuestaFalsa SimularLectura(string comando)
        {
            int idxCabecera = comando.LastIndexOf("echo \"[Líneas ", StringComparison.Ordinal);
            if (idxCabecera >= 0)
            {
                int finCabecera = comando.IndexOf(']', idxCabecera);
                if (finCabecera > idxCabecera)
                {
                    const int total = 10;
                    int fin = total;
                    int idxFin = comando.IndexOf("total < ", StringComparison.Ordinal);
                    if (idxFin >= 0)
                    {
                        int idxEspacio = comando.IndexOf(' ', idxFin + 8);
                        if (idxEspacio > idxFin && int.TryParse(comando[(idxFin + 8)..idxEspacio], out int parsedFin))
                            fin = parsedFin;
                    }

                    int finEfectivo = Math.Min(total, fin);
                    string cabecera = comando[(idxCabecera + 6)..(finCabecera + 1)]
                        .Replace("$total", total.ToString(CultureInfo.InvariantCulture))
                        .Replace("$fin_efectivo", finEfectivo.ToString(CultureInfo.InvariantCulture));
                    return new($"{cabecera}\nlinea 1\nlinea 2\nlinea 3\n", 0);
                }
            }

            return new("linea 1\nlinea 2\nlinea 3\n", 0);
        }

        public ValueTask DisposeAsync() => _gestor.DisposeAsync();

        [Fact]
        public async Task Exec_EsperaYDevuelveResultado()
        {
            // Arrange
            ResultadoComando resultado = await _herramientas.EjecutarAsync("servidor", "hola");

            // Act & Assert
            _ = resultado.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = resultado.CodigoSalida.Should().Be(2);
            _ = resultado.Salida.Should().Be("eco:hola\n");
            _ = resultado.Destino.Should().Be("ana@servidor:22");
            _ = resultado.HasMasSalida.Should().BeFalse();
            _ = resultado.SegundosSinSalida.Should().BeNull();
        }

        [Fact]
        public async Task Exec_SinEsperar_DevuelveEjecutando_YLuegoSePuedeCancelar()
        {
            // Arrange
            ResultadoComando parcial = await _herramientas.EjecutarAsync("servidor", "colgado", esperarSegundos: 0);
            await EsperasPrueba.EsperarHastaAsync(() => _gestor.BuscarPeticion(parcial.Id).Salida.Longitud > 0);
            ResultadoComando enCurso = await _herramientas.LeerSalidaAsync(parcial.Id);

            // Act
            ResultadoComando cancelado = await _herramientas.CancelarAsync(parcial.Id);

            // Assert
            _ = enCurso.CodigoSalida.Should().BeNull();
            _ = enCurso.Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
            _ = enCurso.SegundosSinSalida.Should().NotBeNull();
            _ = enCurso.Salida.Should().Be("empezando\n");
            _ = cancelado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = cancelado.CodigoSalida.Should().Be(130);
        }

        [Fact]
        public async Task Output_PaginaConSiguienteByte()
        {
            // Arrange
            ResultadoComando primera = await _herramientas.EjecutarAsync("servidor", "largo");
            string texto = primera.Salida;
            ResultadoComando actual = primera;
            while (actual.HasMasSalida)
            {
                actual = await _herramientas.LeerSalidaAsync(primera.Id, actual.SiguienteByte);
                texto += actual.Salida;
            }

            // Act & Assert
            _ = primera.HasMasSalida.Should().BeTrue();
            _ = primera.SiguienteByte.Should().Be(16);
            _ = texto.Should().Be(string.Concat(Enumerable.Range(0, 10).Select(i => $"linea {i}\n")));
        }

        [Fact]
        public async Task Input_DevuelveLaSalidaPosteriorAlEnvio()
        {
            // Arrange
            ResultadoComando pregunta = await _herramientas.EjecutarAsync("servidor", "pregunta", esperarSegundos: 0);
            await EsperasPrueba.EsperarHastaAsync(() => _gestor.BuscarPeticion(pregunta.Id).Salida.Longitud > 0);

            // Act
            ResultadoComando respuesta = await _herramientas.EnviarEntradaAsync(pregunta.Id, "ana", esperarSegundos: 5);

            // Assert
            _ = respuesta.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = respuesta.Salida.Should().Be("recibido:ana\n");
        }

        [Fact]
        public async Task Input_EnComandoTerminado_Lanza()
        {
            // Arrange
            ResultadoComando terminado = await _herramientas.EjecutarAsync("servidor", "hola");

            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.EnviarEntradaAsync(terminado.Id, "x")).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("Completado");
        }

        [Fact]
        public async Task Status_FiltraPorDestino()
        {
            // Arrange
            _ = await _herramientas.EjecutarAsync("uno", "a");
            _ = await _herramientas.EjecutarAsync("dos", "b");

            // Act
            ResultadoEstado todo = _herramientas.Estado();
            ResultadoEstado filtrado = _herramientas.Estado("dos");

            // Assert
            _ = todo.Sesiones.Count.Should().Be(2);
            _ = todo.Peticiones.Count.Should().Be(2);
            _ = filtrado.Sesiones.Should().ContainSingle().Subject.Destino.Should().Be("ana@dos:22");
            _ = filtrado.Peticiones.Should().ContainSingle().Subject.Comando.Should().Be("b");
        }

        [Fact]
        public async Task Disconnect_YReset()
        {
            // Arrange
            _ = await _herramientas.EjecutarAsync("servidor", "a");

            // Act & Assert
            _ = _herramientas.ReiniciarShell("servidor").IsHecho.Should().BeTrue();
            _ = (await _herramientas.DesconectarAsync("servidor")).IsHecho.Should().BeTrue();
            _ = (await _herramientas.DesconectarAsync("servidor")).IsHecho.Should().BeFalse();
            _ = _herramientas.ReiniciarShell("servidor").IsHecho.Should().BeFalse();
        }

        [Fact]
        public async Task Json_CamelCaseEstadoComoTextoYSinNulos()
        {
            // Arrange
            ResultadoComando resultado = await _herramientas.EjecutarAsync("servidor", "hola");

            // Act
            string json = JsonSerializer.Serialize(resultado, ContextoJsonNativeSsh.OpcionesHerramientas);

            // Assert
            _ = json.Should().Contain("\"estado\":\"Completado\"");
            _ = json.Should().Contain("\"codigoSalida\":2");
            _ = json.Should().NotContain("aviso");
            _ = json.Should().NotContain("segundosSinSalida");
        }

        [Fact]
        public async Task ReadFile_ConMetadatos_DevuelveCabeceraConTotalLineas()
        {
            // Simula comando que devuelve cabecera y contenido
            string contenido = await _herramientas.LeerArchivoAsync("servidor", "fichero.txt", lineaInicio: 1, lineaFin: 2);
            _ = contenido.Should().StartWith("[Líneas 1-2 de ");
        }

        [Fact]
        public async Task ReadFile_LecturaExitosa_DevuelveTextoPlano()
        {
            // Act
            string texto = await _herramientas.LeerArchivoAsync("servidor", "archivo.txt", 1, 3);

            // Assert
            _ = texto.Should().Be("[Líneas 1-3 de 10]\nlinea 1\nlinea 2\nlinea 3\n");
        }


        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task ReadFile_RutaVacia_LanzaExcepcion(string ruta)
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.LeerArchivoAsync("servidor", ruta)).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("vacía");
        }

        [Fact]
        public async Task ReadFile_FinMenorQueInicio_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.LeerArchivoAsync("servidor", "archivo.txt", lineaInicio: 10, lineaFin: 5)).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("no puede ser menor");
        }

        [Fact]
        public async Task ReadFile_FalloCodigoDistintoDeCero_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.LeerArchivoAsync("servidor", "error.txt")).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Be("Error: el archivo no existe: error.txt");
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task WriteFile_RutaVacia_LanzaExcepcion(string ruta)
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.EscribirArchivoAsync("servidor", ruta, "contenido")).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("vacía");
        }

        [Fact]
        public async Task WriteFile_ArchivoExisteSinOverwriteNiAppend_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.EscribirArchivoAsync("servidor", "ya_existe.txt", "nuevo contenido", overwrite: false, append: false)).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("ya existe");
        }

        [Fact]
        public async Task WriteFile_OverwriteYAppendSimultaneos_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.EscribirArchivoAsync("servidor", "archivo.txt", "contenido", overwrite: true, append: true)).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("No se puede especificar 'overwrite=true' y 'append=true' simultáneamente.");
        }

        [Fact]
        public async Task WriteFile_EscrituraExitosa_DevuelveMensajeConfirmacion()
        {
            // Act
            string resultado = await _herramientas.EscribirArchivoAsync("servidor", "nuevo.txt", "hola mundo");

            // Assert
            _ = resultado.Should().Contain("Archivo escrito correctamente: nuevo.txt");
            _ = resultado.Should().Contain("10 bytes");
        }

        [Fact]
        public async Task WriteFile_AnexadoExitoso_DevuelveMensajeConfirmacion()
        {
            // Act
            string resultado = await _herramientas.EscribirArchivoAsync("servidor", "nuevo.txt", "hola", append: true);

            // Assert
            _ = resultado.Should().Contain("Contenido anexado correctamente a: nuevo.txt");
            _ = resultado.Should().Contain("4 bytes");
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Search_PatronVacio_LanzaExcepcion(string patron)
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.BuscarAsync("servidor", patron)).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("patrón de búsqueda no puede estar vacío");
        }

        [Fact]
        public async Task Search_SinCoincidencias_DevuelveMensajeInformativoSinError()
        {
            // Act
            string resultado = await _herramientas.BuscarAsync("servidor", "sin_coincidencias");

            // Assert
            _ = resultado.Should().Be("No se encontraron coincidencias para el patrón: 'sin_coincidencias' en '.'.");
        }

        [Fact]
        public async Task Search_ConCoincidencias_DevuelveTextoFiltrado()
        {
            // Act
            string resultado = await _herramientas.BuscarAsync("servidor", "algo");

            // Assert
            _ = resultado.Should().Be("archivo.txt:1:coincidencia encontrada");
        }

        [Fact]
        public async Task Search_LimiteAlcanzado_AnexaAviso()
        {
            // Act
            string resultado = await _herramientas.BuscarAsync("servidor", "muchas_coincidencias", maxResultados: 3);

            // Assert
            _ = resultado.Should().Contain("[Límite alcanzado: se muestran 3 coincidencias.");
            _ = resultado.Should().Contain("coincidencia 3");
            _ = resultado.Should().NotContain("coincidencia 4");
        }

        [Fact]
        public async Task Search_PatronConGuion_UsaFlagEParaProtegerPatron()
        {
            // Act
            string resultado = await _herramientas.BuscarAsync("servidor", "--version");

            // Assert
            _ = resultado.Should().Be("archivo.txt:1:coincidencia encontrada");
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("-E -e '--version'"));
        }

        [Fact]
        public async Task Search_RutaInexistente_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.BuscarAsync("servidor", "algo", "ruta_inexistente")).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("la ruta de búsqueda no existe");
        }

        [Fact]
        public async Task Search_FalloGrep_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.BuscarAsync("servidor", "error_fatal")).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("error de sintaxis");
        }
    }
}
