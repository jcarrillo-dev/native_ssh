using System.Globalization;
using System.Text;

namespace NativeSsh.Protocolo
{
    // Lo que contó en el servidor el borrado de los directorios de guardarSalida (SalidaGuardada.ScriptLimpieza): las
    // salidas que había antes de borrar, las que quedan en los directorios que no se pudieron borrar, esos shells y lo demás
    // que queda en ellos (entradas que no son salidas del protocolo). Quedan y Otros son null si alguno de esos directorios no
    // se pudo listar (p. ej. sin permiso de lectura): entonces no se sabe lo que queda.
    public sealed record RecuentoLimpieza(int Contados, int? Quedan, Guid[] ShellsQueSiguen, int? Otros);

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
    //   Si lo acaba de crear el propio envoltorio y no cumple (una ACL por defecto en /tmp que el 'ls' de GNU marca con '+'),
    //   lo vuelve a borrar ('rmdir': está vacío) y el error lo dice así, no que ya existía.
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
        // entera), sus 5 primeras líneas, de cada una lo que sigue a su último '\r' seguido de algo que no es otro '\r' (como en un terminal: de
        // una barra de progreso, lo último que mostró, aunque la línea pase del recorte; RC3), cortadas a 513 bytes (el byte de más dice que la línea seguía) y, en total, como
        // mucho esto.
        public const int MaxBytesVistaPrevia = LineasVistaPrevia * (MaxBytesLineaVistaPrevia + 2);

        // El texto que se evalúa en el shell en lugar del comando. nonce: el de las marcas (FiltroMarcasGuardado). token: otro
        // aleatorio por petición, para el nombre de las variables del envoltorio. idShell: el del shell que lo ejecuta (su
        // directorio). prefijo: OpcionesNativeSsh.PrefijoGuardado ('/tmp/native_ssh'); el base es prefijo + '-<usuario>' y su
        // padre, el directorio que debe tener el bit sticky.
        // El nonce no está en ninguna variable que el comando pueda ver (ni en el nombre ni en el valor; tampoco sus hijos con
        // 'set -a'): solo en los argumentos de los printf de las marcas. Empieza quitando __ns_k y __ns_b, las variables de
        // ConstructorComando con el texto que se evalúa (lleva el nonce); las del envoltorio llevan el token en el nombre y el
        // comando y la ruta en el valor. Así el comando no puede imitar una marca (y FiltroMarcasGuardado solo aplica las que
        // siguen el orden del envoltorio).
        // El resumen no depende de nada que el comando pueda cambiar en el shell: el nombre de las variables del envoltorio es
        // otro en cada petición (un 'readonly' de un comando anterior no impide asignarlas) y, después del comando, solo se
        // usan para borrarlas (sin error si no se puede); el código de salida y el recuento los saca una subshell con
        // parámetros posicionales (no se pueden hacer de solo lectura) y la ruta vuelta a calcular desde el prefijo, 'id -un' y
        // los dos ids, no la variable con la ruta, que el comando puede reasignar o quitar.
        // Las órdenes internas del envoltorio van con 'builtin' (set, printf, exit, unset, eval, '[', ':', umask, shopt, true,
        // false) y las externas con 'command': una función con su nombre, que dejara un comando anterior, no las sustituye.
        // Una función 'builtin' o 'command' sí: no se protege más (ConstructorComando tampoco). 'builtin' y no 'command' para
        // las internas, para que una función 'command' que solo intercepte una orden externa no cambie además el código ni el
        // recuento. Si la preparación no da la ruta, el envoltorio no ejecuta el comando y lo dice con la marca E 'prep' (que
        // no cuenta si la preparación ya dio su motivo: FiltroMarcasGuardado aplica solo la primera E).
        public static string Envolver(string comando, Guid nonce, Guid token, Guid idShell, string idPeticion, string prefijo)
        {
            string n = nonce.ToString("N");
            string t = token.ToString("N");
            string s = idShell.ToString("N");
            string literal = comando.Replace("'", "'\\''", StringComparison.Ordinal);
            string preparacion = Sustituir(PlantillaPreparacion, n, s, idPeticion, prefijo);
            string resumen = Sustituir(PlantillaResumen, n, s, idPeticion, prefijo);
            return $$"""
                builtin unset __ns_k __ns_b
                __ns_gc{{t}}='{{literal}}'
                if __ns_gf{{t}}=$({{preparacion}}); then
                builtin printf '%s_%s_A_%s__\n' '__NSG' '{{n}}' "$({ builtin set +x +v; } 2>/dev/null; builtin printf '%s' "$__ns_gf{{t}}" | command base64 | command tr -d '\n')"
                { builtin eval "$__ns_gc{{t}}"
                } >>"$__ns_gf{{t}}" 2>&1
                ({{resumen}})
                else
                builtin printf '%s_%s_E_prep__0_0__\n' '__NSG' '{{n}}' >&2
                (builtin exit 1)
                fi
                builtin eval "builtin unset __ns_gc{{t}} __ns_gf{{t}} 2>/dev/null; (builtin exit $?)"
                """.ReplaceLineEndings("\n");
        }

