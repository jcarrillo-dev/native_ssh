using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // V06 y V08 de la auditoría del E-quinquies: la escritura atascada en SSH.NET es la del propio ssh_input (servidor
    // congelado con el envío TCP lleno, o servidor sano con el terminal en modo raw y un proceso que no lee, que agota la
    // ventana del canal). ssh_cancel cancelaba en su propio hilo la continuación del bucle, que esperaba sin límite al
    // candado de escritura retenido por esa entrada: no volvía hasta un ssh_disconnect. Y la entrada, al fallar después,
    // decía que el comando «no admite entrada». Ahora ssh_cancel vuelve en unos segundos sin enviar Ctrl+C (diciendo que el
    // proceso sigue y que ssh_disconnect lo libera) y la entrada falla con su causa real.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El sshd se libera en IAsyncLifetime.DisposeAsync.")]
    public sealed class EntradaAtascadaTests : IAsyncLifetime
    {
        // Duración única para poder matar el proceso de la prueba sin tocar otros 'sleep' de la máquina.
        private const string Proceso = "sleep 119.25";

        private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(30);

        private readonly SshdPropio _sshd = new("");

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync()
        {
            _ = BashLocal.Ejecutar($"pkill -f '^{Proceso}$' || true");
            _sshd.Dispose();
            return Task.CompletedTask;
        }

        private static async Task<string> MensajeDeFalloAsync(Task tarea)
        {
            try
            {
                await tarea;
                return "sin error";
            }
            catch (NativeSshException ex)
            {
                return ex.Message;
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task EntradaPropiaAtascada_CancelVuelveSinCtrlCYLaEntradaDiceLaCausaReal(bool isCongelado)
        {
            // Arrange
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones() with { EsperaTrasCtrlC = TimeSpan.FromSeconds(1) };
            await using GestorSesiones gestor = new(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);
            string destino = _sshd.Sshd.Destino.Clave;
            _ = await herramientas.EjecutarAsync(destino, "export NS_ENTRADA=si", esperarSegundos: 20);
            SesionShell sesion = gestor.ListarSesiones().Single();
            string[] propios = _sshd.Sshd.ProcesosDeConexion();
            int aceptadas = _sshd.Sshd.ConexionesAceptadas();
            ResultadoComando comando = await herramientas.EjecutarAsync(destino, isCongelado ? Proceso : $"stty raw -echo; {Proceso}", timeoutSegundos: 200, esperarSegundos: 0);
            await Task.Delay(1500);
            string texto = new('b', 6 * 1024 * 1024);
            Task<string> entrada;
            ResultadoComando cancelado;
            TimeSpan duracionCancelacion;
            bool isDesconectado;

            // Act
            try
            {
                if (isCongelado)
                    _ = BashLocal.Ejecutar($"kill -STOP {string.Join(' ', propios)}");
                entrada = Task.Run(() => MensajeDeFalloAsync(herramientas.EnviarEntradaAsync(comando.Id, texto, esperarSegundos: 0)));
                await EsperasPrueba.EsperarHastaAsync(() => sesion.SegundosEnEscritura >= SesionShell.UmbralAvisoEscrituraBloqueada.TotalSeconds, TimeSpan.FromSeconds(15));
                Stopwatch reloj = Stopwatch.StartNew();
                cancelado = await herramientas.CancelarAsync(comando.Id).WaitAsync(EsperaMaxima);
                duracionCancelacion = reloj.Elapsed;
                Task<ResultadoAccion> desconexion = herramientas.DesconectarAsync(destino);
                isDesconectado = await Task.WhenAny(desconexion, Task.Delay(EsperaMaxima)) == desconexion;
            }
            finally
            {
                if (isCongelado)
                    _ = BashLocal.Ejecutar($"kill -CONT {string.Join(' ', propios)}");
            }

            string fallo = await entrada.WaitAsync(EsperaMaxima);
            ResultadoComando siguiente = await herramientas.EjecutarAsync(destino, "echo \"v=$NS_ENTRADA\"", esperarSegundos: 20);

            // Assert
            _ = duracionCancelacion.Should().BeLessThan(TimeSpan.FromSeconds(6));
            _ = cancelado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = cancelado.Aviso.Should().Contain("no se envió Ctrl+C").And.Contain("sigue en ejecución").And.Contain("ssh_disconnect");
            _ = isDesconectado.Should().BeTrue();
            _ = fallo.Should().Contain("parte de la entrada").And.Contain("ssh_disconnect").And.NotContain("no admite entrada");
            _ = siguiente.Salida.Should().Be("v=\n");
            _ = _sshd.Sshd.ConexionesAceptadas().Should().Be(aceptadas + 1);
        }
    }
}
