using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Las peticiones internas de las herramientas que van por el shell (lectura y escritura de ssh_replace_file_content,
    // escritura de ssh_write_file, validación, export y vuelta a $HOME de ssh_set_cwd) salen en ssh_status con su id (para
    // ssh_cancel), pero ninguna otra herramienta devuelve su salida (ni la propia, en su error, la de la lectura): la lectura
    // de replace es el archivo entero en base64, que el modelo puede no haber leído nunca. ssh_output, ssh_input y ssh_cancel sobre ese id dicen de qué herramienta es y que su
    // resultado lo da esa herramienta. Por el binario, con el sshd local.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class PeticionesInternasTests : IAsyncLifetime
    {
        private const string Contenido = "CLAVE=no-la-he-leido-nunca\nOTRA=1\n";

        private readonly SshdPropio _sshd = new(string.Empty);
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_internas_{Guid.NewGuid():N}")).FullName;
        private ClienteMcpBinario? _cliente;

        public async Task InitializeAsync() => _cliente = await ClienteMcpBinario.IniciarAsync(new Dictionary<string, string>
        {
            ["NATIVE_SSH_DIRECTORIO_SSH"] = _sshd.Sshd.DirectorioEstado,
            ["NATIVE_SSH_IDENTIDADES"] = _sshd.Sshd.RutaClave
        });

        public async Task DisposeAsync()
        {
            if (_cliente is not null)
                await _cliente.DisposeAsync();
            _sshd.Dispose();
            Directory.Delete(_directorio, recursive: true);
        }

        private string Destino => _sshd.Sshd.Destino.Clave;

        // El id de la entrada más reciente de ssh_status cuya descripción empieza por «[herramienta]».
        private async Task<string> IdEnEstadoAsync(string herramienta)
        {
            (_, string estado) = await _cliente!.LlamarAsync("ssh_status", []);
            return JsonDocument.Parse(estado).RootElement.GetProperty("peticiones").EnumerateArray()
                .First(p => p.GetProperty("comando").GetString()!.StartsWith($"[{herramienta}]", StringComparison.Ordinal)).GetProperty("id").GetString()!;
        }

        // ssh_output, ssh_input y ssh_cancel sobre el id: ninguna da la salida (ni el contenido ni su base64); ssh_output y
        // ssh_cancel dicen de qué herramienta es y que su resultado lo da ella (ssh_input, con la petición ya terminada, que no
        // se envió nada).
        private async Task ComprobarSinSalidaAsync(string id, string herramienta)
        {
            (bool isErrorSalida, string salida) = await _cliente!.LlamarAsync("ssh_output", new JsonObject { ["id"] = id });
            (bool isErrorEntrada, string entrada) = await _cliente.LlamarAsync("ssh_input", new JsonObject { ["id"] = id, ["texto"] = "x" });
            (_, string cancelacion) = await _cliente.LlamarAsync("ssh_cancel", new JsonObject { ["id"] = id });

            _ = (isErrorSalida, isErrorEntrada).Should().Be((true, true));
            _ = entrada.Should().Contain("No se envió la entrada");
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Contenido));
            foreach (string respuesta in new[] { salida, entrada, cancelacion })
                _ = respuesta.Should().NotContain("CLAVE").And.NotContain(base64[..12]).And.NotContain("__NS_");
            foreach (string respuesta in new[] { salida, cancelacion })
                _ = respuesta.Should().Contain($"interna de {herramienta}").And.Contain($"Su resultado lo da la respuesta de {herramienta}");
        }

        [Fact]
        public async Task LecturaDeReplace_SuIdSaleEnSshStatusPeroNingunaHerramientaDaSuSalida()
        {
            // Arrange
            string archivo = Path.Combine(_directorio, "secreto.env");
            await File.WriteAllTextAsync(archivo, Contenido);
            (bool isErrorReplace, string error) = await _cliente!.LlamarAsync("ssh_replace_file_content", new JsonObject
            {
                ["destino"] = Destino,
                ["ruta"] = archivo,
                ["targetContent"] = "NO_ESTA",
                ["replacementContent"] = "x"
            });

            // Act
            string id = await IdEnEstadoAsync("ssh_replace_file_content");

            // Assert
            _ = isErrorReplace.Should().BeTrue();
            _ = error.Should().NotContain("CLAVE");
            await ComprobarSinSalidaAsync(id, "ssh_replace_file_content");
        }

        // Un 'printf' exportado como función en el shell persistente que hacía terminar la lectura previa de replace con un
        // código distinto de 0 después de imprimir el base64 (devolviendo 1 en el marcador final, matando el script con
        // SIGTERM en él o sin imprimir ninguno de los dos marcadores): el script se pone a salvo de las funciones heredadas,
        // así que el reemplazo sale bien, y en ningún caso la respuesta lleva el archivo (si la lectura fallara así, su error
        // no lleva nada de su salida: MensajeErrorLecturaReplace).
        [Theory]
        [InlineData("*FIN*) return 1;;")]
        [InlineData("*FIN*) kill -TERM $$;;")]
        [InlineData("*INICIO*|*FIN*) return 1;;")]
        public async Task LecturaDeReplace_CodigoDistintoDeCeroTrasElBase64_NoDevuelveElArchivo(string caso)
        {
            // Arrange
            string archivo = Path.Combine(_directorio, "secreto.env");
            await File.WriteAllTextAsync(archivo, Contenido + "objetivo\n");
            string funcion = caso.StartsWith("*INICIO*", StringComparison.Ordinal)
                ? $"printf() {{ case \"$*\" in {caso} esac; builtin printf \"$@\"; }}"
                : $"printf() {{ builtin printf \"$@\"; local r=$?; case \"$*\" in {caso} esac; return $r; }}";
            (bool isErrorExec, string exec) = await _cliente!.LlamarAsync("ssh_exec", new JsonObject { ["destino"] = Destino, ["comando"] = $"{funcion}; export -f printf; echo listo" });

            // Act
            (bool isError, string error) = await _cliente.LlamarAsync("ssh_replace_file_content", new JsonObject
            {
                ["destino"] = Destino,
                ["ruta"] = archivo,
                ["targetContent"] = "objetivo",
                ["replacementContent"] = "cambiado"
            });
            _ = await _cliente.LlamarAsync("ssh_exec", new JsonObject { ["destino"] = Destino, ["comando"] = "unset -f printf; export -n printf" });

            // Assert
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Contenido + "objetivo\n"));
            _ = isErrorExec.Should().BeFalse(exec);
            _ = error.Should().NotContain(base64[..12]).And.NotContain(base64[^12..]).And.NotContain("__NS_B64_").And.NotContain("CLAVE");
            _ = isError.Should().BeFalse(error);
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be(Contenido + "cambiado\n");
        }

        // Un 'printf' exportado que no imprime los marcadores y falla (dejaba el archivo en base64, sin marcadores, en la
        // salida de la lectura): el script se pone a salvo de las funciones heredadas y el reemplazo sale bien; la respuesta
        // no lleva nada del archivo: ni el base64 (entero, o su última línea, más corta) ni el contenido en claro.
        [Theory]
        [InlineData(1)]
        [InlineData(14)]
        [InlineData(63)]
        [InlineData(77)]
        public async Task LecturaDeReplace_PrintfSinMarcadores_NoDaElArchivo(int bytes)
        {
            // Arrange
            string contenido = "objetivo" + new string('K', bytes)[..Math.Max(0, bytes - 8)];
            contenido = contenido[..bytes];
            string archivo = Path.Combine(_directorio, $"a{bytes}.txt");
            await File.WriteAllTextAsync(archivo, contenido);
            (bool isErrorExec, string exec) = await _cliente!.LlamarAsync("ssh_exec", new JsonObject { ["destino"] = Destino, ["comando"] = "printf() { case \"$1\" in *__NS_B64_*) return 1;; esac; builtin printf \"$@\"; }; export -f printf; echo listo" });

            // Act
            (bool isError, string error) = await _cliente.LlamarAsync("ssh_replace_file_content", new JsonObject { ["destino"] = Destino, ["ruta"] = archivo, ["targetContent"] = "o", ["replacementContent"] = "x", ["allowMultiple"] = true });
            _ = await _cliente.LlamarAsync("ssh_exec", new JsonObject { ["destino"] = Destino, ["comando"] = "unset -f printf; export -n printf" });

            // Assert
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(contenido));
            string ultimaLinea = base64[((base64.Length - 1) / 76 * 76)..];
            _ = isErrorExec.Should().BeFalse(exec);
            _ = error.Should().NotContain(ultimaLinea).And.NotContain(base64);
            if (bytes >= 6)
                _ = error.Should().NotContain(contenido);
            _ = isError.Should().BeFalse(error);
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be(contenido.Replace('o', 'x'));
        }

        // Los errores conocidos de la lectura previa dicen la causa con la ruta que pidió el modelo (native_ssh los construye a
        // partir del código de salida del script, sin texto de la salida): el archivo no existe (también con '__NS_B64_' en el
        // nombre), es un directorio, no es un archivo regular, sin permiso de lectura, sin acceso por un directorio, demasiado
        // grande o con el candado tomado por otro.
        [Theory]
        [InlineData("no_existe", "Error: el archivo no existe: {0}")]
        [InlineData("no__NS_B64_existe.txt", "Error: el archivo no existe: {0}")]
        [InlineData("dir", "Error: la ruta es un directorio: {0}")]
        [InlineData("fifo", "Error: {0} no es un archivo regular (es una FIFO, un socket o un dispositivo)")]
        [InlineData("privado", "Error: sin permiso de lectura: {0} (prueba con sudo=true).")]
        [InlineData("cerrado/f", "Error: sin permiso para acceder a la ruta: {0}")]
        [InlineData("grande", "supera el máximo editable con ssh_replace_file_content (773184 bytes)")]
        [InlineData("ocupado", "Error: hay otra escritura, reemplazo o restauración en curso sobre {0}")]
        public async Task LecturaDeReplace_ErroresConocidos_DicenLaCausaConLaRuta(string nombre, string esperado)
        {
            // Arrange
            string ruta = Path.Combine(_directorio, nombre);
            _ = Directory.CreateDirectory(Path.Combine(_directorio, "dir"));
            _ = Directory.CreateDirectory(Path.Combine(_directorio, "cerrado"));
            await File.WriteAllTextAsync(Path.Combine(_directorio, "cerrado", "f"), "objetivo\n");
            await File.WriteAllTextAsync(Path.Combine(_directorio, "privado"), "objetivo\n");
            await File.WriteAllTextAsync(Path.Combine(_directorio, "ocupado"), "objetivo\n");
            await File.WriteAllTextAsync(Path.Combine(_directorio, "grande"), "objetivo\n" + new string('x', 800_000));
            File.SetUnixFileMode(Path.Combine(_directorio, "privado"), UnixFileMode.None);
            File.SetUnixFileMode(Path.Combine(_directorio, "cerrado"), UnixFileMode.None);
            using Process mkfifo = Process.Start("mkfifo", Path.Combine(_directorio, "fifo"));
            await mkfifo.WaitForExitAsync();
            // .NET toma un flock exclusivo con FileShare.None: el candado compartido de la lectura no se puede tomar.
            using FileStream ocupado = new(Path.Combine(_directorio, "ocupado"), FileMode.Open, FileAccess.Read, FileShare.None);
            try
            {
                // Act
                (bool isError, string error) = await _cliente!.LlamarAsync("ssh_replace_file_content", new JsonObject { ["destino"] = Destino, ["ruta"] = ruta, ["targetContent"] = "objetivo", ["replacementContent"] = "x" });

                // Assert
                _ = isError.Should().BeTrue(error);
                _ = error.Should().Contain(string.Format(CultureInfo.InvariantCulture, esperado, ruta)).And.NotContain("después de empezar a dar el archivo");
            }
            finally
            {
                File.SetUnixFileMode(Path.Combine(_directorio, "cerrado"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        // Una función exportada en el shell persistente (el 'bash -c' del script la heredaría) que hace salir la lectura con
        // uno de sus códigos propios (80, 85, 87) por otra causa: el script se pone a salvo de las funciones heredadas al
        // empezar, así que no hay mensaje con una causa falsa («no existe», «candado», «supera el máximo»).
        [Theory]
        [InlineData(80)]
        [InlineData(85)]
        [InlineData(87)]
        public async Task LecturaDeReplace_CodigoPropioPorOtraCausa_NoDaUnMensajeFalso(int codigo)
        {
            // Arrange
            string archivo = Path.Combine(_directorio, $"c{codigo}.txt");
            await File.WriteAllTextAsync(archivo, Contenido + "objetivo\n");
            (bool isErrorExec, string exec) = await _cliente!.LlamarAsync("ssh_exec", new JsonObject { ["destino"] = Destino, ["comando"] = $"printf() {{ builtin printf \"$@\"; [ \"$2\" = FIN ] && return {codigo}; return 0; }}; export -f printf; true" });

            // Act
            (bool isError, string respuesta) = await _cliente.LlamarAsync("ssh_replace_file_content", new JsonObject { ["destino"] = Destino, ["ruta"] = archivo, ["targetContent"] = "objetivo", ["replacementContent"] = "x" });
            _ = await _cliente.LlamarAsync("ssh_exec", new JsonObject { ["destino"] = Destino, ["comando"] = "unset -f printf; export -n printf; true" });

            // Assert
            _ = isErrorExec.Should().BeFalse(exec);
            _ = respuesta.Should().NotContainAny("no existe", "candado", "supera el máximo").And.NotContain("CLAVE");
            _ = isError.Should().BeFalse(respuesta);
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be(Contenido + "x\n");
        }

        [Fact]
        public async Task EscriturasDeWriteYReplace_NingunaHerramientaDaSuSalida()
        {
            // Arrange
            string archivo = Path.Combine(_directorio, "escrito.env");
            (bool isErrorWrite, string write) = await _cliente!.LlamarAsync("ssh_write_file", new JsonObject { ["destino"] = Destino, ["ruta"] = archivo, ["contenido"] = Contenido });
            string idWrite = await IdEnEstadoAsync("ssh_write_file");
            (bool isErrorReplace, string replace) = await _cliente.LlamarAsync("ssh_replace_file_content", new JsonObject
            {
                ["destino"] = Destino,
                ["ruta"] = archivo,
                ["targetContent"] = "OTRA=1",
                ["replacementContent"] = "OTRA=1"
            });
            string idReplace = await IdEnEstadoAsync("ssh_replace_file_content");

            // Act y Assert
            _ = (isErrorWrite, isErrorReplace).Should().Be((false, false), write + replace);
            await ComprobarSinSalidaAsync(idWrite, "ssh_write_file");
            await ComprobarSinSalidaAsync(idReplace, "ssh_replace_file_content");
        }

        [Fact]
        public async Task PeticionesDeSetCwd_NingunaHerramientaDaSuSalida()
        {
            // Arrange
            (bool isErrorFijar, string fijar) = await _cliente!.LlamarAsync("ssh_set_cwd", new JsonObject { ["destino"] = Destino, ["ruta"] = _directorio });
            string idFijar = await IdEnEstadoAsync("ssh_set_cwd");
            (bool isErrorHome, string home) = await _cliente.LlamarAsync("ssh_set_cwd", new JsonObject { ["destino"] = Destino, ["ruta"] = "" });
            string idHome = await IdEnEstadoAsync("ssh_set_cwd");

            // Act y Assert
            _ = (isErrorFijar, isErrorHome).Should().Be((false, false), fijar + home);
            _ = idHome.Should().NotBe(idFijar);
            await ComprobarSinSalidaAsync(idFijar, "ssh_set_cwd");
            await ComprobarSinSalidaAsync(idHome, "ssh_set_cwd");
        }
    }
}
