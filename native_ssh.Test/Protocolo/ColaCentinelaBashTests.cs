using System.Buffers;
using System.Diagnostics;
using System.Linq;
using System.Text;
using FluentAssertions;
using NativeSsh.Protocolo;

namespace NativeSsh.Test.Protocolo
{
    // La cola de las líneas del protocolo (ConstructorComando.EscribirCola) ejecutada por un bash real de la máquina, con la
    // entrada en un fichero: los envíos y lo «tecleado» entre ellos en el orden en que llegarían al terminal. Comprueba por
    // comportamiento lo que antes se comparaba con el texto de la cola: que lo tecleado no se ejecuta, que el recuento
    // viaja en el marcador del envío siguiente y se reinicia, el tope del recuento y que nada del usuario lo rompe. Lo que
    // depende del terminal (Ctrl+D, Ctrl+C) se prueba con un terminal real en EntradaPorAdelantadoTests.
    [Trait("Categoria", "SoloLinux")]
    public sealed class ColaCentinelaBashTests : IDisposable
    {
        private static readonly TimeSpan Limite = TimeSpan.FromSeconds(60);

        private readonly Guid _centinela = IdentidadShell.NuevoTestigo();
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_cola_{Guid.NewGuid():N}")).FullName;
        private readonly ArrayBufferWriter<byte> _entrada = new();
        private readonly List<Guid> _ids = [];
        private int _codigoSalidaBash;
        private readonly List<int> _codigos = [];

        public void Dispose() => Directory.Delete(_directorio, recursive: true);

        private string Testigo(string nombre) => Path.Combine(_directorio, nombre);

        // Un envío de comando, como lo escribe SesionShell; devuelve su posición en los marcadores.
        private int Comando(string comando)
        {
            Guid id = Guid.NewGuid();
            ConstructorComando.EscribirComando(comando, id, _centinela, _entrada);
            _ids.Add(id);
            return _ids.Count - 1;
        }

        private void Teclear(string texto) => _entrada.Write(Encoding.UTF8.GetBytes(texto));

        // Un envío de comando al que otro lector se le come la línea 'perdida' (0 = el salto, 1 = el centinela, 2 = la primera
        // del base64...); devuelve su posición en los marcadores.
        private int ComandoSinLinea(string comando, int perdida)
        {
            Guid id = Guid.NewGuid();
            ArrayBufferWriter<byte> envio = new();
            ConstructorComando.EscribirComando(comando, id, _centinela, envio);
            List<string> lineas = [.. Encoding.UTF8.GetString(envio.WrittenSpan).Split('\n')];
            lineas.RemoveAt(perdida);
            _entrada.Write(Encoding.UTF8.GetBytes(string.Join('\n', lineas)));
            _ids.Add(id);
            return _ids.Count - 1;
        }

        // Un envío de comando que llega cambiado: 'cambio' recibe sus líneas (sin el '\n' final) y devuelve las que llegan al
        // shell, como si otro proceso que lee del terminal se comiera parte de él; devuelve su posición en los marcadores.
        private int ComandoCambiado(string comando, Func<List<string>, List<string>> cambio)
        {
            Guid id = Guid.NewGuid();
            ArrayBufferWriter<byte> envio = new();
            ConstructorComando.EscribirComando(comando, id, _centinela, envio);
            List<string> lineas = [.. Encoding.UTF8.GetString(envio.WrittenSpan).Split('\n')];
            _entrada.Write(Encoding.UTF8.GetBytes(string.Join('\n', cambio(lineas))));
            _ids.Add(id);
            return _ids.Count - 1;
        }

        // Una línea de recuperación (tras Ctrl+C), como la escribe SesionShell; devuelve su posición en los marcadores.
        // isComidas: las dos primeras líneas (el salto y el centinela) se las come otro lector y no llegan; entonces su
        // marcador no sale (la cola que esperaba la consume sin ejecutarla) y no se añade.
        private int Recuperacion(Guid id, bool isComidas = false)
        {
            ArrayBufferWriter<byte> linea = new();
            ConstructorComando.EscribirRecuperacion(id, _centinela, linea);
            ReadOnlySpan<byte> texto = linea.WrittenSpan;
            if (isComidas)
            {
                _entrada.Write(texto[(2 + ConstructorComando.LongitudLineaCentinela)..]);
                return -1;
            }

            _entrada.Write(texto);
            _ids.Add(id);
            return _ids.Count - 1;
        }

