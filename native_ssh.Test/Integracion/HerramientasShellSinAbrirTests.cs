using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Protocolo;
using NativeSsh.Sesion;

namespace NativeSsh.Test.Integracion
{
    // Servidor en el que el shell de ssh_exec no llega a inicializarse (como uno sin bash: el shell de login sale al fallar
    // 'exec bash' y cierra el canal; aquí un ForceCommand que hace lo mismo). Las herramientas que van por ese shell
    // (ssh_write_file, ssh_replace_file_content, ssh_set_cwd) no llegan a enviar su comando: el error lo dice, da la causa
    // de la sesión y no remite a ssh_stat, ssh_read_file ni ssh_output, que no aportan nada (no se ejecutó nada) y en un
    // servidor sin bash tampoco funcionan (A-nonies, AN4). La causa es la de la sesión: el aviso de que el servidor no tiene
    // bash (RespuestaSinBash, E-nonies, EN4) o, si el shell de login sale por otra causa, sus últimas líneas (camino
    // genérico de EN4; R08 de la revisión de la integración).
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El sshd y el gestor se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class HerramientasShellSinAbrirTests : IAsyncLifetime
    {
        private readonly SshdPropio _sshd = new(string.Empty, "/bin/sh -c 'echo ash: bash: not found >&2; exit 127'");
        private SshdPropio? _sshdOtraCausa;
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_sinabrir_{Guid.NewGuid():N}")).FullName;
        private GestorSesiones? _gestor;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (_gestor is not null)
                await _gestor.DisposeAsync();
            _sshd.Dispose();
            _sshdOtraCausa?.Dispose();
            Directory.Delete(_directorio, recursive: true);
        }

        private HerramientasSsh Herramientas(SshdPropio? sshd = null)
        {
            OpcionesNativeSsh opciones = (sshd ?? _sshd).Sshd.Opciones();
            _gestor = new GestorSesiones(
                (sshd ?? _sshd).Sshd.Fabrica(opciones),
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
            _ = mensaje.Should().Contain(RespuestaSinBash.MarcaMensaje).And.Contain("no llegó a ejecutarse");
            _ = mensaje.Should().NotContain("ssh_stat").And.NotContain("ssh_read_file").And.NotContain("ssh_output");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("uno\n");
            _ = File.Exists(Path.Combine(_directorio, "b.txt")).Should().BeFalse();
        }

        // El shell de login sale durante la inicialización por otra causa que la falta de bash: el error dice que no se
        // ejecutó nada (y, en las escrituras, que el archivo no se ha modificado) con lo último que escribió el servidor, sin
        // el aviso de bash y sin remitir a otras herramientas para comprobar el archivo ni la salida (no hay nada que comprobar).
        // Sí dice que las de lectura siguen funcionando y cómo abrir un shell sin perfil (RC2): el login es el que falla.
        [Theory]
        [InlineData("sobrescribir")]
        [InlineData("anexar")]
        [InlineData("reemplazar")]
        [InlineData("cwd")]
        public async Task ShellQueSaleEnLaInicializacionPorOtraCausa_ElErrorDaEsaCausaYDiceQueNoSeEjecutoNada(string operacion)
        {
            // Arrange
            _sshdOtraCausa = new(string.Empty, "/bin/sh -c 'echo cosa rara del login >&2; exit 1'");
            HerramientasSsh herramientas = Herramientas(_sshdOtraCausa);
            string destino = _sshdOtraCausa.Sshd.Destino.Clave;
            string archivo = Path.Combine(_directorio, "a.txt");
            await File.WriteAllTextAsync(archivo, "uno\n");

            // Act
            Exception? error = await Record.ExceptionAsync(() => operacion switch
            {
                "sobrescribir" => herramientas.EscribirArchivoAsync(destino, archivo, "nuevo\n", overwrite: true),
                "anexar" => herramientas.EscribirArchivoAsync(destino, archivo, "mas\n", append: true),
                "reemplazar" => herramientas.ReemplazarContenidoArchivoAsync(destino, archivo, "uno", "UNO"),
                _ => herramientas.FijarDirectorioAsync(destino, _directorio)
            });

            // Assert
            string mensaje = error.Should().BeOfType<NativeSshException>().Which.Message;
            _ = mensaje.Should().Contain("no llegó a ejecutarse").And.Contain("cosa rara del login").And.NotContain(RespuestaSinBash.MarcaMensaje);
            _ = mensaje.Should().ContainEquivalentOf(operacion == "cwd" ? "sigue siendo" : "no se ha modificado");
            _ = mensaje.Should().NotContain("compruébalo").And.NotContain("ssh_output");
            _ = mensaje.Should().Contain("siguen funcionando").And.Contain("sinPerfil=true");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("uno\n");
        }
    }
}
