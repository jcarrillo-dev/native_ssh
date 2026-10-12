using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Utilidades
{
    // Las pruebas leen la respuesta de ssh_exec, ssh_output, ssh_input y ssh_cancel tal como la recibe el cliente MCP (dos
    // bloques de texto, HerramientasSsh.DosBloques) y la vuelven a juntar en un ResultadoComando. Cada lectura comprueba el
    // contrato: exactamente dos bloques de texto, sin error, el primero un JSON de control en una sola línea y sin la salida,
    // el segundo la salida tal cual.
    public static class ResultadoDosBloques
    {
        private static JsonTypeInfo<ControlComando> TipoControl => (JsonTypeInfo<ControlComando>)ContextoJsonNativeSsh.OpcionesHerramientas.GetTypeInfo(typeof(ControlComando));

        public static async Task<ResultadoComando> ComoComando(this Task<CallToolResult> respuesta) => Decodificar(await respuesta.ConfigureAwait(false));

        public static ResultadoComando Decodificar(CallToolResult respuesta)
        {
            ArgumentNullException.ThrowIfNull(respuesta);
            if (respuesta.IsError == true)
                throw new InvalidOperationException("La respuesta viene marcada como error.");
            if (respuesta.Content is not [TextContentBlock control, TextContentBlock salida])
                throw new InvalidOperationException($"Se esperaban dos bloques de texto (control y salida) y hay {respuesta.Content.Count}.");
            if (control.Text.Contains('\n', StringComparison.Ordinal) || !control.Text.StartsWith('{'))
                throw new InvalidOperationException($"El bloque de control no es un JSON en una sola línea: {control.Text}");

            using (JsonDocument documento = JsonDocument.Parse(control.Text))
                if (documento.RootElement.TryGetProperty("salida", out _))
                    throw new InvalidOperationException("El bloque de control lleva la salida.");

            ControlComando c = JsonSerializer.Deserialize(control.Text, TipoControl) ?? throw new InvalidOperationException("Bloque de control vacío.");
            return new ResultadoComando(c.Id, c.Destino, c.Estado, c.CodigoSalida, salida.Text, c.DesdeByte, c.SiguienteByte, c.HasMasSalida, c.HasSalidaDescartada, c.SegundosEjecucion, c.SegundosSinSalida, c.Aviso);
        }
    }
}
