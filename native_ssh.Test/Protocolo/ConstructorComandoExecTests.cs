using System.Text.RegularExpressions;
using FluentAssertions;
using NativeSsh.Protocolo;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Protocolo
{
    public partial class ConstructorComandoExecTests
    {
        [GeneratedRegex(@"^bash --noprofile --norc -c '[^'\\!]*' ns [A-Za-z0-9+/=]+ [0-9]+ (-|[A-Za-z0-9+/=]+) </dev/null$")]
        private static partial Regex RegexComandoPortable();

        [Fact]
        public void Construir_SinDirectorio_EjecutaElScriptEnHomeConBashDesdeBase64()
        {
            // Act
            string comando = ConstructorComandoExec.Construir("echo 'hola' \"$HOME\"\nexit 3", null, TimeSpan.FromSeconds(30));

            // Assert
            _ = ScriptExec.Decodificar(comando).Should().Be("cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~\necho 'hola' \"$HOME\"\nexit 3");
        }

        [Fact]
        public void Construir_ConDirectorio_LoRecibeExplicitamenteConComillasEscapadas()
        {
            // Act
            string comando = ConstructorComandoExec.Construir("pwd", "/srv/it's here", TimeSpan.FromSeconds(30));

            // Assert
            _ = ScriptExec.Decodificar(comando).Should().Be("export __NS_CWD='/srv/it'\\''s here'\ncd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~\npwd");
        }

        [Theory]
        [InlineData(30, "30")]
        [InlineData(1.2, "2")]
        [InlineData(0.1, "1")]
        public void Construir_PasaElTimeoutRemotoEnSegundosRedondeadoHaciaArriba(double segundos, string esperado)
        {
            // Act
            string comando = ConstructorComandoExec.Construir("true", null, TimeSpan.FromSeconds(segundos));

            // Assert
            _ = comando.Split(' ')[^3].Should().Be(esperado);
        }

        [Fact]
        public void Construir_SoloUsaSintaxisComunATodosLosShellsDeLogin()
        {
            // Act: lo interpreta el shell de login (sh, bash, zsh, fish, tcsh...): una palabra simple, un literal entre
            // comillas simples sin '\' ni '!' (fish y csh los tratan distinto) y una redirección de entrada.
            string comando = ConstructorComandoExec.Construir("echo \\ ! ' \" $x", "/a'b", TimeSpan.FromSeconds(5));
            string conPath = ConstructorComandoExec.Construir("true", null, TimeSpan.FromSeconds(5), "/home/ana/.cargo/bin:/usr/bin:/bin");

            // Assert
            _ = RegexComandoPortable().IsMatch(comando).Should().BeTrue(comando);
            _ = RegexComandoPortable().IsMatch(conPath).Should().BeTrue(conPath);
        }

        [Fact]
        public void Construir_PasaElPathDelLoginEnBase64OGuionSiNoSeConoce()
        {
            // Act
            string sinPath = ConstructorComandoExec.Construir("true", null, TimeSpan.FromSeconds(5));
            string conPath = ConstructorComandoExec.Construir("true", null, TimeSpan.FromSeconds(5), "/home/ana/.cargo/bin:/usr/bin");

            // Assert
            _ = ScriptExec.DecodificarPath(sinPath).Should().BeNull();
            _ = ScriptExec.DecodificarPath(conPath).Should().Be("/home/ana/.cargo/bin:/usr/bin");
        }
    }
}
