using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NativeSsh.Buffers;

namespace NativeSsh.Protocolo
{
    // Lo que deja en el terminal un comando lanzado con guardarSalida (Envolver): la ruta del archivo al empezar (A), el
    // resumen al terminar (F: líneas, bytes, ruta y vista previa) o, si no llegó a ejecutarse, el motivo (E). Lineas y Bytes
    // son null si no se pudieron contar (p. ej. el comando borró el archivo).
    public sealed record InformeGuardado(string? Archivo, long? Lineas, long? Bytes, byte[]? VistaPrevia, string? Error, bool IsSinSaltoFinal = false)
    {
        public bool IsTerminado => VistaPrevia is not null;
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
    // - el directorio base se crea con 'mkdir -m 700' y, exista ya o no, se comprueba que no es un enlace, que es un
    //   directorio, que es del usuario (-O) y que su modo es exactamente drwx------ (sin ACL): si no, error sin escribir
    //   nada (un /tmp/native_ssh común lo podría crear antes otro usuario para leer o redirigir las salidas, incluidas las
    //   de root). Con el base así, nadie más puede crear, renombrar ni sustituir nada dentro;
    // - el directorio del shell se crea igual ('mkdir -m 700', exclusivo como 'mktemp -d': falla si ya existe) y se
    //   comprueba igual; el nombre lo fija C#, así que la limpieza (ScriptLimpieza) sabe qué borrar sin leer nada del shell;
    // - el archivo se crea en una subshell con umask 077 y noclobber ('set -C'): 0600 y nunca pisa uno que exista; el comando
    //   escribe en él anexando (un 'set -C' del usuario no lo impide).
    // El comando se evalúa en el shell de la sesión (como sin guardarSalida: sus 'export' y 'cd' cuentan) con su salida y su
    // error redirigidos al archivo; la entrada sigue siendo el terminal (ssh_input funciona y sudo pide la contraseña por el
    // terminal, que sí se ve). El código de salida es el del comando. Si no se pudo preparar el archivo, el comando no se
    // ejecuta y la marca E dice por qué (la sesión lo termina en Error: SesionShell.EjecutarAsync).
    // Al crear el directorio de un shell se barren los de otros shells del mismo base sin nada modificado en 24 h (restos de
    // un proceso que murió sin limpiar; un comando que sigue escribiendo mantiene reciente su archivo). Antes de cada comando
    // se comprueba el cupo del shell (MaxArchivosPorShell, MaxBytesPorShell): por encima, error sin ejecutarlo. No se corta
    // un comando a medias: el cupo se mira antes, y un solo comando puede escribir más.
    // Las marcas van al terminal con printf '%s_%s', así que el texto del envoltorio no las contiene (una traza de 'set -v'
    // no las imita); llevan un nonce por petición. Todo en base64 salvo los números: la ruta y la vista previa no rompen la
    // línea ni la confunden con otra cosa.
    public static partial class SalidaGuardada
    {
        public const int MaxArchivosPorShell = 100;
        public const long MaxBytesPorShell = 256L * 1024 * 1024;
        public const int LineasVistaPrevia = 5;
        public const int MaxBytesLineaVistaPrevia = 512;

        // Lo que lee el servidor para la vista previa: el primer MiB del archivo (una primera línea enorme no se lee entera),
        // sus 5 primeras líneas cortadas a 513 bytes (el byte de más dice que la línea seguía) y, en total, como mucho esto.
        private const int MaxBytesVistaPrevia = LineasVistaPrevia * (MaxBytesLineaVistaPrevia + 2);

