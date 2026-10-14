using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Varias sesiones del mismo usuario@servidor:puerto (dos alias con distinta IdentityFile): ssh_disconnect cuenta por
    // separado las que estaban conectadas y las que no llegaron a conectarse, y ssh_reset_shell sin sesión con esas claves
    // dice que hay otra del mismo destino con otras claves. Por el binario, con el sshd local.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class VariasSesionesMismoDestinoTests : IAsyncLifetime
    {
        private readonly SshdPropio _sshd = new(string.Empty);
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_variasses_{Guid.NewGuid():N}")).FullName;
        private ClienteMcpBinario? _cliente;

        public async Task InitializeAsync()
        {
            // Dos alias del mismo usuario@127.0.0.1:puerto, con distinta IdentityFile (la misma clave en dos rutas): dos sesiones.
            string claveB = Path.Combine(_directorio, "clave_b");
            File.Copy(_sshd.Sshd.RutaClave, claveB);
            File.SetUnixFileMode(claveB, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string config = Path.Combine(_directorio, "config");
            StringBuilder texto = new();
            foreach ((string alias, string clave) in new[] { ("sa", _sshd.Sshd.RutaClave), ("sb", claveB) })
                _ = texto.Append(CultureInfo.InvariantCulture, $"Host {alias}\n  HostName 127.0.0.1\n  Port {_sshd.Sshd.Puerto}\n  User {Environment.UserName}\n  IdentityFile {clave}\n  IdentitiesOnly yes\n  UserKnownHostsFile {_sshd.Sshd.RutaKnownHosts}\n");
            await File.WriteAllTextAsync(config, texto.ToString());
            _cliente = await ClienteMcpBinario.IniciarAsync(new Dictionary<string, string>
            {
                ["NATIVE_SSH_DIRECTORIO_SSH"] = _sshd.Sshd.DirectorioEstado,
                ["NATIVE_SSH_CONFIG_SSH"] = config
            });
        }

        public async Task DisposeAsync()
        {
            if (_cliente is not null)
                await _cliente.DisposeAsync();
            _sshd.Dispose();
            Directory.Delete(_directorio, recursive: true);
        }

        private string Destino => $"{Environment.UserName}@127.0.0.1:{_sshd.Sshd.Puerto}";

        [Fact]
        public async Task Disconnect_VariasSesionesSinConectar_NoDiceQueCerroConexiones()
        {
            // Arrange
            (_, string resetA) = await _cliente!.LlamarAsync("ssh_reset_shell", new JsonObject { ["destino"] = "sa", ["sinPerfil"] = true });
            (_, string resetB) = await _cliente.LlamarAsync("ssh_reset_shell", new JsonObject { ["destino"] = "sb", ["sinPerfil"] = true });

            // Act
            (bool isError, string texto) = await _cliente.LlamarAsync("ssh_disconnect", new JsonObject { ["destino"] = Destino });

            // Assert
            _ = resetA.Should().Contain("No había sesión");
            _ = resetB.Should().Contain("No había sesión");
            _ = isError.Should().BeFalse(texto);
            _ = texto.Should().NotContain("Se cerraron", "ninguna de las dos sesiones llegó a conectarse").And.NotContain("Conexión cerrada")
                .And.Contain("2 sesiones sin conectar descartadas").And.Contain("no había conexión que cerrar");
        }

        [Fact]
        public async Task Disconnect_UnaConectadaYOtraSinConectar_CuentaCadaUna()
        {
            // Arrange
            (bool isErrorExec, string exec) = await _cliente!.LlamarAsync("ssh_exec", new JsonObject { ["destino"] = "sa", ["comando"] = "echo hola" });
            _ = await _cliente.LlamarAsync("ssh_reset_shell", new JsonObject { ["destino"] = "sb", ["sinPerfil"] = true });

            // Act
            (bool isError, string texto) = await _cliente.LlamarAsync("ssh_disconnect", new JsonObject { ["destino"] = Destino });

            // Assert
            _ = isErrorExec.Should().BeFalse(exec);
            _ = isError.Should().BeFalse(texto);
            _ = texto.Should().Contain("Conexión cerrada").And.Contain(_sshd.Sshd.RutaClave).And.Contain("Sesión sin conectar descartada")
                .And.Contain("clave_b").And.NotContain("Se cerraron 2");
        }

        // ssh_reset_shell con sinPerfil=true y usuario@servidor:puerto (las claves de native_ssh) mientras hay una sesión del
        // mismo destino con otras claves (el alias): no había sesión con esas claves, pero sí otra del destino, que no se toca.
        [Fact]
        public async Task ResetSinPerfil_OtraSesionDelMismoDestinoConOtrasClaves_LoDice()
        {
            // Arrange
            (bool isErrorExec, string exec) = await _cliente!.LlamarAsync("ssh_exec", new JsonObject { ["destino"] = "sa", ["comando"] = "echo hola" });

            // Act
            (_, string reset) = await _cliente.LlamarAsync("ssh_reset_shell", new JsonObject { ["destino"] = Destino, ["sinPerfil"] = true });
            (_, string resetSinSesion) = await _cliente.LlamarAsync("ssh_reset_shell", new JsonObject { ["destino"] = "sb" });

            // Assert
            _ = isErrorExec.Should().BeFalse(exec);
            _ = reset.Should().Contain("No había sesión con ese destino y esas claves").And.Contain("hay otra de").And.Contain(_sshd.Sshd.RutaClave).And.Contain("no se toca");
            _ = resetSinSesion.Should().Contain("No había sesión con ese destino y esas claves").And.Contain("no se toca");
        }
    }
}
