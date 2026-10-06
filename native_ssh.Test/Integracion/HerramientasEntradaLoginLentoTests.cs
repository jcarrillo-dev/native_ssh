using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // ssh_input sobre un comando cuyo shell todavía se está abriendo (el login de este sshd tarda 3 s): la petición ya está en
    // ejecución, pero el comando aún no se ha enviado al shell y la sesión rechaza la entrada. El error dice esa causa (el
    // shell se está abriendo; reintentar cuando ssh_status lo muestre activo) y no una lista cerrada de causas que no la
    // incluye; con el shell abierto, la misma entrada llega (auditoría del A-septies, sondas Y01 e Y02).
    [Collection(ColeccionSshdLoginLento.Nombre)]
    [Trait("Categoria", "Integracion")]
    public sealed class HerramientasEntradaLoginLentoTests(SshdLocalLoginLento fixture) : IAsyncDisposable
    {
        private const string ComandoLectura = "read x; echo \"leido:$x\"";

        private readonly SshdLocal _sshd = fixture.Sshd;
        private GestorSesiones? _gestor;

        public async ValueTask DisposeAsync()
        {
            if (_gestor is not null)
                await _gestor.DisposeAsync();
        }

        private HerramientasSsh Herramientas()
        {
            OpcionesNativeSsh opciones = _sshd.Opciones();
            _gestor = new GestorSesiones(
                _sshd.Fabrica(opciones),
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            return new HerramientasSsh(_gestor, opciones);
        }

        private string MensajeShellAbriendose(string id) =>
            $"No se envió la entrada al comando {id}, que sigue en ejecución: el shell de {_sshd.Destino.Clave} todavía no está activo (se está abriendo: conexión, inicio de sesión o inicialización del shell, y el comando aún no se le ha enviado; o su canal se acaba de cerrar o se perdió la conexión). Reintenta cuando ssh_status muestre el shell activo (isShellActivo=true) con este comando en curso.";

        // Primer comando de un destino (conexión + login de 3 s) con esperarSegundos=0 y ssh_input al momento: el patrón
        // habitual de un LLM con un comando interactivo.
        [Fact]
        public async Task Input_MientrasSeConectaYAbreElShell_DiceQueElShellSeEstaAbriendo()
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            string destino = _sshd.Destino.Clave;
            ResultadoComando exec = await herramientas.EjecutarAsync(destino, ComandoLectura, esperarSegundos: 0);
            await EsperasPrueba.EsperarHastaAsync(() => _gestor!.BuscarPeticion(exec.Id).Estado == EstadoPeticionEnum.Ejecutando);

            // Act
            Exception? error = await Record.ExceptionAsync(() => herramientas.EnviarEntradaAsync(exec.Id, "hola", esperarSegundos: 0));
            await EsperasPrueba.EsperarHastaAsync(() => herramientas.Estado(destino).Sesiones[0].IsShellActivo, TimeSpan.FromSeconds(20));
            ResultadoComando reintento = await ReintentarAsync(herramientas, exec.Id);

            // Assert
            _ = error.Should().BeOfType<NativeSshException>().Which.Message.Should().Be(MensajeShellAbriendose(exec.Id));
            _ = reintento.Salida.Should().Contain("leido:hola", "con el shell abierto, la misma entrada llega: el comando sí la admitía");
        }

        // Tras ssh_reset_shell, el comando siguiente reabre el shell sobre la misma conexión (otro login de 3 s).
        [Fact]
        public async Task Input_MientrasSeReabreElShellTrasReset_DiceQueElShellSeEstaAbriendo()
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            string destino = _sshd.Destino.Clave;
            _ = (await herramientas.EjecutarAsync(destino, "true", esperarSegundos: 30)).Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = herramientas.ReiniciarShell(destino).IsHecho.Should().BeTrue();
            ResultadoComando exec = await herramientas.EjecutarAsync(destino, ComandoLectura, esperarSegundos: 0);
            await EsperasPrueba.EsperarHastaAsync(() => _gestor!.BuscarPeticion(exec.Id).Estado == EstadoPeticionEnum.Ejecutando);

            // Act
            Exception? error = await Record.ExceptionAsync(() => herramientas.EnviarEntradaAsync(exec.Id, "hola", esperarSegundos: 0));
            bool isConectada = herramientas.Estado(destino).Sesiones[0].IsConectada;
            await EsperasPrueba.EsperarHastaAsync(() => herramientas.Estado(destino).Sesiones[0].IsShellActivo, TimeSpan.FromSeconds(20));
            ResultadoComando reintento = await ReintentarAsync(herramientas, exec.Id);

            // Assert
            _ = isConectada.Should().BeTrue("el shell se reabre sobre la misma conexión");
            _ = error.Should().BeOfType<NativeSshException>().Which.Message.Should().Be(MensajeShellAbriendose(exec.Id));
            _ = reintento.Salida.Should().Contain("leido:hola");
        }

        // Con el shell ya activo, el comando puede tardar un instante en enviarse (inicialización): se reintenta hasta que
        // la entrada se acepta.
        private static async Task<ResultadoComando> ReintentarAsync(HerramientasSsh herramientas, string id)
        {
            for (int intento = 0; ; intento++)
            {
                try
                {
                    return await herramientas.EnviarEntradaAsync(id, "hola", esperarSegundos: 10);
                }
                catch (NativeSshException) when (intento < 50)
                {
                    await Task.Delay(100);
                }
            }
        }
    }
}