        // El texto que se evalúa en el shell en lugar del comando. idShell: el del shell que lo ejecuta (su directorio).
        // prefijo: OpcionesNativeSsh.PrefijoGuardado ('/tmp/native_ssh'); el base es prefijo + '-<usuario>' y su padre, el
        // directorio que debe tener el bit sticky.
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
            string resumen = PlantillaResumen
                .Replace("@LINEAS@", LineasVistaPrevia.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@CORTE@", (MaxBytesLineaVistaPrevia + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("@TOTAL@", MaxBytesVistaPrevia.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            return $$"""
                __ns_gc='{{literal}}'
                if __ns_gf=$({{preparacion}}); then
                printf '%s_%s_A_%s__\n' '__NSG' '{{n}}' "$(printf '%s' "$__ns_gf" | command base64 | command tr -d '\n')"
                { eval "$__ns_gc"
                } >>"$__ns_gf" 2>&1
                __ns_gr=$?
                printf '%s_%s_F_%s__\n' '__NSG' '{{n}}' "$({{resumen}})"
                else
                __ns_gr=1
                fi
                unset __ns_gc __ns_gf
                eval "unset __ns_gr; (exit $__ns_gr)"
                """.ReplaceLineEndings("\n");
        }

        // Dentro de una sustitución de órdenes (una subshell): sus opciones y funciones no tocan el shell de la sesión. Escribe
        // la ruta del archivo en su salida o, si algo no cumple, la marca E en el terminal (stderr) y sale con 1. Sin 'set -e',
        // 'set -u', 'set -f', failglob ni nullglob del usuario; LC_ALL=C para leer el modo de 'ls'.
        private static readonly string PlantillaPreparacion = """
            set +e +u +f; shopt -u failglob nullglob 2>/dev/null; unset GLOBIGNORE
            __ns_ge() { printf '%s_%s_E_%s_%s_%s_%s__\n' '__NSG' '@N@' "$1" "$(printf '%s' "${2-}" | command base64 | command tr -d '\n')" "${3:-0}" "${4:-0}" >&2; exit 1; }
            __ns_gk() { [ ! -L "$1" ] && [ -d "$1" ] && [ -O "$1" ] && case $(LC_ALL=C command ls -ld -- "$1" 2>/dev/null) in 'drwx------ '*|'drwx------. '*) true ;; *) false ;; esac; }
            u=$(command id -un 2>/dev/null)
            case $u in ''|*/*|.|..) __ns_ge usuario "$u" ;; esac
            [ -k @PADRE@ ] || [ -O @PADRE@ ] || __ns_ge tmp @PADRE@
            b=@PREFIJO@-$u
            command mkdir -m 700 -- "$b" 2>/dev/null
            __ns_gk "$b" || __ns_ge base "$b"
            d=$b/@S@
            if command mkdir -m 700 -- "$d" 2>/dev/null; then
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
            (umask 077; set -C; : >"$f") 2>/dev/null || __ns_ge archivo "$f"
            printf '%s' "$f"
            """.ReplaceLineEndings("\n");

        // Líneas: las de 'wc -l' más una si el archivo no termina en salto (una última línea sin '\n' cuenta, como en la
        // cabecera de ssh_read_file); -1 si no se pudo contar (el comando borró o movió el archivo).
        private static readonly string PlantillaResumen = """
            set +e +u +f
            b=$(command wc -c < "$__ns_gf" 2>/dev/null) || b=-1
            l=$(command wc -l < "$__ns_gf" 2>/dev/null) || l=-1
            b=$((b)); l=$((l))
            z=0
            if [ "$b" -gt 0 ] && [ "$l" -ge 0 ] && [ "$(command tail -c 1 "$__ns_gf" 2>/dev/null | command wc -l)" -eq 0 ]; then l=$((l + 1)); z=1; fi
            p=$(command head -c 1048576 "$__ns_gf" 2>/dev/null | command head -n @LINEAS@ | command cut -b 1-@CORTE@ | command head -c @TOTAL@ | command base64 | command tr -d '\n')
            printf '%d_%d_%d_%s_%s' "$l" "$b" "$z" "$(printf '%s' "$__ns_gf" | command base64 | command tr -d '\n')" "$p"
            """.ReplaceLineEndings("\n");

        // Por un canal exec (sin el shell): borra los directorios de los shells indicados, solo si el base sigue siendo un
        // directorio propio y no un enlace. Los nombres son hexadecimales (los fija C#).
        public static string ScriptLimpieza(IEnumerable<Guid> idsShell, string prefijo)
        {
            StringBuilder ids = new();
            foreach (Guid id in idsShell)
                _ = ids.Append(' ').Append(id.ToString("N"));

            return $$"""
                u=$(id -un 2>/dev/null) || exit 0
                b={{Literal(prefijo)}}-$u
                { [ -n "$u" ] && [ -d "$b" ] && [ ! -L "$b" ] && [ -O "$b" ]; } || exit 0
                for g in{{ids}}; do
                  d=$b/$g
                  [ -d "$d" ] && [ ! -L "$d" ] && rm -rf -- "$d"
                done
                exit 0
                """.ReplaceLineEndings("\n");
        }

        // Todo lo que el comando dejó en el terminal (sin secuencias ANSI), hasta el tope del búfer.
        public static string LeerTerminal(BufferSalida salida)
        {
            byte[] datos = new byte[Math.Max(0, (int)Math.Min(salida.Longitud, int.MaxValue))];
            LecturaSalida lectura = salida.Leer(0, datos);
            int longitud = ConversorTexto.LimpiarAnsi(datos.AsSpan(0, lectura.Copiados));
            return ConversorTexto.ATexto(datos.AsSpan(0, longitud));
        }

        // Las marcas de la petición con este nonce: la última de cada tipo.
        public static InformeGuardado Leer(string terminal, Guid nonce)
        {
            string? archivo = null;
            long? lineas = null;
            long? bytes = null;
            byte[]? vista = null;
            string? error = null;
            bool isSinSalto = false;
            string prefijo = $"__NSG_{nonce:N}_";
            foreach (Match marca in RegexMarca().Matches(terminal))
            {
                string cuerpo = marca.Groups["cuerpo"].Value;
                if (!cuerpo.StartsWith(prefijo, StringComparison.Ordinal))
                    continue;

                string[] partes = cuerpo[prefijo.Length..].Split('_');
                switch (partes)
                {
                    case ["A", string ruta]:
                        archivo = Decodificar(ruta) ?? archivo;
                        break;
                    case ["F", string l, string b, string z, string ruta, string previa]:
                        lineas = long.TryParse(l, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long nl) && nl >= 0 ? nl : null;
                        bytes = long.TryParse(b, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long nb) && nb >= 0 ? nb : null;
                        archivo = Decodificar(ruta) ?? archivo;
                        vista = DecodificarBytes(previa) ?? [];
                        isSinSalto = z == "1";
                        break;
                    case ["E", string codigo, string ruta, string n, string t]:
                        error = DescribirError(codigo, Decodificar(ruta) ?? string.Empty, n, t);
                        break;
                    default:
                        // Una marca con otra forma (truncada por el tope del búfer, p. ej.): no se toma.
                        break;
                }
            }

            return new InformeGuardado(archivo, lineas, bytes, vista, error, isSinSalto);
        }

        // El texto del terminal sin las marcas de la petición (lo que el comando escribió de verdad en el terminal).
        public static string QuitarMarcas(string terminal, Guid nonce)
        {
            string prefijo = $"__NSG_{nonce:N}_";
            return RegexMarca().Replace(terminal, m => m.Groups["cuerpo"].Value.StartsWith(prefijo, StringComparison.Ordinal) ? string.Empty : m.Value);
        }

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
            "sesion" => $"{NoEjecutado} el directorio de este shell, '{ruta}', no es un directorio propio con permisos 700 (se cambió o se sustituyó). No se ha escrito nada. Usa ssh_reset_shell (el shell nuevo usa otro directorio) y vuelve a lanzar el comando.",
            "archivos" or "bytes" => $"{NoEjecutado} este shell ya tiene {n} archivo(s) de salida guardados ({t} bytes) en '{ruta}' y el límite es de {MaxArchivosPorShell} archivos y {MaxBytesPorShell / (1024 * 1024)} MiB por shell. Borra los que ya no necesites (ssh_exec con 'rm -- <ruta>') o usa ssh_reset_shell (borra todos y abre un shell nuevo), y vuelve a lanzarlo.",
            "archivo" => $"{NoEjecutado} no se pudo crear el archivo '{ruta}' (sin espacio o sin permiso en /tmp, o ya existía: nunca se pisa uno existente).",
            _ => $"{NoEjecutado} no se pudo preparar el archivo de salida ({codigo})."
        };

        private static string Literal(string valor) => "'" + valor.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        private static string Padre(string prefijo)
        {
            int barra = prefijo.TrimEnd('/').LastIndexOf('/');
            return barra <= 0 ? "/" : prefijo[..barra];
        }

        private const string NoEjecutado = "El comando NO se ejecutó: con guardarSalida=true su salida va a un archivo del servidor y no se pudo preparar:";

        private static string? Decodificar(string base64) => DecodificarBytes(base64) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

        private static byte[]? DecodificarBytes(string base64)
        {
            if (base64.Length == 0)
                return [];

            try
            {
                return Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // Una marca entera hasta su fin de línea: el texto anterior de la misma línea (lo que el comando escribió sin salto) se
        // conserva.
        [GeneratedRegex(@"(?<cuerpo>__NSG_[0-9a-f]{32}_[AFE]_[A-Za-z0-9+/=_-]*)__\n?", RegexOptions.CultureInvariant)]
        private static partial Regex RegexMarca();
    }
}