        // Ejecuta todo con 'bash -s' y devuelve, por envío, el recuento de su marcador (null si no lo llevaba) y la salida.
        // entorno: variables de más para el bash (p. ej. el locale).
        private async Task<(int? Recuento, string Salida)[]> EjecutarAsync(IReadOnlyDictionary<string, string>? entorno = null)
        {
            string fichero = Path.Combine(_directorio, "entrada");
            await File.WriteAllBytesAsync(fichero, _entrada.WrittenMemory.ToArray());
            ProcessStartInfo inicio = new("bash", ["--norc", "--noprofile", "-c", "exec bash --norc --noprofile -s < \"$1\"", "ns", fichero])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach ((string nombre, string valor) in entorno ?? new Dictionary<string, string>())
                inicio.Environment[nombre] = valor;
            using Process bash = Process.Start(inicio)!;
            Task<string> errores = bash.StandardError.ReadToEndAsync();
            string salida;
            try
            {
                salida = await bash.StandardOutput.ReadToEndAsync().WaitAsync(Limite);
                await bash.WaitForExitAsync().WaitAsync(Limite);
            }
            finally
            {
                // Una cola que no termina (p. ej. una mutación sin el tope de EOF) dejaría el bash girando para siempre.
                if (!bash.HasExited)
                    bash.Kill(entireProcessTree: true);
            }

            _codigoSalidaBash = bash.ExitCode;
            _ = (await errores).Should().BeEmpty("la cola no escribe nada en stderr");

            _codigos.Clear();
            (int?, string)[] resultado = new (int?, string)[_ids.Count];
            int desde = 0;
            for (int i = 0; i < _ids.Count; i++)
            {
                string cabecera = $"__NSX_{_ids[i]:N}_";
                int posicion = salida.IndexOf(cabecera, desde, StringComparison.Ordinal);
                _ = posicion.Should().BeGreaterThanOrEqualTo(0, $"el envío {i} da su marcador; salida: {salida}");
                int fin = salida.IndexOf("__\n", posicion + cabecera.Length, StringComparison.Ordinal);
                string[] partes = salida[(posicion + cabecera.Length)..fin].Split('_');
                _codigos.Add(int.Parse(partes[0], System.Globalization.CultureInfo.InvariantCulture));
                resultado[i] = (partes.Length > 1 ? int.Parse(partes[1], System.Globalization.CultureInfo.InvariantCulture) : null, salida[desde..posicion]);
                desde = fin + 3;
            }

            return resultado;
        }

        [Fact]
        public async Task LineasTecleadas_NoSeEjecutanYCuentanEnElMarcadorSiguienteUnaVez()
        {
            // Arrange
            _ = Comando("echo A");
            Teclear($"respuesta\ntouch {Testigo("uno")}\n\n");
            _ = Comando("echo B");
            _ = Comando("echo C");

            // Act
            (int? Recuento, string Salida)[] marcadores = await EjecutarAsync();

            // Assert
            _ = File.Exists(Testigo("uno")).Should().BeFalse();
            _ = marcadores.Select(m => m.Recuento).Should().Equal(null, 3, null);
            _ = marcadores[1].Salida.Should().Be("B\n");
        }

        [Fact]
        public async Task LineaAMedias_SeJuntaConElCentinelaYCuentaComoUna()
        {
            // Arrange
            _ = Comando("true");
            Teclear($"l1\ntouch {Testigo("medias")}");
            _ = Comando("echo B");

            // Act
            (int? Recuento, string Salida)[] marcadores = await EjecutarAsync();

            // Assert
            _ = File.Exists(Testigo("medias")).Should().BeFalse();
            _ = marcadores[1].Recuento.Should().Be(2);
        }

