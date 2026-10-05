using System.Text;
using NativeSsh.Comun;

namespace NativeSsh.Herramientas
{
    internal enum LimpiezaEscrituraEnum
    {
        // El script no llegó a decir qué hizo con el temporal y la copia de seguridad.
        Desconocida,

        // Los borró.
        Limpio,

        // Intentó borrarlos y alguno sigue en el servidor.
        Restos,

        // Los dejó a propósito: el destino no está escrito ni restaurado.
        Conservados
    }

    // Lo que el script de una escritura (ssh_write_file, ssh_replace_file_content) anuncia mientras trabaja, en líneas
    // "__NS_W_<id>__ <clave> [<valor>]" de su salida con el id de la petición (nada ajeno puede imitarlo):
    // - "tmp <base64>" y "copia <base64>": rutas del temporal con el contenido nuevo y de la copia de seguridad del
    //   original, en cuanto se crean;
    // - "fase <nombre>": colocando/colocado (renombrado atómico del temporal, o enlace del archivo nuevo), creando/creado
    //   (archivo nuevo copiado sin enlace), escribiendo/escrito (escritura en el sitio), restaurando/restaurado (vuelta
    //   al original desde la copia), fallido (ni escrito ni restaurado), anexando/anexado (append);
    // - "conservados", "limpio" o "restos": qué hizo al salir con el temporal y la copia.
    // Si la escritura no termina (tiempo agotado, cancelación, conexión perdida), el error dice qué pasó con el archivo
    // según lo último que el script llegó a anunciar, y nada más. Texto: la salida sin los anuncios.
    internal sealed record InformeEscritura(string? Fase, string? Temporal, string? Copia, LimpiezaEscrituraEnum Limpieza, string Texto)
    {
        internal const string PrefijoMarca = "__NS_W_";

        public static InformeEscritura Leer(string salida, string idMarcas)
        {
            string marca = $"{PrefijoMarca}{idMarcas}__ ";
            StringBuilder texto = new();
            string? fase = null;
            string? temporal = null;
            string? copia = null;
            LimpiezaEscrituraEnum limpieza = LimpiezaEscrituraEnum.Desconocida;
            foreach (string linea in salida.Split('\n'))
            {
                int posicion = linea.IndexOf(marca, StringComparison.Ordinal);
                if (posicion < 0)
                {
                    _ = texto.Append(linea).Append('\n');
                    continue;
                }

                // Lo que otro proceso escribió en el terminal justo antes del anuncio, en la misma línea, es texto.
                if (posicion > 0)
                    _ = texto.Append(linea, 0, posicion).Append('\n');

                string[] partes = linea[(posicion + marca.Length)..].Split(' ');
                bool hasValor = partes.Length > 1;
                switch (partes[0])
                {
                    case "tmp" when hasValor:
                        temporal = DecodificarRuta(partes[1]);
                        break;
                    case "copia" when hasValor:
                        copia = DecodificarRuta(partes[1]);
                        break;
                    case "fase" when hasValor:
                        fase = partes[1];
                        break;
                    case "conservados":
                        limpieza = LimpiezaEscrituraEnum.Conservados;
                        break;
                    case "limpio":
                        limpieza = LimpiezaEscrituraEnum.Limpio;
                        break;
                    case "restos":
                        limpieza = LimpiezaEscrituraEnum.Restos;
                        break;
                    default:
                        break;
                }
            }

            return new InformeEscritura(fase, temporal, copia, limpieza, texto.ToString().Trim());
        }

        // Solo un renombrado (mv) o un enlace (ln) colocan el contenido nuevo de una vez.
        public bool IsAtomica => Fase == "colocado";

        // Lo que se añade al mensaje de una escritura terminada con éxito.
        public string AvisoExito() => Fase switch
        {
            "colocado" => string.Empty,
            "escrito" => $" Escritura no atómica: el archivo tiene enlaces duros, su directorio no admite escritura o no se pudieron copiar y comprobar en un temporal todos sus atributos (propietario, grupo, modo, ACL, atributos extendidos, contexto SELinux), así que se sobrescribió en el sitio {CopiaDeSeguridadTrasExito} conservando inodo, enlaces duros, propietario, grupo, modo, ACL, atributos extendidos y contexto SELinux.",
            "creado" => " Escritura no atómica: el sistema de archivos no admite enlaces duros, así que el archivo nuevo se creó copiando el contenido.",
            _ => " No se recibió del servidor la confirmación de cómo se colocó el contenido: no se puede asegurar que la escritura fuera atómica."
        };

