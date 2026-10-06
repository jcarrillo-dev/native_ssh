using FluentAssertions;
using NativeSsh.Protocolo;

namespace NativeSsh.Test.Protocolo
{
    public class RespuestaSinBashTests
    {
        [Theory]
        [InlineData("exec bash --noediting --norc --noprofile\r\n-ash: exec: line 1: bash: not found\r\n", "-ash: exec: line 1: bash: not found")]
        [InlineData("ash: bash: not found\n", "ash: bash: not found")]
        [InlineData("sh: 1: exec: bash: not found\n", "sh: 1: exec: bash: not found")]
        [InlineData("zsh: command not found: bash\n", "zsh: command not found: bash")]
        [InlineData("-sh: bash: command not found\n", "-sh: bash: command not found")]
        [InlineData("timeout: failed to run command ‘bash’: No such file or directory\n", "timeout: failed to run command ‘bash’: No such file or directory")]
        [InlineData("timeout: failed to run command 'bash': No such file or directory\n", "timeout: failed to run command 'bash': No such file or directory")]
        [InlineData("-ash: exec: line 1: bash: not found\u001b[0m\r", "-ash: exec: line 1: bash: not found[0m")]
        public void BuscarLinea_ConLaRespuestaDeUnShellSinBash_DevuelveEsaLineaLimpia(string texto, string linea)
        {
            // Act
            string? encontrada = RespuestaSinBash.BuscarLinea(texto);

            // Assert
            _ = encontrada.Should().Be(linea);
        }

        [Theory]
        [InlineData("")]
        [InlineData("ash: rg: not found\n")]
        [InlineData("bash: foo: command not found\n")]
        [InlineData("bash: /x: No such file or directory\n")]
        [InlineData("bashrc: not found\n")]
        [InlineData("mibash: not found\n")]
        public void BuscarLinea_SinQueFalteBash_DevuelveNull(string texto)
        {
            // Act
            string? encontrada = RespuestaSinBash.BuscarLinea(texto);

            // Assert
            _ = encontrada.Should().BeNull();
        }

        [Fact]
        public void Limpiar_AcotaLaLineaYQuitaLosCaracteresDeControl()
        {
            // Act
            string limpia = RespuestaSinBash.Limpiar($"\u0007{new string('x', 1000)}\r");

            // Assert
            _ = limpia.Should().Be(new string('x', 300));
        }

        [Fact]
        public void Describir_NombraBashYQueNativeSshLoNecesita()
        {
            // Act
            string mensaje = RespuestaSinBash.Describir("u@h:22", "ash: bash: not found");

            // Assert
            _ = mensaje.Should().Contain("no tiene bash").And.Contain("«ash: bash: not found»").And.Contain("native_ssh lo necesita").And.Contain("No se ejecutó nada")
                .And.NotContain("ssh_stat").And.NotContain("ssh_read_file");
            _ = RespuestaSinBash.IsMensajeSinBash(mensaje).Should().BeTrue();
            _ = RespuestaSinBash.IsMensajeSinBash("ash: bash: not found").Should().BeFalse();
            _ = RespuestaSinBash.IsMensajeSinBash(null).Should().BeFalse();
        }
    }
}
