using System.Diagnostics;
using System.Runtime.Versioning;
using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // Ejecuta el script de ssh_read_file con bash local y un PATH controlado: con solo las órdenes que necesita y un 'sed'
    // que se comporta como el de busybox (añade un salto final a una última línea que no lo tiene), el contenido llega tal
    // cual (Bloque S, P3: con 'sed -n A,Bp' el de busybox inventaba ese salto).
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class LecturaRangoScriptTests : IDisposable
    {
        private readonly string _raiz = Path.Combine(Path.GetTempPath(), $"ns_lectura_{Guid.NewGuid():N}");
        private readonly string _bin;
        private readonly string _archivo;

        public LecturaRangoScriptTests()
        {
            _bin = Directory.CreateDirectory(Path.Combine(_raiz, "bin")).FullName;
            _archivo = Path.Combine(_raiz, "sin salto.txt");
            File.WriteAllText(_archivo, "uno\ndos\r\ntres sin salto");
            foreach (string programa in new[] { "bash", "head", "tail", "wc", "dirname", "readlink" })
                _ = File.CreateSymbolicLink(Path.Combine(_bin, programa), Ruta(programa));
            // Como el sed de busybox: la última línea sale siempre con salto.
            Crear("sed", $"salida=$({Ruta("sed")} \"$@\"; codigo=$?; printf x; exit $codigo)\ncodigo=$?\nsalida=${{salida%x}}\nprintf '%s' \"$salida\"\ncase $salida in ''|*$'\\n') ;; *) printf '\\n' ;; esac\nexit $codigo");
        }

        public void Dispose()
        {
            if (Directory.Exists(_raiz))
                Directory.Delete(_raiz, recursive: true);
        }

        private static string Ruta(string programa)
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(':'))
            {
                string candidato = Path.Combine(dir, programa);
                if (File.Exists(candidato))
                    return candidato;
            }

            throw new InvalidOperationException($"{programa} no está en el PATH.");
        }

        private void Crear(string nombre, string cuerpo)
        {
            string ruta = Path.Combine(_bin, nombre);
            if (File.Exists(ruta))
                File.Delete(ruta);
            File.WriteAllText(ruta, $"#!{Ruta("bash")}\n{cuerpo}\n");
            File.SetUnixFileMode(ruta, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        private (int Codigo, string Salida) Ejecutar(string script)
        {
            ProcessStartInfo inicio = new("/bin/bash", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
            inicio.Environment["PATH"] = _bin;
            using Process proceso = Process.Start(inicio)!;
            string salida = proceso.StandardOutput.ReadToEnd();
            _ = proceso.StandardError.ReadToEnd();
            proceso.WaitForExit();
            return (proceso.ExitCode, salida);
        }

        [Theory]
        [InlineData(1, 800, "[Líneas 1-3 de 3]\nuno\ndos\r\ntres sin salto")]
        [InlineData(2, 3, "[Líneas 2-3 de 3]\ndos\r\ntres sin salto")]
        [InlineData(3, 3, "[Líneas 3-3 de 3]\ntres sin salto")]
        [InlineData(1, 2, "[Líneas 1-2 de 3]\nuno\ndos\r\n")]
        [InlineData(5, 9, "[Líneas 5-3 de 3]\n")]
        [InlineData(-1, 0, "[Líneas 3-3 de 3]\ntres sin salto")]
        [InlineData(-9, 0, "[Líneas 1-3 de 3]\nuno\ndos\r\ntres sin salto")]
        public void LeerRango_ConSedDeBusybox_DevuelveLosBytesTalCual(int inicio, int fin, string esperado)
        {
            // Act
            (int codigo, string salida) = Ejecutar(HerramientasSsh.ConstruirComandoLectura(_archivo, inicio, fin));

            // Assert
            _ = codigo.Should().Be(0);
            _ = salida.Should().Be(esperado);
        }

        // Si 'head' falla (p. ej. un error de lectura), el código de 'tail' no lo tapa.
        [Fact]
        public void LeerRango_FalloDeHead_NoLoTapaElCodigoDeTail()
        {
            // Arrange
            Crear("head", "printf 'head: error de lectura\\n' >&2\nexit 1");

            // Act
            (int codigo, _) = Ejecutar(HerramientasSsh.ConstruirComandoLectura(_archivo, 1, 800));

            // Assert
            _ = codigo.Should().NotBe(0);
        }
    }
}
