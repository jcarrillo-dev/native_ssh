using FluentAssertions;
using System.Buffers;
using System.Linq;
using System.Text;
using NativeSsh.Protocolo;

namespace NativeSsh.Test.Protocolo
{
    // Estructura de las líneas del protocolo. Lo que hace la cola (esperar el centinela, contar y descartar lo tecleado, el
    // tope del recuento) se prueba por comportamiento con un bash real en ColaCentinelaBashTests y por un terminal real en
    // EntradaPorAdelantadoTests.
    public class ConstructorComandoTests
    {
        private static readonly Guid Id = Guid.Parse("0123456789abcdef0123456789abcdef");
        private static readonly Guid Testigo = Guid.Parse("fedcba9876543210fedcba9876543210");
        private static readonly Guid Centinela = Guid.Parse("00112233445566778899aabbccddeeff");

        private static string LineaCentinela => $"#{Centinela:N}\n";

        // Delante del centinela, en los envíos que pueden llegar tras algo tecleado (comando y recuperación).
        private static string PrefijoCentinela => "\n" + LineaCentinela;

        // El marcador de fin (con el recuento de la cola anterior).
        private static string Marcador => $"command printf '%s_%s_%d%s__\\n' '__NSX' '{Id:N}' \"${{__ns_rc-997}}\" \"${{__ns_n:+_$__ns_n}}\"; unset __ns_rc; ";

        // Principio de toda cola: el marcador y después la espera del centinela.
        private static string MarcadorDeLaCola => $"{Marcador}__ns_n=; ";

        private const string FinDeLaCola = "; __ns_n=${__ns_c#0}; unset __ns_d __ns_x __ns_c __ns_e __ns_f __ns_g __ns_s __ns_v";

        private static string Componer(Action<ArrayBufferWriter<byte>> accion)
        {
            ArrayBufferWriter<byte> escritor = new();
            accion(escritor);
            return Encoding.UTF8.GetString(escritor.WrittenSpan);
        }

        // El comando del envío: el base64 decodificado sin el salto de línea y el nonce del final, que deben estar.
        private static string DecodificarComando(string texto)
        {
            const string Nonce = "__ns_k=${__ns_b%$'\\n";
            int inicio = texto.IndexOf(Nonce, StringComparison.Ordinal) + Nonce.Length;
            string nonce = string.Concat("\n", texto.AsSpan(inicio, 32));
            StringBuilder base64 = new();
            foreach (string linea in texto.Split('\n'))
                if (linea.StartsWith("__ns_b='", StringComparison.Ordinal))
                    _ = base64.Append(linea[8..^1]);
                else if (linea.StartsWith("__ns_b+='", StringComparison.Ordinal))
                    _ = base64.Append(linea[9..^1]);

            string decodificado = Encoding.UTF8.GetString(Convert.FromBase64String(base64.ToString()));
            _ = decodificado.Should().EndWith(nonce);
            return decodificado[..^nonce.Length];
        }

        // El nonce del envío de un comando (32 hexadecimales).
        private static string Nonce(string texto) => texto.Substring(texto.IndexOf("__ns_k=${__ns_b%$'\\n", StringComparison.Ordinal) + 20, 32);

        // La cola de 'texto': desde su marcador hasta el final.
        private static string Cola(string texto) => texto[texto.IndexOf("command printf '%s_%s_%d%s__", StringComparison.Ordinal)..];

        [Theory]
        [InlineData("echo hola")]
        [InlineData("cat <<'EOF'\nñandú ✓ \"x\" 'y' $HOME #no\nEOF")]
        [InlineData("a=1; b=2; echo \"$a$b\" | tr -d '\\n'")]
        public void Comando_IdaYVuelta(string comando)
        {
            // Arrange
            string texto = Componer(w => ConstructorComando.EscribirComando(comando, Id, Centinela, w));

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
            string texto = Componer(w => ConstructorComando.EscribirComando(comando, Id, Centinela, w));
            string[] lineas = texto.Split('\n');

            // Assert
            _ = lineas.Should().AllSatisfy(linea => (Encoding.UTF8.GetByteCount(linea) <= 4095).Should().BeTrue());
            int lineasBase64 = lineas.Count(l => l.StartsWith("__ns_b", StringComparison.Ordinal));
            _ = lineasBase64.Should().Be((bytes + ConstructorComando.BytesPorLinea - 1) / ConstructorComando.BytesPorLinea);
            int base64 = lineas.Where(l => l.StartsWith("__ns_b", StringComparison.Ordinal)).Sum(l => l.Length - l.IndexOf('\'', StringComparison.Ordinal) - 2);
            _ = lineas[^2].Should().Contain($"command [ \"${{#__ns_b}}\" = '{base64}' ]", "ejecuta el comando solo si llegaron todas las líneas del base64");
            _ = DecodificarComando(texto).Should().Be(comando);
        }

