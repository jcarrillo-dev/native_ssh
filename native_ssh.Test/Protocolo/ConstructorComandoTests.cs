using FluentAssertions;
using System.Buffers;
using System.Linq;
using System.Text;
using NativeSsh.Protocolo;

namespace NativeSsh.Test.Protocolo
{
    public class ConstructorComandoTests
    {
        private static readonly Guid Id = Guid.Parse("0123456789abcdef0123456789abcdef");

        private static string Componer(Action<ArrayBufferWriter<byte>> accion)
        {
            ArrayBufferWriter<byte> escritor = new();
            accion(escritor);
            return Encoding.UTF8.GetString(escritor.WrittenSpan);
        }

        private static string DecodificarComando(string texto)
        {
            StringBuilder base64 = new();
            foreach (string linea in texto.Split('\n'))
            {
                if (linea.StartsWith("__ns_b='", StringComparison.Ordinal))
                    _ = base64.Append(linea[8..^1]);
                else if (linea.StartsWith("__ns_b+='", StringComparison.Ordinal))
                    _ = base64.Append(linea[9..^1]);
            }

            return Encoding.UTF8.GetString(Convert.FromBase64String(base64.ToString()));
        }

        [Theory]
        [InlineData("echo hola")]
        [InlineData("cat <<'EOF'\nñandú ✓ \"x\" 'y' $HOME #no\nEOF")]
        [InlineData("a=1; b=2; echo \"$a$b\" | tr -d '\\n'")]
        public void Comando_IdaYVuelta(string comando)
        {
            // Arrange
            string texto = Componer(w => ConstructorComando.EscribirComando(comando, Id, w));

            // Act & Assert
            _ = DecodificarComando(texto).Should().Be(comando);
        }

        [Fact]
        public void ComandoLargo_LineasPorDebajoDelLimiteDelTty()
        {
            // Arrange
            string comando = new string('x', 20_000) + "ñ";
            int bytes = Encoding.UTF8.GetByteCount(comando);

            // Act
            string texto = Componer(w => ConstructorComando.EscribirComando(comando, Id, w));
            string[] lineas = texto.Split('\n');

            // Assert
            _ = lineas.Should().AllSatisfy(linea => (Encoding.UTF8.GetByteCount(linea) <= 4095).Should().BeTrue());
            int lineasBase64 = lineas.Count(l => l.StartsWith("__ns_b", StringComparison.Ordinal));
            _ = lineasBase64.Should().Be((bytes + ConstructorComando.BytesPorLinea - 1) / ConstructorComando.BytesPorLinea);
            _ = DecodificarComando(texto).Should().Be(comando);
        }

        [Fact]
        public void Comando_Estructura()
        {
            // Arrange
            string texto = Componer(w => ConstructorComando.EscribirComando("ls", Id, w));

            // Act & Assert
            _ = texto.Should().StartWith("\n__ns_b='");
            _ = texto.Should().EndWith("; read -r __ns_d\n");
            _ = texto.Should().Contain("__ns_rc=$?; unset __ns_b; cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; ");
            _ = texto.Should().Contain($"printf '%s_%s_%d__\\n' '__NSX' '{Id:N}' \"$__ns_rc\"");
            _ = texto.Should().NotContain($"__NSX_{Id:N}_");
        }

        [Fact]
        public void Interrupcion_EmpiezaConCtrlC()
        {
            // Arrange
            string texto = Componer(w => ConstructorComando.EscribirInterrupcion(Id, w));

            // Act & Assert
            _ = texto.Should().StartWith("\u0003\n__ns_rc=130; unset __ns_b; cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; ");
            _ = texto.Should().Contain($"'{Id:N}'");
            _ = texto.Should().EndWith("; read -r __ns_d\n");
        }

        [Fact]
        public void Sincronizacion_DosSaltosDeLineaYUnaSolaLineaConMarcador()
        {
            // Arrange
            string texto = Componer(w => ConstructorComando.EscribirSincronizacion(Id, w));

            // Act & Assert
            _ = texto.Should().Be($"\n\n__ns_rc=0; printf '%s_%s_%d__\\n' '__NSX' '{Id:N}' \"$__ns_rc\"; read -r __ns_d\n");
        }

        [Fact]
        public void Inicializacion_Estructura()
        {
            // Arrange
            string texto = Componer(w => ConstructorComando.EscribirInicializacion(Id, w));
            string linea = Componer(w => ConstructorComando.EscribirLineaInicializacion(Id, w));

            // Act & Assert
            _ = texto.Should().StartWith("exec bash --noediting --norc --noprofile\nPS1=;");
            _ = texto.Should().EndWith(linea);
            _ = linea.Should().StartWith("PS1=");
            _ = linea.Should().Contain("stty -echo -onlcr");
            _ = linea.Should().Contain("set +o history +m");
            _ = linea.Should().Contain($"'{Id:N}'");
            _ = linea.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().ContainSingle();
        }

        [Fact]
        public void Inicializacion_ConDirectorio_ReexportaNsCwdEnBase64AntesDelCd()
        {
            // Act
            string linea = Componer(w => ConstructorComando.EscribirLineaInicializacion(Id, w, "/srv/it's"));

            // Assert
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("/srv/it's"));
            _ = linea.Should().Contain($"export __NS_CWD=\"$(printf '%s' '{base64}' | base64 -d)\"; cd \"${{__NS_CWD:-$HOME}}\"");
            _ = linea.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().ContainSingle();
        }

        [Fact]
        public void CapturaPath_SaltoInicialYPathEnBase64EntreMarcadoresEnUnaLineaConMarcador()
        {
            // Act
            string texto = Componer(w => ConstructorComando.EscribirCapturaPath(Id, w));

            // Assert: el primer '\n' satisface el 'read' del marcador anterior; los marcadores no aparecen en el texto.
            _ = texto.Should().Be($"\n__ns_rc=0; printf '%s_%s_\\n' '__NSP' '{Id:N}'; printf '%s' \"$PATH\" | base64 | tr -d '\\n'; printf '\\n%s_%s_\\n' '__NSQ' '{Id:N}'; printf '%s_%s_%d__\\n' '__NSX' '{Id:N}' \"$__ns_rc\"; read -r __ns_d\n");
            _ = texto.Should().NotContain($"__NSP_{Id:N}").And.NotContain($"__NSQ_{Id:N}");
        }

        [Fact]
        public void ClausulaFallback_PresenteEnTodosLosComandos()
        {
            // Arrange
            const string clausulaEsperada = "cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; ";

            // Act
            string comando = Componer(w => ConstructorComando.EscribirComando("ls", Id, w));
            string inicializacion = Componer(w => ConstructorComando.EscribirLineaInicializacion(Id, w));
            string interrupcion = Componer(w => ConstructorComando.EscribirInterrupcion(Id, w));

            // Assert
            _ = comando.Should().Contain(clausulaEsperada);
            _ = inicializacion.Should().Contain(clausulaEsperada);
            _ = interrupcion.Should().Contain(clausulaEsperada);
        }
    }
}