        // Una línea que lleva el centinela en medio (no al final) no termina la espera: se cuenta y se descarta.
        [Fact]
        public async Task CentinelaEnMedioDeUnaLinea_NoTerminaLaEspera()
        {
            // Arrange
            _ = Comando("true");
            Teclear($"#{_centinela:N}; touch {Testigo("medio")}\n");
            _ = Comando("echo B");

            // Act
            (int? Recuento, string Salida)[] marcadores = await EjecutarAsync();

            // Assert
            _ = File.Exists(Testigo("medio")).Should().BeFalse();
            _ = marcadores[1].Recuento.Should().Be(1);
        }

        // El recuento se queda en MarcadorFin.MaxLineasDescartadas (más cifras romperían el marcador) y se descartan todas.
        [Fact]
        public async Task MasLineasQueElTope_ElRecuentoSeQuedaEnElTopeYNoSeEjecutaNinguna()
        {
            // Arrange
            _ = Comando("true");
            Teclear(string.Concat(Enumerable.Repeat("x\n", MarcadorFin.MaxLineasDescartadas + 1)));
            Teclear($"touch {Testigo("tope")}\n");
            _ = Comando("echo B");
            _ = Comando("echo C");

            // Act
            (int? Recuento, string Salida)[] marcadores = await EjecutarAsync();

            // Assert
            _ = File.Exists(Testigo("tope")).Should().BeFalse();
            _ = marcadores.Select(m => m.Recuento).Should().Equal(null, MarcadorFin.MaxLineasDescartadas, null);
        }

        // Una función o un alias 'read', 'break' o 'test', las opciones del usuario (set -u -e -f -a, pipefail, un IFS raro,
        // opciones de patrones) y alias de 'case', 'esac', 'in' y de las variables del bucle no cambian nada.
        [Theory]
        [InlineData("read() { return 0; }")]
        [InlineData("shopt -s expand_aliases; alias read=true")]
        [InlineData("break() { :; }; test() { return 0; }; [() { return 0; }")]
        [InlineData("set -u -e -o pipefail -f -a; IFS='0123456789abcdef '; shopt -s nocasematch extglob nullglob failglob dotglob expand_aliases; alias case='echo ALIAS' esac='echo ALIAS' in='echo ALIAS' __ns_x='echo ALIAS' __ns_d='echo ALIAS'")]
        public async Task LoQueDejaElUsuario_NoRompeLaEspera(string preparacion)
        {
            // Arrange
            _ = Comando(preparacion);
            _ = Comando("echo A");
            Teclear($"touch {Testigo("usuario")}\notra\n");
            _ = Comando("echo B");

            // Act
            (int? Recuento, string Salida)[] marcadores = await EjecutarAsync();

            // Assert
            _ = File.Exists(Testigo("usuario")).Should().BeFalse();
            _ = marcadores.Select(m => m.Salida).Should().Equal("", "A\n", "B\n");
            _ = marcadores.Select(m => m.Recuento).Should().Equal(null, null, 2);
        }

        // Con 'set -x' la traza no lleva lo tecleado (el bucle descarta su stderr).
        [Fact]
        public async Task ConTraza_LoTecleadoNoApareceEnLaSalida()
        {
            // Arrange
            string canario = $"Canario{Guid.NewGuid():N}";
            _ = Comando("exec 2>&1; set -x");
            Teclear($"{canario}\n");
            _ = Comando("set +x");

            // Act
            (int? Recuento, string Salida)[] marcadores = await EjecutarAsync();

            // Assert
            _ = marcadores.Should().AllSatisfy(m => m.Salida.Should().NotContain(canario));
            _ = marcadores[1].Recuento.Should().Be(1);
        }

        // La línea vacía que deja el salto delante del centinela (si no había una línea a medias) no se cuenta; las vacías
        // tecleadas, sí: el recuento es exacto.
        [Theory]
        [InlineData("", 0)]
        [InlineData("\n", 1)]
        [InlineData("\n\n", 2)]
        [InlineData("uno\n\n", 2)]
        [InlineData("\nuno\n", 2)]
        [InlineData("uno", 1)]
        [InlineData("uno\ndos", 2)]
        public async Task LineaVaciaDelSalto_NoSeCuenta(string tecleado, int esperado)
        {
            // Arrange
            _ = Comando("true");
            Teclear(tecleado);
            _ = Comando("echo B");

            // Act
            (int? Recuento, string Salida)[] marcadores = await EjecutarAsync();

            // Assert
            _ = marcadores[1].Recuento.Should().Be(esperado == 0 ? null : esperado);
            _ = marcadores[1].Salida.Should().Be("B\n");
        }

