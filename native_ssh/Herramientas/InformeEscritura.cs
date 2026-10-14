using System.Text;
using System.Text.RegularExpressions;
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

    // Lo que el script anunció del candado del archivo ("candado <estado>", ver HerramientasSsh.DefinicionCandado).
    internal enum CandadoEscrituraEnum
    {
        // Sin anuncio: el archivo no existía (escritura nueva) o el script no llegó a tomarlo.
        SinAnuncio,

        // "tomado": excluye a las demás operaciones sobre el archivo mientras el script siga en marcha.
        Tomado,

        // "sin_comprobar": tomado, pero no se pudo comprobar que esté sobre el archivo que sigue en la ruta (sin /proc ni
        // stat de GNU coreutils, python3 o perl en el servidor, o la ruta no se pudo consultar por algo distinto de que no
        // exista, p. ej. sin permiso para atravesar un directorio).
        SinComprobar,

        // "ninguno": ninguna herramienta pudo tomarlo (no hay flock, python3 ni perl que funcionen, o el sistema de
        // archivos no admite flock).
        SinHerramienta,

        // "sin_abrir": el archivo no se pudo abrir ni para leer ni para anexar (sin permiso, o desapareció entretanto).
        SinAbrir
    }

    // Lo que el script de una escritura (ssh_write_file, ssh_replace_file_content) anuncia mientras trabaja, en líneas
    // "__NS_W_<id>__ <clave> [<valor>]" de su salida con el id de la petición (nada ajeno puede imitarlo):
    // - "tmp <base64>" y "copia <base64>": rutas del temporal con el contenido nuevo y de la copia de seguridad del
    //   original, en cuanto se crean; "capacidad <base64>": cómo devolver a mano la security.capability original;
    // - "fase <nombre> [perdidos]": colocando/colocado (renombrado atómico del temporal, o enlace del archivo nuevo),
    //   creando/creado (archivo nuevo copiado sin enlace), escribiendo/escrito (escritura en el sitio), restaurando/
    //   restaurado (vuelta al original desde la copia), fallido (ni escrito ni restaurado), anexando/anexado (append).
    //   Tras escrito y restaurado, lo que el núcleo quitó al escribir y no se pudo devolver: setuid, setgid, capacidades,
    //   o capacidades? (no se pudo comprobar si las tenía), separados por comas, o '-'. Solo si no falta nada se dice que
    //   todo se devolvió y se comprobó;
    // - "candado <estado>": qué pasó al tomar el candado del archivo (flock, ver DefinicionCandado) antes de tocar nada:
    //   tomado, sin_comprobar (tomado sin poder comprobar que esté sobre el archivo de la ruta), ninguno (no hubo
    //   herramienta que lo tomara) o sin_abrir (no se pudo abrir ni para leer ni para anexar); en los dos últimos sigue
    //   sin exclusión (CandadoEscrituraEnum). Sin anuncio, el archivo no existía;
    // - "conservados", "limpio" o "restos": qué hizo al salir con el temporal y la copia.
    // Si la escritura no termina (tiempo agotado, cancelación, conexión perdida), el error dice qué pasó con el archivo
    // según lo último que el script llegó a anunciar, y nada más. El anuncio final de la trampa de salida ("conservados",
    // "limpio" o "restos") confirma que el script terminó y que todo lo anterior llegó; sin él (la salida se cortó al
    // cerrarse el canal o perderse la conexión, o el script sigue en marcha) el script pudo seguir después, y el error lo
    // dice: interrumpido escribiendo o restaurando en el sitio, el script sigue hasta restaurar o conservar las copias. Si
    // termina con un código distinto de 0 sin ese anuncio (lo mató SIGKILL a mitad, o anexaba), el error dice lo mismo
    // según lo anunciado, con el código y el id. Texto: la salida sin los anuncios ni los avisos de bash sobre hijos
    // muertos por una señal. Si la salida superó el máximo por petición y se descartó su principio (IsSalidaIncompleta),
    // pueden faltar anuncios: nada se da por confirmado y los mensajes lo dicen; como lo que se conserva es el final, si
    // el anuncio final llegó dicen que el script anunció que terminó (no que no lo confirmó).
    internal sealed partial record InformeEscritura(string? Fase, IReadOnlyList<string> Perdidos, string? Temporal, string? Copia, string? Capacidad, LimpiezaEscrituraEnum Limpieza, string Texto)
    {
        internal const string PrefijoMarca = "__NS_W_";

        // Lo que se incluye de la salida del script en un error (ninguna herramienta da la de una petición interna).
        private const int MaxCaracteresSalidaEnError = 2000;

        // Si la salida se descartó en parte, se conserva su final: el anuncio final del script, si llegó, sigue ahí.
        private string AvisoSalidaIncompleta => IsFinalRecibido
            ? "La salida del script superó el máximo de bytes por petición y se descartó su principio: el script anunció que terminó, pero puede faltar parte de lo que anunció antes."
            : "La salida del script superó el máximo de bytes por petición y se descartó su principio: puede faltar parte de lo que anunció.";

        public bool IsSalidaIncompleta { get; init; }

        // Ver el anuncio "candado".
        public CandadoEscrituraEnum Candado { get; init; }

        public static InformeEscritura Leer(string salida, string idMarcas, bool isSalidaCompleta = true)
        {
            string marca = $"{PrefijoMarca}{idMarcas}__ ";
            StringBuilder texto = new();
            string? fase = null;
            string[] perdidos = [];
            string? temporal = null;
            string? copia = null;
            string? capacidad = null;
            CandadoEscrituraEnum candado = CandadoEscrituraEnum.SinAnuncio;
            LimpiezaEscrituraEnum limpieza = LimpiezaEscrituraEnum.Desconocida;
            foreach (string linea in salida.Split('\n'))
            {
                int posicion = linea.IndexOf(marca, StringComparison.Ordinal);
                if (posicion < 0)
                {
                    if (!RegexAvisoDeBash().IsMatch(linea))
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
                    case "candado" when hasValor:
                        candado = LeerCandado(partes[1]);
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

            return new InformeEscritura(fase, perdidos, temporal, copia, capacidad, limpieza, texto.ToString().Trim()) { IsSalidaIncompleta = !isSalidaCompleta, Candado = candado };
        }

        // Aviso que el bash del script ("ns", su $0) escribe en su stderr al recoger un hijo que murió por una señal: «ns: línea
        // 155: 2904918 Superado el límite de tamaño de fichero (`core' generado) cat -- "$tmp" > "$destino"» (la palabra
        // «línea» y la descripción van en el idioma del servidor). No es un error del script: no pasa al texto del informe.
        // Forma completa (pretty_print_job de bash): tras el número de línea, el PID con «%5ld» (alineado con espacios si es
        // corto), la descripción y la orden tal como está escrita en el script, con sus variables sin expandir. Todas las
        // órdenes del script que pueden morir así nombran sus archivos con una variable entre comillas ("$tmp", "$destino",
        // "$1"...), y eso es lo que distingue el aviso de un error de bash con una ruta del usuario, que va expandida: «ns:
        // línea 12: 12 x: Permiso denegado» (ruta relativa que empieza por dígitos y un espacio) se queda en el texto, como
        // «ns: línea 3: base64: orden no encontrada». Solo se perdería el error de una ruta que, además de empezar por dígitos
        // y un espacio, contenga literalmente comillas, '$' y una letra.
        [GeneratedRegex(@"^ns: [^:]+ [0-9]+: +[0-9]+ .*""\$[{A-Za-z0-9_]", RegexOptions.CultureInvariant)]
        private static partial Regex RegexAvisoDeBash();

        private static CandadoEscrituraEnum LeerCandado(string estado) => estado switch
        {
            "tomado" => CandadoEscrituraEnum.Tomado,
            "sin_comprobar" => CandadoEscrituraEnum.SinComprobar,
            "ninguno" => CandadoEscrituraEnum.SinHerramienta,
            "sin_abrir" => CandadoEscrituraEnum.SinAbrir,
            _ => CandadoEscrituraEnum.SinAnuncio
        };

        // Solo un renombrado (mv) o un enlace (ln) colocan el contenido nuevo de una vez.
        public bool IsAtomica => Fase == "colocado";

        // Lo que se añade al mensaje de una escritura terminada con éxito.
        public string AvisoExito() => (IsSalidaIncompleta ? $"{AvisoExitoSegunFase()} {AvisoSalidaIncompleta}" : AvisoExitoSegunFase()) + AvisoSinCandado();

        private string AvisoExitoSegunFase() => Fase switch
        {
            "colocado" => string.Empty,
            "escrito" => $" Escritura no atómica: el archivo tiene enlaces duros, su directorio no admite escritura o no se pudieron copiar y comprobar en un temporal todos sus atributos (propietario, grupo, modo, ACL, atributos extendidos, contexto SELinux), así que se sobrescribió en el sitio {CopiaDeSeguridadTrasExito} conservando inodo, enlaces duros, propietario, grupo, ACL, atributos extendidos y contexto SELinux. {(Perdidos.Count == 0 ? "El modo con sus bits setuid y setgid y las capacidades (security.capability), que el núcleo quita al escribir en un archivo (los bits, salvo si escribe root), se devolvieron y se comprobaron." : "El núcleo quita al escribir en un archivo los bits setuid y setgid (salvo si escribe root) y las capacidades (security.capability); lo que el archivo tenía se le devolvió y se comprobó, con estas excepciones.")}{AvisoPerdidos}",
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
            if (IsFinalConfirmado || (!hasAnuncios && !IsSalidaIncompleta))
                return (Texto.Length == 0 ? porDefecto : Texto) + (Fase == "restaurado" ? AvisoPerdidos : string.Empty);

            string final = IsFinalRecibido ? "; el script anunció que terminó, pero se descartó el principio de su salida" : " sin que el script confirmara cómo terminó";
            StringBuilder mensaje = new($"La escritura en '{ruta}' terminó con el código {codigo}{DescribirSenal(codigo)}{final} (petición {idPeticion}). ");
            _ = mensaje.Append(DescribirArchivo(ruta, isScriptTerminado: true)).Append(Restos);
            if (IsSalidaIncompleta)
                _ = mensaje.Append(' ').Append(AvisoSalidaIncompleta);
            if (Texto.Length > 0)
                _ = mensaje.Append(SalidaDelScript);
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
        // isEjecutada: el comando del script pudo llegar al shell (si no, se sabe con certeza que no se ejecutó: se interrumpió
        // y terminó sin enviarse, en la cola o mientras se abría el shell; ver HerramientasSsh.IsInterrumpidaSinEnviar).
        public string MensajeInterrupcion(string ruta, EstadoPeticionEnum estado, string idPeticion, string? aviso, bool isEjecutada)
        {
            StringBuilder mensaje = new($"La escritura en '{ruta}' no terminó (estado: {estado}; petición {idPeticion}). ");
            if (!isEjecutada)
                _ = mensaje.Append($"No se llegó a ejecutar: '{ruta}' no se ha modificado.");
            else
            {
                _ = mensaje.Append(DescribirArchivo(ruta, isScriptTerminado: false)).Append(Restos);
                if (!IsFinalRecibido)
                    _ = mensaje.Append(" El script no confirmó cómo terminó (pudo interrumpirse o perderse su salida después de lo anterior, que es lo último que anunció): compruébalo con ssh_stat o ssh_read_file.");
                if (IsSalidaIncompleta)
                    _ = mensaje.Append(' ').Append(AvisoSalidaIncompleta);
            }

            if (aviso is { Length: > 0 })
                _ = mensaje.Append(' ').Append(aviso);
            // También con una fase anunciada: ssh_output no da la salida de una petición interna y esta es la única vía para ver,
            // p. ej., el error de cp o de cat que hizo fallar la escritura. Nunca lleva el contenido (va por stdin).
            if (Texto.Length > 0)
                _ = mensaje.Append(SalidaDelScript);
            return mensaje.ToString();
        }

        // Llegó el anuncio final del script ("conservados", "limpio" o "restos"): terminó.
        private bool IsFinalRecibido => Limpieza != LimpiezaEscrituraEnum.Desconocida;

        // Con la salida incompleta, que quede el anuncio final no confirma lo anterior.
        private bool IsFinalConfirmado => !IsSalidaIncompleta && IsFinalRecibido;

        // La salida del script (sin los anuncios) en un error, acotada a su final.
        private string SalidaDelScript => DescribirSalidaDelScript(Texto);

        // " Salida del script: …" con 'texto' acotado a su final (también la de ssh_set_cwd, HerramientasSsh.ExigirCompletada).
        internal static string DescribirSalidaDelScript(string texto) => texto.Length <= MaxCaracteresSalidaEnError
            ? $" Salida del script: {texto}"
            : $" Salida del script (sus últimos {MaxCaracteresSalidaEnError} caracteres): …{texto.AsSpan(texto.Length - MaxCaracteresSalidaEnError)}";

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
                : $"'{ruta}' puede haber quedado a medio escribir. Tras la interrupción, el script restaura el original desde la copia de seguridad y solo después borra la copia y el contenido nuevo; si no lo consigue, la copia del original queda en '{Copia}' y el contenido nuevo en '{Temporal}' (para recuperar el original, copia la primera sobre el archivo).{AvisoCandadoEnCurso}",
            "fallido" => $"'{ruta}' quedó a medio escribir y no se pudo restaurar: la copia de seguridad del original está en '{Copia}' y el contenido nuevo en '{Temporal}' (no se han borrado; para recuperar el original, copia la primera sobre el archivo).",
            "restaurado" => $"Se restauró el contenido original de '{ruta}'.{AvisoPerdidos}",
            "escrito" => $"'{ruta}' ya tiene el contenido nuevo completo (escrito en el sitio).{AvisoPerdidos}",
            "anexando" => $"Al final de '{ruta}' puede haberse anexado solo una parte del contenido.",
            "anexado" => $"El contenido ya se anexó completo al final de '{ruta}'.",
            _ => $"No se sabe en qué estado quedó '{ruta}' (fase anunciada: '{Fase}')."
        };

        // Interrumpido escribiendo o restaurando en el sitio, el script sigue en marcha: qué pasa mientras con una escritura
        // nueva sobre el mismo archivo.
        private string AvisoCandadoEnCurso => Candado switch
        {
            CandadoEscrituraEnum.Tomado => $" {CandadoTomadoEnCurso}",
            CandadoEscrituraEnum.SinComprobar => $" {CandadoTomadoEnCurso} Pero {CandadoSinComprobar}: si otro proceso lo sustituyó justo mientras se tomaba, no excluye a nadie.",
            CandadoEscrituraEnum.SinHerramienta => $" {SinCandadoPorHerramientas} {SinCandadoEnCurso}",
            CandadoEscrituraEnum.SinAbrir => $" {SinCandadoPorApertura} {SinCandadoEnCurso}",
            CandadoEscrituraEnum.SinAnuncio => string.Empty,
            _ => string.Empty
        };

        private const string CandadoTomadoEnCurso = "Mientras el script siga en marcha tiene tomado el candado del archivo (flock): las escrituras, los anexados y los reemplazos nuevos sobre él fallan al momento sin tocarlo; reintenta cuando termine.";
        private const string CandadoSinComprobar = "no se pudo comprobar que quedara sobre el archivo que está en la ruta (en el servidor no hay /proc, ni stat de GNU coreutils, python3 o perl con que comparar los inodos, o no se pudo consultar la ruta)";
        private const string SinCandadoPorHerramientas = "El script no pudo tomar el candado del archivo (flock): en el servidor no hay flock (de util-linux, o uno sin -E como el de busybox que funcione), python3 ni perl que lo consigan, o el sistema de archivos no admite flock.";
        private const string SinCandadoPorApertura = "El script no pudo tomar el candado del archivo (flock): no pudo abrirlo ni para leer ni para anexar (sin permiso, o el archivo desapareció entretanto).";
        private const string SinCandadoEnCurso = "Hasta que el script termine, una escritura nueva sobre el archivo podría mezclarse con la restauración: antes de escribir, comprueba con ssh_stat o ssh_read_file que ya terminó.";
        private const string SinProteccion = "no estuvo protegida frente a otra simultánea sobre el mismo archivo.";

        // Lo que se añade al éxito de una escritura que no tuvo un candado comprobado.
        public string AvisoSinCandado() => Candado switch
        {
            CandadoEscrituraEnum.SinComprobar => $" El script tomó el candado del archivo (flock), pero {CandadoSinComprobar}: si otro proceso lo sustituyó justo mientras se tomaba, la escritura {SinProteccion}",
            CandadoEscrituraEnum.SinHerramienta => $" {SinCandadoPorHerramientas} La escritura {SinProteccion}",
            CandadoEscrituraEnum.SinAbrir => $" {SinCandadoPorApertura} La escritura {SinProteccion}",
            CandadoEscrituraEnum.SinAnuncio or CandadoEscrituraEnum.Tomado => string.Empty,
            _ => string.Empty
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
