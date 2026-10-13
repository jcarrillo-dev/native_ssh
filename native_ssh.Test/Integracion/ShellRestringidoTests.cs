using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
    // El shell PTY contra un servidor con MaxSessions 2 (fixture restringida).
    [Collection(ColeccionSshdRestringido.Nombre)]
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El gestor se libera en IAsyncLifetime.DisposeAsync.")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class ShellRestringidoTests : IAsyncLifetime
    {
        private readonly SshdLocalRestringido _fixture;
        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;

        public ShellRestringidoTests(SshdLocalRestringido fixture)
        {
            _fixture = fixture;
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

        public async Task DisposeAsync() => await _gestor.DisposeAsync();

        [Fact]
        public async Task MaxSessionsAlAbrirElShell_MensajeClaroYElShellSeAbreAlQuedarHueco()
        {
            // Arrange: antes de que exista el shell, dos canales exec ocupan las dos sesiones que admite el servidor.
            string duracion = $"3.{Random.Shared.Next(100_000, 999_999).ToString(CultureInfo.InvariantCulture)}";
            Task<ResultadoCanalExec>[] ocupan = [.. Enumerable.Range(0, 2).Select(_ => _gestor.EjecutarExecAsync(Destino, $"sleep {duracion}; echo ocupado", TimeSpan.FromSeconds(30), CancellationToken.None))];
            await EsperasPrueba.EsperarHastaAsync(() => BashLocal.Ejecutar($"ps -eo args= | awk '$1==\"sleep\" && $2==\"{duracion}\"' | wc -l") == "2");

            // Act
            ResultadoComando sinHueco = await _herramientas.EjecutarAsync(Destino, "echo sin_hueco", esperarSegundos: 10);
            ResultadoCanalExec[] ocupados = await Task.WhenAll(ocupan);
            ResultadoComando conHueco = await _herramientas.EjecutarAsync(Destino, "echo con_hueco");

            // Assert
            _ = sinHueco.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = sinHueco.Aviso.Should().Contain("MaxSessions").And.Contain("sigue abierta").And.NotContain("Failed to open a channel");
            _ = ocupados.Should().AllSatisfy(r => r.CodigoSalida.Should().Be(0));
            _ = conHueco.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = conHueco.Salida.Should().Be("con_hueco\n");
            _ = _herramientas.Estado().Sesiones.Single().ConexionesAbiertas.Should().Be(1);
        }
    }
}
