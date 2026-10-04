using FluentAssertions;
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

        private static RespuestaFalsa Guion(string comando) => comando switch
        {
            "colgado" => new("empezando\n", 0, ComportamientoFalsoEnum.Colgado),
            "pregunta" => new("¿Nombre? ", 0, ComportamientoFalsoEnum.PideEntrada),
            "largo" => new(string.Concat(Enumerable.Range(0, 10).Select(i => $"linea {i}\n"))),
            _ => new($"eco:{comando}\n", 2)
        };

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
    }
}
