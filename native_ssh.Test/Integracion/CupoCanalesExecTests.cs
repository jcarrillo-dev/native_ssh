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
    [Trait("Categoria", "SoloLinux")]
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
            _ = await herramientas.EjecutarAsync(destino, "true").ComoComando();
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

        // Q4c de la auditoría del E-quinquies: el adaptador de SSH.NET avisa de la confirmación de cada apertura; sin ese
        // aviso, una lectura que espera cupo detrás de cuatro canales confirmados diría que hay aperturas sin confirmar.
        [Fact]
        public async Task CupoOcupadoPorCanalesConfirmados_ElMensajeNoAfirmaAperturasSinConfirmar()
        {
            // Arrange
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones();
            await using GestorSesiones gestor = Gestor(opciones);
            string destino = _sshd.Sshd.Destino.Clave;
            _ = await new HerramientasSsh(gestor, opciones).EjecutarAsync(destino, "true").ComoComando();

            // Act
            string mensaje = await MensajeConCupoOcupadoAsync(gestor, destino);

            // Assert
            _ = mensaje.Should().Contain("siguen ocupados").And.NotContain("sin confirmar");
        }

        // Q4d de la auditoría del E-quinquies: una lectura abandonada con la apertura sin confirmar (servidor congelado)
        // deja de contar como tal cuando su apertura termina sin confirmación (aquí, al caer la conexión). Si siguiera
        // contando, el mensaje de cupo de la misma sesión, ya sobre la conexión nueva, afirmaría una apertura inexistente.
        [Fact]
        public async Task AperturaAbandonadaQueTerminaSinConfirmar_DejaDeContarParaElMensajeDeCupo()
        {
            // Arrange: sin freno del limitador (la caída obliga a abrir otra conexión).
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones(maxConexiones: 1000);
            await using GestorSesiones gestor = Gestor(opciones);
            string destino = _sshd.Sshd.Destino.Clave;
            _ = await new HerramientasSsh(gestor, opciones).EjecutarAsync(destino, "true").ComoComando();
            SesionShell sesion = gestor.ListarSesiones().Single();
            string[] propios = _sshd.Sshd.ProcesosDeConexion();
            string abandonada;

            // Act
            try
            {
                _ = BashLocal.Ejecutar($"kill -STOP {string.Join(' ', propios)}");
                abandonada = await MensajeDeFalloAsync(gestor.EjecutarExecAsync(destino, "echo a", TimeSpan.FromSeconds(1), CancellationToken.None));
            }
            finally
            {
                // SIGKILL también termina un proceso detenido; el CONT, por si acaso.
                _ = BashLocal.Ejecutar($"kill -KILL {string.Join(' ', propios)}; kill -CONT {string.Join(' ', propios)} 2>/dev/null; true");
            }

            await EsperasPrueba.EsperarHastaAsync(() => sesion.CanalesExecActivos == 0, TimeSpan.FromSeconds(15));
            string mensaje = await MensajeConCupoOcupadoAsync(gestor, destino);

            // Assert
            _ = abandonada.Should().Contain("sin que el servidor confirmara la apertura");
            _ = mensaje.Should().Contain("siguen ocupados").And.NotContain("sin confirmar");
        }

        private GestorSesiones Gestor(OpcionesNativeSsh opciones) => new(
            _sshd.Sshd.Fabrica(opciones),
            new LimitadorConexiones(opciones, TimeProvider.System),
            new RegistroPeticiones(opciones, TimeProvider.System),
            opciones,
            TimeProvider.System,
            NullLoggerFactory.Instance);

        // Ocupa el cupo con cuatro lecturas cuyo proceso remoto ya arrancó (apertura confirmada) y devuelve el error de una
        // quinta que vence esperando cupo (su límite local, 1 s más el margen, acaba antes que el plazo de las cuatro).
        private static async Task<string> MensajeConCupoOcupadoAsync(GestorSesiones gestor, string destino)
        {
            long sleep = 670_000 + Random.Shared.Next(10_000);
            try
            {
                Task<string>[] ocupantes = [.. Enumerable.Range(0, SesionShell.MaxCanalesExecSimultaneos).Select(_ => MensajeDeFalloAsync(gestor.EjecutarExecAsync(destino, $"sleep {sleep}", TimeSpan.FromSeconds(20), CancellationToken.None)))];
                await EsperasPrueba.EsperarHastaAsync(() => CanalesExecIntegracionTests.ProcesosSleepVivos(sleep, sleep).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == SesionShell.MaxCanalesExecSimultaneos, TimeSpan.FromSeconds(10));
                string mensaje = await MensajeDeFalloAsync(gestor.EjecutarExecAsync(destino, "echo q", TimeSpan.FromSeconds(1), CancellationToken.None));
                CanalesExecIntegracionTests.MatarProcesosSleep(sleep, sleep);
                _ = await Task.WhenAll(ocupantes);
                return mensaje;
            }
            finally
            {
                CanalesExecIntegracionTests.MatarProcesosSleep(sleep, sleep);
            }
        }
    }
}
