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
            foreach (string programa in new[] { "bash", "head", "cat", "find" })
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

        // grep como el de busybox: sin -P, -I ni las opciones largas de GNU (--exclude-dir, --exclude, --include), que
        // rechaza con su ayuda y código 2; el resto lo hace el grep real.
        private void InstalarGrepDeBusybox()
        {
            string envoltorio = Path.Combine(_bin, "grep");
            File.WriteAllText(envoltorio, $$"""
                #!{{Ruta("bash")}}
                anterior=
                for a in "$@"; do
                  case $anterior in -e|-m) anterior=; continue ;; esac
                  case $a in
                    --) break ;;
                    --*|-*[IP]*) printf 'grep: unrecognized option: %s\nBusyBox v1.37.0 multi-call binary.\n\nUsage: grep [-HhnlLoqvsrRiwFE] [-m N] ...\n' "${a#-}" >&2; exit 2 ;;
                  esac
                  anterior=$a
                done
                exec {{Ruta("grep")}} "$@"

                """);
            File.SetUnixFileMode(envoltorio, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        private void CrearArbolConExclusiones()
        {
            foreach (string carpeta in new[] { ".git", "build", "sub dir" })
                _ = Directory.CreateDirectory(Path.Combine(_datos, carpeta));
            File.WriteAllText(Path.Combine(_datos, ".git", "d.py"), "valor 1\n");
            File.WriteAllText(Path.Combine(_datos, "build", "e.js"), "valor 2\n");
            File.WriteAllText(Path.Combine(_datos, "src", "f.min.js"), "valor 3\n");
            File.WriteAllBytes(Path.Combine(_datos, "src", "g.o"), [.. "valor 4\n"u8, 0, 1]);
            File.WriteAllText(Path.Combine(_datos, "sub dir", "h.py"), "otra\nvalor 5\n");
        }

        // Sin grep de GNU (busybox, Bloque S, P2) el grep -E con --exclude-dir fallaba siempre, con la ayuda de busybox, y
        // aun así anunciaba el motor. Ahora recorre con find: las mismas exclusiones (directorios podados, archivos por
        // nombre y binarios por extensión), el filtro de extensión y el motor grep -E, que es el que de verdad buscó.
        [Theory]
        [InlineData(null)]
        [InlineData("py")]
        [InlineData("js")]
        public void SinGrepDeGnu_RecorreConFindYGrepE_ConLasMismasExclusiones(string? extension)
        {
            // Arrange
            InstalarGrepDeBusybox();
            CrearArbolConExclusiones();

            // Act
            (int codigo, string salida) = Ejecutar(HerramientasSsh.ConstruirComandoBusqueda("valor [0-9]+", ".", extension, 50));

            // Assert
            _ = codigo.Should().Be(0);
            string[] lineas = salida.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _ = lineas[0].Should().Be("__NS_MOTOR:grep -E");
            // Con "js" solo queda un archivo (f.min.js excluido, build podado): grep lo recibe solo y aun así da su nombre.
            _ = extension switch
            {
                null => lineas[1..].Should().BeEquivalentTo("./src/a.py:1:valor 123", "./src/b.js:1:valor 456", "./sub dir/h.py:2:valor 5"),
                "py" => lineas[1..].Should().BeEquivalentTo("./src/a.py:1:valor 123", "./sub dir/h.py:2:valor 5"),
                _ => lineas[1..].Should().Equal("./src/b.js:1:valor 456")
            };
        }

        // Sin coincidencias sale con 0 y solo el motor (InterpretarBusqueda lo da como «sin coincidencias»); una regex
        // inválida sale con 2 (su error va a stderr); una ruta que es un archivo se busca con su nombre; y el límite corta.
        [Fact]
        public void SinGrepDeGnu_SinCoincidenciasRegexInvalidaArchivoYLimite()
        {
            // Arrange
            InstalarGrepDeBusybox();
            CrearArbolConExclusiones();

            // Act
            (int codigoNada, string salidaNada) = Ejecutar(HerramientasSsh.ConstruirComandoBusqueda("no_aparece", ".", null, 50));
            (int codigoInvalida, string salidaInvalida) = Ejecutar(HerramientasSsh.ConstruirComandoBusqueda("valor (", ".", null, 50));
            (int codigoArchivo, string salidaArchivo) = Ejecutar(HerramientasSsh.ConstruirComandoBusqueda("valor", "sub dir/h.py", null, 50));
            (int codigoLimite, string salidaLimite) = Ejecutar(HerramientasSsh.ConstruirComandoBusqueda("valor", ".", null, 2));

            // Assert
            _ = (codigoNada, salidaNada).Should().Be((0, "__NS_MOTOR:grep -E\n"));
            _ = (codigoInvalida, salidaInvalida).Should().Be((2, "__NS_MOTOR:grep -E\n"));
            _ = (codigoArchivo, salidaArchivo).Should().Be((0, "__NS_MOTOR:grep -E\nsub dir/h.py:2:valor 5\n"));
            _ = codigoLimite.Should().Be(0);
            _ = salidaLimite.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(4, "el motor y el límite + 1 líneas");
        }

        // La poda es de lo que hay dentro de la ruta pedida: buscar dentro de un directorio excluido (aquí node_modules) o de
        // un enlace simbólico a un directorio (como grep -r con la ruta de la línea de órdenes) encuentra lo que contiene.
        [Theory]
        [InlineData("node_modules", "py", "node_modules/c.py:1:valor 789")]
        [InlineData("enlace", "js", "enlace/b.js:1:valor 456")]
        public void SinGrepDeGnu_RutaExcluidaOEnlaceADirectorio_BuscaDentro(string ruta, string extension, string esperado)
        {
            // Arrange
            InstalarGrepDeBusybox();
            _ = Directory.CreateSymbolicLink(Path.Combine(_datos, "enlace"), Path.Combine(_datos, "src"));

            // Act
            (int codigo, string salida) = Ejecutar(HerramientasSsh.ConstruirComandoBusqueda("valor [0-9]+", ruta, extension, 50));

            // Assert
            _ = codigo.Should().Be(0);
            _ = salida.Should().Be($"__NS_MOTOR:grep -E\n{esperado}\n");
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