        // El envío de un comando: salto y centinela, el comando en base64 y una última línea que lo ejecuta y termina con la
        // cola.
        [Fact]
        public void Comando_Estructura()
        {
            // Arrange
            string texto = Componer(w => ConstructorComando.EscribirComando("ls", Id, Centinela, w));

            // Act & Assert
            _ = texto.Should().StartWith($"{PrefijoCentinela}__ns_b='");
            _ = texto.Split('\n')[^2].Should().StartWith($"__ns_rc={MarcadorFin.CodigoEnvioIncompleto}; __ns_b=${{__ns_b-}}; command [ \"${{#__ns_b}}\" = '48' ] && {{ __ns_b=$(command printf '%s' \"$__ns_b\" | base64 -d); }} 2>/dev/null && __ns_k=${{__ns_b%$'\\n{Nonce(texto)}'}} && command [ \"${{#__ns_k}}\" -lt \"${{#__ns_b}}\" ] && {{ eval \"$__ns_k\"; __ns_rc=$?; }}; unset __ns_b __ns_k; cd \"${{__NS_CWD:-$HOME}}\" 2>/dev/null || cd ~; {MarcadorDeLaCola}");
            _ = texto.Should().EndWith(FinDeLaCola + "\n");
            _ = texto.Should().NotContain($"__NSX_{Id:N}_").And.NotContain("trap");
            _ = MarcadorFin.CodigoEnvioIncompleto.Should().BeInRange(256, 999).And.NotBe(MarcadorFin.CodigoShellAnidado);
        }

        // Todo envío salvo la inicialización (la lee el shell de login, que puede no ser bash) empieza por la línea centinela
        // del canal: es lo que termina la espera de la cola anterior (ConstructorComando.EscribirCola). Los de comando y
        // recuperación, que pueden llegar tras una línea a medias tecleada, llevan un salto delante. La sincronización lleva
        // dos (la de la copia de la inicialización que bash pueda tener en su búfer).
        [Fact]
        public void TodoEnvioSalvoLaInicializacion_EmpiezaPorLaLineaCentinela()
        {
            // Act
            string comando = Componer(w => ConstructorComando.EscribirComando("ls", Id, Centinela, w));
            string reenvio = Componer(w => ConstructorComando.EscribirReenvioInicializacion(Id, Centinela, w));
            string sincronizacion = Componer(w => ConstructorComando.EscribirSincronizacion(Id, Centinela, w));
            string captura = Componer(w => ConstructorComando.EscribirCapturaPath(Id, Testigo, Centinela, w));
            string recuperacion = Componer(w => ConstructorComando.EscribirRecuperacion(Id, Centinela, w));
            string inicializacion = Componer(w => ConstructorComando.EscribirInicializacion(Id, w));

            // Assert
            _ = new[] { comando, recuperacion }.Should().AllSatisfy(t => t.Should().StartWith(PrefijoCentinela).And.NotStartWith(PrefijoCentinela + LineaCentinela));
            _ = new[] { reenvio, captura }.Should().AllSatisfy(t => t.Should().StartWith(LineaCentinela).And.NotStartWith(LineaCentinela + LineaCentinela));
            _ = sincronizacion.Should().StartWith(LineaCentinela + LineaCentinela + "__ns_rc=0; ");
            _ = inicializacion.Should().StartWith("exec bash --noediting --norc --noprofile\nPS1=;");
            _ = reenvio.Should().Be(LineaCentinela + Componer(w => ConstructorComando.EscribirLineaInicializacion(Id, w)));
            _ = ConstructorComando.LongitudLineaCentinela.Should().Be(LineaCentinela.Length - 1);
        }

