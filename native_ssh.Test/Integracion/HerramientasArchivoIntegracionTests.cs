using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;

namespace NativeSsh.Test.Integracion
{
    // Herramientas de archivo contra el sshd local: lo que depende del shell real (enlaces, permisos, tiempos...).
    [Collection(ColeccionSshd.Nombre)]
    [Trait("Categoria", "Integracion")]
    public sealed class HerramientasArchivoIntegracionTests : IAsyncDisposable
    {
        private readonly SshdLocal _sshd;
        private readonly string _directorio = Path.Combine(Path.GetTempPath(), $"ns_archivos_{Guid.NewGuid():N}");
        private readonly GestorSesiones _gestor;

        public HerramientasArchivoIntegracionTests(SshdLocal sshd)
        {
            _sshd = sshd;
            _ = Directory.CreateDirectory(_directorio);
            OpcionesNativeSsh opciones = sshd.Opciones();
            _gestor = new GestorSesiones(
                sshd.Fabrica(opciones),
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
        }

        private string Destino => _sshd.Destino.Clave;

        public async ValueTask DisposeAsync()
        {
            await _gestor.DisposeAsync();
            if (Directory.Exists(_directorio))
                Directory.Delete(_directorio, recursive: true);
        }

        private HerramientasSsh Herramientas(OpcionesNativeSsh? opciones = null, TimeSpan? timeout = null) =>
            new(_gestor, opciones ?? _sshd.Opciones()) { TimeoutForzado = timeout };

        private string Ruta(string nombre) => Path.Combine(_directorio, nombre);

        private static string Bash(string comando)
        {
            ProcessStartInfo inicio = new("bash", ["-c", comando]) { RedirectStandardOutput = true };
            using Process proceso = Process.Start(inicio)!;
            string salida = proceso.StandardOutput.ReadToEnd();
            proceso.WaitForExit();
            return salida.Trim();
        }

        [Fact]
        public async Task LeerArchivo_BloqueadoEnUnaFifo_LanzaTiempoAgotadoYElShellSigueUsable()
        {
            // Arrange
            string fifo = Ruta("fifo");
            _ = Bash($"mkfifo '{fifo}'");
            string normal = Ruta("normal.txt");
            await File.WriteAllTextAsync(normal, "uno\n");
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(1));

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync(Destino, fifo))
                .Should().ThrowAsync<NativeSshException>()).Which;
            string siguiente = await herramientas.LeerArchivoAsync(Destino, normal);

            // Assert
            _ = error.Message.Should().Contain("TiempoAgotado");
            _ = siguiente.Should().Be("[Líneas 1-1 de 1]\nuno\n");
        }
    }
}
