using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace NativeSsh.Test.Utilidades
{
    // Cliente MCP mínimo por stdio contra el binario (el NativeSsh que se compila junto a las pruebas, o el de
    // NATIVE_SSH_BINARIO, p. ej. el publicado con AOT): initialize y tools/call, con plazo por respuesta. Guarda todo lo que
    // el servidor escribió en stdout (las respuestas) y en stderr (los logs) para buscar en ellos.
    public sealed class ClienteMcpBinario : IAsyncDisposable
    {
        private static readonly TimeSpan Limite = TimeSpan.FromSeconds(90);
        private readonly Process _proceso;
        private readonly Task<string> _errores;
        private readonly StringBuilder _salida = new();
        private int _siguienteId;

        private ClienteMcpBinario(Process proceso)
        {
            _proceso = proceso;
            _errores = proceso.StandardError.ReadToEndAsync();
        }

        public static string RutaBinario => Environment.GetEnvironmentVariable("NATIVE_SSH_BINARIO") is { Length: > 0 } ruta ? ruta : Path.Combine(AppContext.BaseDirectory, "NativeSsh");

        // Todo lo que el servidor escribió en stdout hasta ahora (una línea JSON-RPC por mensaje).
        public string Salida => _salida.ToString();

        // El campo 'instructions' de la respuesta a initialize (null si no vino).
        public string? Instrucciones { get; private set; }

        public static async Task<ClienteMcpBinario> IniciarAsync(IReadOnlyDictionary<string, string> entorno)
        {
            ProcessStartInfo inicio = new(RutaBinario)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            _ = inicio.Environment.Remove("NATIVE_SSH_PASSWORD");
            _ = inicio.Environment.Remove("NATIVE_SSH_PASSPHRASE");
            _ = inicio.Environment.Remove("NATIVE_SSH_PASSPHRASE_FICHERO");

            // Nunca la configuración de ssh real del usuario (nombra servidores reales): la prueba que la quiera pone la suya.
            inicio.Environment["NATIVE_SSH_CONFIG_SSH"] = "none";
            foreach ((string clave, string valor) in entorno)
                inicio.Environment[clave] = valor;
            ClienteMcpBinario cliente = new(Process.Start(inicio)!);
            JsonNode inicializacion = await cliente.EnviarAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "pruebas", ["version"] = "1" }
            });
            if (inicializacion["result"]?["serverInfo"]?["name"]?.GetValue<string>() != "native_ssh")
                throw new InvalidOperationException($"initialize inesperado: {inicializacion.ToJsonString()}");

            cliente.Instrucciones = inicializacion["result"]?["instructions"]?.GetValue<string>();

            await cliente.EscribirAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
            return cliente;
        }

        // Resultado de tools/call: si es error y el texto de sus bloques, unidos con '\n'.
        public async Task<(bool IsError, string Texto)> LlamarAsync(string herramienta, JsonObject argumentos)
        {
            JsonNode respuesta = await EnviarAsync("tools/call", new JsonObject { ["name"] = herramienta, ["arguments"] = argumentos });
            JsonNode resultado = respuesta["result"] ?? throw new InvalidOperationException($"{herramienta}: respuesta sin 'result': {respuesta.ToJsonString()}");
            StringBuilder texto = new();
            foreach (JsonNode? bloque in resultado["content"]!.AsArray())
                _ = texto.Append(texto.Length == 0 ? string.Empty : "\n").Append(bloque?["text"]?.GetValue<string>());
            return (resultado["isError"]?.GetValue<bool>() ?? false, texto.ToString());
        }

        // Resultado de tools/call con sus bloques de contenido por separado: el tipo y el texto de cada uno, en orden.
        public async Task<(bool IsError, IReadOnlyList<(string Tipo, string Texto)> Bloques)> LlamarBloquesAsync(string herramienta, JsonObject argumentos)
        {
            JsonNode respuesta = await EnviarAsync("tools/call", new JsonObject { ["name"] = herramienta, ["arguments"] = argumentos });
            JsonNode resultado = respuesta["result"] ?? throw new InvalidOperationException($"{herramienta}: respuesta sin 'result': {respuesta.ToJsonString()}");
            List<(string Tipo, string Texto)> bloques = [];
            foreach (JsonNode? bloque in resultado["content"]!.AsArray())
                bloques.Add((bloque?["type"]?.GetValue<string>() ?? string.Empty, bloque?["text"]?.GetValue<string>() ?? string.Empty));
            return (resultado["isError"]?.GetValue<bool>() ?? false, bloques);
        }

        // Cierra stdin (el servidor termina, como en pruebas/humo-stdio.sh; si no sale en 10 s, se mata) y devuelve stderr.
        public async Task<string> TerminarAsync()
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

            return await _errores;
        }

        private async Task<JsonNode> EnviarAsync(string metodo, JsonObject parametros)
        {
            int id = ++_siguienteId;
            await EscribirAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = metodo, ["params"] = parametros });
            using CancellationTokenSource plazo = new(Limite);
            while (true)
            {
                string? linea;
                try
                {
                    linea = await _proceso.StandardOutput.ReadLineAsync(plazo.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException($"El binario no respondió a {metodo} en {Limite.TotalSeconds:0} s.");
                }

                if (linea is null)
                    throw new InvalidOperationException($"El binario cerró stdout sin responder a {metodo}.");

                _ = _salida.AppendLine(linea);
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

        public async ValueTask DisposeAsync()
        {
            if (!_proceso.HasExited)
                _ = await TerminarAsync();
            _proceso.Dispose();
        }
    }
}
