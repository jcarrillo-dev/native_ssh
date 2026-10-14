using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;

namespace NativeSsh.Test.Integracion
{
    // ssh_set_cwd cuando el shell de ssh_exec deja de poder abrirse (como un servidor sin bash o uno que deja de aceptar el
    // login): el comando no llega al shell, el error dice que no se hizo y el directorio de trabajo de native_ssh, el que
    // usan las herramientas de lectura, sigue siendo el anterior (sonda N01 de la auditoría del A-nonies; A-decies, AD1).
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El sshd y el gestor se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class HerramientasSetCwdShellQueNoSeAbreTests : IAsyncLifetime
    {
        private const string Bloqueo = "bloqueo_shell";
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_setcwd_{Guid.NewGuid():N}")).FullName;
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

        // sshd cuyo shell interactivo se abre con normalidad hasta que existe el archivo de bloqueo; desde entonces sale al
        // momento, como uno sin bash. Los canales exec (herramientas de lectura) siguen funcionando.
        private HerramientasSsh Herramientas()
        {
            string bloqueo = Path.Combine(_directorio, Bloqueo);
            _sshd = new SshdPropio(string.Empty, $"/bin/bash -c 'if [ -e {bloqueo} ]; then echo bash: not found >&2; exit 127; fi; exec /bin/bash -l'");
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones();
            _gestor = new GestorSesiones(
                _sshd.Sshd.Fabrica(opciones),
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            return new HerramientasSsh(_gestor, opciones);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ShellQueYaNoSeAbre_DiceQueNoSeHizoYElDirectorioDeTrabajoNoCambia(bool isConRuta)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            string destino = _sshd!.Sshd.Destino.Clave;
            string trabajo = Directory.CreateDirectory(Path.Combine(_directorio, "trabajo")).FullName;
            string otro = Directory.CreateDirectory(Path.Combine(_directorio, "otro")).FullName;
            await File.WriteAllTextAsync(Path.Combine(trabajo, "marca.txt"), "aqui\n");
            _ = await herramientas.FijarDirectorioAsync(destino, trabajo);
            await File.WriteAllTextAsync(Path.Combine(_directorio, Bloqueo), string.Empty);
            _ = herramientas.ReiniciarShell(destino);

            // Act
            Exception? error = await Record.ExceptionAsync(() => herramientas.FijarDirectorioAsync(destino, isConRuta ? otro : ""));
            string lectura = await herramientas.LeerArchivoAsync(destino, "marca.txt");

            // Assert
            string mensaje = error.Should().BeOfType<NativeSshException>().Which.Message;
            _ = mensaje.Should().Contain("no se hizo").And.Contain("no llegó a ejecutarse").And.Contain($"El directorio de trabajo sigue siendo {trabajo}.");
            _ = lectura.Should().Contain("aqui");
            _ = _gestor!.ListarSesiones().Single().DirectorioTrabajo.Should().Be(trabajo);
        }
    }
}