        // La línea de recuperación termina en el centinela: si otro lector (un programa que atrapa Ctrl+C y lee dos líneas) se
        // come el salto y el centinela que la preceden, la cola que esperaba la lee en su lugar, termina sin contarla como
        // tecleada y sin ejecutarla, y la ejecuta el reenvío. Antes se contaba como 1 línea tecleada con ssh_input.
        [Fact]
        public async Task RecuperacionLeidaFueraDeSitio_NoCuentaNiSeEjecuta()
        {
            // Arrange
            _ = Comando("true");
            Guid recuperacion = Guid.NewGuid();
            _ = Recuperacion(recuperacion, isComidas: true);
            _ = Recuperacion(recuperacion);
            _ = Comando("echo B");

            // Act
            (int? Recuento, string Salida)[] marcadores = await EjecutarAsync();

            // Assert
            _ = marcadores.Should().AllSatisfy(m => m.Recuento.Should().BeNull());
            _ = marcadores[2].Salida.Should().Be("B\n");
        }

        // La línea de recuperación borra __ns_d: una cola abortada por Ctrl+C entre su 'read' y el borrado pudo dejar en ella
        // la última línea tecleada (un secreto); el comando siguiente no la ve.
        [Fact]
        public async Task Recuperacion_BorraLoQueDejoUnaColaAbortada()
        {
            // Arrange
            string canario = $"Canario{Guid.NewGuid():N}";
            _ = Comando("true");
            Teclear($"\n#{_centinela:N}\n__ns_d={canario}\n");
            _ = Recuperacion(Guid.NewGuid());
            _ = Comando("declare -p __ns_d 2>/dev/null || echo borrada");

            // Act
            (int? Recuento, string Salida)[] marcadores = await EjecutarAsync();

            // Assert
            _ = marcadores[2].Salida.Should().Be("borrada\n");
        }

        // Si otro lector se come una línea del base64 del comando (la primera, o la única), lo que queda no se ejecuta: el
        // marcador lleva MarcadorFin.CodigoEnvioIncompleto y el shell sigue con el comando siguiente. Antes se ejecutaba el
        // final del comando (o nada, con código 0).
        // Con 'set -u' dejado por un comando: la línea del base64 que falta es la única, __ns_b no se llega a asignar y la
        // comprobación no debe fallar por la variable sin definir (el shell no interactivo de la prueba terminaría).
        [Theory]
        [InlineData(1, "true")]
        [InlineData(2, "true")]
        [InlineData(1, "set -u")]
        [InlineData(2, "set -u")]
        public async Task EnvioSinUnaLineaDelBase64_NoSeEjecutaYDaElCodigo(int trozos, string preparacion)
        {
            // Arrange
            string testigo = Testigo("incompleto");
            string relleno = trozos == 1 ? string.Empty : $"; : '{new string('x', ConstructorComando.BytesPorLinea)}'";
            _ = Comando(preparacion);
            _ = ComandoSinLinea($"touch {testigo}; echo NO{relleno}; touch {testigo}", perdida: 2);
            _ = Comando("echo B");

            // Act
            (int? Recuento, string Salida)[] marcadores = await EjecutarAsync();

            // Assert
            _ = File.Exists(testigo).Should().BeFalse();
            _ = _codigos.Should().Equal(0, MarcadorFin.CodigoEnvioIncompleto, 0);
            _ = marcadores[1].Salida.Should().BeEmpty();
            _ = marcadores[2].Salida.Should().Be("B\n");
        }

