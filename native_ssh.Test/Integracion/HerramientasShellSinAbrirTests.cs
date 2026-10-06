using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;

namespace NativeSsh.Test.Integracion
{
    // Servidor en el que el shell de ssh_exec no llega a inicializarse (como uno sin bash: el shell de login sale al fallar
    // 'exec bash' y cierra el canal; aquí un ForceCommand que hace lo mismo). Las herramientas que van por ese shell
    // (ssh_write_file, ssh_replace_file_content, ssh_set_cwd) no llegan a enviar su comando: el error lo dice, da la causa
    // de la sesión y no remite a ssh_stat, ssh_read_file ni ssh_output, que no aportan nada (no se ejecutó nada) y en un
    // servidor sin bash tampoco funcionan (A-nonies, AN4).
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    public sealed class HerramientasShellSinAbrirTests : IAsyncDisposable
    {
        private readonly SshdPropio _sshd = new(string.Empty, "/bin/sh -c 'echo ash: bash: not found >&2; exit 127'");
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_sinabrir_{Guid.NewGuid():N}")).FullName;
        private GestorSesiones? _gestor;

        public async ValueTask DisposeAsync()
        {
            if (_gestor is not null)
                await _gestor.DisposeAsync();
            _sshd.Dispose();
            Directory.Delete(_directorio, recursive: true);
        }

        private HerramientasSsh Herramientas()
        {
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
        [InlineData("sobrescribir")]
        [InlineData("anexar")]
        [InlineData("crear")]
        [InlineData("reemplazar")]
        [InlineData("cwd")]
        public async Task ShellQueNoSeInicializa_ElErrorDiceQueNoSeEjecutoNadaSinRemitirAOtrasHerramientas(string operacion)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            string archivo = Path.Combine(_directorio, "a.txt");
            await File.WriteAllTextAsync(archivo, "uno\n");

            // Act
            Exception? error = await Record.ExceptionAsync(() => operacion switch
            {
                "sobrescribir" => herramientas.EscribirArchivoAsync(destino, archivo, "nuevo\n", overwrite: true),
                "anexar" => herramientas.EscribirArchivoAsync(destino, archivo, "mas\n", append: true),
                "crear" => herramientas.EscribirArchivoAsync(destino, Path.Combine(_directorio, "b.txt"), "nuevo\n"),
                "reemplazar" => herramientas.ReemplazarContenidoArchivoAsync(destino, archivo, "uno", "UNO"),
                _ => herramientas.FijarDirectorioAsync(destino, _directorio)
            });

            // Assert
            string mensaje = error.Should().BeOfType<NativeSshException>().Which.Message;
            _ = mensaje.Should().Contain($"El shell de {destino} se cerró durante la inicialización.").And.Contain("no llegó a ejecutarse");
            _ = mensaje.Should().NotContain("ssh_stat").And.NotContain("ssh_read_file").And.NotContain("ssh_output");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("uno\n");
            _ = File.Exists(Path.Combine(_directorio, "b.txt")).Should().BeFalse();
        }
    }
}