        // Toda cola salvo la de la línea de inicialización imprime el marcador y después espera el centinela del canal, una sola
        // vez y en la misma línea, que termina borrando las variables del bucle.
        [Fact]
        public void TodasLasColas_MarcadorYDespuesLaEsperaDelCentinela()
        {
            // Arrange
            IdentidadShell identidad = new(123456, 789, Testigo);
            string[] textos =
            [
                Componer(w => ConstructorComando.EscribirComando("ls", Id, Centinela, w)),
                Componer(w => ConstructorComando.EscribirComando("ls", Id, Centinela, w, identidad)),
                Componer(w => ConstructorComando.EscribirSincronizacion(Id, Centinela, w)),
                Componer(w => ConstructorComando.EscribirCapturaPath(Id, Testigo, Centinela, w)),
                Componer(w => ConstructorComando.EscribirRecuperacion(Id, Centinela, w, identidad))
            ];

            // Act & Assert
            foreach (string texto in textos)
            {
                string cola = Cola(texto);
                bool isRecuperacion = ReferenceEquals(texto, textos[^1]);
                _ = cola.Should().StartWith(MarcadorDeLaCola);
                _ = cola.Should().Contain("command read -r __ns_d").And.Contain($"'#{Centinela:N}'");
                _ = cola.Split($"{Centinela:N}").Should().HaveCount(isRecuperacion ? 3 : 2, "el centinela aparece una sola vez en la cola (y al final de la línea de recuperación)");
                _ = cola.Should().EndWith(isRecuperacion ? $"{FinDeLaCola} #{Centinela:N}\n" : $"{FinDeLaCola}\n");
                _ = cola.Count(c => c == '\n').Should().Be(1, "la cola es una sola línea");
                _ = cola.Should().NotContain("!", "con la expansión del historial activa, '!' rompería la línea");
            }
        }

        // La línea de inicialización consume una sola línea tras el marcador (siempre es una línea centinela) y es corta y de
        // longitud fija (sin el directorio de trabajo, que va en la captura): con un login lento con readline, sus dos copias y
        // el centinela del reenvío deben caber en una lectura del búfer de entrada de bash (unos 1024 bytes, medido con bash
        // 5.3).
        [Fact]
        public void LineaDeInicializacion_ConsumeUnaSolaLineaYEsCorta()
        {
            // Act
            string linea = Componer(w => ConstructorComando.EscribirLineaInicializacion(Id, w));

            // Assert
            _ = linea.Should().EndWith($"{Marcador}command read -r __ns_d; unset __ns_d\n");
            _ = linea.Should().NotContain("__NS_CWD");
            _ = ((2 * Encoding.UTF8.GetByteCount(linea)) + LineaCentinela.Length).Should().BeLessThan(600, "deja margen de sobra hasta los 1024 bytes");
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
        public void Recuperacion_NoLlevaCtrlCYBorraLoQuePudoDejarUnaColaAbortada()
        {
            // Arrange
            string texto = Componer(w => ConstructorComando.EscribirRecuperacion(Id, Centinela, w));

            // Act & Assert
            _ = texto.Should().StartWith($"{PrefijoCentinela}__ns_rc=130; unset __ns_b __ns_k __ns_d; cd \"${{__NS_CWD:-$HOME}}\" 2>/dev/null || cd ~; {MarcadorDeLaCola}");
            _ = texto.Should().EndWith($"{FinDeLaCola} #{Centinela:N}\n", "termina en el centinela: una cola que la lea fuera de sitio la reconoce y no la cuenta");
            _ = texto.Should().NotContain("\u0003");
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
            _ = linea.Should().Contain("stty -echo -onlcr -ixon");
            _ = linea.Should().Contain("set +o history +m");
            _ = linea.Should().Contain($"'{Id:N}'");
            _ = linea.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().ContainSingle();
        }

        // El resto de la preparación va en la captura: variables, mesg (o su equivalente sin mesg) y el directorio de trabajo
        // reexportado en base64 antes del cd.
        [Fact]
        public void CapturaPath_PreparaElShellYReexportaNsCwdEnBase64AntesDelCd()
        {
            // Act
            string captura = Componer(w => ConstructorComando.EscribirCapturaPath(Id, Testigo, Centinela, w, "/srv/it's"));

            // Assert
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("/srv/it's"));
            _ = captura.Should().StartWith($"{LineaCentinela}__ns_rc=0; export TERM=dumb PAGER=cat GIT_PAGER=cat SYSTEMD_PAGER=cat MANPAGER=cat; {{ mesg n || chmod g-w -- \"$(tty)\"; }} 2>/dev/null; export __NS_CWD=\"$(command printf '%s' '{base64}' | base64 -d)\"; cd \"${{__NS_CWD:-$HOME}}\" 2>/dev/null || cd ~; eval ");
            _ = captura.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2, "el centinela y una sola línea");
        }