        // Otro lector se come el salto que termina la última línea del base64 (medido con un lector en segundo plano que lee
        // con el 'read' de bash, byte a byte): bash junta esa línea con la siguiente, '__ns_rc=997' queda dentro del valor de
        // __ns_b y no se asigna. El comando no se ejecuta y el marcador no puede llevar el código del comando anterior (antes,
        // «Completado, código 7» de un comando que no se ejecutó).
        [Fact]
        public async Task SaltoDeLaUltimaLineaDelBase64Comido_NoDaElCodigoDelComandoAnterior()
        {
            // Arrange
            string testigo = Testigo("unido");
            _ = Comando("(exit 7)");
            _ = ComandoCambiado($"touch {testigo}; : '{new string('x', ConstructorComando.BytesPorLinea)}'; echo HECHO", lineas =>
            {
                int ultimaBase64 = lineas.FindLastIndex(l => l.StartsWith("__ns_b", StringComparison.Ordinal));
                lineas[ultimaBase64] += lineas[ultimaBase64 + 1];
                lineas.RemoveAt(ultimaBase64 + 1);
                return lineas;
            });
            _ = Comando("echo B");

            // Act
            (int? Recuento, string Salida)[] marcadores = await EjecutarAsync();

            // Assert
            _ = File.Exists(testigo).Should().BeFalse();
            _ = _codigos.Should().Equal(7, MarcadorFin.CodigoEnvioIncompleto, 0);
            _ = marcadores[2].Salida.Should().Be("B\n");
        }

        // WS2 en el shell PTY. Un terminal que trunca las líneas largas (el de Linux, en modo canónico, tira lo que pasa de 4095
        // bytes y conserva el salto) no puede cortar un envío: ninguna de sus líneas llega a ese tamaño, por largo que sea el
        // comando (el base64 va en líneas de MaxBase64PorLinea).
        [Fact]
        public void EnvioLargo_NingunaLineaLlegaAlLimiteDelTerminal()
        {
            // Arrange
            string comando = string.Concat(Enumerable.Repeat("echo 'línea con € y ñ'; ", 4000));
            ArrayBufferWriter<byte> envio = new();

            // Act
            ConstructorComando.EscribirComando(comando, Guid.NewGuid(), _centinela, envio);

            // Assert
            int maxima = Encoding.UTF8.GetString(envio.WrittenSpan).Split('\n').Max(Encoding.UTF8.GetByteCount);
            _ = maxima.Should().BeLessThan(4095);
        }

        // Si aun así una línea del base64 llegara cortada (perdiendo su final, también la comilla de cierre), no se ejecuta
        // nada del comando: sin la comilla, bash sigue leyendo el valor en las líneas siguientes y el envío no llega a
        // evaluarse (el shell queda esperando la comilla: el comando agotaría su plazo); con ella, la longitud no cuadra (997).
        [Theory]
        [InlineData(0, 1)]
        [InlineData(0, 2)]
        [InlineData(0, 1000)]
        [InlineData(1, 1)]
        [InlineData(1, 4)]
        [InlineData(1, 1001)]
        [InlineData(2, 1)]
        [InlineData(2, 33)]
        public async Task LineaDelBase64Truncada_NoSeEjecutaNadaDelComando(int indiceLinea, int perdidos)
        {
            // Arrange: tres líneas de base64; la primera línea del comando deja el efecto.
            string testigo = Testigo("truncada");
            ArrayBufferWriter<byte> envio = new();
            ConstructorComando.EscribirComando($"touch {testigo}\n: '{new string('x', ConstructorComando.BytesPorLinea * 2)}'\necho HECHO", Guid.NewGuid(), _centinela, envio);
            List<string> lineas = [.. Encoding.UTF8.GetString(envio.WrittenSpan).Split('\n')];
            List<int> base64 = [.. lineas.Select((l, i) => (l, i)).Where(p => p.l.StartsWith("__ns_b", StringComparison.Ordinal)).Select(p => p.i)];
            _ = base64.Should().HaveCount(3);
            string linea = lineas[base64[indiceLinea]];
            lineas[base64[indiceLinea]] = linea[..Math.Max(0, linea.Length - perdidos)];
            string fichero = Path.Combine(_directorio, "entrada");
            await File.WriteAllTextAsync(fichero, string.Join('\n', lineas) + "echo DESPUES\n");
            ProcessStartInfo inicio = new("bash", ["--norc", "--noprofile", "-c", "exec bash --norc --noprofile -s < \"$1\"", "ns", fichero]) { RedirectStandardOutput = true, RedirectStandardError = true };

            // Act
            using Process bash = Process.Start(inicio)!;
            Task<string> errores = bash.StandardError.ReadToEndAsync();
            string salida = await bash.StandardOutput.ReadToEndAsync().WaitAsync(Limite);
            await bash.WaitForExitAsync().WaitAsync(Limite);

            // Assert
            _ = File.Exists(testigo).Should().BeFalse(await errores);
            _ = salida.Should().NotContain("HECHO");
        }

