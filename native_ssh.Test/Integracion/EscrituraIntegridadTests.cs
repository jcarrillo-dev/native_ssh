using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // El contenido que una escritura pasa al script por su tubería ('printf %s <base64> | bash -c ...') puede no llegar
    // entero: una función del shell PTY (aquí, un 'printf' que se traga justo ese contenido) lo desvía. Las escrituras no
    // responden éxito si el archivo no tiene lo pedido: el script comprueba lo decodificado (tamaño y sha256) antes de
    // colocarlo o anexarlo y, si no coincide, sale sin tocar el destino y la herramienta lo dice. Por el binario, con el sshd
    // local.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class EscrituraIntegridadTests : IAsyncLifetime
    {
        private const string Original = "PIN=4711\nobjetivo\n";
        private const string Nuevo = "PIN=4711\nnuevo\n";

        private readonly SshdPropio _sshd = new(string.Empty);
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_integridad_{Guid.NewGuid():N}")).FullName;
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

        private async Task ExecAsync(string comando)
        {
            (bool isError, string texto) = await _cliente!.LlamarAsync("ssh_exec", new JsonObject { ["destino"] = Destino, ["comando"] = comando });
            _ = isError.Should().BeFalse(texto);
        }

        // desvio: la función del shell PTY que se traga el contenido (el base64 de "PIN=4711\n" al principio): un 'printf'
        // o, porque las escrituras llaman a printf con 'command', un 'command' que no deja pasar ese printf.
        [Theory]
        [InlineData("sobrescribir", "printf")]
        [InlineData("crear", "printf")]
        [InlineData("anexar", "printf")]
        [InlineData("reemplazar", "printf")]
        [InlineData("sobrescribir", "command")]
        [InlineData("crear", "command")]
        [InlineData("anexar", "command")]
        [InlineData("reemplazar", "command")]
        public async Task EscrituraConLaTuberiaDesviada_NoDiceExito(string modo, string desvio)
        {
            // Arrange
            string archivo = Path.Combine(_directorio, $"{modo}_{desvio}.txt");
            if (modo != "crear")
                await File.WriteAllTextAsync(archivo, Original);
            (string herramienta, JsonObject argumentos, string? esperado) = modo switch
            {
                "sobrescribir" => ("ssh_write_file", new JsonObject { ["destino"] = Destino, ["ruta"] = archivo, ["contenido"] = Nuevo, ["overwrite"] = true }, Nuevo),
                "crear" => ("ssh_write_file", new JsonObject { ["destino"] = Destino, ["ruta"] = archivo, ["contenido"] = Nuevo }, Nuevo),
                "anexar" => ("ssh_write_file", new JsonObject { ["destino"] = Destino, ["ruta"] = archivo, ["contenido"] = Nuevo, ["append"] = true }, Original + Nuevo),
                _ => ("ssh_replace_file_content", new JsonObject { ["destino"] = Destino, ["ruta"] = archivo, ["targetContent"] = "objetivo", ["replacementContent"] = "x" }, "PIN=4711\nx\n")
            };
            const string Prefijo = "UElOPTQ3MTEK";
            string funcion = desvio == "printf"
                ? $"printf() {{ if [ \"$1\" = \"%s\" ] && [ $# -eq 2 ] && [ \"${{2#{Prefijo}}}\" != \"$2\" ]; then return 0; fi; builtin printf \"$@\"; }}"
                : $"command() {{ if [ \"$1\" = printf ] && [ \"$2\" = \"%s\" ] && [ $# -eq 3 ] && [ \"${{3#{Prefijo}}}\" != \"$3\" ]; then return 0; fi; builtin command \"$@\"; }}";
            await ExecAsync($"{funcion}; true");

            // Act
            (bool isError, string respuesta) = await _cliente!.LlamarAsync(herramienta, argumentos);
            await ExecAsync($"unset -f {desvio}; true");

            // Assert: éxito solo con lo pedido en el archivo; si no, el error lo dice y el archivo está como estaba.
            string? despues = File.Exists(archivo) ? await File.ReadAllTextAsync(archivo) : null;
            if (isError)
            {
                _ = respuesta.Should().Contain("no llegó íntegro").And.Contain($"'{archivo}'");
                _ = despues.Should().Be(modo == "crear" ? null : Original, respuesta);
            }
            else
                _ = despues.Should().Be(esperado, respuesta);
        }

        // Una función exportada con el nombre de una orden interna especial ('set'): el script arranca bash en modo POSIX para
        // quitar las funciones heredadas, y bash no arranca con ella (código 2): la escritura, el anexado y el reemplazo fallan
        // sin tocar el archivo, con un mensaje propio que lo dice (no el texto de bash) y sin el contenido.
        [Theory]
        [InlineData("sobrescribir")]
        [InlineData("anexar")]
        [InlineData("reemplazar")]
        public async Task FuncionHeredadaConNombreDeOrdenEspecial_FallaSinTocarElArchivo(string modo)
        {
            // Arrange
            string archivo = Path.Combine(_directorio, $"especial_{modo}.txt");
            await File.WriteAllTextAsync(archivo, Original);
            await ExecAsync("set() { builtin set \"$@\"; }; export -f set; true");
            JsonObject argumentos = modo == "reemplazar"
                ? new JsonObject { ["destino"] = Destino, ["ruta"] = archivo, ["targetContent"] = "objetivo", ["replacementContent"] = "x" }
                : new JsonObject { ["destino"] = Destino, ["ruta"] = archivo, ["contenido"] = Nuevo, ["overwrite"] = modo == "sobrescribir", ["append"] = modo == "anexar" };

            // Act
            (bool isError, string respuesta) = await _cliente!.LlamarAsync(modo == "reemplazar" ? "ssh_replace_file_content" : "ssh_write_file", argumentos);
            await ExecAsync("unset -f set; export -n set; true");

            // Assert
            _ = isError.Should().BeTrue(respuesta);
            _ = respuesta.Should().Contain("bash no llegó a ejecutar el script").And.Contain($"'{archivo}'").And.NotContain("4711").And.NotContain("orden interna especial: ");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be(Original);
        }

        // Sin 'wc' en el servidor no se puede comprobar el tamaño de lo que llegó: la escritura falla sin tocar nada y el error
        // lo dice, sin culpar al shell de alterar el contenido.
        [Fact]
        public async Task SinWc_NoCulpaAlShell()
        {
            // Arrange
            string sinWc = Directory.CreateDirectory(Path.Combine(_directorio, "sinwc")).FullName;
            foreach (string programa in Directory.GetFiles("/usr/bin"))
                if (Path.GetFileName(programa) != "wc")
                    _ = File.CreateSymbolicLink(Path.Combine(sinWc, Path.GetFileName(programa)), programa);
            string archivo = Path.Combine(_directorio, "sinwc.txt");
            await File.WriteAllTextAsync(archivo, Original);
            await ExecAsync($"export PATH={sinWc}");

            // Act
            (bool isError, string respuesta) = await _cliente!.LlamarAsync("ssh_write_file", new JsonObject { ["destino"] = Destino, ["ruta"] = archivo, ["contenido"] = Nuevo, ["overwrite"] = true });
            _ = await _cliente.LlamarAsync("ssh_reset_shell", new JsonObject { ["destino"] = Destino });

            // Assert
            _ = isError.Should().BeTrue(respuesta);
            _ = respuesta.Should().Contain("no tiene 'wc'").And.Contain($"No se ha modificado '{archivo}'").And.NotContain("alteró");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be(Original);
        }

        // Opciones del shell de la sesión exportadas en SHELLOPTS y BASHOPTS (noclobber, xtrace con un PS4 que ejecuta algo,
        // failglob) no pasan al script: la escritura sale bien y el PS4 no se ejecuta en el script ($0 = ns; en el shell de la
        // sesión sí, que es del usuario).
        [Theory]
        [InlineData("set -o noclobber; export SHELLOPTS", "set +o noclobber; export -n SHELLOPTS")]
        [InlineData("shopt -s failglob nullglob; export BASHOPTS", "shopt -u failglob nullglob; export -n BASHOPTS")]
        [InlineData("export PS4='$([ \"$0\" = ns ] && touch {0})+ '; set -o xtrace; export SHELLOPTS", "set +o xtrace; export -n SHELLOPTS PS4")]
        public async Task OpcionesExportadasDelShell_NoPasanAlScript(string preparar, string deshacer)
        {
            // Arrange
            string archivo = Path.Combine(_directorio, $"op_{Guid.NewGuid():N}.txt");
            string testigo = Path.Combine(_directorio, $"testigo_{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(archivo, Original);
            await ExecAsync(preparar.Replace("{0}", testigo, StringComparison.Ordinal) + "; true");

            // Act
            (bool isError, string respuesta) = await _cliente!.LlamarAsync("ssh_write_file", new JsonObject { ["destino"] = Destino, ["ruta"] = archivo, ["contenido"] = Nuevo, ["overwrite"] = true });
            (bool isErrorReplace, string reemplazo) = await _cliente.LlamarAsync("ssh_replace_file_content", new JsonObject { ["destino"] = Destino, ["ruta"] = archivo, ["targetContent"] = "nuevo", ["replacementContent"] = "otro" });
            await ExecAsync(deshacer + "; true");

            // Assert
            _ = isError.Should().BeFalse(respuesta);
            _ = isErrorReplace.Should().BeFalse(reemplazo);
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("PIN=4711\notro\n");
            _ = File.Exists(testigo).Should().BeFalse("el PS4 heredado no debe ejecutarse en el script");
        }

        // La comprobación de lo decodificado con lo que haya en el servidor: sha256sum o, sin él, cksum; sin ninguno, solo el
        // tamaño, y lo dice (verificacion=tamano). Aquí, en un bash local con un PATH que solo tiene lo que se le da.
        [Theory]
        [InlineData("wc sha256sum cksum", "hola\n", "sha256", 0)]
        [InlineData("wc cksum", "hola\n", "cksum", 0)]
        [InlineData("wc", "hola\n", "tamano", 0)]
        [InlineData("wc sha256sum cksum", "holA\n", "", 1)]
        [InlineData("wc cksum", "holA\n", "", 1)]
        [InlineData("wc", "hola", "", 1)]
        public async Task VerificarContenido_ConLoQueHayEnElServidor(string herramientas, string escrito, string verificacion, int codigo)
        {
            // Arrange
            string bin = Directory.CreateDirectory(Path.Combine(_directorio, $"bin_{Guid.NewGuid():N}")).FullName;
            foreach (string herramienta in herramientas.Split(' '))
                _ = File.CreateSymbolicLink(Path.Combine(bin, herramienta), $"/usr/bin/{herramienta}");
            string archivo = Path.Combine(_directorio, $"v_{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(archivo, escrito);
            byte[] esperado = Encoding.UTF8.GetBytes("hola\n");
            string guion = $"{NativeSsh.Herramientas.HerramientasSsh.DefinicionVerificarContenido}\ntam_esperado={esperado.Length}; sha_esperado={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(esperado))}; cksum_esperado='{NativeSsh.Herramientas.CksumPosix.Calcular(esperado)} {esperado.Length}'\nverificar_contenido \"$1\"; r=$?; printf '%s %s' \"$r\" \"$verificacion\"";
            System.Diagnostics.ProcessStartInfo inicio = new("/usr/bin/bash") { UseShellExecute = false, RedirectStandardOutput = true };
            foreach (string argumento in new[] { "--norc", "--noprofile", "-c", guion, "ns", archivo })
                inicio.ArgumentList.Add(argumento);
            inicio.Environment["PATH"] = bin;

            // Act
            using System.Diagnostics.Process proceso = System.Diagnostics.Process.Start(inicio)!;
            string salida = await proceso.StandardOutput.ReadToEndAsync();
            await proceso.WaitForExitAsync();

            // Assert
            _ = salida.Should().Be(codigo == 0 ? $"0 {verificacion}" : $"{codigo} ");
        }
    }
}
