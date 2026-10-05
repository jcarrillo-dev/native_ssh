using System.Diagnostics;
using System.Runtime.Versioning;
using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // Ejecuta el script de ssh_search con bash local y un PATH controlado para recorrer los tres motores.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    public sealed class MotoresBusquedaTests : IDisposable
    {
        private readonly string _raiz = Path.Combine(Path.GetTempPath(), $"ns_motores_{Guid.NewGuid():N}");
        private readonly string _datos;
        private readonly string _bin;

        public MotoresBusquedaTests()
        {
            _datos = Directory.CreateDirectory(Path.Combine(_raiz, "datos")).FullName;
            _bin = Directory.CreateDirectory(Path.Combine(_raiz, "bin")).FullName;
            _ = Directory.CreateDirectory(Path.Combine(_datos, "src"));
            _ = Directory.CreateDirectory(Path.Combine(_datos, "node_modules"));
            File.WriteAllText(Path.Combine(_datos, "src", "a.py"), "valor 123\ntexto\n");
            File.WriteAllText(Path.Combine(_datos, "src", "b.js"), "valor 456\n");
            File.WriteAllText(Path.Combine(_datos, "node_modules", "c.py"), "valor 789\n");
            foreach (string programa in new[] { "bash", "head", "cat" })
                _ = File.CreateSymbolicLink(Path.Combine(_bin, programa), Ruta(programa));
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

        private (int Codigo, string Salida) Ejecutar(string script)
        {
            ProcessStartInfo inicio = new("/bin/bash", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
            inicio.Environment["PATH"] = _bin;
            inicio.WorkingDirectory = _datos;
            using Process proceso = Process.Start(inicio)!;
            string salida = proceso.StandardOutput.ReadToEnd();
            _ = proceso.StandardError.ReadToEnd();
            proceso.WaitForExit();
            return (proceso.ExitCode, salida);
        }

        private void InstalarGrep(bool isConPcre)
        {
            string grep = Ruta("grep");
            if (isConPcre)
            {
                _ = File.CreateSymbolicLink(Path.Combine(_bin, "grep"), grep);
                return;
            }

            // grep sin soporte de -P (como el de busybox): rechaza la opción con código 2.
            string envoltorio = Path.Combine(_bin, "grep");
            File.WriteAllText(envoltorio, $"#!{Ruta("bash")}\nfor a in \"$@\"; do case \"$a\" in -P|-qP) echo 'grep: -P no soportado' >&2; exit 2;; esac; done\nexec {grep} \"$@\"\n");
            File.SetUnixFileMode(envoltorio, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        [Fact]
        public void ConRg_EncuentraElPatronPcreYRespetaExclusionesYExtension()
        {
            // Arrange
            if (!File.Exists("/usr/bin/rg") && !File.Exists("/usr/local/bin/rg"))
                return;
            _ = File.CreateSymbolicLink(Path.Combine(_bin, "rg"), Ruta("rg"));
            InstalarGrep(isConPcre: true);

            // Act
            (int codigo, string salida) = Ejecutar(HerramientasSsh.ConstruirComandoBusqueda(@"\d+", ".", "py", 50));

            // Assert
            _ = codigo.Should().Be(0);
            _ = salida.Should().Be("__NS_MOTOR:rg\n./src/a.py:1:valor 123\n");
        }

        [Fact]
        public void SinRg_UsaGrepPYEncuentraElPatronPcre()
        {
            // Arrange
            InstalarGrep(isConPcre: true);

            // Act
            (int codigo, string salida) = Ejecutar(HerramientasSsh.ConstruirComandoBusqueda(@"\d+", ".", null, 50));

            // Assert
            _ = codigo.Should().Be(0);
            string[] lineas = salida.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _ = lineas[0].Should().Be("__NS_MOTOR:grep -P");
            _ = lineas[1..].Should().BeEquivalentTo("./src/a.py:1:valor 123", "./src/b.js:1:valor 456");
        }

        [Fact]
        public void SinRgNiGrepP_UsaGrepEComoUltimoRecurso()
        {
            // Arrange
            InstalarGrep(isConPcre: false);

            // Act
            (int codigoPcre, string salidaPcre) = Ejecutar(HerramientasSsh.ConstruirComandoBusqueda(@"\d+", ".", null, 50));
            (int codigoEre, string salidaEre) = Ejecutar(HerramientasSsh.ConstruirComandoBusqueda("[0-9]+", ".", "js", 50));

            // Assert: en ERE '\d' no es un dígito, así que el patrón PCRE no encuentra nada.
            _ = codigoPcre.Should().Be(1);
            _ = salidaPcre.Should().Be("__NS_MOTOR:grep -E\n");
            _ = codigoEre.Should().Be(0);
            _ = salidaEre.Should().Be("__NS_MOTOR:grep -E\n./src/b.js:1:valor 456\n");
        }
    }
}