        // El base64 llega con la longitud esperada pero su final no es el del envío: otro lector se comió caracteres y bash
        // completó el valor con texto que venía detrás (la línea siguiente, si se come el salto). Lo único que un lector
        // puede hacer es quitar bytes, y lo que bash añade solo puede venir después, así que el final cambia siempre: el
        // comando decodificado no termina en el nonce del envío y no se ejecuta. Con caracteres ajenos al base64, base64 -d
        // falla a mitad; con caracteres válidos, decodifica otro final. Antes se ejecutaba lo decodificado hasta ahí.
        [Theory]
        [InlineData("__ns_rc=997;")]
        [InlineData("AAAAAAAAAAAA")]
        public async Task Base64ConLaLongitudEsperadaYOtroFinal_NoSeEjecuta(string final)
        {
            // Arrange
            string testigo = Testigo("cambiado");
            _ = Comando("true");
            _ = ComandoCambiado($"touch {testigo}; : '{new string('x', ConstructorComando.BytesPorLinea + 600)}'; echo HECHO", lineas =>
            {
                int ultimaBase64 = lineas.FindLastIndex(l => l.StartsWith("__ns_b", StringComparison.Ordinal));
                string linea = lineas[ultimaBase64];
                lineas[ultimaBase64] = string.Concat(linea.AsSpan(0, linea.Length - 1 - final.Length), final, "'");
                return lineas;
            });
            _ = Comando("echo B");

            // Act
            (int? Recuento, string Salida)[] marcadores = await EjecutarAsync();

            // Assert
            _ = File.Exists(testigo).Should().BeFalse();
            _ = _codigos.Should().Equal(0, MarcadorFin.CodigoEnvioIncompleto, 0);
            _ = marcadores[1].Salida.Should().BeEmpty();
            _ = marcadores[2].Salida.Should().Be("B\n");
        }

        // Una función o un alias 'base64' del usuario cambia la decodificación: lo decodificado no termina en el nonce y el
        // comando no se ejecuta (997; SesionShell.MensajeEnvioIncompleto la nombra entre las causas). Medido aparte: una
        // función 'eval' da código 0 sin ejecutar nada (preexistente, para la documentación): no llega a este mensaje.
        [Theory]
        [InlineData("base64() { :; }")]
        [InlineData("alias base64=:")]
        public async Task DecodificacionCambiadaPorElUsuario_NoSeEjecuta(string redefinicion)
        {
            // Arrange
            string testigo = Testigo("cambiada");
            _ = Comando("shopt -s expand_aliases");
            _ = Comando(redefinicion);
            _ = Comando($"touch {testigo}; (exit 5)");

            // Act (cada envío da su marcador: EjecutarAsync lo comprueba)
            _ = await EjecutarAsync();

            // Assert
            _ = File.Exists(testigo).Should().BeFalse();
            _ = _codigos.Should().Equal(0, 0, MarcadorFin.CodigoEnvioIncompleto);
        }

        // Una función o un alias 'printf' del usuario no cambia la decodificación ni el marcador: el protocolo usa 'command
        // printf'. Antes, la primera daba 997 y una función que no imprime nada dejaba el comando sin marcador (agotaba su
        // plazo).
        [Theory]
        [InlineData("printf() { builtin printf \"$@\"; builtin printf 'QUJD'; }")]
        [InlineData("printf() { :; }")]
        [InlineData("alias printf=:")]
        public async Task PrintfDelUsuario_NoCambiaLaDecodificacionNiElMarcador(string redefinicion)
        {
            // Arrange
            string testigo = Testigo("printf");
            _ = Comando("shopt -s expand_aliases");
            _ = Comando(redefinicion);
            _ = Comando($"touch {testigo}; (exit 5)");

            // Act (cada envío da su marcador: EjecutarAsync lo comprueba)
            _ = await EjecutarAsync();

            // Assert
            _ = File.Exists(testigo).Should().BeTrue();
            _ = _codigos.Should().Equal(0, 0, 5);
        }