        private static string Sustituir(string plantilla, string n, string s, string idPeticion, string prefijo) => plantilla
            .Replace("@PREFIJO@", Literal(prefijo), StringComparison.Ordinal)
            .Replace("@PADRE@", Literal(Padre(prefijo)), StringComparison.Ordinal)
            .Replace("@N@", n, StringComparison.Ordinal)
            .Replace("@S@", s, StringComparison.Ordinal)
            .Replace("@I@", idPeticion, StringComparison.Ordinal)
            .Replace("@MAXF@", MaxArchivosPorShell.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@MAXB@", MaxBytesPorShell.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        // En lugar de Envolver cuando la sesión ya tiene MaxShellsConGuardado shells con salidas guardadas sin borrar y este
        // shell aún no tiene directorio (SesionShell): no crea nada ni ejecuta el comando; solo la marca E con el tope.
        public static string EnvolverSinCupoShells(Guid nonce, int maxShells) => $$"""
            builtin unset __ns_k __ns_b
            builtin printf '%s_%s_E_shells__%d_0__\n' '__NSG' '{{nonce:N}}' {{maxShells.ToString(CultureInfo.InvariantCulture)}} >&2
            (builtin exit 1)
            """.ReplaceLineEndings("\n");

        // Dentro de una sustitución de órdenes (una subshell): sus opciones y funciones no tocan el shell de la sesión. Escribe
        // la ruta del archivo en su salida o, si algo no cumple, la marca E en el terminal (stderr) y sale con 1. Sin traza
        // ('set -x', 'set -v'), 'set -e', 'set -u', 'set -f', failglob ni nullglob del usuario; LC_ALL=C para leer el modo de
        // 'ls'.
        private static readonly string PlantillaPreparacion = """
            { builtin set +x +v; } 2>/dev/null
            builtin set +e +u +f; builtin shopt -u failglob nullglob 2>/dev/null; builtin unset GLOBIGNORE
            __ns_ge() { builtin printf '%s_%s_E_%s_%s_%s_%s__\n' '__NSG' '@N@' "$1" "$(builtin printf '%s' "${2-}" | command base64 | command tr -d '\n')" "${3:-0}" "${4:-0}" >&2; builtin exit 1; }
            __ns_gk() { builtin [ ! -L "$1" ] && builtin [ -d "$1" ] && builtin [ -O "$1" ] && case $(LC_ALL=C command ls -ld -- "$1" 2>/dev/null) in 'drwx------ '*|'drwx------. '*) builtin true ;; *) builtin false ;; esac; }
            u=$(command id -un 2>/dev/null)
            case $u in ''|*/*|.|..) __ns_ge usuario "$u" ;; esac
            builtin [ -k @PADRE@ ] || builtin [ -O @PADRE@ ] || __ns_ge tmp @PADRE@
            b=@PREFIJO@-$u
            if command mkdir -m 700 -- "$b" 2>/dev/null; then
              command chmod 700 -- "$b" 2>/dev/null
              __ns_gk "$b" || { command rmdir -- "$b" 2>/dev/null && __ns_ge basenuevo "$b" 1; __ns_ge basenuevo "$b" 0; }
            fi
            __ns_gk "$b" || __ns_ge base "$b"
            d=$b/@S@
            if command mkdir -m 700 -- "$d" 2>/dev/null; then
              command chmod 700 -- "$d" 2>/dev/null
              for x in "$b"/*; do
                { builtin [ "$x" != "$d" ] && builtin [ -d "$x" ] && builtin [ ! -L "$x" ]; } || continue
                r=$(command find "$x" -mmin -1440 2>/dev/null) && builtin [ -z "$r" ] && command rm -rf -- "$x"
              done
            fi
            __ns_gk "$d" || __ns_ge sesion "$d"
            n=0; t=0
            for x in "$d"/*; do
              builtin [ -f "$x" ] || continue
              s=$(command wc -c < "$x" 2>/dev/null) || s=0
              n=$((n + 1)); t=$((t + s))
            done
            builtin [ "$n" -lt @MAXF@ ] || __ns_ge archivos "$d" "$n" "$t"
            builtin [ "$t" -lt @MAXB@ ] || __ns_ge bytes "$d" "$n" "$t"
            f=$d/@I@.out
            { (builtin umask 077; builtin set -C; builtin : >"$f") && command chmod 600 -- "$f"; } 2>/dev/null || __ns_ge archivo "$f"
            builtin printf '%s' "$f"
            """.ReplaceLineEndings("\n");

        // Una subshell justo detrás del comando: guarda su código ($? al entrar) en $1, imprime la marca F y sale con él, así que
        // el código del envoltorio es el del comando. Solo números: líneas, bytes y si el archivo no termina en salto (1).
        // Líneas: las de 'wc -l' más una si el archivo no termina en salto (una última línea sin '\n' cuenta, como en la
        // cabecera de ssh_read_file); -1 si no se pudo contar (el archivo ya no estaba). Sin variables: solo parámetros
        // posicionales ($1 código, $2 ruta, $3 bytes, $4 líneas, $5 sin salto final), que el comando no puede dejar de solo
        // lectura (una variable de solo lectura del shell también lo es en la subshell, y asignarla la abortaría).
        private static readonly string PlantillaResumen = """
            builtin set -- "$?"
            { builtin set +x +v; } 2>/dev/null
            builtin set +e +u +f
            builtin set -- "$1" @PREFIJO@-"$(command id -un 2>/dev/null)"/@S@/@I@.out
            builtin set -- "$1" "$2" "$(command wc -c 2>/dev/null <"$2" || builtin printf '%s' -1)" "$(command wc -l 2>/dev/null <"$2" || builtin printf '%s' -1)"
            builtin set -- "$1" "$2" "$(($3))" "$(($4))" 0
            if builtin [ "$3" -gt 0 ] && builtin [ "$4" -ge 0 ] && builtin [ "$(command tail -c 1 "$2" 2>/dev/null | command wc -l)" -eq 0 ]; then builtin set -- "$1" "$2" "$3" "$(($4 + 1))" 1; fi
            builtin printf '%s_%s_F_%d_%d_%d__\n' '__NSG' '@N@' "$4" "$3" "$5"
            builtin exit "$1"
            """.ReplaceLineEndings("\n");

        // Por un canal exec (sin el shell ni el terminal), acotado como ssh_read_file: el principio del archivo para la vista
        // previa. Solo un archivo regular que no sea un enlace (código 3 si no).
        public static string ScriptVistaPrevia(string archivo) => $$"""
            f={{Literal(archivo)}}
            { [ -f "$f" ] && [ ! -L "$f" ]; } || exit 3
            head -c 1048576 -- "$f" | head -n {{LineasVistaPrevia}} | LC_ALL=C sed 's/.*'$'\r''\([^'$'\r'']\)/\1/' | cut -b 1-{{MaxBytesLineaVistaPrevia + 1}} | head -c {{MaxBytesVistaPrevia}}
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
        // directorio propio y no un enlace. Los nombres son hexadecimales (los fija C#). Cuenta las salidas del protocolo
        // (los '<id>.out' regulares de primer nivel, no lo que el comando dejara allí) antes de borrar y, en los directorios
        // que siguen existiendo después (el borrado falló, p. ej. un directorio sin permiso de escritura, o el base sin él:
        // entonces el directorio queda vacío), las que quedan y cuántas otras entradas; lo dice en su última línea
        // ('__ns_archivos=<antes>_<quedan>_<shells que siguen, separados por comas>_<otras>', RecuentoLimpieza; <otras> es -1
        // si algún directorio no se pudo listar). Código 0 si se borró todo, 1 si algún directorio sigue y 2 si no se pudo comprobar nada (no se
        // supo el usuario, o el base no está, es un enlace o no es propio): entonces no borra nada.
        public const int CodigoLimpiezaParcial = 1;
        public const int CodigoLimpiezaSinComprobar = 2;

        public static string ScriptLimpieza(IEnumerable<Guid> idsShell, string prefijo)
        {
            StringBuilder ids = new();
            foreach (Guid id in idsShell)
                _ = ids.Append(' ').Append(id.ToString("N"));

            return $$"""
                c=0
                q=0
                o=0
                k=0
                r=2
                s=
                u=$(id -un 2>/dev/null)
                b={{Literal(prefijo)}}-$u
                if [ -n "$u" ] && [ -d "$b" ] && [ ! -L "$b" ] && [ -O "$b" ]; then
                  r=0
                  for g in{{ids}}; do
                    d=$b/$g
                    { [ -d "$d" ] && [ ! -L "$d" ]; } || continue
                    { [ -r "$d" ] && [ -x "$d" ]; } || k=1
                    for x in "$d"/*.out; do
                      { [ -f "$x" ] && [ ! -L "$x" ]; } && c=$((c + 1))
                    done
                    rm -rf -- "$d" 2>/dev/null
                    if [ -e "$d" ]; then
                      r=1
                      s=$s,$g
                      { [ -r "$d" ] && [ -x "$d" ]; } || k=1
                      for x in "$d"/*.out; do
                        { [ -f "$x" ] && [ ! -L "$x" ]; } && q=$((q + 1)) && o=$((o - 1))
                      done
                      for x in "$d"/* "$d"/.[!.]* "$d"/..?*; do
                        { [ -e "$x" ] || [ -L "$x" ]; } && o=$((o + 1))
                      done
                    fi
                  done
                fi
                [ "$k" = 0 ] || o=-1
                printf '\n__ns_archivos=%d_%d_%s_%d\n' "$c" "$q" "${s#,}" "$o"
                exit $r
                """.ReplaceLineEndings("\n");
        }

        // Lo que contó ScriptLimpieza (su última línea); null si la salida no la trae o no cuadra.
        public static RecuentoLimpieza? LeerRecuentoLimpieza(ReadOnlySpan<byte> salida)
        {
            ReadOnlySpan<byte> etiqueta = "\n__ns_archivos="u8;
            int inicio = salida.LastIndexOf(etiqueta);
            if (inicio < 0)
                return null;

            ReadOnlySpan<byte> resto = salida[(inicio + etiqueta.Length)..];
            int fin = resto.IndexOf((byte)'\n');
            string[] campos = Encoding.ASCII.GetString(fin < 0 ? resto : resto[..fin]).Split('_');
            if (campos.Length != 4
                || !int.TryParse(campos[0], NumberStyles.None, CultureInfo.InvariantCulture, out int contados)
                || !int.TryParse(campos[1], NumberStyles.None, CultureInfo.InvariantCulture, out int quedan)
                || !int.TryParse(campos[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int otros)
                || otros < -1)
                return null;

            List<Guid> siguen = [];
            foreach (string id in campos[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Guid.TryParseExact(id, "N", out Guid shell))
                    return null;
                siguen.Add(shell);
            }

            bool isListado = otros >= 0;
            return new RecuentoLimpieza(contados, isListado ? quedan : null, [.. siguen], isListado ? otros : null);
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
                // Primero como un terminal (RC3: de una barra de progreso queda lo último que mostró) y después el recorte.
                byte[] copia = linea.ToArray();
                int longitud = ConversorTexto.LimpiarAnsi(copia, isComoTerminal: true);
                bool isCortada = longitud > MaxBytesLineaVistaPrevia;
                if (isCortada)
                    longitud = ConversorTexto.AjustarFinUtf8(copia.AsSpan(0, MaxBytesLineaVistaPrevia));
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
            "basenuevo" => $"{NoEjecutado} native_ssh acaba de crear '{ruta}', pero no quedó como un directorio propio con permisos exactos 700: lo habitual es una ACL por defecto en el directorio que lo contiene ('ls -ld' lo marca con '+'), que daría acceso a otros usuarios del servidor. {(n == "1" ? "Se ha vuelto a borrar" : "No se pudo volver a borrar (está vacío: bórralo con 'rmdir')")} y no se ha escrito nada. Lanza el comando sin guardarSalida, o pide al administrador que quite la ACL por defecto de ese directorio ('setfacl -k').",
            "shells" => $"{NoEjecutado} esta sesión ya tiene {n} shells anteriores con salidas guardadas sin borrar (shells que se cerraron tras un plazo agotado, una cancelación o una caída, o cuyo directorio no se pudo borrar con ssh_reset_shell), que es el límite. Lee lo que necesites de esos archivos y usa ssh_reset_shell o ssh_disconnect, que los borran; si un borrado falla (p. ej. un directorio sin permiso de escritura), su respuesta o la del ssh_reset_shell siguiente lo dice: arréglalo ('chmod u+w' sobre ese directorio) y vuelve a usarlos. Después vuelve a lanzar el comando.",
            "sesion" => $"{NoEjecutado} el directorio de este shell, '{ruta}', no es un directorio propio con permisos 700 (se cambió o se sustituyó). No se ha escrito nada. Usa ssh_reset_shell (el shell nuevo usa otro directorio) y vuelve a lanzar el comando.",
            "archivos" or "bytes" => $"{NoEjecutado} este shell ya tiene {n} archivo(s) de salida guardados ({t} bytes) en '{ruta}' y el límite es de {MaxArchivosPorShell} archivos y {MaxBytesPorShell / (1024 * 1024)} MiB por shell. Borra los que ya no necesites (ssh_exec con 'rm -- <ruta>') o usa ssh_reset_shell (borra todos y abre un shell nuevo), y vuelve a lanzarlo.",
            "prep" => $"{NoEjecutado} la preparación del archivo se interrumpió sin dar el motivo, por algo que un comando anterior dejó en el shell (p. ej. una variable de solo lectura con el nombre de una de las suyas). Usa ssh_reset_shell (abre un shell limpio) y vuelve a lanzar el comando.",
            "archivo" => $"{NoEjecutado} no se pudo crear el archivo '{ruta}' con permisos 600 (sin espacio o sin permiso en /tmp, o ya existía: nunca se pisa uno existente).",
            _ => $"{NoEjecutado} no se pudo preparar el archivo de salida ({codigo})."
        };

        private static string Literal(string valor) => "'" + valor.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        private static string Padre(string prefijo)
        {
            int barra = prefijo.TrimEnd('/').LastIndexOf('/');
            return barra <= 0 ? "/" : prefijo[..barra];
        }

        // Sin ninguna marca (ni A, ni E, ni F): algo que un comando anterior dejó en el shell (p. ej. una función 'builtin')
        // interceptó las del envoltorio, y no se sabe si el comando se ejecutó (la E 'prep' dice cuándo no llegó a hacerlo). El
        // archivo, si se creó, está en el directorio de este shell, que ssh_reset_shell borra; el usuario del servidor no se
        // conoce aquí.
        public static string DescribirSinMarcas(string prefijo, Guid idShell, string idPeticion) =>
            $"No se pudo confirmar si el comando se ejecutó: con guardarSalida=true su salida va a un archivo del servidor, y no llegó ninguna de las marcas con que el envoltorio dice la ruta del archivo, el recuento o por qué no lo ejecutó: algo que un comando anterior dejó en el shell las interceptó (p. ej. una función con el nombre 'builtin'). Si se ejecutó, su salida puede estar en '{prefijo}-<usuario>/{idShell:N}/{idPeticion}.out' (<usuario>: el que da 'id -un' en el servidor); compruébalo y léela antes de usar ssh_reset_shell, que la borra. Después usa ssh_reset_shell (abre un shell limpio) y, si no se ejecutó, vuelve a lanzarlo.";

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
