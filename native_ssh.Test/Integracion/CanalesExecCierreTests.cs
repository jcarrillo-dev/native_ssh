using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Cierre de los canales exec contra el sshd local (con ForceCommand, que registra en INFO toda petición 'signal').
    [Collection(ColeccionSshd.Nombre)]
    [Trait("Categoria", "Integracion")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El gestor se libera en IAsyncLifetime.DisposeAsync.")]
    public sealed class CanalesExecCierreTests : IAsyncLifetime
    {
        private readonly SshdLocal _sshd;
        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;

        public CanalesExecCierreTests(SshdLocal sshd)
        {
            _sshd = sshd;
            OpcionesNativeSsh opciones = sshd.Opciones() with { MaxBytesSalidaPorPeticion = 64 * 1024 };
            _gestor = new GestorSesiones(
                sshd.Fabrica(opciones),
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            _herramientas = new HerramientasSsh(_gestor, opciones);
        }

        private string Destino => _sshd.Destino.Clave;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync() => await _gestor.DisposeAsync();

        private Task<ResultadoCanalExec> ExecAsync(string script, double segundos, CancellationToken cancellationToken = default) =>
            _gestor.EjecutarExecAsync(Destino, script, TimeSpan.FromSeconds(segundos), cancellationToken);

        [Fact]
        public async Task Abortos_TopePlazoLocalCancelacionYCierre_NoEnvianSenalesNiCortanLaConexion()
        {
            // Arrange
            long sleep = 610_000 + Random.Shared.Next(10_000);
            _ = await _herramientas.EjecutarAsync(Destino, "export NS_VIVA=si").ComoComando();
            int aceptadas = _sshd.ConexionesAceptadas();
            int senales = _sshd.ContarLineasLog(SshdLocal.LineasSenalODesconexion);

            try
            {
                // Act: 'yes' no termina (el canal sigue abierto al superar el tope); el 'sleep' desligado con setsid
                // escapa del plazo remoto y mantiene abierta la salida, así que solo actúa el plazo local (1 s + 3 s).
                ResultadoCanalExec tope = await ExecAsync("yes", 20);
                using CancellationTokenSource cancelacion = new(TimeSpan.FromSeconds(1));
                Func<Task> cancelada = () => ExecAsync($"sleep {sleep}", 30, cancelacion.Token);
                _ = await cancelada.Should().ThrowAsync<OperationCanceledException>();
                NativeSshException plazoLocal = (await FluentActions.Awaiting(() => ExecAsync($"setsid sleep {sleep + 1} & wait", 1))
                    .Should().ThrowAsync<NativeSshException>()).Which;
                ResultadoComando viva = await _herramientas.EjecutarAsync(Destino, "echo \"$NS_VIVA\"").ComoComando();
                int conexiones = _herramientas.Estado().Sesiones.Single().ConexionesAbiertas;
                int aceptadasDespues = _sshd.ConexionesAceptadas();

                // La lectura cancelada conserva su canal (y su cupo) mientras su 'sleep' remoto vive: se espera al proceso
                // de la que sigue en vuelo, no al contador.
                Task<ResultadoCanalExec> enVuelo = ExecAsync($"sleep {sleep + 2}", 30);
                await EsperasPrueba.EsperarHastaAsync(() => CanalesExecIntegracionTests.ProcesosSleepVivos(sleep + 2, sleep + 2).Length > 0);
                await Task.Delay(300);
                _ = await _gestor.DesconectarAsync(Destino);
                NativeSshException cierre = (await FluentActions.Awaiting(() => enVuelo).Should().ThrowAsync<NativeSshException>()).Which;
                await Task.Delay(500);

                // Assert
                _ = tope.HasExcedidoTope.Should().BeTrue();
                _ = plazoLocal.Message.Should().Contain("no terminó en 1 s");
                _ = viva.Salida.Should().Be("si\n");
                _ = viva.Aviso.Should().BeNull();
                _ = conexiones.Should().Be(1);
                _ = aceptadasDespues.Should().Be(aceptadas);
                _ = cierre.Message.Should().Contain("se cerró");
                _ = (_sshd.ContarLineasLog(SshdLocal.LineasSenalODesconexion) - senales).Should().Be(0);
            }
            finally
            {
                CanalesExecIntegracionTests.MatarProcesosSleep(sleep, sleep + 1);
                CanalesExecIntegracionTests.MatarProcesosSleep(sleep + 2, sleep + 2);
            }
        }

        [Fact]
        public async Task ServidorCongelado_LaLecturaFallaAlPlazoLocalYElCanalSeCierraAlDescongelarlo()
        {
            // Arrange: los procesos de la conexión de esta prueba (otra anterior podría seguir cerrándose).
            string[] antes = _sshd.ProcesosDeConexion();
            _ = await ExecAsync("true", 10);
            string[] propios = [.. _sshd.ProcesosDeConexion().Except(antes)];
            _ = propios.Should().NotBeEmpty();
            SesionShell sesion = _gestor.ListarSesiones().Single();
            int aceptadas = _sshd.ConexionesAceptadas();
            int senales = _sshd.ContarLineasLog(SshdLocal.LineasSenalODesconexion);
            Stopwatch reloj;
            NativeSshException error;
            int activosCongelado;

            // Act: la apertura del canal no recibe respuesta (antes esperaba los 15 s de ConnectionInfo.Timeout).
            try
            {
                _ = BashLocal.Ejecutar($"kill -STOP {string.Join(' ', propios)}");
                reloj = Stopwatch.StartNew();
                // Acotada: si la lectura no respetara su límite local, falla sin dejar el servidor congelado.
                error = (await FluentActions.Awaiting(() => ExecAsync("echo hola", 2).WaitAsync(TimeSpan.FromSeconds(30))).Should().ThrowAsync<NativeSshException>()).Which;
                reloj.Stop();
                activosCongelado = sesion.CanalesExecActivos;
            }
            finally
            {
                _ = BashLocal.Ejecutar($"kill -CONT {string.Join(' ', propios)}");
            }

            await EsperasPrueba.EsperarHastaAsync(() => sesion.CanalesExecActivos == 0, TimeSpan.FromSeconds(15));
            ResultadoCanalExec siguiente = await ExecAsync("echo sigue", 10);
            await Task.Delay(300);

            // Assert: plazo de 2 s más el margen local de 3 s; el canal conserva su cupo hasta cerrarse.
            _ = error.Message.Should().Contain("no terminó en 2 s");
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
            _ = activosCongelado.Should().Be(1);
            _ = Encoding.UTF8.GetString(siguiente.Salida.Span).Should().Be("sigue\n");
            _ = _sshd.ConexionesAceptadas().Should().Be(aceptadas);
            _ = (_sshd.ContarLineasLog(SshdLocal.LineasSenalODesconexion) - senales).Should().Be(0);
        }

        [Fact]
        public async Task Tope_CierraElCanalAlMomentoYElProcesoQueEscribeMuerePorSigpipe()
        {
            // Arrange: 'yes' no termina solo; su plazo remoto (30 s) es mucho mayor que lo que debe tardar todo.
            string marca = $"ns_tope_{Guid.NewGuid():N}";
            Stopwatch reloj = Stopwatch.StartNew();

            try
            {
                // Act
                ResultadoCanalExec resultado = await ExecAsync($"yes {marca}", 30);
                TimeSpan duracion = reloj.Elapsed;
                string vivos = BashLocal.Ejecutar($"pgrep -f '^yes {marca}$' || true");
                for (int i = 0; i < 50 && vivos.Length > 0; i++)
                {
                    await Task.Delay(100);
                    vivos = BashLocal.Ejecutar($"pgrep -f '^yes {marca}$' || true");
                }

                // Assert: al superar el tope se cierra el canal, así que 'yes' muere al escribir y la lectura no espera a su
                // plazo remoto.
                _ = resultado.HasExcedidoTope.Should().BeTrue();
                _ = duracion.Should().BeLessThan(TimeSpan.FromSeconds(10));
                _ = vivos.Should().BeEmpty();
            }
            finally
            {
                _ = BashLocal.Ejecutar($"pkill -KILL -f '^yes {marca}$' || true");
            }
        }

        [Fact]
        public async Task CancelarUnaLectura_NoRetieneAQuienCancelaMientrasSuCanalSeCierra()
        {
            // Arrange: lectura muda con plazo remoto de 3 s; su canal no termina de cerrarse hasta que el servidor mata el
            // proceso, y quien cancela no debe esperar a eso (el cierre nunca va en el callback del token).
            long sleep = 630_000 + Random.Shared.Next(10_000);
            using CancellationTokenSource cancelacion = new();
            Task<ResultadoCanalExec> lectura = ExecAsync($"sleep {sleep}", 3, cancelacion.Token);

            try
            {
                await EsperasPrueba.EsperarHastaAsync(() => CanalesExecIntegracionTests.ProcesosSleepVivos(sleep, sleep).Length > 0);

                // Act
                Stopwatch reloj = Stopwatch.StartNew();
                cancelacion.Cancel();
                TimeSpan duracion = reloj.Elapsed;
                _ = await FluentActions.Awaiting(() => lectura).Should().ThrowAsync<OperationCanceledException>();

                // Assert
                _ = duracion.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
            }
            finally
            {
                CanalesExecIntegracionTests.MatarProcesosSleep(sleep, sleep);
            }
        }

        [Fact]
        public async Task AbortoLocal_ElProcesoRemotoMuereAlVencerSuPlazoRemoto()
        {
            // Arrange
            long sleep = 620_000 + Random.Shared.Next(10_000);
            using CancellationTokenSource cancelacion = new(TimeSpan.FromSeconds(1));
            Stopwatch reloj = Stopwatch.StartNew();

            try
            {
                // Act: se cancela a 1 s; el plazo remoto (3 s) es lo único que mata el proceso.
                Func<Task> cancelada = () => ExecAsync($"sleep {sleep}", 3, cancelacion.Token);
                _ = await cancelada.Should().ThrowAsync<OperationCanceledException>();
                string vivosTrasCancelar = CanalesExecIntegracionTests.ProcesosSleepVivos(sleep, sleep);
                string vivos = await CanalesExecIntegracionTests.EsperarSinProcesosSleepAsync(sleep, sleep);

                // Assert
                _ = vivosTrasCancelar.Should().NotBeEmpty();
                _ = vivos.Should().BeEmpty();
                _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
            }
            finally
            {
                CanalesExecIntegracionTests.MatarProcesosSleep(sleep, sleep);
            }
        }
    }
}
