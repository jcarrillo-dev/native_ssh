using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;

namespace NativeSsh.Test.Integracion
{
    // Canales exec contra un servidor cuyo shell de login tiene otro PATH (sin 'timeout', con un 'rg' propio) y MaxSessions 2.
    [Collection(ColeccionSshdRestringido.Nombre)]
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El gestor se libera en IAsyncLifetime.DisposeAsync.")]
    public sealed class CanalesExecRestringidoTests : IAsyncLifetime
    {
        private readonly SshdLocalRestringido _fixture;
        private readonly string _directorio = Path.Combine(Path.GetTempPath(), $"ns_restringido_{Guid.NewGuid():N}");
        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;

        public CanalesExecRestringidoTests(SshdLocalRestringido fixture)
        {
            _fixture = fixture;
            _ = Directory.CreateDirectory(_directorio);
            OpcionesNativeSsh opciones = fixture.Sshd.Opciones();
            _gestor = new GestorSesiones(
                fixture.Sshd.Fabrica(opciones),
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            _herramientas = new HerramientasSsh(_gestor, opciones);
        }

        private string Destino => _fixture.Sshd.Destino.Clave;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            await _gestor.DisposeAsync();
            if (Directory.Exists(_directorio))
                Directory.Delete(_directorio, recursive: true);
        }

        private async Task<string> ExecAsync(string script, double segundos = 30)
        {
            ResultadoCanalExec resultado = await _gestor.EjecutarExecAsync(Destino, script, TimeSpan.FromSeconds(segundos), CancellationToken.None);
            return Encoding.UTF8.GetString(resultado.Salida.Span);
        }

        [Fact]
        public async Task CanalesExec_UsanElPathDelLoginCapturadoPorElShell_YSshSearchVeSuRg()
        {
            // Arrange
            await File.WriteAllTextAsync(Path.Combine(_directorio, "a.txt"), "algo\n");

            // Act: sin shell PTY todavía, el canal exec solo tiene el PATH por defecto de sshd.
            string antes = await ExecAsync("command -v ns_sonda_path || echo sin-sonda");
            ResultadoComando rgDelShell = await _herramientas.EjecutarAsync(Destino, "command -v rg");
            string despues = await ExecAsync("command -v ns_sonda_path || echo sin-sonda");
            string busqueda = await _herramientas.BuscarAsync(Destino, "algo", _directorio);

            // Assert
            _ = antes.Should().Be("sin-sonda\n");
            _ = rgDelShell.Salida.Should().Be($"{_fixture.DirectorioLogin}/rg\n");
            _ = despues.Should().Be($"{_fixture.DirectorioLogin}/ns_sonda_path\n");
            _ = busqueda.Should().Be($"{SshdLocalRestringido.LineaRgFalso}\n\n[motor: rg]");
        }

        [Fact]
        public async Task SinTimeoutEnElServidor_ElWatchdogMataElScriptYSusHijosAlVencer()
        {
            // Arrange: el shell captura un PATH sin 'timeout'.
            _ = await _herramientas.EjecutarAsync(Destino, "true");
            string sinTimeout = await ExecAsync("command -v timeout || echo sin-timeout");
            long hijo = 500_000 + Random.Shared.Next(100_000);
            long principal = hijo + 100_000;

            try
            {
                // Act
                ResultadoCanalExec ejecucion = await _gestor.EjecutarExecAsync(Destino, $"sleep {hijo} & trap '' TERM; sleep {principal}; wait", TimeSpan.FromSeconds(2), CancellationToken.None);
                string vivos = await CanalesExecIntegracionTests.EsperarSinProcesosSleepAsync(hijo, principal);

                // Assert
                _ = sinTimeout.Should().Be("sin-timeout\n");
                _ = vivos.Should().BeEmpty();
                _ = ejecucion.CodigoSalida.Should().Be(124);
            }
            finally
            {
                CanalesExecIntegracionTests.MatarProcesosSleep(hijo, principal);
            }
        }

        [Fact]
        public async Task MaxSessionsAlcanzado_MensajeClaroYElShellSigueDisponible()
        {
            // Arrange: el shell ocupa una de las dos sesiones que admite el servidor.
            ResultadoComando largo = await _herramientas.EjecutarAsync(Destino, "sleep 4; echo fin", esperarSegundos: 0);
            await File.WriteAllTextAsync(Path.Combine(_directorio, "a.txt"), "uno\n");

            // Act: cuatro canales a la vez (el cupo local) con un solo hueco en el servidor.
            Task<ResultadoCanalExec>[] intentos = [.. Enumerable.Range(0, 4).Select(_ => _gestor.EjecutarExecAsync(Destino, "sleep 1; echo ok", TimeSpan.FromSeconds(30), CancellationToken.None))];
            List<string> rechazos = [];
            int correctos = 0;
            foreach (Task<ResultadoCanalExec> intento in intentos)
            {
                try
                {
                    correctos += (await intento).CodigoSalida == 0 ? 1 : 0;
                }
                catch (NativeSshException ex)
                {
                    rechazos.Add(ex.Message);
                }
            }

            ResultadoComando fin = await _herramientas.LeerSalidaAsync(largo.Id, esperarSegundos: 20);
            ResultadoComando otro = await _herramientas.EjecutarAsync(Destino, "echo sigue");
            string lectura = await _herramientas.LeerArchivoAsync(Destino, Path.Combine(_directorio, "a.txt"));

            // Assert
            _ = correctos.Should().Be(1);
            _ = rechazos.Should().HaveCount(3).And.AllSatisfy(m => m.Should().Contain("límite de sesiones").And.Contain("MaxSessions").And.Contain("sigue disponible"));
            _ = fin.Salida.Should().Be("fin\n");
            _ = otro.Salida.Should().Be("sigue\n");
            _ = lectura.Should().Be("[Líneas 1-1 de 1]\nuno\n");
            _ = _herramientas.Estado().Sesiones.Single().ConexionesAbiertas.Should().Be(1);
        }
    }
}
