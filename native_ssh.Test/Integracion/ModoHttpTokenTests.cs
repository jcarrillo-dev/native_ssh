using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Test.Utilidades;
using Xunit.Abstractions;

namespace NativeSsh.Test.Integracion
{
    // Modo HTTP con el binario (CI2/CT5): sin token configurado no arranca (ni abre el puerto); con él, solo en loopback
    // con --http, y toda petición sin 'Authorization: Bearer <token>' exacto recibe 401 con un mensaje claro, sin llegar al
    // servidor MCP. Ni el token bueno ni uno incorrecto aparecen en los logs, aunque estén en Trace.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    public sealed class ModoHttpTokenTests(ITestOutputHelper salida) : IAsyncLifetime
    {
        private const string Token = "token-http-de-prueba-7f3a9c";
        private const string TokenIncorrecto = "token-http-incorrecto-5e1b2d";
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_http_{Guid.NewGuid():N}")).FullName;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private Process? _proceso;
        private Task<string>? _errores;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            _http.Dispose();
            if (_proceso is not null)
            {
                if (!_proceso.HasExited)
                    _proceso.Kill(entireProcessTree: true);
                await _proceso.WaitForExitAsync();
                _proceso.Dispose();
            }

            Directory.Delete(_directorio, recursive: true);
        }

