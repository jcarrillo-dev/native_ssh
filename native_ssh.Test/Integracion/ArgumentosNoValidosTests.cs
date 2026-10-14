using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using FluentAssertions;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // RC5: por el binario (lo que recibe el cliente), un argumento desconocido, de tipo equivocado u obligatorio que falta se
    // rechaza sin ejecutar nada, con un error que nombra el argumento, lo que llegó y lo que se admite, también dentro de
    // 'ediciones'. Antes el SDK ignoraba en silencio los desconocidos (una edición con startLine/endLine se aplicaba sin
    // rango) y de los demás solo decía «An error occurred invoking '<herramienta>'.».
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class ArgumentosNoValidosTests : IAsyncLifetime
    {
        private readonly SshdPropio _sshd = new(string.Empty);
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_argumentos_{Guid.NewGuid():N}")).FullName;
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

        [Fact]
        public async Task DestinoBooleano_NombraElArgumentoYNoEjecutaNada()
        {
            // Arrange
            string testigo = Path.Combine(_directorio, "no_debe_existir");

            // Act
            (bool isError, string texto) = await _cliente!.LlamarAsync("ssh_exec", new JsonObject { ["destino"] = false, ["comando"] = $"touch {testigo}" });

            // Assert
            _ = isError.Should().BeTrue();
            _ = texto.Should().Contain("'destino' llegó como booleano (false) y debe ser texto").And.Contain("Admitidos: destino (texto, obligatorio), comando (texto, obligatorio)").And.NotContain("An error occurred");
            _ = File.Exists(testigo).Should().BeFalse();
        }

        [Fact]
        public async Task ArgumentoDesconocidoEnElNivelSuperior_SeRechazaConLosAdmitidos()
        {
            // Act
            (bool isError, string texto) = await _cliente!.LlamarAsync("ssh_read_file", new JsonObject { ["destino"] = Destino, ["ruta"] = "/etc/hostname", ["startLine"] = 1 });

            // Assert
            _ = isError.Should().BeTrue();
            _ = texto.Should().Contain("Argumento desconocido: startLine").And.Contain("lineaInicio (entero)").And.Contain("lineaFin (entero)");
        }

        [Fact]
        public async Task EdicionConRangoDeLineas_SeRechazaSinTocarElArchivo()
        {
            // Arrange
            string archivo = Path.Combine(_directorio, "ediciones.txt");
            await File.WriteAllTextAsync(archivo, "uno\ndos\ntres\ndos\n");

            // Act
            (bool isError, string texto) = await _cliente!.LlamarAsync("ssh_replace_file_content", new JsonObject
            {
                ["destino"] = Destino,
                ["ruta"] = archivo,
                ["ediciones"] = new JsonArray(new JsonObject { ["targetContent"] = "tres", ["replacementContent"] = "TRES", ["startLine"] = 3, ["endLine"] = 3 })
            });
            (bool isErrorTipo, string textoTipo) = await _cliente.LlamarAsync("ssh_replace_file_content", new JsonObject
            {
                ["destino"] = Destino,
                ["ruta"] = archivo,
                ["ediciones"] = new JsonArray(new JsonObject { ["targetContent"] = "dos", ["replacementContent"] = "DOS", ["allowMultiple"] = "yes" })
            });

            // Assert
            _ = (isError, isErrorTipo).Should().Be((true, true));
            _ = texto.Should().Contain("campos desconocidos: startLine, endLine").And.Contain("Sin rango de líneas");
            _ = textoTipo.Should().Contain("allowMultiple llegó como texto (\"yes\") y debe ser booleano");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("uno\ndos\ntres\ndos\n");
        }

        [Fact]
        public async Task FaltaUnObligatorio_LoNombra()
        {
            // Act
            (bool isError, string texto) = await _cliente!.LlamarAsync("ssh_exec", new JsonObject { ["destino"] = Destino });

            // Assert
            _ = isError.Should().BeTrue();
            _ = texto.Should().Contain("Falta 'comando', que es obligatorio");
        }

        // Lo que el SDK admitía sigue funcionando: un entero como texto con el número, null en un argumento que lo admite y los
        // nombres de una edición con otras mayúsculas.
        [Fact]
        public async Task LoQueElSdkAdmitia_SigueFuncionando()
        {
            // Arrange
            string archivo = Path.Combine(_directorio, "mayusculas.txt");
            await File.WriteAllTextAsync(archivo, "uno\n");

            // Act
            (bool isErrorTexto, string texto) = await _cliente!.LlamarAsync("ssh_exec", new JsonObject { ["destino"] = Destino, ["comando"] = "echo hola", ["timeoutSegundos"] = "10", ["esperarSegundos"] = 30 });
            (bool isErrorNulo, _) = await _cliente.LlamarAsync("ssh_read_file", new JsonObject { ["destino"] = Destino, ["ruta"] = archivo, ["lineaFin"] = null });
            (bool isErrorMayusculas, _) = await _cliente.LlamarAsync("ssh_replace_file_content", new JsonObject
            {
                ["destino"] = Destino,
                ["ruta"] = archivo,
                ["ediciones"] = new JsonArray(new JsonObject { ["TargetContent"] = "uno", ["ReplacementContent"] = "UNO" })
            });

            // Assert
            _ = (isErrorTexto, isErrorNulo, isErrorMayusculas).Should().Be((false, false, false), texto);
            _ = texto.Should().Contain("hola");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("UNO\n");
        }

        // null en un argumento de texto: el SDK lo enlazaba (como null) en cualquiera, obligatorio u opcional, y lo decidía la
        // herramienta: en los opcionales, su valor por defecto; en los obligatorios, su propio error. El filtro no lo rechaza.
        [Theory]
        [InlineData("ssh_list_dir", "ruta")]
        [InlineData("ssh_find", "ruta")]
        [InlineData("ssh_find", "tipo")]
        [InlineData("ssh_search", "ruta")]
        public async Task NullEnUnTextoOpcional_UsaElValorPorDefecto(string herramienta, string argumento)
        {
            // Arrange
            JsonObject argumentos = herramienta switch
            {
                "ssh_list_dir" => new JsonObject { ["destino"] = Destino },
                "ssh_find" => new JsonObject { ["destino"] = Destino, ["patron"] = "*", ["ruta"] = _directorio },
                _ => new JsonObject { ["destino"] = Destino, ["patron"] = $"no_esta_{Guid.NewGuid():N}" }
            };
            argumentos[argumento] = null;
            // Sin ruta, la del directorio de trabajo: el de la prueba, no $HOME.
            (bool isErrorCwd, string cwd) = await _cliente!.LlamarAsync("ssh_set_cwd", new JsonObject { ["destino"] = Destino, ["ruta"] = _directorio });
            _ = isErrorCwd.Should().BeFalse(cwd);

            // Act
            (bool isError, string texto) = await _cliente.LlamarAsync(herramienta, argumentos);

            // Assert
            _ = texto.Should().NotContain("Argumentos no válidos");
            _ = isError.Should().BeFalse(texto);
        }

        [Fact]
        public async Task NullEnUnTextoObligatorio_LoDecideLaHerramienta()
        {
            // Act
            (bool isErrorDestino, string destino) = await _cliente!.LlamarAsync("ssh_exec", new JsonObject { ["destino"] = null, ["comando"] = "echo hola" });
            (bool isErrorRuta, string ruta) = await _cliente.LlamarAsync("ssh_stat", new JsonObject { ["destino"] = Destino, ["ruta"] = null });
            (bool isErrorId, string id) = await _cliente.LlamarAsync("ssh_output", new JsonObject { ["id"] = null });
            (bool isErrorEntero, string entero) = await _cliente.LlamarAsync("ssh_exec", new JsonObject { ["destino"] = Destino, ["comando"] = "echo hola", ["esperarSegundos"] = null });

            // Assert
            _ = (isErrorDestino, isErrorRuta, isErrorId, isErrorEntero).Should().Be((true, true, true, true));
            _ = destino.Should().Contain("Destino '' no válido").And.NotContain("Argumentos no válidos");
            _ = ruta.Should().Contain("La ruta no puede estar vacía").And.NotContain("Argumentos no válidos");
            _ = id.Should().Contain("'id' llegó como null").And.NotContain("Argumentos no válidos");
            _ = entero.Should().Contain("'esperarSegundos' llegó como null y debe ser un entero");
        }
    }
}