        // Error de una escritura que terminó con un código distinto de 0: el que dio el script (sin los anuncios).
        public string MensajeError(string porDefecto) => Texto.Length == 0 ? porDefecto : Texto;

        // Error de una escritura que no terminó: estado y id de la petición, qué pasó con el archivo y qué queda en el servidor.
        public string MensajeInterrupcion(string ruta, EstadoPeticionEnum estado, string idPeticion, string? aviso)
        {
            StringBuilder mensaje = new($"La escritura en '{ruta}' no terminó (estado: {estado}; petición {idPeticion}, su salida completa con ssh_output). ");
            _ = mensaje.Append(DescribirArchivo(ruta)).Append(Restos);
            if (aviso is { Length: > 0 })
                _ = mensaje.Append(' ').Append(aviso);
            if (Fase is null && Texto.Length > 0)
                _ = mensaje.Append(" Salida del script: ").Append(Texto);
            return mensaje.ToString();
        }

        private string DescribirArchivo(string ruta) => Fase switch
        {
            null => $"No se llegó a modificar '{ruta}'.",
            "colocando" => $"Se interrumpió durante el renombrado atómico del contenido nuevo: '{ruta}' tiene su contenido anterior (o no existe, si era nuevo) o el nuevo completo, nunca una mezcla.",
            "colocado" => $"'{ruta}' ya tiene el contenido nuevo completo (renombrado atómico).",
            "creando" => $"'{ruta}', que no existía, puede haber quedado creado con solo una parte del contenido nuevo.",
            "creado" => $"'{ruta}' ya se creó con el contenido nuevo completo.",
            "escribiendo" or "restaurando" => $"'{ruta}' puede haber quedado a medio escribir: la copia de seguridad del original está en '{Copia}' y el contenido nuevo en '{Temporal}' (no se han borrado; para recuperar el original, copia la primera sobre el archivo).",
            "fallido" => $"'{ruta}' quedó a medio escribir y no se pudo restaurar: la copia de seguridad del original está en '{Copia}' y el contenido nuevo en '{Temporal}' (no se han borrado; para recuperar el original, copia la primera sobre el archivo).",
            "restaurado" => $"Se restauró el contenido original de '{ruta}'.",
            "escrito" => $"'{ruta}' ya tiene el contenido nuevo completo (escrito en el sitio).",
            "anexando" => $"Al final de '{ruta}' puede haberse anexado solo una parte del contenido.",
            "anexado" => $"El contenido ya se anexó completo al final de '{ruta}'.",
            _ => $"No se sabe en qué estado quedó '{ruta}' (fase anunciada: '{Fase}')."
        };

        // Temporales que pueden seguir en el servidor, salvo los que ya se nombraron como conservados.
        private string Restos
        {
            get
            {
                bool isYaNombrados = Fase is "escribiendo" or "restaurando" or "fallido";
                string rutas = Temporal is null
                    ? Copia is null ? string.Empty : $"la copia de seguridad '{Copia}'"
                    : Copia is null ? $"el temporal '{Temporal}'" : $"el temporal '{Temporal}' y la copia de seguridad '{Copia}'";
                return Limpieza == LimpiezaEscrituraEnum.Limpio || isYaNombrados || rutas.Length == 0
                    ? string.Empty
                    : Limpieza == LimpiezaEscrituraEnum.Restos
                        ? $" No se pudieron borrar del servidor {rutas}."
                        : $" Si la limpieza no llegó a ejecutarse, pueden quedar en el servidor {rutas}.";
            }
        }

        private string CopiaDeSeguridadTrasExito => Limpieza == LimpiezaEscrituraEnum.Limpio || Copia is null
            ? "(con una copia de seguridad previa del original, ya borrada)"
            : $"(con una copia de seguridad previa del original en '{Copia}', que no se pudo borrar)";

        private static string? DecodificarRuta(string base64)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