        private Process Arrancar(int puerto, Dictionary<string, string> entorno, string modo = "--http")
        {
            ProcessStartInfo inicio = new(ClienteMcpBinario.RutaBinario, [modo, "--puerto", puerto.ToString(CultureInfo.InvariantCulture)])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string variable in new[] { TokenHttp.VariableToken, TokenHttp.VariableFichero, "NATIVE_SSH_PUERTO_HTTP", "NATIVE_SSH_PASSWORD" })
                _ = inicio.Environment.Remove(variable);
            inicio.Environment["NATIVE_SSH_NIVEL_LOG"] = "Trace";
            foreach ((string clave, string valor) in entorno)
                inicio.Environment[clave] = valor;
            _proceso = Process.Start(inicio)!;
            _errores = _proceso.StandardError.ReadToEndAsync();
            return _proceso;
        }

        private static bool IsEscuchando(int puerto)
        {
            try
            {
                using TcpClient cliente = new();
                cliente.Connect(IPAddress.Loopback, puerto);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private static async Task EsperarEscuchaAsync(int puerto) => await EsperasPrueba.EsperarHastaAsync(() => IsEscuchando(puerto), TimeSpan.FromSeconds(30));

        private static HttpRequestMessage Inicializar(int puerto, string? autorizacion)
        {
            HttpRequestMessage peticion = new(HttpMethod.Post, $"http://127.0.0.1:{puerto}/mcp")
            {
                Content = new StringContent(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = "initialize",
                    ["params"] = new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "pruebas", ["version"] = "1" } }
                }.ToJsonString(), Encoding.UTF8, "application/json")
            };
            peticion.Headers.Accept.ParseAdd("application/json");
            peticion.Headers.Accept.ParseAdd("text/event-stream");
            if (autorizacion is not null)
                _ = peticion.Headers.TryAddWithoutValidation("Authorization", autorizacion);
            return peticion;
        }

        [Theory]
        [InlineData("sin token")]
        [InlineData("fichero 0644")]
        [InlineData("token corto")]
        public async Task SinTokenValido_NoArrancaNiAbreElPuertoYDiceQueFalta(string caso)
        {
            // Arrange
            int puerto = PuertosPruebas.Libre();
            string fichero = Path.Combine(_directorio, "token");
            await File.WriteAllTextAsync(fichero, Token + "\n");
            File.SetUnixFileMode(fichero, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            Dictionary<string, string> entorno = caso switch
            {
                "fichero 0644" => new() { [TokenHttp.VariableFichero] = fichero },
                "token corto" => new() { [TokenHttp.VariableToken] = "corto123" },
                _ => []
            };

            // Act
            Process proceso = Arrancar(puerto, entorno);
            bool isTerminado = proceso.WaitForExit(TimeSpan.FromSeconds(20));
            bool isEscuchando = IsEscuchando(puerto);
            if (!isTerminado)
                proceso.Kill(entireProcessTree: true);
            string errores = await _errores!;
            salida.WriteLine(errores);

            // Assert
            _ = isTerminado.Should().BeTrue("sin token válido el modo HTTP no arranca");
            _ = proceso.ExitCode.Should().Be(2);
            _ = errores.Should().Contain(caso switch
            {
                "fichero 0644" => "0644",
                "token corto" => "demasiado corto",
                _ => TokenHttp.VariableToken
            });
            _ = errores.Should().NotContain(Token).And.NotContain("corto123");
            _ = isEscuchando.Should().BeFalse();
        }

        [Theory]
        [InlineData("variable")]
        [InlineData("fichero")]
        public async Task ConToken_SoloLasPeticionesConElTokenExactoLleganAlServidorMcp(string fuente)
        {
            // Arrange
            int puerto = PuertosPruebas.Libre();
            string fichero = Path.Combine(_directorio, "token");
            await File.WriteAllTextAsync(fichero, Token + "\n");
            File.SetUnixFileMode(fichero, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Process proceso = Arrancar(puerto, fuente == "variable" ? new() { [TokenHttp.VariableToken] = Token } : new() { [TokenHttp.VariableFichero] = fichero });
            await EsperarEscuchaAsync(puerto);

            // Act
            using HttpResponseMessage sinCabecera = await _http.SendAsync(Inicializar(puerto, null));
            string textoSinCabecera = await sinCabecera.Content.ReadAsStringAsync();
            using HttpResponseMessage incorrecto = await _http.SendAsync(Inicializar(puerto, $"Bearer {TokenIncorrecto}"));
            string textoIncorrecto = await incorrecto.Content.ReadAsStringAsync();
            using HttpResponseMessage basico = await _http.SendAsync(Inicializar(puerto, $"Basic {Token}"));
            using HttpRequestMessage get = new(HttpMethod.Get, $"http://127.0.0.1:{puerto}/mcp");
            using HttpResponseMessage getSinToken = await _http.SendAsync(get);
            using HttpRequestMessage delete = new(HttpMethod.Delete, $"http://127.0.0.1:{puerto}/mcp");
            using HttpResponseMessage deleteSinToken = await _http.SendAsync(delete);
            using HttpResponseMessage correcto = await _http.SendAsync(Inicializar(puerto, $"Bearer {Token}"));
            string textoCorrecto = await correcto.Content.ReadAsStringAsync();
            proceso.Kill(entireProcessTree: true);
            await proceso.WaitForExitAsync();
            string errores = await _errores!;
            salida.WriteLine($"{textoSinCabecera}\n{textoIncorrecto}\n{textoCorrecto}\nstderr: {errores.Length} caracteres");

            // Assert
            _ = sinCabecera.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            _ = sinCabecera.Headers.WwwAuthenticate.Should().ContainSingle().Which.Scheme.Should().Be("Bearer");
            _ = textoSinCabecera.Should().Contain("falta la cabecera 'Authorization: Bearer <token>'");
            _ = incorrecto.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            _ = textoIncorrecto.Should().Contain("no es el configurado").And.NotContain(TokenIncorrecto);
            _ = basico.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            _ = getSinToken.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            _ = deleteSinToken.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            _ = correcto.StatusCode.Should().Be(HttpStatusCode.OK);
            _ = textoCorrecto.Should().Contain("\"native_ssh\"");
            _ = errores.Should().Contain("dbug:", "los logs están en Trace (y Debug)").And.Contain("Petición HTTP rechazada desde 127.0.0.1: token incorrecto");
            _ = errores.Should().NotContain(Token).And.NotContain(TokenIncorrecto);
        }

        // Una ráfaga de peticiones rechazadas no llena el log: como mucho 10 avisos por minuto (CB2d; antes, uno por petición).
        [Fact]
        public async Task RafagaDePeticionesRechazadas_ElLogTieneTope()
        {
            // Arrange
            int puerto = PuertosPruebas.Libre();
            Process proceso = Arrancar(puerto, new() { [TokenHttp.VariableToken] = Token });
            await EsperarEscuchaAsync(puerto);

            // Act
            for (int i = 0; i < 25; i++)
            {
                using HttpResponseMessage respuesta = await _http.SendAsync(Inicializar(puerto, i % 2 == 0 ? null : $"Bearer {TokenIncorrecto}"));
                _ = respuesta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }

            proceso.Kill(entireProcessTree: true);
            await proceso.WaitForExitAsync();
            string errores = await _errores!;
            int avisos = errores.Split("Petición HTTP rechazada").Length - 1;
            salida.WriteLine($"avisos de rechazo en stderr: {avisos}");

            // Assert
            _ = avisos.Should().Be(10);
            _ = errores.Should().NotContain(TokenIncorrecto);
        }

        // Con --http, solo loopback: el puerto no responde en otra dirección de la máquina.
        [Fact]
        public async Task ConHttp_SoloEscuchaEnLoopback()
        {
            // Arrange
            int puerto = PuertosPruebas.Libre();
            _ = Arrancar(puerto, new() { [TokenHttp.VariableToken] = Token });
            await EsperarEscuchaAsync(puerto);

            // Act
            string escuchas = BashLocal.Ejecutar($"ss -Hltn 'sport = :{puerto}' | awk '{{print $4}}' | LC_ALL=C sort | tr '\\n' ' '");

            // Assert
            _ = escuchas.Trim().Should().Be($"127.0.0.1:{puerto} [::1]:{puerto}");
        }
    }
}
