using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace NativeSsh.Herramientas
{
    // Validación de los argumentos de cada llamada contra el esquema de su herramienta (RC5), antes de que el SDK de MCP los
    // enlace. Medido con el SDK 2.2.0 (bloque ST+RC): un argumento desconocido se ignora en silencio, en el nivel superior
    // (ssh_read_file con startLine leía sin rango) y dentro de 'ediciones' (una edición con startLine/endLine se aplicaba sin
    // rango, donde estuviera su targetContent); un argumento de tipo equivocado o un obligatorio que falta solo daban «An
    // error occurred invoking '<herramienta>'.», sin decir cuál. Aquí se rechazan sin ejecutar nada, con un error que nombra
    // el argumento, lo que llegó y lo que se admite.
    // Se admite lo mismo que el SDK: en el nivel superior los nombres distinguen mayúsculas (medido: 'Destino' no es
    // 'destino'); dentro de un objeto de una lista (cada edición) no (medido: 'TargetContent' se enlazaba); un entero puede
    // llegar como texto con el número ('10', medido), pero no con decimales (10.0 fallaba); null, en un texto, una lista o un
    // objeto (obligatorio u opcional) y donde el esquema lo admite, no en un entero ni en un booleano (ver IsDelTipo). Del
    // esquema se miran 'type', 'properties', 'required' e 'items', que es lo que genera el SDK para estas herramientas.
    internal static class ValidadorArgumentos
    {
        private const int MaxCaracteresValor = 40;

        public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filtro(McpRequestHandler<CallToolRequestParams, CallToolResult> siguiente) =>
            (contexto, token) => contexto.MatchedPrimitive is McpServerTool herramienta && Validar(herramienta.ProtocolTool.Name, herramienta.ProtocolTool.InputSchema, contexto.Params?.Arguments) is { } error
                ? ValueTask.FromResult(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = error }] })
                : siguiente(contexto, token);

        // null si los argumentos valen; si no, el error para el modelo.
        internal static string? Validar(string herramienta, JsonElement esquema, IDictionary<string, JsonElement>? argumentos)
        {
            if (esquema.ValueKind != JsonValueKind.Object || !esquema.TryGetProperty("properties", out JsonElement propiedades))
                return null;

            List<string> problemas = [];
            List<string> desconocidos = [];
            if (argumentos is not null)
                foreach ((string nombre, JsonElement valor) in argumentos)
                    if (!propiedades.TryGetProperty(nombre, out JsonElement propiedad))
                        desconocidos.Add($"{nombre} ({Tipo(valor)})");
                    else
                        ValidarValor($"'{nombre}'", propiedad, valor, problemas);

            foreach (string obligatorio in Obligatorios(esquema))
                if (argumentos is null || !argumentos.ContainsKey(obligatorio))
                    problemas.Add($"falta '{obligatorio}', que es obligatorio");

            if (desconocidos.Count == 0 && problemas.Count == 0)
                return null;

            StringBuilder texto = new($"Argumentos no válidos para {herramienta}: no se ha ejecutado nada. ");
            if (desconocidos.Count > 0)
                _ = texto.Append(desconocidos.Count == 1 ? "Argumento desconocido: " : "Argumentos desconocidos: ").AppendJoin(", ", desconocidos).Append(". ");
            foreach (string problema in problemas)
                _ = texto.Append(char.ToUpperInvariant(problema[0])).Append(problema.AsSpan(1)).Append(". ");
            _ = texto.Append("Admitidos: ");
            DescribirAdmitidos(texto, esquema, propiedades);
            return texto.Append('.').ToString();
        }

        private static void ValidarValor(string ruta, JsonElement esquema, JsonElement valor, List<string> problemas)
        {
            List<string> tipos = Tipos(esquema);
            if (tipos.Count > 0 && !tipos.Exists(t => IsDelTipo(valor, t)))
            {
                problemas.Add($"{ruta} llegó como {DescribirLlegado(valor)} y debe ser {DescribirTipos(tipos)}");
                return;
            }

            if (valor.ValueKind == JsonValueKind.Array && esquema.TryGetProperty("items", out JsonElement elementos))
            {
                int i = 0;
                foreach (JsonElement elemento in valor.EnumerateArray())
                    ValidarValor($"{ruta}[{++i}]", elementos, elemento, problemas);
                return;
            }

            if (valor.ValueKind == JsonValueKind.Object && esquema.TryGetProperty("properties", out JsonElement propiedades))
                ValidarObjeto(ruta, esquema, propiedades, valor, problemas);
        }

        // Un objeto dentro de una lista (cada edición de ssh_replace_file_content): sus nombres no distinguen mayúsculas, como
        // en el SDK. Con campos desconocidos, el error dice cuáles y los que lleva cada elemento.
        private static void ValidarObjeto(string ruta, JsonElement esquema, JsonElement propiedades, JsonElement valor, List<string> problemas)
        {
            List<string> desconocidos = [];
            List<string> presentes = [];
            foreach (JsonProperty campo in valor.EnumerateObject())
            {
                if (BuscarPropiedad(propiedades, campo.Name) is not { } propiedad)
                {
                    desconocidos.Add(campo.Name);
                    continue;
                }

                presentes.Add(propiedad.Name);
                ValidarValor($"{ruta}.{propiedad.Name}", propiedad.Value, campo.Value, problemas);
            }

            if (desconocidos.Count > 0)
            {
                StringBuilder admitidos = new();
                DescribirAdmitidos(admitidos, esquema, propiedades);
                string extra = esquema.TryGetProperty("description", out JsonElement descripcion) && descripcion.ValueKind == JsonValueKind.String ? $" ({descripcion.GetString()})" : "";
                problemas.Add($"{ruta}: {(desconocidos.Count == 1 ? "campo desconocido" : "campos desconocidos")}: {string.Join(", ", desconocidos)}; cada elemento lleva {admitidos}{extra}");
            }

            foreach (string obligatorio in Obligatorios(esquema))
                if (!presentes.Contains(obligatorio))
                    problemas.Add($"{ruta}: falta '{obligatorio}', que es obligatorio");
        }

        private static JsonProperty? BuscarPropiedad(JsonElement propiedades, string nombre)
        {
            foreach (JsonProperty propiedad in propiedades.EnumerateObject())
                if (string.Equals(propiedad.Name, nombre, StringComparison.OrdinalIgnoreCase))
                    return propiedad;

            return null;
        }

        private static List<string> Obligatorios(JsonElement esquema)
        {
            List<string> obligatorios = [];
            if (esquema.TryGetProperty("required", out JsonElement requeridos) && requeridos.ValueKind == JsonValueKind.Array)
                foreach (JsonElement requerido in requeridos.EnumerateArray())
                    if (requerido.GetString() is { } nombre)
                        obligatorios.Add(nombre);

            return obligatorios;
        }

        private static List<string> Tipos(JsonElement esquema)
        {
            List<string> tipos = [];
            if (!esquema.TryGetProperty("type", out JsonElement tipo))
                return tipos;

            if (tipo.ValueKind == JsonValueKind.String)
                tipos.Add(tipo.GetString()!);
            else if (tipo.ValueKind == JsonValueKind.Array)
                foreach (JsonElement uno in tipo.EnumerateArray())
                    if (uno.GetString() is { } nombre)
                        tipos.Add(nombre);

            return tipos;
        }

        // Un tipo desconocido para el validador no rechaza nada: lo decide el SDK.
        // null: el SDK lo enlaza tal cual en un parámetro de tipo referencia (texto, lista u objeto), sea obligatorio u opcional,
        // y lo decide la herramienta (medido con el SDK 2.2.0: 'ruta' null en ssh_find, ssh_list_dir y ssh_search y 'tipo' null
        // en ssh_find buscan con su valor por defecto; 'destino' null da el error de la herramienta, «Destino '' no válido»;
        // 'contenido' null en ssh_write_file escribía un archivo vacío y ahora lo rechaza la herramienta). En un entero o un booleano sin "null" en el esquema
        // fallaba con el mensaje genérico: aquí se rechaza nombrándolo.
        private static bool IsDelTipo(JsonElement valor, string tipo) => tipo switch
        {
            "string" => valor.ValueKind is JsonValueKind.String or JsonValueKind.Null,
            "boolean" => valor.ValueKind is JsonValueKind.True or JsonValueKind.False,
            // Entero de 64 bits (desdeByte es long): uno de 32 fuera de rango lo rechaza el SDK con su mensaje genérico.
            "integer" => valor.ValueKind == JsonValueKind.Number ? valor.TryGetInt64(out _) : valor.ValueKind == JsonValueKind.String && long.TryParse(valor.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
            "number" => valor.ValueKind == JsonValueKind.Number,
            "array" => valor.ValueKind is JsonValueKind.Array or JsonValueKind.Null,
            "object" => valor.ValueKind is JsonValueKind.Object or JsonValueKind.Null,
            "null" => valor.ValueKind == JsonValueKind.Null,
            _ => true
        };

        private static string Tipo(JsonElement valor) => valor.ValueKind switch
        {
            JsonValueKind.String => "texto",
            JsonValueKind.Number => "número",
            JsonValueKind.True or JsonValueKind.False => "booleano",
            JsonValueKind.Array => "lista",
            JsonValueKind.Object => "objeto",
            JsonValueKind.Null => "null",
            JsonValueKind.Undefined => "valor desconocido",
            _ => "valor desconocido"
        };

        // Lo que llegó: el tipo y, si es un escalar, el valor (un texto, recortado; nunca el de una lista o un objeto).
        private static string DescribirLlegado(JsonElement valor) => valor.ValueKind switch
        {
            JsonValueKind.String => $"texto ({Recortar(valor.GetRawText())})",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => $"{Tipo(valor)} ({Recortar(valor.GetRawText())})",
            JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined => Tipo(valor),
            _ => Tipo(valor)
        };

        private static string Recortar(string texto) => texto.Length <= MaxCaracteresValor ? texto : string.Concat(texto.AsSpan(0, MaxCaracteresValor - 1), "…");

        private static string DescribirTipos(List<string> tipos)
        {
            List<string> nombres = [];
            foreach (string tipo in tipos)
                nombres.Add(tipo switch
                {
                    "string" => "texto",
                    "integer" => "un entero (p. ej. 30)",
                    "boolean" => "booleano (true o false, sin comillas)",
                    "number" => "número",
                    "array" => "una lista",
                    "object" => "un objeto",
                    "null" => "null",
                    _ => tipo
                });
            return string.Join(" o ", nombres);
        }

        // «nombre (tipo, obligatorio), …» de las propiedades del esquema.
        private static void DescribirAdmitidos(StringBuilder texto, JsonElement esquema, JsonElement propiedades)
        {
            List<string> obligatorios = Obligatorios(esquema);
            bool isPrimera = true;
            foreach (JsonProperty propiedad in propiedades.EnumerateObject())
            {
                _ = texto.Append(isPrimera ? "" : ", ").Append(propiedad.Name).Append(" (");
                List<string> tipos = Tipos(propiedad.Value);
                _ = tipos.Remove("null");
                _ = texto.Append(tipos.Count == 0 ? "cualquiera" : string.Join(" o ", tipos.ConvertAll(t => t switch
                {
                    "string" => "texto",
                    "integer" => "entero",
                    "boolean" => "booleano",
                    "number" => "número",
                    "array" => "lista",
                    "object" => "objeto",
                    _ => t
                })));
                _ = texto.Append(obligatorios.Contains(propiedad.Name) ? ", obligatorio)" : ")");
                isPrimera = false;
            }
        }
    }
}
