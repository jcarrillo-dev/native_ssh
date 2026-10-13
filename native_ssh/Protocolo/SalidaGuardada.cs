using System.Globalization;
using System.Text;

namespace NativeSsh.Protocolo
{
    // Lo que se sabe del archivo de un comando lanzado con guardarSalida. Archivo, Lineas, Bytes, IsSinSaltoFinal, IsTerminado
    // y Error vienen de las marcas que el envoltorio deja en el terminal (FiltroMarcasGuardado): la ruta al empezar (A), el
    // recuento al terminar (F) o, si no llegó a ejecutarse, el motivo (E). Lineas y Bytes son null si no se pudieron contar (el
    // archivo ya no estaba al terminar). VistaPrevia la lee después la sesión por un canal exec (SesionShell), nunca si hubo
    // un secreto; MotivoSinVistaPrevia dice por qué no la hay cuando debía haberla.
    public sealed record InformeGuardado(string? Archivo, long? Lineas, long? Bytes, byte[]? VistaPrevia, string? Error, bool IsSinSaltoFinal = false)
    {
        public bool IsTerminado { get; init; }

        public string? MotivoSinVistaPrevia { get; init; }
    }

    // ssh_exec con guardarSalida=true (C12): la salida completa del comando (stdout y stderr, como en el terminal) va a un
    // archivo del servidor y no a la respuesta. El comando del usuario se envuelve al enviarlo al shell (SesionShell, que
    // conoce el shell que lo ejecuta) y viaja por el protocolo de siempre (ConstructorComando.EscribirComando): por el PTY,
    // con __NS_CWD, la cola y el centinela intactos; el envoltorio es solo el texto que se evalúa.
    // Archivo: /tmp/native_ssh-<usuario>/<id del shell>/<id de la petición>.out, donde <usuario> es el de la sesión en el
    // servidor ('id -un' en el propio servidor: es el que cuenta para los permisos) y <id del shell> es aleatorio (128 bits)
    // y propio de cada shell de la sesión. Seguridad (un /tmp común lo pueden tocar otros usuarios del servidor):
    // - /tmp debe tener el bit sticky (o ser del propio usuario): sin él, otro usuario podría renombrar el directorio base y
    //   poner el suyo entre la comprobación y el uso;
    // - el directorio base se crea con 'mkdir -m 700' seguido de 'chmod 700' (con una ACL por defecto en /tmp, la máscara
    //   queda así sin permisos y ninguna entrada con nombre da acceso) y, exista ya o no, se comprueba que no es un enlace,
    //   que es un directorio, que es del usuario (-O) y que 'ls -ld' da exactamente drwx------ (el de GNU coreutils añade
    //   '+' si hay ACL, y entonces se rechaza; el de busybox no la muestra): si no, error sin escribir nada (un
    //   /tmp/native_ssh común lo podría crear antes otro usuario para leer o redirigir las salidas, incluidas las de root).
    //   Con el base así, nadie más puede crear, renombrar ni sustituir nada dentro;
    // - el directorio del shell se crea igual ('mkdir -m 700' y 'chmod 700', exclusivo como 'mktemp -d': falla si ya
    //   existe) y se comprueba igual; el nombre lo fija C#, así que la limpieza (ScriptLimpieza) sabe qué borrar sin leer
    //   nada del shell;
    // - el archivo se crea en una subshell con umask 077 y noclobber ('set -C'), y después 'chmod 600' (una ACL por defecto
    //   anula la umask al crearlo): nunca pisa uno que exista; el comando escribe en él anexando (un 'set -C' del usuario no
    //   lo impide).
    // El comando se evalúa en el shell de la sesión (como sin guardarSalida: sus 'export' y 'cd' cuentan) con su salida y su
    // error redirigidos al archivo; la entrada sigue siendo el terminal (ssh_input funciona y sudo pide la contraseña por el
    // terminal, que sí se ve). El código de salida es el del comando. Si no se pudo preparar el archivo, el comando no se
    // ejecuta y la marca E dice por qué (la sesión lo termina en Error: SesionShell.EjecutarAsync).
    // Al crear el directorio de un shell se barren los de otros shells del mismo base sin nada modificado en 24 h (restos de
    // un proceso que murió sin limpiar; un comando que sigue escribiendo mantiene reciente su archivo). Antes de cada comando
    // se comprueba el cupo del shell (MaxArchivosPorShell, MaxBytesPorShell): por encima, error sin ejecutarlo. No se corta
    // un comando a medias: el cupo se mira antes, y un solo comando puede escribir más.
    // Nada derivado de lo que hay en el archivo viaja por el terminal: las marcas solo llevan números (líneas, bytes) y la
    // ruta, que no sale de la salida; la vista previa la lee la sesión después por un canal exec (ScriptVistaPrevia), y no se
    // lee si se envió un secreto. Las marcas no llegan al búfer de la salida (FiltroMarcasGuardado las quita al pasar), así
    // que ninguna página ni desdeByte puede enseñar un trozo. Van al terminal con printf '%s_%s', así que el texto del
    // envoltorio no las contiene (una traza de 'set -v' no las imita); llevan un nonce por petición. Las subshells del
    // envoltorio empiezan quitando 'set -x' y 'set -v' con su traza a /dev/null; las líneas del envoltorio que corren en el
    // shell de la sesión sí se trazan si el usuario dejó 'set -x', pero solo llevan la ruta y los números.
    public static class SalidaGuardada
    {
        public const int MaxArchivosPorShell = 100;
        public const long MaxBytesPorShell = 256L * 1024 * 1024;
        public const int LineasVistaPrevia = 5;
        public const int MaxBytesLineaVistaPrevia = 512;