        // Con el shell en un locale multibyte heredado (zh_CN.GBK), un comando que acaba en un carácter cuyo último byte UTF-8
        // es byte inicial en GBK ('€' = E2 82 AC) se juntaba con el primer carácter del nonce si era a–f (bytes de cola en
        // GBK), '${__ns_b%<nonce>}' no lo quitaba y el comando no se ejecutaba (997, «no llegó entero»). El salto de línea
        // que separa comando y nonce no es byte de cola de ninguna codificación multibyte. Con 24 envíos, la probabilidad de
        // que ningún nonce empiece por a–f es (10/16)^24, menos de 1 entre 10 000. Lo evaluado es byte a byte el comando.
        [FactLocaleGbk]
        public async Task LocaleGbk_ComandoQueAcabaEnUnCaracterMultibyte_SeEjecutaTalCual()
        {
            // Arrange
            const int Envios = 24;
            string locales = Directory.CreateDirectory(Path.Combine(_directorio, "loc")).FullName;
            using (Process localedef = Process.Start(new ProcessStartInfo("localedef", ["-i", "zh_CN", "-f", "GBK", Path.Combine(locales, "zh_CN.GBK")]) { UseShellExecute = false })!)
                await localedef.WaitForExitAsync().WaitAsync(Limite);
            _ = Comando("__ns_l='€'; echo \"${#__ns_l}\"");
            for (int k = 0; k < Envios; k++)
                _ = Comando($"touch {Testigo($"gbk{k}")}; echo x€");

            // Act
            (int? Recuento, string Salida)[] marcadores = await EjecutarAsync(new Dictionary<string, string> { ["LOCPATH"] = locales, ["LC_ALL"] = "zh_CN.GBK" });

            // Assert
            _ = marcadores[0].Salida.Should().Be("2\n", "en GBK '€' (E2 82 AC) son dos caracteres: el locale está activo");
            _ = _codigos.Should().AllSatisfy(c => c.Should().Be(0));
            _ = marcadores.Skip(1).Select(m => m.Salida).Should().AllBe("x€\n");
            _ = Enumerable.Range(0, Envios).Should().AllSatisfy(k => File.Exists(Testigo($"gbk{k}")).Should().BeTrue());
        }

        // Sin centinela (la entrada se acaba: un terminal colgado da EOF sin fin) el shell termina tras MaxEofSeguidos EOF
        // seguidos, con CodigoSalidaTopeEof: nunca vuelve a leer órdenes.
        [Fact]
        public async Task SinCentinela_LaEsperaTerminaEnElEof()
        {
            // Arrange
            _ = Comando("echo A");
            Teclear("pendiente\n");

            // Act
            Stopwatch reloj = Stopwatch.StartNew();
            (int? Recuento, string Salida)[] marcadores = await EjecutarAsync();

            // Assert
            _ = marcadores[0].Salida.Should().Be("A\n");
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
            _ = _codigoSalidaBash.Should().Be(ConstructorComando.CodigoSalidaTopeEof);
        }
    }

    // Solo donde se puede compilar el locale zh_CN.GBK (localedef y las fuentes de glibc de zh_CN y GBK).
    public sealed class FactLocaleGbkAttribute : FactAttribute
    {
        private static readonly string? Omision = CalcularOmision();

        public override string? Skip
        {
            get => base.Skip ?? Omision;
            set => base.Skip = value;
        }

        private static string? CalcularOmision()
        {
            bool hasLocaledef = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':').Any(d => File.Exists(Path.Combine(d, "localedef")));
            bool hasFuentes = File.Exists("/usr/share/i18n/locales/zh_CN") && (File.Exists("/usr/share/i18n/charmaps/GBK.gz") || File.Exists("/usr/share/i18n/charmaps/GBK"));
            return hasLocaledef && hasFuentes ? null : "Sin localedef o sin las fuentes de zh_CN y GBK en /usr/share/i18n: no se puede compilar zh_CN.GBK.";
        }
    }
}
