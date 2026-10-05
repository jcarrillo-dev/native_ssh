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
    //   original, en cuanto se crean; "capacidad <base64>": cómo devolver a mano la security.capability original;
    // - "fase <nombre> [perdidos]": colocando/colocado (renombrado atómico del temporal, o enlace del archivo nuevo),
    //   creando/creado (archivo nuevo copiado sin enlace), escribiendo/escrito (escritura en el sitio), restaurando/
    //   restaurado (vuelta al original desde la copia), fallido (ni escrito ni restaurado), anexando/anexado (append).
    //   Tras escrito y restaurado, lo que el núcleo quitó al escribir y no se pudo devolver: setuid, setgid, capacidades,
    //   o capacidades? (no se pudo comprobar), separados por comas, o '-';
    // - "conservados", "limpio" o "restos": qué hizo al salir con el temporal y la copia.
    // Si la escritura no termina (tiempo agotado, cancelación, conexión perdida), el error dice qué pasó con el archivo
    // según lo último que el script llegó a anunciar, y nada más. El anuncio final de la trampa de salida ("conservados",
    // "limpio" o "restos") confirma que el script terminó y que todo lo anterior llegó; sin él (la salida se cortó al
    // cerrarse el canal o perderse la conexión, o el script sigue en marcha) el script pudo seguir después, y el error lo
    // dice. Texto: la salida sin los anuncios.
    internal sealed record InformeEscritura(string? Fase, IReadOnlyList<string> Perdidos, string? Temporal, string? Copia, string? Capacidad, LimpiezaEscrituraEnum Limpieza, string Texto)
    {
        internal const string PrefijoMarca = "__NS_W_";

        public static InformeEscritura Leer(string salida, string idMarcas)
        {
            string marca = $"{PrefijoMarca}{idMarcas}__ ";
            StringBuilder texto = new();
            string? fase = null;
            string[] perdidos = [];
            string? temporal = null;
            string? copia = null;
            string? capacidad = null;
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
                    case "capacidad" when hasValor:
                        capacidad = DecodificarRuta(partes[1]);
                        break;
                    case "fase" when hasValor:
                        fase = partes[1];
                        perdidos = partes.Length > 2 && partes[2] != "-" ? partes[2].Split(',') : [];
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

            return new InformeEscritura(fase, perdidos, temporal, copia, capacidad, limpieza, texto.ToString().Trim());
        }

        // Solo un renombrado (mv) o un enlace (ln) colocan el contenido nuevo de una vez.
        public bool IsAtomica => Fase == "colocado";

        // Lo que se añade al mensaje de una escritura terminada con éxito.
        public string AvisoExito() => Fase switch
        {
            "colocado" => string.Empty,
            "escrito" => $" Escritura no atómica: el archivo tiene enlaces duros, su directorio no admite escritura o no se pudieron copiar y comprobar en un temporal todos sus atributos (propietario, grupo, modo, ACL, atributos extendidos, contexto SELinux), así que se sobrescribió en el sitio {CopiaDeSeguridadTrasExito} conservando inodo, enlaces duros, propietario, grupo, ACL, atributos extendidos y contexto SELinux. {(Perdidos.Count == 0 ? "El modo con sus bits setuid y setgid y las capacidades (security.capability), que el núcleo quita al escribir en un archivo, se devolvieron y se comprobaron." : "El núcleo quita al escribir en un archivo los bits setuid y setgid y las capacidades (security.capability): se devolvieron y se comprobaron, salvo lo que sigue.")}{AvisoPerdidos}",
            "creado" => " Escritura no atómica: el sistema de archivos no admite enlaces duros, así que el archivo nuevo se creó copiando el contenido.",
            _ => " No se recibió del servidor la confirmación de cómo se colocó el contenido: no se puede asegurar que la escritura fuera atómica."
        };

        // Error de una escritura que terminó con un código distinto de 0. Si el script confirmó cómo terminó (su anuncio final)
        // o falló antes de anunciar nada, es el error que dio (sin los anuncios) y, si se restauró el original, lo que no se
        // le pudo devolver. Si no, el script murió a mitad (SIGKILL del OOM killer, kill -9) o no tiene trampa de salida
        // (anexar): qué pasó con el archivo según lo último anunciado, lo que queda en el servidor, el código, el id de la
        // petición y lo que el script llegó a escribir.
        public string MensajeError(string porDefecto, string ruta, int codigo, string idPeticion)
        {
            bool hasAnuncios = Fase is not null || Temporal is not null || Copia is not null;
            if (IsFinalConfirmado || !hasAnuncios)
                return (Texto.Length == 0 ? porDefecto : Texto) + (Fase == "restaurado" ? AvisoPerdidos : string.Empty);

            StringBuilder mensaje = new($"La escritura en '{ruta}' terminó con el código {codigo}{DescribirSenal(codigo)} sin que el script confirmara cómo terminó (petición {idPeticion}, su salida completa con ssh_output). ");
            _ = mensaje.Append(DescribirArchivo(ruta, isScriptTerminado: true)).Append(Restos);
            if (Texto.Length > 0)
                _ = mensaje.Append(" Salida del script: ").Append(Texto);
            return mensaje.ToString();
        }

        // Un código de salida de 128 + N es el de un proceso que terminó la señal N.
        private static string DescribirSenal(int codigo) => codigo switch
        {
            137 => " (SIGKILL: p. ej. el OOM killer o un kill -9)",
            > 128 and < 128 + 65 => $" (la señal {codigo - 128})",
            _ => string.Empty
        };

        // Error de una escritura que no terminó: estado y id de la petición, qué pasó con el archivo y qué queda en el servidor.
        // isEjecutada: la petición llegó a ejecutarse (no se canceló en la cola).
        public string MensajeInterrupcion(string ruta, EstadoPeticionEnum estado, string idPeticion, string? aviso, bool isEjecutada)
        {
            StringBuilder mensaje = new($"La escritura en '{ruta}' no terminó (estado: {estado}; petición {idPeticion}, su salida completa con ssh_output). ");
            if (!isEjecutada)
                _ = mensaje.Append($"No se llegó a ejecutar: '{ruta}' no se ha modificado.");
            else
            {
                _ = mensaje.Append(DescribirArchivo(ruta, isScriptTerminado: false)).Append(Restos);
                if (!IsFinalConfirmado)
                    _ = mensaje.Append(" El script no confirmó cómo terminó (pudo interrumpirse o perderse su salida después de lo anterior, que es lo último que anunció): compruébalo con ssh_stat o ssh_read_file.");
            }

            if (aviso is { Length: > 0 })
                _ = mensaje.Append(' ').Append(aviso);
            if (Fase is null && Texto.Length > 0)
                _ = mensaje.Append(" Salida del script: ").Append(Texto);
            return mensaje.ToString();
        }

        private bool IsFinalConfirmado => Limpieza != LimpiezaEscrituraEnum.Desconocida;

        // isScriptTerminado: el script ya no corre (terminó con un código), en lugar de quedar en marcha tras la interrupción.
        private string DescribirArchivo(string ruta, bool isScriptTerminado) => Fase switch
        {
            null => IsFinalConfirmado ? $"No se llegó a modificar '{ruta}'." : $"No consta ninguna modificación de '{ruta}'.",
            "colocando" => $"Se interrumpió durante el renombrado atómico del contenido nuevo: '{ruta}' tiene su contenido anterior (o no existe, si era nuevo) o el nuevo completo, nunca una mezcla.",
            "colocado" => $"'{ruta}' ya tiene el contenido nuevo completo (renombrado atómico).",
            "creando" => $"'{ruta}', que no existía, puede haber quedado creado con solo una parte del contenido nuevo.",
            "creado" => $"'{ruta}' ya se creó con el contenido nuevo completo.",
            // Interrumpido escribiendo o restaurando, el script sigue (ignora ya las señales) hasta restaurar o rendirse; si
            // terminó sin confirmarlo, murió a mitad y la copia sigue en su sitio.
            "escribiendo" or "restaurando" => isScriptTerminado
                ? $"'{ruta}' puede haber quedado a medio escribir y el script ya no lo restaurará: la copia de seguridad del original está en '{Copia}' y el contenido nuevo en '{Temporal}' (para recuperar el original, copia la primera sobre el archivo)."
                : $"'{ruta}' puede haber quedado a medio escribir. Tras la interrupción, el script restaura el original desde la copia de seguridad y solo después borra la copia y el contenido nuevo; si no lo consigue, la copia del original queda en '{Copia}' y el contenido nuevo en '{Temporal}' (para recuperar el original, copia la primera sobre el archivo).",
            "fallido" => $"'{ruta}' quedó a medio escribir y no se pudo restaurar: la copia de seguridad del original está en '{Copia}' y el contenido nuevo en '{Temporal}' (no se han borrado; para recuperar el original, copia la primera sobre el archivo).",
            "restaurado" => $"Se restauró el contenido original de '{ruta}'.{AvisoPerdidos}",
            "escrito" => $"'{ruta}' ya tiene el contenido nuevo completo (escrito en el sitio).{AvisoPerdidos}",
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

        // Lo que el núcleo quitó al escribir en el sitio (o al restaurar) y no se pudo devolver.
        private string AvisoPerdidos
        {
            get
            {
                if (Perdidos.Count == 0)
                    return string.Empty;

                StringBuilder aviso = new(" ATENCIÓN:");
                foreach (string perdido in Perdidos)
                    _ = aviso.Append(' ').Append(perdido switch
                    {
                        "setuid" => "el núcleo quitó al escribir el bit setuid y no se pudo devolver (chmod u+s, como propietario o con sudo=true).",
                        "setgid" => "el núcleo quitó al escribir el bit setgid y no se pudo devolver (chmod g+s: sin sudo=true, el propietario solo puede ponerlo si pertenece al grupo del archivo).",
                        "capacidades" => $"el núcleo borró al escribir las capacidades del archivo (security.capability) y no se pudieron devolver: hace falta sudo=true y setfattr o setcap.{(Capacidad is null ? string.Empty : $" Para devolverlas, con privilegios: «{Capacidad} <archivo>».")}",
                        "capacidades?" => "no se pudo comprobar si el archivo tenía capacidades (security.capability): en el servidor no hay getfattr ni getcap; si las tenía, el núcleo las borró al escribir.",
                        _ => $"no se pudo devolver '{perdido}'."
                    });
                return aviso.ToString();
            }
        }

        // Solo se dice que la copia se borró si el script lo anunció.
        private string CopiaDeSeguridadTrasExito => Limpieza switch
        {
            LimpiezaEscrituraEnum.Limpio => "(con una copia de seguridad previa del original, ya borrada)",
            LimpiezaEscrituraEnum.Restos => $"(con una copia de seguridad previa del original en '{Copia}', que no se pudo borrar)",
            LimpiezaEscrituraEnum.Desconocida or LimpiezaEscrituraEnum.Conservados => $"(con una copia de seguridad previa del original en '{Copia}', que puede seguir en el servidor)",
            _ => $"(con una copia de seguridad previa del original en '{Copia}', que puede seguir en el servidor)"
        };

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