        // Lo que lee la vista previa (ScriptVistaPrevia): el primer MiB del archivo (una primera línea enorme no se lee
        // entera), sus 5 primeras líneas cortadas a 513 bytes (el byte de más dice que la línea seguía) y, en total, como
        // mucho esto.
        public const int MaxBytesVistaPrevia = LineasVistaPrevia * (MaxBytesLineaVistaPrevia + 2);

        // El texto que se evalúa en el shell en lugar del comando. idShell: el del shell que lo ejecuta (su directorio).
        // prefijo: OpcionesNativeSsh.PrefijoGuardado ('/tmp/native_ssh'); el base es prefijo + '-<usuario>' y su padre, el
        // directorio que debe tener el bit sticky. Empieza quitando __ns_k y __ns_b, las variables de ConstructorComando con el
        // texto que se evalúa (lleva el nonce de las marcas): el comando no las ve, ni sus hijos con 'set -a', así que no puede
        // imitar una marca (y FiltroMarcasGuardado solo aplica las que siguen el orden del envoltorio).
        public static string Envolver(string comando, Guid nonce, Guid idShell, string idPeticion, string prefijo)
        {
            string n = nonce.ToString("N");
            string s = idShell.ToString("N");
            string literal = comando.Replace("'", "'\\''", StringComparison.Ordinal);
            string preparacion = PlantillaPreparacion
                .Replace("@PREFIJO@", Literal(prefijo), StringComparison.Ordinal)
                .Replace("@PADRE@", Literal(Padre(prefijo)), StringComparison.Ordinal)
                .Replace("@N@", n, StringComparison.Ordinal)
                .Replace("@S@", s, StringComparison.Ordinal)
                .Replace("@I@", idPeticion, StringComparison.Ordinal)
                .Replace("@MAXF@", MaxArchivosPorShell.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@MAXB@", MaxBytesPorShell.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            return $$"""
                unset __ns_k __ns_b
                __ns_gc='{{literal}}'
                if __ns_gf=$({{preparacion}}); then
                printf '%s_%s_A_%s__\n' '__NSG' '{{n}}' "$({ set +x +v; } 2>/dev/null; printf '%s' "$__ns_gf" | command base64 | command tr -d '\n')"
                { eval "$__ns_gc"
                } >>"$__ns_gf" 2>&1
                __ns_gr=$?
                printf '%s_%s_F_%s__\n' '__NSG' '{{n}}' "$({{PlantillaResumen}})"
                else
                __ns_gr=1
                fi
                unset __ns_gc __ns_gf
                eval "unset __ns_gr; (exit $__ns_gr)"
                """.ReplaceLineEndings("\n");
        }

        // En lugar de Envolver cuando la sesión ya tiene MaxShellsConGuardado shells con salidas guardadas sin borrar y este
        // shell aún no tiene directorio (SesionShell): no crea nada ni ejecuta el comando; solo la marca E con el tope.
        public static string EnvolverSinCupoShells(Guid nonce, int maxShells) => $$"""
            unset __ns_k __ns_b
            printf '%s_%s_E_shells__%d_0__\n' '__NSG' '{{nonce:N}}' {{maxShells.ToString(CultureInfo.InvariantCulture)}} >&2
            (exit 1)
            """.ReplaceLineEndings("\n");

        // Dentro de una sustitución de órdenes (una subshell): sus opciones y funciones no tocan el shell de la sesión. Escribe
        // la ruta del archivo en su salida o, si algo no cumple, la marca E en el terminal (stderr) y sale con 1. Sin traza
        // ('set -x', 'set -v'), 'set -e', 'set -u', 'set -f', failglob ni nullglob del usuario; LC_ALL=C para leer el modo de
        // 'ls'.
        private static readonly string PlantillaPreparacion = """
            { set +x +v; } 2>/dev/null
            set +e +u +f; shopt -u failglob nullglob 2>/dev/null; unset GLOBIGNORE
            __ns_ge() { printf '%s_%s_E_%s_%s_%s_%s__\n' '__NSG' '@N@' "$1" "$(printf '%s' "${2-}" | command base64 | command tr -d '\n')" "${3:-0}" "${4:-0}" >&2; exit 1; }
            __ns_gk() { [ ! -L "$1" ] && [ -d "$1" ] && [ -O "$1" ] && case $(LC_ALL=C command ls -ld -- "$1" 2>/dev/null) in 'drwx------ '*|'drwx------. '*) true ;; *) false ;; esac; }
            u=$(command id -un 2>/dev/null)
            case $u in ''|*/*|.|..) __ns_ge usuario "$u" ;; esac
            [ -k @PADRE@ ] || [ -O @PADRE@ ] || __ns_ge tmp @PADRE@
            b=@PREFIJO@-$u
            command mkdir -m 700 -- "$b" 2>/dev/null && command chmod 700 -- "$b" 2>/dev/null
            __ns_gk "$b" || __ns_ge base "$b"
            d=$b/@S@
            if command mkdir -m 700 -- "$d" 2>/dev/null; then
              command chmod 700 -- "$d" 2>/dev/null
              for x in "$b"/*; do
                { [ "$x" != "$d" ] && [ -d "$x" ] && [ ! -L "$x" ]; } || continue
                r=$(command find "$x" -mmin -1440 2>/dev/null) && [ -z "$r" ] && command rm -rf -- "$x"
              done
            fi
            __ns_gk "$d" || __ns_ge sesion "$d"
            n=0; t=0
            for x in "$d"/*; do
              [ -f "$x" ] || continue
              s=$(command wc -c < "$x" 2>/dev/null) || s=0
              n=$((n + 1)); t=$((t + s))
            done
            [ "$n" -lt @MAXF@ ] || __ns_ge archivos "$d" "$n" "$t"
            [ "$t" -lt @MAXB@ ] || __ns_ge bytes "$d" "$n" "$t"
            f=$d/@I@.out
            { (umask 077; set -C; : >"$f") && command chmod 600 -- "$f"; } 2>/dev/null || __ns_ge archivo "$f"
            printf '%s' "$f"
            """.ReplaceLineEndings("\n");

        // Solo números: líneas, bytes y si el archivo no termina en salto (1). Líneas: las de 'wc -l' más una si el archivo no
        // termina en salto (una última línea sin '\n' cuenta, como en la cabecera de ssh_read_file); -1 si no se pudo contar
        // (el archivo ya no estaba).
        private static readonly string PlantillaResumen = """
            { set +x +v; } 2>/dev/null
            set +e +u +f
            b=$(command wc -c < "$__ns_gf" 2>/dev/null) || b=-1
            l=$(command wc -l < "$__ns_gf" 2>/dev/null) || l=-1
            b=$((b)); l=$((l))
            z=0
            if [ "$b" -gt 0 ] && [ "$l" -ge 0 ] && [ "$(command tail -c 1 "$__ns_gf" 2>/dev/null | command wc -l)" -eq 0 ]; then l=$((l + 1)); z=1; fi
            printf '%d_%d_%d' "$l" "$b" "$z"
            """.ReplaceLineEndings("\n");

        // Por un canal exec (sin el shell ni el terminal), acotado como ssh_read_file: el principio del archivo para la vista
        // previa. Solo un archivo regular que no sea un enlace (código 3 si no).
        public static string ScriptVistaPrevia(string archivo) => $$"""
            f={{Literal(archivo)}}
            { [ -f "$f" ] && [ ! -L "$f" ]; } || exit 3
            head -c 1048576 -- "$f" | head -n {{LineasVistaPrevia}} | cut -b 1-{{MaxBytesLineaVistaPrevia + 1}} | head -c {{MaxBytesVistaPrevia}}
            """.ReplaceLineEndings("\n");

        // La ruta que dio la marca A es la que corresponde a este shell y esta petición (prefijo-<usuario>/<shell>/<id>.out):
        // la vista previa solo lee esa.
        public static bool IsRutaEsperada(string archivo, string prefijo, Guid idShell, string idPeticion)
        {
            string inicio = prefijo + "-";
            string sufijo = $"/{idShell:N}/{idPeticion}.out";
            return archivo.Length > inicio.Length + sufijo.Length
                && archivo.StartsWith(inicio, StringComparison.Ordinal)
                && archivo.EndsWith(sufijo, StringComparison.Ordinal)
                && !archivo.AsSpan(inicio.Length, archivo.Length - inicio.Length - sufijo.Length).Contains('/');
        }

        // Por un canal exec (sin el shell): borra los directorios de los shells indicados, solo si el base sigue siendo un
        // directorio propio y no un enlace. Los nombres son hexadecimales (los fija C#). Antes de borrar cuenta los archivos
        // que hay en ellos (como el cupo: los de primer nivel) y lo dice en su última línea ('__ns_archivos=<n>',
        // ArchivosLimpieza); código 1 si algún borrado falló.
        public static string ScriptLimpieza(IEnumerable<Guid> idsShell, string prefijo)
        {
            StringBuilder ids = new();
            foreach (Guid id in idsShell)
                _ = ids.Append(' ').Append(id.ToString("N"));

            return $$"""
                c=0
                r=0
                u=$(id -un 2>/dev/null)
                b={{Literal(prefijo)}}-$u
                if [ -n "$u" ] && [ -d "$b" ] && [ ! -L "$b" ] && [ -O "$b" ]; then
                  for g in{{ids}}; do
                    d=$b/$g
                    { [ -d "$d" ] && [ ! -L "$d" ]; } || continue
                    for x in "$d"/*; do
                      [ -f "$x" ] && c=$((c + 1))
                    done
                    rm -rf -- "$d" || r=1
                  done
                fi
                printf '\n__ns_archivos=%d\n' "$c"
                exit $r
                """.ReplaceLineEndings("\n");
        }

        // Los archivos que contó ScriptLimpieza antes de borrar (su última línea); null si la salida no la trae.
        public static int? ArchivosLimpieza(ReadOnlySpan<byte> salida)
        {
            ReadOnlySpan<byte> etiqueta = "\n__ns_archivos="u8;
            int inicio = salida.LastIndexOf(etiqueta);
            if (inicio < 0)
                return null;

            ReadOnlySpan<byte> resto = salida[(inicio + etiqueta.Length)..];
            int fin = resto.IndexOf((byte)'\n');
            ReadOnlySpan<byte> numero = fin < 0 ? resto : resto[..fin];
            return int.TryParse(numero, NumberStyles.None, CultureInfo.InvariantCulture, out int archivos) ? archivos : null;
        }

        // Una marca ya separada por FiltroMarcasGuardado: tipo ('A', 'F' o 'E') y sus campos. Devuelve el informe con ella
        // aplicada (una marca con otros campos no cambia nada).
        public static InformeGuardado Aplicar(InformeGuardado informe, char tipo, string[] campos) => (tipo, campos) switch
        {
            ('A', [string ruta]) => informe with { Archivo = Decodificar(ruta) ?? informe.Archivo },
            ('F', [string l, string b, string z]) => informe with
            {
                Lineas = long.TryParse(l, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long nl) && nl >= 0 ? nl : null,
                Bytes = long.TryParse(b, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long nb) && nb >= 0 ? nb : null,
                IsSinSaltoFinal = z == "1",
                IsTerminado = true
            },
            ('E', [string codigo, string ruta, string n, string t]) => informe with { Error = DescribirError(codigo, Decodificar(ruta) ?? string.Empty, n, t) },
            _ => informe
        };

        // Campos de cada tipo de marca (FiltroMarcasGuardado: una marca con otro número no se toma).
        public static int CamposDe(byte tipo) => tipo switch
        {
            (byte)'A' => 1,
            (byte)'F' => 3,
            (byte)'E' => 4,
            _ => -1
        };

        // Las 5 primeras líneas (como mucho) en texto: cada una cortada a MaxBytesLineaVistaPrevia bytes sin partir un carácter
        // UTF-8 y con ' […]' si seguía; sin secuencias ANSI ni el CR de los CRLF. 'cut' termina siempre la última línea con un
        // salto: si la vista previa es el archivo entero (como mucho 5 líneas) y este no terminaba en salto, se quita.
        public static string TextoVistaPrevia(InformeGuardado informe)
        {
            byte[] vista = informe.VistaPrevia ?? [];
            StringBuilder texto = new();
            ReadOnlySpan<byte> resto = vista;
            for (int i = 0; i < LineasVistaPrevia && !resto.IsEmpty; i++)
            {
                int salto = resto.IndexOf((byte)'\n');
                ReadOnlySpan<byte> linea = salto < 0 ? resto : resto[..salto];
                resto = salto < 0 ? [] : resto[(salto + 1)..];
                bool isCortada = linea.Length > MaxBytesLineaVistaPrevia;
                byte[] copia = (isCortada ? linea[..ConversorTexto.AjustarFinUtf8(linea[..MaxBytesLineaVistaPrevia])] : linea).ToArray();
                int longitud = ConversorTexto.LimpiarAnsi(copia);
                if (longitud > 0 && copia[longitud - 1] == (byte)'\r')
                    longitud--;
                _ = texto.Append(ConversorTexto.ATexto(copia.AsSpan(0, longitud)));
                if (isCortada)
                    _ = texto.Append(" […]");
                if (salto >= 0 || isCortada)
                    _ = texto.Append('\n');
            }

            bool isArchivoEntero = informe.Lineas is { } total && total <= LineasVistaPrevia;
            if (isArchivoEntero && informe.IsSinSaltoFinal && texto.Length > 0 && texto[^1] == '\n')
                texto.Length--;
            return texto.ToString();
        }

        private static string DescribirError(string codigo, string ruta, string n, string t) => codigo switch
        {
            "usuario" => $"{NoEjecutado} no se pudo saber en el servidor el usuario de la sesión ('id -un' falló o dio un nombre no válido: '{ruta}').",
            "tmp" => $"{NoEjecutado} '{ruta}' no tiene el bit sticky (modo 1777) ni es del usuario de la sesión: cualquier usuario del servidor podría sustituir el directorio de native_ssh entre la comprobación y la escritura. No se ha escrito nada. Lanza el comando sin guardarSalida.",
            "base" => $"{NoEjecutado} '{ruta}' ya existe y no es un directorio propio con permisos 700 (es un enlace, es de otro usuario o tiene otros permisos o una ACL): otro usuario del servidor pudo crearlo para leer o redirigir las salidas. No se ha escrito nada. Compruébalo con ssh_stat: si no es tuyo, no lo uses y avisa al administrador; si es tuyo, déjalo con 'chmod 700' (sin ACL) o bórralo, y vuelve a lanzar el comando.",
            "shells" => $"{NoEjecutado} esta sesión ya tiene {n} shells con salidas guardadas sin borrar (el actual y los que se abrieron solos tras un plazo agotado, una cancelación o una caída), que es el límite. Lee lo que necesites de esos archivos y usa ssh_reset_shell o ssh_disconnect (borran todos), y vuelve a lanzar el comando.",
            "sesion" => $"{NoEjecutado} el directorio de este shell, '{ruta}', no es un directorio propio con permisos 700 (se cambió o se sustituyó). No se ha escrito nada. Usa ssh_reset_shell (el shell nuevo usa otro directorio) y vuelve a lanzar el comando.",
            "archivos" or "bytes" => $"{NoEjecutado} este shell ya tiene {n} archivo(s) de salida guardados ({t} bytes) en '{ruta}' y el límite es de {MaxArchivosPorShell} archivos y {MaxBytesPorShell / (1024 * 1024)} MiB por shell. Borra los que ya no necesites (ssh_exec con 'rm -- <ruta>') o usa ssh_reset_shell (borra todos y abre un shell nuevo), y vuelve a lanzarlo.",
            "archivo" => $"{NoEjecutado} no se pudo crear el archivo '{ruta}' con permisos 600 (sin espacio o sin permiso en /tmp, o ya existía: nunca se pisa uno existente).",
            _ => $"{NoEjecutado} no se pudo preparar el archivo de salida ({codigo})."
        };

        private static string Literal(string valor) => "'" + valor.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        private static string Padre(string prefijo)
        {
            int barra = prefijo.TrimEnd('/').LastIndexOf('/');
            return barra <= 0 ? "/" : prefijo[..barra];
        }

        private const string NoEjecutado = "El comando NO se ejecutó: con guardarSalida=true su salida va a un archivo del servidor y no se pudo preparar:";

        private static string? Decodificar(string base64)
        {
            if (base64.Length == 0)
                return string.Empty;

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
