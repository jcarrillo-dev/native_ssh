using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace NativeSsh.Test.Integracion
{
    // Lo mismo con el binario: el servidor MCP (el NativeSsh que se compila junto a las pruebas, o el de
    // NATIVE_SSH_BINARIO, p. ej. el publicado con AOT) por stdio, con las opciones de serie (limitador de 4 conexiones cada
    // 30 s) y las claves del contenedor por NATIVE_SSH_DIRECTORIO_SSH y NATIVE_SSH_IDENTIDADES, como pruebas/humo-stdio.sh.
    public sealed partial class ContenedorSinBashTests
    {
        // Respuesta de una llamada tools/call: si el servidor la marcó como error, su texto y cuánto tardó.
        private sealed record RespuestaMcp(string Herramienta, bool IsError, string Texto, TimeSpan Duracion);

        // Secuencia de llamadas al binario: por el shell (ssh_exec dos veces y tras ssh_reset_shell, ssh_write_file), por
        // canales exec (las cinco de lectura), ssh_status, ssh_reset_shell y ssh_disconnect (con un ssh_exec después).
        private async Task<(List<RespuestaMcp> Respuestas, RegistroSshd Antes, RegistroSshd AntesDeDesconectar, RegistroSshd Despues)> RecorrerConElBinarioAsync()
        {
            ServidorContenedor servidor = Preparar();
            string archivo = $"/home/{servidor.Usuario}/ns_{Guid.NewGuid():N}.txt";
            _ = servidor.ComoUsuario("printf 'uno\\n' > \"$1\"", archivo);
            List<RespuestaMcp> respuestas = [];
            RegistroSshd antes = await LeerRegistroAsync(servidor);
            RegistroSshd antesDeDesconectar;
            try
            {
                await using ClienteMcpStdio cliente = await ClienteMcpStdio.IniciarAsync(servidor);
                async Task Llamar(string herramienta, JsonObject argumentos, string? etiqueta = null)
                {
                    RespuestaMcp respuesta = await cliente.LlamarAsync(herramienta, argumentos, etiqueta);
                    salida.WriteLine($"{respuesta.Duracion.TotalSeconds:0.00} s {respuesta.Herramienta} (isError={respuesta.IsError}): {respuesta.Texto}");
                    respuestas.Add(respuesta);
                }

                await Llamar("ssh_exec", new JsonObject { ["destino"] = Destino, ["comando"] = "echo primero" });
                await Llamar("ssh_exec", new JsonObject { ["destino"] = Destino, ["comando"] = "echo siguiente" }, "ssh_exec siguiente");
                await Llamar("ssh_status", new JsonObject { ["destino"] = Destino });
                await Llamar("ssh_read_file", new JsonObject { ["destino"] = Destino, ["ruta"] = archivo });
                await Llamar("ssh_list_dir", new JsonObject { ["destino"] = Destino, ["ruta"] = "/tmp" });
                await Llamar("ssh_find", new JsonObject { ["destino"] = Destino, ["patron"] = "*.txt", ["ruta"] = "/tmp" });
                await Llamar("ssh_search", new JsonObject { ["destino"] = Destino, ["patron"] = "uno", ["ruta"] = archivo });
                await Llamar("ssh_stat", new JsonObject { ["destino"] = Destino, ["ruta"] = archivo });
                await Llamar("ssh_write_file", new JsonObject { ["destino"] = Destino, ["ruta"] = archivo, ["contenido"] = "nuevo\n", ["overwrite"] = true });
                await Llamar("ssh_reset_shell", new JsonObject { ["destino"] = Destino });
                await Llamar("ssh_exec", new JsonObject { ["destino"] = Destino, ["comando"] = "echo tras_reinicio" }, "ssh_exec tras ssh_reset_shell");
                antesDeDesconectar = await LeerRegistroAsync(servidor);
                await Llamar("ssh_disconnect", new JsonObject { ["destino"] = Destino });
                await Llamar("ssh_status", new JsonObject { ["destino"] = Destino }, "ssh_status tras ssh_disconnect");
                await Llamar("ssh_exec", new JsonObject { ["destino"] = Destino, ["comando"] = "echo tras_desconectar" }, "ssh_exec tras ssh_disconnect");
            }
            finally
            {
                _ = servidor.RootConCodigo("rm -f -- \"$1\"", archivo);
            }

            return (respuestas, antes, antesDeDesconectar, await LeerRegistroAsync(servidor));
        }

        // Ninguna llamada se cuelga ni deja colgado al servidor MCP; las que no pueden funcionar sin bash son error (las de
        // ssh_exec, un comando en estado Error) y las de estado y sesión funcionan; una conexión TCP hasta ssh_disconnect y
        // otra después, un canal por llamada, y el limitador nunca interviene.
        [FactContenedor]
        public async Task SinBash_Binario_NingunaLlamadaSeCuelgaConUnaConexionYUnCanalPorLlamada()
        {
            // Arrange y Act
            (List<RespuestaMcp> respuestas, RegistroSshd antes, RegistroSshd antesDeDesconectar, RegistroSshd despues) = await RecorrerConElBinarioAsync();

            // Assert
            _ = respuestas.Should().OnlyContain(r => r.Duracion < PlazoLlamada);
            _ = respuestas.Should().OnlyContain(r => !r.Texto.Contains("retenida localmente", StringComparison.Ordinal));
            Dictionary<string, RespuestaMcp> porNombre = respuestas.ToDictionary(r => r.Herramienta);
            foreach (string herramienta in new[] { "ssh_exec", "ssh_exec siguiente", "ssh_exec tras ssh_reset_shell", "ssh_exec tras ssh_disconnect" })
            {
                _ = porNombre[herramienta].IsError.Should().BeFalse();
                _ = JsonNode.Parse(porNombre[herramienta].Texto)!["estado"]!.GetValue<string>().Should().Be("Error");
            }

            foreach (string herramienta in new[] { "ssh_read_file", "ssh_list_dir", "ssh_find", "ssh_search", "ssh_stat", "ssh_write_file" })
                _ = porNombre[herramienta].IsError.Should().BeTrue(herramienta);

            JsonNode estado = JsonNode.Parse(porNombre["ssh_status"].Texto)!;
            _ = estado["sesiones"]!.AsArray().Should().ContainSingle();
            _ = estado["sesiones"]![0]!["isConectada"]!.GetValue<bool>().Should().BeTrue();
            _ = estado["sesiones"]![0]!["isShellActivo"]!.GetValue<bool>().Should().BeFalse();
            _ = estado["sesiones"]![0]!["conexionesAbiertas"]!.GetValue<int>().Should().Be(1);
            _ = porNombre["ssh_reset_shell"].IsError.Should().BeFalse();
            _ = porNombre["ssh_disconnect"].IsError.Should().BeFalse();
            _ = JsonNode.Parse(porNombre["ssh_disconnect"].Texto)!["isHecho"]!.GetValue<bool>().Should().BeTrue();
            _ = JsonNode.Parse(porNombre["ssh_status tras ssh_disconnect"].Texto)!["sesiones"]!.AsArray().Should().BeEmpty();

            // Hasta ssh_disconnect: una conexión, cuatro canales de shell (tres ssh_exec y ssh_write_file) y cinco exec.
            _ = (antesDeDesconectar - antes).Should().Be(new RegistroSshd(Conexiones: 1, Shells: 4, Exec: 5));
            _ = (despues - antesDeDesconectar).Should().Be(new RegistroSshd(Conexiones: 1, Shells: 1, Exec: 0));
        }

        // Lo mismo que SinBash_ElErrorDiceQueFaltaBash…, visto por el cliente a través del binario (era un fallo conocido).
        [FactContenedor]
        public async Task SinBash_Binario_ElErrorDiceQueFaltaBashYQueNativeSshLoNecesita()
        {
            // Arrange y Act
            (List<RespuestaMcp> respuestas, _, _, _) = await RecorrerConElBinarioAsync();

            // Assert
            List<string> mensajes = [];
            foreach (RespuestaMcp respuesta in respuestas)
                if (respuesta.Herramienta.StartsWith("ssh_exec", StringComparison.Ordinal))
                    mensajes.Add($"{respuesta.Herramienta}: {JsonNode.Parse(respuesta.Texto)!["aviso"]}");
                else if (respuesta.IsError)
                    mensajes.Add($"{respuesta.Herramienta}: {respuesta.Texto}");

            _ = mensajes.Should().HaveCount(10);
            _ = mensajes.Should().OnlyContain(m => MensajeSinBash().IsMatch(m), "el LLM debe saber que el servidor no tiene bash y que native_ssh lo necesita");
        }

        // Cliente MCP mínimo por stdio (JSON-RPC, un mensaje por línea): initialize y tools/call, cada respuesta con plazo.
        private sealed class ClienteMcpStdio : IAsyncDisposable
        {
            private readonly Process _proceso;
            private readonly Task<string> _errores;
            private int _siguienteId;

            private ClienteMcpStdio(Process proceso)
            {
                _proceso = proceso;
                _errores = proceso.StandardError.ReadToEndAsync();
            }

            public static async Task<ClienteMcpStdio> IniciarAsync(ServidorContenedor servidor)
            {
                string binario = Environment.GetEnvironmentVariable("NATIVE_SSH_BINARIO") is { Length: > 0 } ruta ? ruta : Path.Combine(AppContext.BaseDirectory, "NativeSsh");
                ProcessStartInfo inicio = new(binario)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                inicio.Environment["NATIVE_SSH_DIRECTORIO_SSH"] = servidor.DirectorioEstado;
                inicio.Environment["NATIVE_SSH_IDENTIDADES"] = servidor.RutaClave;
                _ = inicio.Environment.Remove("NATIVE_SSH_PASSWORD");
                ClienteMcpStdio cliente = new(Process.Start(inicio)!);
                JsonNode inicializacion = await cliente.EnviarAsync("initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "pruebas-sinbash", ["version"] = "1" }
                });
                _ = inicializacion["result"]!["serverInfo"]!["name"]!.GetValue<string>().Should().Be("native_ssh");
                await cliente.EscribirAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
                return cliente;
            }

            public async Task<RespuestaMcp> LlamarAsync(string herramienta, JsonObject argumentos, string? etiqueta)
            {
                Stopwatch reloj = Stopwatch.StartNew();
                JsonNode respuesta = await EnviarAsync("tools/call", new JsonObject { ["name"] = herramienta, ["arguments"] = argumentos });
                JsonNode resultado = respuesta["result"] ?? throw new InvalidOperationException($"{herramienta}: respuesta sin 'result': {respuesta.ToJsonString()}");
                return new RespuestaMcp(etiqueta ?? herramienta, resultado["isError"]?.GetValue<bool>() ?? false, resultado["content"]![0]!["text"]!.GetValue<string>(), reloj.Elapsed);
            }

            private async Task<JsonNode> EnviarAsync(string metodo, JsonObject parametros)
            {
                int id = ++_siguienteId;
                await EscribirAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = metodo, ["params"] = parametros });
                using CancellationTokenSource plazo = new(LimiteSeguridad);
                while (true)
                {
                    string? linea;
                    try
                    {
                        linea = await _proceso.StandardOutput.ReadLineAsync(plazo.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new TimeoutException($"El binario no respondió a {metodo} en {LimiteSeguridad.TotalSeconds:0} s.");
                    }

                    if (linea is null)
                        throw new InvalidOperationException($"El binario cerró stdout sin responder a {metodo}.");

                    JsonNode mensaje = JsonNode.Parse(linea)!;
                    if (mensaje["id"]?.GetValue<int>() == id)
                        return mensaje;
                }
            }

            private async Task EscribirAsync(JsonObject mensaje)
            {
                await _proceso.StandardInput.WriteLineAsync(mensaje.ToJsonString());
                await _proceso.StandardInput.FlushAsync();
            }

            // Cerrar stdin termina el servidor MCP (como en humo-stdio.sh); si no sale en 10 s, se mata.
            public async ValueTask DisposeAsync()
            {
                _proceso.StandardInput.Close();
                using CancellationTokenSource plazo = new(TimeSpan.FromSeconds(10));
                try
                {
                    await _proceso.WaitForExitAsync(plazo.Token);
                }
                catch (OperationCanceledException)
                {
                    _proceso.Kill(entireProcessTree: true);
                    await _proceso.WaitForExitAsync();
                }

                _ = await _errores;
                _proceso.Dispose();
            }
        }
    }
}
