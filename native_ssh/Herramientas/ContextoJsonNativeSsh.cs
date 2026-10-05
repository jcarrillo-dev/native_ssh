using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using NativeSsh.Comun;

namespace NativeSsh.Herramientas
{
    // Metadatos JSON generados en compilación: Native AOT no puede usar reflexión para los resultados.
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UseStringEnumConverter = true)]
    [JsonSerializable(typeof(ResultadoComando))]
    [JsonSerializable(typeof(ResultadoEstado))]
    [JsonSerializable(typeof(ResultadoAccion))]
    [JsonSerializable(typeof(ResultadoMetadatos))]
    public sealed partial class ContextoJsonNativeSsh : JsonSerializerContext
    {
        // Opciones del SDK de MCP (tipos del protocolo y primitivos) más los contratos propios.
        // Perezosa: un inicializador estático se ejecutaría antes que 'Default' (generado) y lo vería nulo.
        public static JsonSerializerOptions OpcionesHerramientas => field ??= CrearOpciones();

        private static JsonSerializerOptions CrearOpciones()
        {
            JsonSerializerOptions opciones = new(McpJsonUtilities.DefaultOptions)
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            bool hasConverter = false;
            foreach (JsonConverter c in opciones.Converters)
            {
                if (c is JsonStringEnumConverter<EstadoPeticionEnum>)
                {
                    hasConverter = true;
                    break;
                }
            }
            if (!hasConverter)
                opciones.Converters.Add(new JsonStringEnumConverter<EstadoPeticionEnum>());
            opciones.TypeInfoResolverChain.Insert(0, Default);
            opciones.MakeReadOnly();
            return opciones;
        }
    }
}
