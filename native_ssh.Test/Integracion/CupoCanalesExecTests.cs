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
    // El cupo local de canales exec frente a las sesiones que el servidor retiene: tras abortar una lectura cuyo proceso
    // remoto no escribe, OpenSSH no libera su sesión hasta que ese proceso termina (como mucho, su plazo remoto). sshd
    // propio con MaxSessions 5 (el shell y los 4 canales exec, sin margen) y LogLevel DEBUG.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El sshd se libera en IAsyncLifetime.DisposeAsync.")]
    public sealed class CupoCanalesExecTests : IAsyncLifetime
    {
        private readonly SshdPropio _sshd = new("LogLevel DEBUG\nMaxSessions 5");

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync()
        {
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
            catch (OperationCanceledException)
            {
                return "cancelada";
            }
        }

        [Fact]
        public async Task LecturasCanceladasQueNoEscriben_RetienenSuCupoHastaQueElServidorCierraSuCanal()
        {
            // Arrange
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones();
            await using GestorSesiones gestor = new(
                _sshd.Sshd.Fabrica(opciones),
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);
            string destino = _sshd.Sshd.Destino.Clave;
            _ = await herramientas.EjecutarAsync(destino, "true");
            SesionShell sesion = gestor.ListarSesiones().Single();
            int aceptadas = _sshd.Sshd.ConexionesAceptadas();
            long sleep = 660_000 + Random.Shared.Next(10_000);

            try
            {
                // Act: cuatro lecturas mudas con plazo remoto de 6 s, canceladas a 0,5 s; enseguida, cuatro más.
                using CancellationTokenSource cancelacion = new(TimeSpan.FromMilliseconds(500));
                string[] canceladas = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => MensajeDeFalloAsync(gestor.EjecutarExecAsync(destino, $"sleep {sleep}", TimeSpan.FromSeconds(6), cancelacion.Token))));
                string[] durante = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => MensajeDeFalloAsync(gestor.EjecutarExecAsync(destino, "echo d", TimeSpan.FromSeconds(1), CancellationToken.None))));
                string vivosDurante = CanalesExecIntegracionTests.ProcesosSleepVivos(sleep, sleep);
                int activosDurante = sesion.CanalesExecActivos;
                await EsperasPrueba.EsperarHastaAsync(() => sesion.CanalesExecActivos == 0, TimeSpan.FromSeconds(15));
                string vivosAlLiberar = CanalesExecIntegracionTests.ProcesosSleepVivos(sleep, sleep);
                string[] despues = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => MensajeDeFalloAsync(gestor.EjecutarExecAsync(destino, "echo r", TimeSpan.FromSeconds(10), CancellationToken.None))));

                // Assert: mientras viven los procesos abortados, sus canales conservan el cupo y las lecturas nuevas fallan
                // al vencer su límite local diciendo por qué; ninguna llega a recibir el rechazo de MaxSessions del servidor.
                _ = canceladas.Should().AllBe("cancelada");
                _ = vivosDurante.Split('\n').Should().HaveCount(4);
                _ = activosDurante.Should().Be(SesionShell.MaxCanalesExecSimultaneos);
                _ = durante.Should().AllSatisfy(m => m.Should().Contain("no terminó en 1 s").And.Contain("sin llegar a obtener canal").And.NotContain("rechazó"));
                _ = vivosAlLiberar.Should().BeEmpty();
                _ = despues.Should().AllBe("sin error");
                _ = _sshd.Sshd.ContarLineasLog("no more sessions").Should().Be(0);
                _ = _sshd.Sshd.ConexionesAceptadas().Should().Be(aceptadas);
            }
            finally
            {
                CanalesExecIntegracionTests.MatarProcesosSleep(sleep, sleep);
            }
        }
    }
}
