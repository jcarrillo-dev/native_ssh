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
            {
                _ = despues.Should().Be(esperado, respuesta);
            }
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