        [Fact]
        public void CapturaPath_CentinelaTestigoYPathEnBase64EntreMarcadoresEnUnaLineaConMarcador()
        {
            // Act
            string texto = Componer(w => ConstructorComando.EscribirCapturaPath(Id, Testigo, Centinela, w));

            // Assert: el centinela termina la espera de la cola anterior; los marcadores no aparecen en el texto. El testigo se
            // guarda en un array de solo lectura y la cabecera de la identidad solo sale entera si quedó guardado.
            _ = texto.Should().StartWith($"{LineaCentinela}__ns_rc=0; export TERM=dumb PAGER=cat GIT_PAGER=cat SYSTEMD_PAGER=cat MANPAGER=cat; {{ mesg n || chmod g-w -- \"$(tty)\"; }} 2>/dev/null; cd \"${{__NS_CWD:-$HOME}}\" 2>/dev/null || cd ~; eval \"declare -r -a __NS_TESTIGO=('{Testigo:N}')\"; __ns_t=${{__NS_TESTIGO-x}}; __ns_t=${{__ns_t#'{Testigo:N}'}}; command printf '%s%s_%s_%d_%d__\\n' '__NSI' \"$__ns_t\" '{Id:N}' \"$$\" \"$PPID\"; command printf '%s_%s_\\n' '__NSP' '{Id:N}'; command printf '%s' \"$PATH\" | base64 | tr -d '\\n'; command printf '\\n%s_%s_\\n' '__NSQ' '{Id:N}'; {MarcadorDeLaCola}");
            _ = texto.Should().NotContain($"__NSP_{Id:N}").And.NotContain($"__NSQ_{Id:N}").And.NotContain($"__NSI_{Id:N}");
        }

        [Fact]
        public void ComandoYRecuperacion_ConIdentidad_ComparanPidPadreYTestigoJustoAntesDelMarcador()
        {
            // Arrange
            IdentidadShell identidad = new(123456, 789, Testigo);
            string comprobacion = $"__ns_t=\"$$ $PPID ${{__NS_TESTIGO-}}\"; __ns_t=${{__ns_t#'123456 789 {Testigo:N}'}}; __ns_t=${{__ns_t:+{MarcadorFin.CodigoShellAnidado}}}; __ns_rc=${{__ns_t:-${{__ns_rc-997}}}}; ";

            // Act
            string comando = Componer(w => ConstructorComando.EscribirComando("ls", Id, Centinela, w, identidad));
            string recuperacion = Componer(w => ConstructorComando.EscribirRecuperacion(Id, Centinela, w, identidad));
            string sinIdentidad = Componer(w => ConstructorComando.EscribirComando("ls", Id, Centinela, w));

            // Assert: la comprobación va después del eval (y del cd), así que la ejecuta el shell que ejecuta la cola.
            _ = comando.Should().Contain($"cd \"${{__NS_CWD:-$HOME}}\" 2>/dev/null || cd ~; {comprobacion}{MarcadorDeLaCola}");
            _ = recuperacion.Should().Contain($"cd \"${{__NS_CWD:-$HOME}}\" 2>/dev/null || cd ~; {comprobacion}{MarcadorDeLaCola}");
            _ = sinIdentidad.Should().NotContain("$PPID").And.NotContain("__NS_TESTIGO");
            _ = MarcadorFin.CodigoShellAnidado.Should().BeInRange(256, 999, "ningún código de salida (0-255) puede coincidir y cabe en los 3 dígitos del marcador");
        }

        [Fact]
        public void ClausulaFallback_PresenteEnTodosLosComandos()
        {
            // Arrange
            const string clausulaEsperada = "cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; ";

            // Act
            string comando = Componer(w => ConstructorComando.EscribirComando("ls", Id, Centinela, w));
            string captura = Componer(w => ConstructorComando.EscribirCapturaPath(Id, Testigo, Centinela, w));
            string interrupcion = Componer(w => ConstructorComando.EscribirRecuperacion(Id, Centinela, w));

            // Assert
            _ = comando.Should().Contain(clausulaEsperada);
            _ = captura.Should().Contain(clausulaEsperada);
            _ = interrupcion.Should().Contain(clausulaEsperada);
        }
    }
}
