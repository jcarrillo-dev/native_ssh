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
        private async Task<(int? Recuento, string Salida)[]> EjecutarAsync()
        {
            string fichero = Path.Combine(_directorio, "entrada");
            await File.WriteAllBytesAsync(fichero, _entrada.WrittenMemory.ToArray());
            ProcessStartInfo inicio = new("bash", ["--norc", "--noprofile", "-c", "exec bash --norc --noprofile -s < \"$1\"", "ns", fichero])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
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
}
