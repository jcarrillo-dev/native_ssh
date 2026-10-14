using System.Diagnostics.CodeAnalysis;
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

        // La lectura previa de replace termina con un código distinto de 0 después de imprimir el base64 (o parte): el error
        // dice el código y, como mucho, lo que el script escribió antes del marcador de inicio; nunca el archivo. Un 'printf'
        // exportado como función en el shell persistente (el 'bash -c' del script la importa) provoca el fallo: devolviendo 1
        // en el marcador final, matando el script con SIGTERM en él o, además, sin imprimir ninguno de los dos marcadores.
        [Theory]
        [InlineData("*FIN*) return 1;;", "código 1")]
        [InlineData("*FIN*) kill -TERM $$;;", "código 143")]
        [InlineData("*INICIO*|*FIN*) return 1;;", "código 1")]
        public async Task LecturaDeReplace_CodigoDistintoDeCeroTrasElBase64_NoDevuelveElArchivo(string caso, string codigo)
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
            _ = isError.Should().BeTrue(error);
            _ = error.Should().Contain(codigo).And.NotContain(base64[..12]).And.NotContain(base64[^12..]).And.NotContain("__NS_B64_").And.NotContain("CLAVE");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be(Contenido + "objetivo\n");
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
            (bool isErrorHome, string home) = await _cliente.LlamarAsync("ssh_set_cwd", new JsonObject { ["destino"] = Destino });
            string idHome = await IdEnEstadoAsync("ssh_set_cwd");

            // Act y Assert
            _ = (isErrorFijar, isErrorHome).Should().Be((false, false), fijar + home);
            _ = idHome.Should().NotBe(idFijar);
            await ComprobarSinSalidaAsync(idFijar, "ssh_set_cwd");
            await ComprobarSinSalidaAsync(idHome, "ssh_set_cwd");
        }
    }
}
