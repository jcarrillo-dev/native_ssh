using System.Diagnostics.CodeAnalysis;
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
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El gestor se libera en IAsyncLifetime.DisposeAsync.")]
    public sealed class HerramientasEntradaLoginLentoTests(SshdLocalLoginLento fixture) : IAsyncLifetime
    {
        private const string ComandoLectura = "read x; echo \"leido:$x\"";

        private readonly SshdLocal _sshd = fixture.Sshd;
        private GestorSesiones? _gestor;

        // IAsyncLifetime (no IAsyncDisposable): es lo que xUnit v2 invoca al terminar cada prueba.
        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
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

        private const string Reintento = "Reintenta en unos segundos (ssh_status muestra isShellActivo=true en cuanto el canal está abierto, aunque la inicialización puede tardar algo más); si sigue fallando, comprueba su estado con ssh_output o ssh_status.";

        private string MensajeShellInactivo(string id) =>
            $"No se envió la entrada al comando {id}, que sigue en ejecución: el shell de {_sshd.Destino.Clave} todavía se está abriendo (conexión, inicio de sesión o inicialización del shell) y el comando aún no se le ha enviado. {Reintento}";

        private string MensajeShellInicializandose(string id) =>
            $"No se envió la entrada al comando {id}, que sigue en ejecución: el shell de {_sshd.Destino.Clave} tiene el canal abierto, pero aún se está inicializando (primer comando del destino, tras ssh_reset_shell o con un inicio de sesión lento) y el comando todavía no se le ha enviado. {Reintento}";

        // Primer comando de un destino (conexión + login de 3 s) con esperarSegundos=0 y ssh_input al momento: el patrón
        // habitual de un LLM con un comando interactivo. Según llegue, el canal aún no está abierto (conexión) o ya lo está
        // y el login sigue: el error dice una de las dos cosas, nunca una lista de causas que no incluye la real.
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
            ResultadoComando reintento = await ReintentarAsync(herramientas, exec.Id);

            // Assert
            _ = error.Should().BeOfType<NativeSshException>().Which.Message.Should().BeOneOf(MensajeShellInactivo(exec.Id), MensajeShellInicializandose(exec.Id));
            _ = reintento.Salida.Should().Contain("leido:hola", "con el shell abierto, la misma entrada llega: el comando sí la admitía");
        }

        // Tras ssh_reset_shell, el comando siguiente reabre el shell sobre la misma conexión (otro login de 3 s). Con el
        // canal ya abierto (ssh_status lo muestra activo) y el login en curso, la entrada se sigue rechazando: el error no
        // dice que el shell no esté activo, sino que aún se está inicializando (una sola causa: el comando no se ha enviado,
        // según la señal de la sesión).
        [Fact]
        public async Task Input_MientrasSeReabreElShellTrasReset_DiceQueAunSePuedeEstarInicializando()
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            string destino = _sshd.Destino.Clave;
            _ = (await herramientas.EjecutarAsync(destino, "true", esperarSegundos: 30)).Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = herramientas.ReiniciarShell(destino).IsHecho.Should().BeTrue();
            ResultadoComando exec = await herramientas.EjecutarAsync(destino, ComandoLectura, esperarSegundos: 0);
            // El canal viejo cuenta como activo hasta que el comando lo cierra para abrir el nuevo: se deja pasar ese
            // instante y después se exige el canal nuevo abierto (el login sigue 3 s).
            await EsperasPrueba.EsperarHastaAsync(() => _gestor!.BuscarPeticion(exec.Id).Estado == EstadoPeticionEnum.Ejecutando);
            await Task.Delay(300);
            await EsperasPrueba.EsperarHastaAsync(() => herramientas.Estado(destino).Sesiones[0].IsShellActivo);

            // Act
            Exception? error = await Record.ExceptionAsync(() => herramientas.EnviarEntradaAsync(exec.Id, "hola", esperarSegundos: 0));
            ResultadoComando reintento = await ReintentarAsync(herramientas, exec.Id);

            // Assert
            _ = error.Should().BeOfType<NativeSshException>().Which.Message.Should().Be(MensajeShellInicializandose(exec.Id));
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
