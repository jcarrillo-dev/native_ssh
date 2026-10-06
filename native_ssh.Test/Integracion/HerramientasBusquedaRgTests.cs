using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;

namespace NativeSsh.Test.Integracion
{
    // ssh_search por el sshd local con el rg de la máquina (sonda N03 de la auditoría del A-nonies; A-decies, AD2): un
    // directorio que se llama como una exclusión de archivos se recorre. Sin rg en la máquina, no prueba nada.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El sshd y el gestor se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    public sealed class HerramientasBusquedaRgTests : IAsyncLifetime
    {
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_busqueda_rg_{Guid.NewGuid():N}")).FullName;
        private SshdPropio? _sshd;
        private GestorSesiones? _gestor;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (_gestor is not null)
                await _gestor.DisposeAsync();
            _sshd?.Dispose();
            Directory.Delete(_directorio, recursive: true);
        }

        [Fact]
        public async Task Rg_DirectorioQueSeLlamaComoUnaExclusionDeArchivos_SeBuscaDentro()
        {
            // Arrange
            if (!File.Exists("/usr/bin/rg") && !File.Exists("/usr/local/bin/rg"))
                return;
            _sshd = new SshdPropio(string.Empty);
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones();
            _gestor = new GestorSesiones(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(_gestor, opciones);
            string raiz = Directory.CreateDirectory(Path.Combine(_directorio, "arbol")).FullName;
            _ = Directory.CreateDirectory(Path.Combine(raiz, "deps.lock"));
            _ = Directory.CreateDirectory(Path.Combine(raiz, "src.map"));
            await File.WriteAllTextAsync(Path.Combine(raiz, "deps.lock", "a.txt"), "valor_n03\n");
            await File.WriteAllTextAsync(Path.Combine(raiz, "src.map", "b.txt"), "valor_n03\n");
            await File.WriteAllTextAsync(Path.Combine(raiz, "c.txt"), "valor_n03\n");
            await File.WriteAllTextAsync(Path.Combine(raiz, "x.lock"), "valor_n03\n");

            // Act
            string salida = await herramientas.BuscarAsync(_sshd.Sshd.Destino.Clave, "valor_n03", raiz);

            // Assert
            _ = salida.Should().Contain("[motor: rg]")
                .And.Contain("deps.lock/a.txt:1:valor_n03")
                .And.Contain("src.map/b.txt:1:valor_n03")
                .And.Contain("arbol/c.txt:1:valor_n03")
                .And.NotContain("x.lock");
        }
    }
}
