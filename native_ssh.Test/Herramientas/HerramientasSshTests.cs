using FluentAssertions;
using System.Globalization;
using System.Linq;
using System.Text;
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
            comando.Contains("colgado_") ? new("parcial\n", 0, ComportamientoFalsoEnum.Colgado) :
            comando.Contains("enorme") ? SimularSalidaEnorme(comando) :
            comando.Contains("error.txt") ? new("Error: el archivo no existe: error.txt\n", 1) :
            comando.Contains("sed -n") ? SimularLectura(comando) :
            comando.Contains("base64 -d") ? SimularEscritura(comando) :
            comando.Contains("grep -rnI") ? SimularBusqueda(comando) :
            comando.Contains("-o -type d -print") || comando.Contains("-o -print 2>/dev/null") ? SimularListDir(comando) :
            comando.Contains("-print -prune") ? SimularFind(comando) :
            comando.Contains("find -L . -maxdepth") ? SimularFind(comando) :
            comando.Contains("stat -c") ? SimularStat(comando) :
            comando.Contains("pwd -P") ? SimularValidacionCwd(comando) :
            comando.Contains("unset __NS_CWD") ? new("/home/ana\n", 0) :
            comando.StartsWith("export __NS_CWD=", StringComparison.Ordinal) ? new("", 0) :
            comando.Contains("| base64\n") ? SimularLecturaBase64(comando) :
            comando switch
            {
                "colgado" => new("empezando\n", 0, ComportamientoFalsoEnum.Colgado),
                "pregunta" => new("¿Nombre? ", 0, ComportamientoFalsoEnum.PideEntrada),
                "largo" => new(string.Concat(Enumerable.Range(0, 10).Select(i => $"linea {i}\n"))),
                _ => new($"eco:{comando}\n", 2)
            };

        private static RespuestaFalsa SimularLecturaBase64(string comando) =>
            comando.Contains("archivo_inexistente.txt", StringComparison.Ordinal)
                ? new("Error: el archivo no existe: archivo_inexistente.txt\n", 1)
                : comando.Contains("es_directorio/", StringComparison.Ordinal)
                    ? new("Error: la ruta es un directorio: es_directorio/\n", 1)
                    : comando.Contains("archivo_multiples.txt", StringComparison.Ordinal)
                        ? new(Convert.ToBase64String(Encoding.UTF8.GetBytes("linea 1\nrepetido\nrepetido\nlinea 4\n")) + "\n", 0)
                        : new(Convert.ToBase64String(Encoding.UTF8.GetBytes("linea 1\nlinea 2\nlinea 3\nlinea 4\nlinea 5\n")) + "\n", 0);

        // Más de 16 KiB por encima de MaxBytesSalidaPorPeticion de las pruebas de descarte: el principio se pierde.
        private const int BytesSalidaEnorme = 64 * 1024;
        private const int MaxBytesSalidaPequena = 20_000;

        private static RespuestaFalsa SimularSalidaEnorme(string comando)
        {
            string texto = string.Concat(Enumerable.Range(0, BytesSalidaEnorme / 32).Select(i => $"linea {i:D6} xxxxxxxxxxxxxxxxxx\n"));
            return comando.Contains("base64", StringComparison.Ordinal) && !comando.Contains("base64 -d", StringComparison.Ordinal)
                ? new(Convert.ToBase64String(Encoding.UTF8.GetBytes(texto), Base64FormattingOptions.InsertLineBreaks).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", 0)
                : new(texto, 0);
        }

        private static (FabricaFalsa Fabrica, GestorSesiones Gestor, HerramientasSsh Herramientas) CrearConSalidaPequena()
        {
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana", MaxBytesSalidaPorPeticion = MaxBytesSalidaPequena, EsperaTrasCtrlC = TimeSpan.FromMilliseconds(300) };
            FabricaFalsa fabrica = new(Guion, tamanoTrozo: 4096);
            GestorSesiones gestor = new(fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            return (fabrica, gestor, new HerramientasSsh(gestor, opciones));
        }

        private static RespuestaFalsa SimularValidacionCwd(string comando) =>
            comando.Contains("ruta_inexistente")
                ? new("Error: la ruta no existe: ruta_inexistente\n", 1)
                : comando.Contains("es_archivo.txt")
                    ? new("Error: la ruta no es un directorio: es_archivo.txt\n", 1)
                    : comando.Contains("/var/www/proyecto")
                        ? new("/var/www/proyecto\n", 0)
                        : new("/tmp/trabajo\n", 0);

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

        private static RespuestaFalsa SimularListDir(string comando) =>
            comando.Contains("ruta_inexistente") ? new("Error: la ruta de exploración no existe: 'ruta_inexistente'\n", 1) :
            comando.Contains("es_archivo.txt") ? new("Error: la ruta no es un directorio: 'es_archivo.txt'\n", 1) :
            comando.Contains("vacio") ? new("", 0) :
            comando.Contains("marcadores") && comando.Contains("-name .git") ? (comando.Contains("-o -print 2>/dev/null")
                ? new(".git\nnode_modules\nProgram.cs\nREADME.md\nsrc\nsrc/models\nsrc/views\n", 0)
                : new(".git\nnode_modules\nsrc\nsrc/models\nsrc/views\n", 0)) :
            comando.Contains("-o -print 2>/dev/null") ? new("Program.cs\nREADME.md\nsrc\nsrc/models\nsrc/views\n", 0) :
            new("src\nsrc/models\nsrc/views\n", 0);

        private static RespuestaFalsa SimularFind(string comando) =>
            comando.Contains("ruta_inexistente") ? new("Error: la ruta de búsqueda no existe: 'ruta_inexistente'\n", 1) :
            comando.Contains("sin_coincidencias") ? new("", 0) :
            comando.Contains("-name '.git'") ? new(".git\n", 0) :
            comando.Contains("-name 'node_modules'") ? new("node_modules\n", 0) :
            comando.Contains("-name '__pycache__'") ? new("__pycache__\n", 0) :
            comando.Contains("-name '.venv'") ? new(".venv\n", 0) :
            comando.Contains("-name '*.cs'") ? new("Program.cs\nHerramientas/HerramientasSsh.cs\n", 0) :
            new("app.service\nnginx.conf\n", 0);

        private static RespuestaFalsa SimularStat(string comando) =>
            comando.Contains("no_existe.txt")
                ? new("NO_EXISTE\n", 0)
                : comando.Contains("fichero_invalido")
                    ? new("error en stat\n", 1)
                    : new("fichero regular|2048|644|2026-10-05 01:00:00\n", 0);

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
        public async Task Status_DevuelveComoMaximoVeintePeticionesLasMasRecientes()
        {
            // Arrange
            for (int i = 0; i < 25; i++)
                _ = await _herramientas.EjecutarAsync("uno", $"cmd{i}");
            _ = await _herramientas.EjecutarAsync("dos", "otro");

            // Act
            ResultadoEstado todo = _herramientas.Estado();
            ResultadoEstado filtrado = _herramientas.Estado("uno");

            // Assert
            _ = todo.Peticiones.Count.Should().Be(20);
            _ = todo.Peticiones[0].Comando.Should().Be("otro");
            _ = filtrado.Peticiones.Count.Should().Be(20);
            _ = filtrado.Peticiones[0].Comando.Should().Be("cmd24");
            _ = filtrado.Peticiones[^1].Comando.Should().Be("cmd5");
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


        private HerramientasSsh HerramientasConTimeoutCorto() =>
            new(_gestor, new OpcionesNativeSsh { UsuarioPorDefecto = "ana", MaxBytesPorRespuesta = 16, EsperaTrasCtrlC = TimeSpan.FromMilliseconds(300) }) { TimeoutForzado = TimeSpan.FromSeconds(1) };

        [Fact]
        public async Task ReadFile_TiempoAgotadoEnElShell_LanzaConElEstadoYNoDevuelveSalidaParcial()
        {
            // Arrange
            HerramientasSsh herramientas = HerramientasConTimeoutCorto();

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync("servidor", "colgado_lectura.txt"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("TiempoAgotado");
            _ = error.Message.Should().NotContain("parcial");
        }

        [Fact]
        public async Task ReadFile_EncoladaTrasComandoColgado_EsperaSuTimeoutCancelaLaPeticionYLanza()
        {
            // Arrange
            HerramientasSsh herramientas = HerramientasConTimeoutCorto();
            ResultadoComando colgado = await _herramientas.EjecutarAsync("servidor", "colgado", esperarSegundos: 0);
            await EsperasPrueba.EsperarHastaAsync(() => _gestor.BuscarPeticion(colgado.Id).Salida.Longitud > 0);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync("servidor", "archivo.txt"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert: la lectura nunca llegó a ejecutarse, se canceló y no bloquea la cola.
            _ = error.Message.Should().Contain("Encolado");
            Peticion lectura = _gestor.ListarPeticiones().First(p => p.Comando.Contains("sed -n", StringComparison.Ordinal));
            _ = lectura.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = _gestor.BuscarPeticion(colgado.Id).Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
            _ = (await _herramientas.CancelarAsync(colgado.Id)).Estado.Should().Be(EstadoPeticionEnum.Cancelado);
        }

        [Fact]
        public async Task Search_EncoladaTrasComandoColgado_CancelaLaPeticionYLanza()
        {
            // Arrange
            HerramientasSsh herramientas = HerramientasConTimeoutCorto();
            ResultadoComando colgado = await _herramientas.EjecutarAsync("servidor", "colgado", esperarSegundos: 0);
            await EsperasPrueba.EsperarHastaAsync(() => _gestor.BuscarPeticion(colgado.Id).Salida.Longitud > 0);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.BuscarAsync("servidor", "patron"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("Encolado");
            _ = _gestor.ListarPeticiones().First(p => p.Comando.Contains("grep -rnI", StringComparison.Ordinal)).Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = await _herramientas.CancelarAsync(colgado.Id);
        }

        [Fact]
        public async Task ReadFile_SalidaConDatosDescartados_LanzaErrorSinDevolverLaCola()
        {
            // Arrange
            (_, GestorSesiones gestor, HerramientasSsh herramientas) = CrearConSalidaPequena();
            await using GestorSesiones gestorLiberable = gestor;

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync("servidor", "archivo_enorme.txt", 1, 5000))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("superó");
            _ = error.Message.Should().NotContain("linea 00");
        }

        [Fact]
        public async Task ReplaceFileContent_LecturaConDatosDescartados_AbortaSinEscribir()
        {
            // Arrange
            (FabricaFalsa fabrica, GestorSesiones gestor, HerramientasSsh herramientas) = CrearConSalidaPequena();
            await using GestorSesiones gestorLiberable = gestor;

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_enorme.txt", "linea 002000", "cambiada", 1, 3000))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("superó");
            _ = error.Message.Should().Contain("No se ha escrito nada");
            _ = fabrica.Ultima!.Comandos.Should().NotContain(c => c.Contains("base64 -d", StringComparison.Ordinal));
        }

        [Fact]
        public async Task SearchFindListDir_SalidaConDatosDescartados_LanzanErrorExplicito()
        {
            // Arrange
            (_, GestorSesiones gestor, HerramientasSsh herramientas) = CrearConSalidaPequena();
            await using GestorSesiones gestorLiberable = gestor;

            // Act
            NativeSshException errorSearch = (await FluentActions.Awaiting(() => herramientas.BuscarAsync("servidor", "enorme"))
                .Should().ThrowAsync<NativeSshException>()).Which;
            NativeSshException errorFind = (await FluentActions.Awaiting(() => herramientas.BuscarPorNombreAsync("servidor", "*.cs", "dir_enorme"))
                .Should().ThrowAsync<NativeSshException>()).Which;
            NativeSshException errorList = (await FluentActions.Awaiting(() => herramientas.ListarDirectoriosAsync("servidor", "dir_enorme"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = errorSearch.Message.Should().Contain("superó");
            _ = errorFind.Message.Should().Contain("superó");
            _ = errorList.Message.Should().Contain("superó");
        }

        [Theory]
        [InlineData(1024 * 1024)]
        [InlineData(64 * 1024)]
        [InlineData(20_000)]
        [InlineData(4096 + 77)]
        [InlineData(4096 + 80)]
        public void MaxBytesArchivoEditable_SuBase64ConSaltosCabeConMargenYUnByteMasNo(int maxBytesSalida)
        {
            // Arrange
            static long Base64ConSaltos(long bytes)
            {
                long caracteres = (bytes + 2) / 3 * 4;
                return caracteres + ((caracteres + 75) / 76);
            }

            long disponible = maxBytesSalida - HerramientasSsh.MargenSalidaBase64;

            // Act
            long maximo = HerramientasSsh.CalcularMaxBytesArchivoEditable(maxBytesSalida);

            // Assert
            _ = Base64ConSaltos(maximo).Should().BeLessThanOrEqualTo(disponible);
            _ = Base64ConSaltos(maximo + 1).Should().BeGreaterThan(disponible);
        }

        [Fact]
        public void MaxBytesArchivoEditable_ValorDocumentadoConElMaximoPorDefecto()
        {
            // Act & Assert: 1 MiB de salida por petición => 773 184 bytes (≈755 KiB) de archivo editable.
            _ = HerramientasSsh.CalcularMaxBytesArchivoEditable(new OpcionesNativeSsh().MaxBytesSalidaPorPeticion).Should().Be(773_184);
            _ = HerramientasSsh.CalcularMaxBytesArchivoEditable(HerramientasSsh.MargenSalidaBase64).Should().Be(0);
        }

        [Fact]
        public async Task ReplaceFileContent_ComandoDeLectura_CompruebaElTamanoAntesDeLeer()
        {
            // Act
            _ = await _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_prueba.txt", "linea 3", "linea 3b", 1, 5);

            // Assert
            string lectura = _fabrica.Ultima!.Comandos.First(c => c.Contains("base64", StringComparison.Ordinal));
            _ = lectura.Should().Contain("773184");
            _ = lectura.Should().Contain("supera el máximo editable");
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

        [Theory]
        [InlineData("py", "--include='*.py'")]
        [InlineData(".ts", "--include='*.ts'")]
        [InlineData("*.json", "--include='*.json'")]
        public async Task Search_ConExtension_IncluyeFlagInclude(string extension, string esperado)
        {
            // Act
            _ = await _herramientas.BuscarAsync("servidor", "patron", extension: extension);

            // Assert
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains(esperado));
        }

        [Fact]
        public async Task ListDir_RutaInexistente_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                _herramientas.ListarDirectoriosAsync("servidor", "ruta_inexistente")).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("la ruta de exploración no existe");
        }

        [Fact]
        public async Task ListDir_SinSubdirectorios_DevuelveMensajeInformativo()
        {
            // Act
            string resultado = await _herramientas.ListarDirectoriosAsync("servidor", "vacio");

            // Assert
            _ = resultado.Should().Be("No se encontraron subdirectorios en 'vacio'.");
        }

        [Fact]
        public async Task ListDir_ConDirectorios_DevuelveListadoOrdenado()
        {
            // Act
            string resultado = await _herramientas.ListarDirectoriosAsync("servidor", "proyecto");

            // Assert
            _ = resultado.Should().Be("src\nsrc/models\nsrc/views");
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("find -L . -maxdepth 3 -mindepth 1"));
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains(@"\( -name __pycache__ -o -name .cache \) -prune"));
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains(@"-o \( -name .git -o -name node_modules"));
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("-o -type d -print"));
        }

        [Fact]
        public async Task ListDir_ConIncluirArchivos_DevuelveArchivosYDirectorios()
        {
            // Act
            string resultado = await _herramientas.ListarDirectoriosAsync("servidor", "proyecto", incluirArchivos: true);

            // Assert
            _ = resultado.Should().Contain("Program.cs");
            _ = resultado.Should().Contain("src");
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains(@"\( -name __pycache__ -o -name .cache \) -prune"));
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains(@"-o \( -name .git -o -name node_modules"));
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("-o -print 2>/dev/null"));
            _ = _fabrica.Ultima!.Comandos.Should().NotContain("-type d -print");
        }

        [Fact]
        public async Task ListDir_ConMarcadores_MuestraMarcadoresYPrune()
        {
            // Act
            string resultado = await _herramientas.ListarDirectoriosAsync("servidor", "marcadores");

            // Assert
            _ = resultado.Should().Contain(".git");
            _ = resultado.Should().Contain("node_modules");
            _ = resultado.Should().Contain("src");
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains(@"-o \( -name .git -o -name node_modules -o -name .venv"));
        }

        [Fact]
        public async Task ListDir_ConIncluirArchivos_VacioDevuelveElementos()
        {
            // Act
            string resultado = await _herramientas.ListarDirectoriosAsync("servidor", "vacio", incluirArchivos: true);

            // Assert
            _ = resultado.Should().Be("No se encontraron elementos en 'vacio'.");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Find_PatronVacio_LanzaExcepcion(string? patron)
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                _herramientas.BuscarPorNombreAsync("servidor", patron!)).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("El patrón de búsqueda no puede estar vacío");
        }

        [Fact]
        public async Task Find_SinResultados_DevuelveMensajeInformativo()
        {
            // Act
            string resultado = await _herramientas.BuscarPorNombreAsync("servidor", "sin_coincidencias");

            // Assert
            _ = resultado.Should().Be("No se encontraron elementos que coincidan con 'sin_coincidencias' en '.'.");
        }

        [Fact]
        public async Task Find_ConResultados_DevuelveRutasOrdenadas()
        {
            // Act
            string resultado = await _herramientas.BuscarPorNombreAsync("servidor", "*.service");

            // Assert
            _ = resultado.Should().Be("app.service\nnginx.conf");
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("-name '*.service'"));
        }

        [Theory]
        [InlineData(".git", ".git")]
        [InlineData("node_modules", "node_modules")]
        [InlineData("__pycache__", "__pycache__")]
        [InlineData(".venv", ".venv")]
        public async Task Find_CarpetasExcluidas_DevuelveCarpetaYComandoContienePrintPrune(string patron, string esperado)
        {
            // Act
            string resultado = await _herramientas.BuscarPorNombreAsync("servidor", patron, tipo: "dir");

            // Assert
            _ = resultado.Should().Be(esperado);
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("-print -prune"));
        }

        [Fact]
        public async Task Find_ArchivosNormales_ExcluyeCarpetasPodadas()
        {
            // Act
            string resultado = await _herramientas.BuscarPorNombreAsync("servidor", "*.cs", tipo: "file");

            // Assert
            _ = resultado.Should().Contain("Program.cs");
            _ = resultado.Should().NotContain("node_modules");
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("-print -prune"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Stat_RutaVacia_LanzaExcepcion(string? ruta)
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                _herramientas.ConsultarMetadatosAsync("servidor", ruta!)).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("La ruta no puede estar vacía");
        }

        [Fact]
        public async Task Stat_ArchivoInexistente_DevuelveExisteFalse()
        {
            // Act
            string resultado = await _herramientas.ConsultarMetadatosAsync("servidor", "no_existe.txt");

            // Assert
            using JsonDocument doc = JsonDocument.Parse(resultado);
            _ = doc.RootElement.GetProperty("existe").GetBoolean().Should().BeFalse();
            _ = doc.RootElement.GetProperty("ruta").GetString().Should().Be("no_existe.txt");
            _ = doc.RootElement.GetProperty("mensaje").GetString().Should().Contain("no existe");
        }

        [Fact]
        public async Task Stat_ArchivoExistente_DevuelveMetadatosJson()
        {
            // Act
            string resultado = await _herramientas.ConsultarMetadatosAsync("servidor", "archivo.txt");

            // Assert
            using JsonDocument doc = JsonDocument.Parse(resultado);
            _ = doc.RootElement.GetProperty("existe").GetBoolean().Should().BeTrue();
            _ = doc.RootElement.GetProperty("tipo").GetString().Should().Be("fichero regular");
            _ = doc.RootElement.GetProperty("tamanoBytes").GetInt64().Should().Be(2048);
            _ = doc.RootElement.GetProperty("tamanoHumano").GetString().Should().Be("2 KiB");
            _ = doc.RootElement.GetProperty("permisos").GetString().Should().Be("644");
            _ = doc.RootElement.GetProperty("ultimaModificacion").GetString().Should().Be("2026-10-05 01:00:00");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task SetCwd_RutaVaciaONull_RestableceHome(string? ruta)
        {
            // Act
            string resultado = await _herramientas.FijarDirectorioAsync("servidor", ruta);

            // Assert
            _ = resultado.Should().Be("Directorio de trabajo persistente restablecido a $HOME (~).");
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("unset __NS_CWD"));
        }

        [Fact]
        public async Task SetCwd_RutaInexistente_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                _herramientas.FijarDirectorioAsync("servidor", "ruta_inexistente"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            _ = error.Message.Should().Contain("la ruta no existe: ruta_inexistente");
        }

        [Fact]
        public async Task SetCwd_RutaValida_FijaVariable()
        {
            // Act
            string resultado = await _herramientas.FijarDirectorioAsync("servidor", "/var/www/proyecto");

            // Assert
            _ = resultado.Should().Be("Directorio de trabajo persistente fijado a: /var/www/proyecto");
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("export __NS_CWD='/var/www/proyecto'"));
        }

        [Theory]
        [InlineData("", "target", 1, 5, "vacía")]
        [InlineData("   ", "target", 1, 5, "vacía")]
        [InlineData("archivo.txt", "", 1, 5, "targetContent")]
        [InlineData("archivo.txt", "target", 0, 5, "mayor o igual a 1")]
        [InlineData("archivo.txt", "target", 5, 2, "no puede ser menor")]
        public async Task ReplaceFileContent_ParametrosInvalidos_LanzaExcepcion(string ruta, string target, int inicio, int fin, string mensajeEsperado)
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                _herramientas.ReemplazarContenidoArchivoAsync("servidor", ruta, target, "replacement", inicio, fin))
                .Should().ThrowAsync<NativeSshException>()).Which;

            _ = error.Message.Should().Contain(mensajeEsperado);
        }

        [Fact]
        public async Task ReplaceFileContent_TargetNoEncontradoEnRango_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_prueba.txt", "inexistente", "nuevo", 1, 3))
                .Should().ThrowAsync<NativeSshException>()).Which;

            _ = error.Message.Should().Contain("No se encontró 'targetContent' en el rango de líneas 1-3");
        }

        [Fact]
        public async Task ReplaceFileContent_MultiplesAparicionesSinAllowMultiple_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_multiples.txt", "repetido", "nuevo", 1, 4, allowMultiple: false))
                .Should().ThrowAsync<NativeSshException>()).Which;

            _ = error.Message.Should().Contain("Se encontraron 2 apariciones de 'targetContent'");
        }

        [Fact]
        public async Task ReplaceFileContent_ReemplazoExitoso_DevuelveMensajeConfirmacion()
        {
            // Act
            string resultado = await _herramientas.ReemplazarContenidoArchivoAsync(
                "servidor",
                "archivo_prueba.txt",
                "linea 3",
                "linea 3 modificada",
                lineaInicio: 2,
                lineaFin: 4);

            // Assert
            _ = resultado.Should().Be("Archivo modificado quirúrgicamente con éxito: archivo_prueba.txt (1 reemplazo(s) realizado(s)).");
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("base64 -d"));
        }

        [Fact]
        public async Task ReplaceFileContent_ConAllowMultiple_ReemplazaTodas()
        {
            // Act
            string resultado = await _herramientas.ReemplazarContenidoArchivoAsync(
                "servidor",
                "archivo_multiples.txt",
                "repetido",
                "cambiado",
                lineaInicio: 1,
                lineaFin: 4,
                allowMultiple: true);

            // Assert
            _ = resultado.Should().Be("Archivo modificado quirúrgicamente con éxito: archivo_multiples.txt (2 reemplazo(s) realizado(s)).");
        }

        [Theory]
        [InlineData("pm2 list | grep online")]
        [InlineData("systemctl status mi-app | grep Active")]
        [InlineData("npm test |& grep FAILED")]
        [InlineData("cat app.log |grep error")]
        [InlineData("pm2 list | egrep online")]
        [InlineData("cat app.log | fgrep error")]
        public async Task Exec_ConTuberiasGrep_NoGeneraAviso(string comando)
        {
            // Act
            ResultadoComando resultado = await _herramientas.EjecutarAsync("servidor", comando);

            // Assert
            _ = resultado.Aviso.Should().BeNull();
        }

        [Theory]
        [InlineData("grep -rn 'list_team_names' src/")]
        [InlineData("rgrep patron .")]
        [InlineData("egrep -rn patron src/")]
        [InlineData("fgrep 'texto' archivo.txt")]
        public async Task Exec_ConGrepDisco_GeneraAvisoSearch(string comando)
        {
            // Act
            ResultadoComando resultado = await _herramientas.EjecutarAsync("servidor", comando);

            // Assert
            _ = resultado.Aviso.Should().NotBeNull();
            _ = resultado.Aviso.Should().Contain("ssh_search");
        }

        [Theory]
        [InlineData("false || grep 'error' app.log")]
        [InlineData("false ||grep 'error' app.log")]
        [InlineData("false || egrep 'error' app.log")]
        [InlineData("false ||egrep 'error' app.log")]
        public async Task Exec_ConOrLogicoGrep_GeneraAvisoSearch(string comando)
        {
            // Act
            ResultadoComando resultado = await _herramientas.EjecutarAsync("servidor", comando);

            // Assert
            _ = resultado.Aviso.Should().NotBeNull();
            _ = resultado.Aviso.Should().Contain("ssh_search");
        }

        [Fact]
        public async Task Exec_ConFindDisco_GeneraAvisoFind()
        {
            // Act
            ResultadoComando resultado = await _herramientas.EjecutarAsync("servidor", "find . -name '*.py'");

            // Assert
            _ = resultado.Aviso.Should().NotBeNull();
            _ = resultado.Aviso.Should().Contain("ssh_find");
        }

        [Fact]
        public async Task Exec_ConCdEncadenado_GeneraAvisoSetCwd()
        {
            // Act
            ResultadoComando resultado = await _herramientas.EjecutarAsync("servidor", "cd /root/DiscordBots && npm test");

            // Assert
            _ = resultado.Aviso.Should().NotBeNull();
            _ = resultado.Aviso.Should().Contain("ssh_set_cwd");
        }

        [Fact]
        public async Task Exec_ConCombinacionCdYGrep_GeneraAvisoCombinado()
        {
            // Act
            ResultadoComando resultado = await _herramientas.EjecutarAsync("servidor", "cd /root/DiscordBots && grep -rn 'algo' src/");

            // Assert
            _ = resultado.Aviso.Should().NotBeNull();
            _ = resultado.Aviso.Should().Contain("ssh_set_cwd");
            _ = resultado.Aviso.Should().Contain("ssh_search");
        }
    }
}
