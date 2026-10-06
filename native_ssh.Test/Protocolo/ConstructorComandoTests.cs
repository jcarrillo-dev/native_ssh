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
        private static readonly Guid Testigo = Guid.Parse("fedcba9876543210fedcba9876543210");

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
        public void CtrlC_VaSoloEnSuEscritura()
        {
            // Act
            string texto = Componer(ConstructorComando.EscribirCtrlC);

            // Assert
            _ = texto.Should().Be("\u0003");
        }

        [Fact]
        public void Recuperacion_NoLlevaCtrlCYEmpiezaConUnaLineaVacia()
        {
            // Arrange
            string texto = Componer(w => ConstructorComando.EscribirRecuperacion(Id, w));

            // Act & Assert
            _ = texto.Should().StartWith("\n__ns_rc=130; unset __ns_b; cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; ");
            _ = texto.Should().NotContain("\u0003");
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
        public void CapturaPath_SaltoInicialTestigoYPathEnBase64EntreMarcadoresEnUnaLineaConMarcador()
        {
            // Act
            string texto = Componer(w => ConstructorComando.EscribirCapturaPath(Id, Testigo, w));

            // Assert: el primer '\n' satisface el 'read' del marcador anterior; los marcadores no aparecen en el texto. El
            // testigo se guarda en un array de solo lectura y la cabecera de la identidad solo sale entera si quedó guardado.
            _ = texto.Should().Be($"\n__ns_rc=0; declare -r -a __NS_TESTIGO=('{Testigo:N}'); __ns_t=${{__NS_TESTIGO-x}}; __ns_t=${{__ns_t#'{Testigo:N}'}}; printf '%s%s_%s_%d_%d__\\n' '__NSI' \"$__ns_t\" '{Id:N}' \"$$\" \"$PPID\"; printf '%s_%s_\\n' '__NSP' '{Id:N}'; printf '%s' \"$PATH\" | base64 | tr -d '\\n'; printf '\\n%s_%s_\\n' '__NSQ' '{Id:N}'; printf '%s_%s_%d__\\n' '__NSX' '{Id:N}' \"$__ns_rc\"; read -r __ns_d\n");
            _ = texto.Should().NotContain($"__NSP_{Id:N}").And.NotContain($"__NSQ_{Id:N}").And.NotContain($"__NSI_{Id:N}");
        }

        [Fact]
        public void ComandoYRecuperacion_ConIdentidad_ComparanPidPadreYTestigoJustoAntesDelMarcador()
        {
            // Arrange
            IdentidadShell identidad = new(123456, 789, Testigo);
            string cola = $"__ns_t=\"$$ $PPID ${{__NS_TESTIGO-}}\"; __ns_t=${{__ns_t#'123456 789 {Testigo:N}'}}; __ns_t=${{__ns_t:+{MarcadorFin.CodigoShellAnidado}}}; __ns_rc=${{__ns_t:-$__ns_rc}}; printf '%s_%s_%d__\\n' '__NSX' '{Id:N}' \"$__ns_rc\"; read -r __ns_d\n";

            // Act
            string comando = Componer(w => ConstructorComando.EscribirComando("ls", Id, w, identidad));
            string recuperacion = Componer(w => ConstructorComando.EscribirRecuperacion(Id, w, identidad));
            string sinIdentidad = Componer(w => ConstructorComando.EscribirComando("ls", Id, w));

            // Assert: la comprobación va después del eval (y del cd), así que la ejecuta el shell que ejecuta la cola.
            _ = comando.Should().EndWith($"cd \"${{__NS_CWD:-$HOME}}\" 2>/dev/null || cd ~; {cola}");
            _ = recuperacion.Should().EndWith($"cd \"${{__NS_CWD:-$HOME}}\" 2>/dev/null || cd ~; {cola}");
            _ = sinIdentidad.Should().NotContain("$PPID").And.NotContain("__NS_TESTIGO");
            _ = MarcadorFin.CodigoShellAnidado.Should().BeInRange(256, 999, "ningún código de salida (0-255) puede coincidir y cabe en los 3 dígitos del marcador");
        }

        [Fact]
        public void ClausulaFallback_PresenteEnTodosLosComandos()
        {
            // Arrange
            const string clausulaEsperada = "cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; ";

            // Act
            string comando = Componer(w => ConstructorComando.EscribirComando("ls", Id, w));
            string inicializacion = Componer(w => ConstructorComando.EscribirLineaInicializacion(Id, w));
            string interrupcion = Componer(w => ConstructorComando.EscribirRecuperacion(Id, w));

            // Assert
            _ = comando.Should().Contain(clausulaEsperada);
            _ = inicializacion.Should().Contain(clausulaEsperada);
            _ = interrupcion.Should().Contain(clausulaEsperada);
        }
    }
}
