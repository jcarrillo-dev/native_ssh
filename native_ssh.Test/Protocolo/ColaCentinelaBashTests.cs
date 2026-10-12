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
            string salida = await bash.StandardOutput.ReadToEndAsync().WaitAsync(Limite);
            await bash.WaitForExitAsync().WaitAsync(Limite);
            _ = (await errores).Should().BeEmpty("la cola no escribe nada en stderr");

            (int?, string)[] resultado = new (int?, string)[_ids.Count];
            int desde = 0;
            for (int i = 0; i < _ids.Count; i++)
            {
                string cabecera = $"__NSX_{_ids[i]:N}_";
                int posicion = salida.IndexOf(cabecera, desde, StringComparison.Ordinal);
                _ = posicion.Should().BeGreaterThanOrEqualTo(0, $"el envío {i} da su marcador; salida: {salida}");
                int fin = salida.IndexOf("__\n", posicion + cabecera.Length, StringComparison.Ordinal);
                string[] partes = salida[(posicion + cabecera.Length)..fin].Split('_');
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

        // Sin centinela (la entrada se acaba: un terminal colgado da EOF sin fin) la espera termina tras MaxEofSeguidos.
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
        }
    }
}
