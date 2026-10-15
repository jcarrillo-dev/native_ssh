using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NativeSsh.Comun;
using NativeSsh.Protocolo;
using NativeSsh.Sesion;

namespace NativeSsh.Herramientas
{
    // Lo que se ve del shell de una petición en ejecución cuya entrada se rechazó (HerramientasSsh.EnviarEntradaAsync). Junto
    // con la señal de la sesión (Peticion.IsComandoEnviado) da una sola causa (HerramientasSsh.MensajeEntradaNoEnviada).
    internal enum ShellEntradaEnum
    {
        // Sin sesión del destino, el comando ya no es el que ocupa el shell o su timeout ya venció: causas generales.
        Indeterminado,

        // La sesión no tiene el shell activo: se está abriendo (conexión, canal) o su canal se acaba de cerrar.
        Inactivo,

        // Canal abierto pero sin inicializar, el comando es el que ocupa el shell y su timeout no ha vencido: sin el comando
        // enviado, el shell aún se inicializa; con él enviado, se está interrumpiendo o cerrando.
        ActivoConElComando,

        // Como ActivoConElComando, pero con el shell ya inicializado (SesionShell.IsShellInicializado): sin el comando
        // enviado, el comando acaba de empezar y su envío está en curso (bajo carga puede durar algo).
        InicializadoConElComando
    }

    [McpServerToolType]
    public sealed partial class HerramientasSsh(GestorSesiones gestor, OpcionesNativeSsh opciones)
    {
        public const int MaxSegundosEspera = 120;
        private const int MaxPeticionesEnEstado = 20;
        private const int MaxCaracteresComando = 120;

        // Timeouts de las herramientas de archivo y búsqueda. La herramienta espera el timeout completo
        // (más el margen del Ctrl+C) y, si la petición sigue sin terminar (p. ej. encolada tras otro comando), la cancela.
        private const int TimeoutLecturaSegundos = 60;
        private const int TimeoutEscrituraSegundos = 60;
        private const int TimeoutBusquedaSegundos = 90;
        private const int TimeoutMetadatosSegundos = 30;
        private static readonly TimeSpan MargenEsperaHerramienta = TimeSpan.FromSeconds(2);

        // 'test -e' falla igual si la ruta no existe que si un directorio de la ruta no se puede atravesar (EACCES). Para
        // distinguirlos no se lee ningún mensaje (el de stat lleva la ruta dentro, y una ruta inexistente que contenga
        // "Permission denied" engañaría): se sube por los directorios de la ruta hasta el primero que existe y se mira si
        // se puede atravesar (-x, con el usuario efectivo, ACL incluidas). Un enlace que no se puede seguir se resuelve un
        // nivel (readlink) y se sigue por su destino, hasta 40 saltos. Si un directorio lo impide, sin_acceso lo nombra
        // ($3 es la ruta pedida) y devuelve 0; si no, no imprime nada y devuelve 1 (el script dice entonces que no existe).
        // Las sustituciones conservan un salto de línea final del nombre (ver SustitucionLiteral).
        internal static readonly string DefinicionSinAcceso = """
            sin_acceso() {
              local ruta=$1 saltos=${2:-0} pedida=${3:-$1} padre destino bloqueo
              [ "$saltos" -gt 40 ] && return 1
              [ -e "$ruta" ] && return 1
              padre=$(dirname -- "$ruta"; printf x); padre=${padre%x}; padre=${padre%$'\n'}
              if [ -L "$ruta" ]; then
                destino=$(readlink -- "$ruta"; printf x); destino=${destino%x}; destino=${destino%$'\n'}
                case $destino in
                  /*) ;;
                  *) destino=$padre/$destino ;;
                esac
                sin_acceso "$destino" $(( saltos + 1 )) "$pedida"
                return
              fi
              [ "$padre" = "$ruta" ] && return 1
              if [ -d "$padre" ]; then
                [ -x "$padre" ] && return 1
                bloqueo=$(readlink -f -- "$padre" 2>/dev/null; printf x); bloqueo=${bloqueo%x}; bloqueo=${bloqueo%$'\n'}
                printf 'Error: sin permiso para acceder a la ruta: %s (el directorio %s no se puede atravesar).\n' "$pedida" "${bloqueo:-$padre}" >&2
                return 0
              fi
              sin_acceso "$padre" $(( saltos + 1 )) "$pedida"
            }
            """.ReplaceLineEndings("\n");

        // Exclusión por archivo entre los scripts que lo escriben (ssh_write_file, también con append, y
        // ssh_replace_file_content, en su lectura previa y en su escritura): una restauración en curso tras una interrupción
        // (que sigue sola, ignorando HUP, INT y TERM) no puede pisar ni mezclarse con una escritura posterior.
        // tomar_candado MODO (-x exclusivo, -s compartido): abre "$destino" (ya resuelto) en un descriptor ('candado') que
        // el script mantiene abierto hasta salir, después de su trampa de salida, y toma sobre él un flock sin esperar. Lo
        // abre para leer o, si no puede, para anexar sin truncarlo (flock no depende del modo de apertura): un archivo con
        // permiso de escritura pero no de lectura también se escribe en el sitio (anexado sin sudo mientras una escritura
        // con sudo lo escribe o lo restaura) y necesita el candado. La guarda [ -e ] evita que '>>' cree un archivo que no
        // existe; si desaparece justo entre las dos, '>>' lo crea vacío, que es lo que la escritura haría igualmente. Con
        // 'anexar' no vacío (ssh_write_file con append) lo abre siempre para anexar, creándolo si no existe, y el anexado
        // escribe por ese mismo descriptor: así la comprobación del inodo es la del descriptor por el que se escribe (con
        // un descriptor aparte para anexar, un renombrado encima entre abrir uno y otro dejaba el candado y la comprobación
        // en el archivo nuevo y el anexado en el viejo, ya fuera de la ruta); si no se puede abrir, el error de bash y 1. Deja
        // en 'estado_candado' (lo anuncia la escritura; ver InformeEscritura): 'tomado'; 'sin_comprobar' (tomado, pero no
        // se pudo comprobar que esté sobre el archivo que sigue en la ruta, ver abajo); 'ninguno' (ninguna herramienta pudo
        // tomarlo: no hay flock, python3 ni perl que funcionen, o el sistema de archivos no admite flock) o 'sin_abrir' (no
        // se pudo abrir ni para leer ni para anexar: sin permiso, o el archivo desapareció entretanto). Con 'ninguno' y
        // 'sin_abrir' se sigue sin exclusión; con ellos y con 'sin_comprobar', la respuesta lo avisa. Si otro proceso lo
        // tiene, da el error y devuelve 1 sin haber tocado nada ('estado_candado' queda en 'ocupado'; si el archivo se
        // sustituyó cada vez que se tomaba, en 'sustituido': la lectura de ssh_replace_file_content sale con su código).
        // - No puede quedar huérfano: un flock pertenece a la descripción de archivo abierta y el núcleo lo suelta al
        //   cerrarse su último descriptor, es decir, al terminar el script y los hijos que lo heredan (también por SIGKILL o
        //   tras caer la conexión, cuando el script acaba su restauración). No se crea ningún archivo de candado (no hace
        //   falta permiso de escritura en el directorio ni hay nada que borrar). Los hijos heredan el descriptor a propósito:
        //   un 'cat' que restaura sigue excluyendo aunque maten al bash del script, hasta que termina él.
        // - Ligado al archivo actual: tras tomarlo, mismo_inodo comprueba que el descriptor y "$destino" son el mismo
        //   archivo (dispositivo e inodo); si entretanto se sustituyó (un renombrado encima), se reintenta, porque el
        //   candado de un inodo que ya no está en la ruta no excluye a nadie. Solo se afirma que se sustituyó si la
        //   comparación se hizo y dio distinto. Si no se puede hacer, se conserva el candado ('sin_comprobar') en vez de
        //   soltarlo: excluye igual a la restauración y a las escrituras en el sitio, que no cambian de inodo (el caso para
        //   el que existe), y lo único que queda sin cubrir es un renombrado encima justo entre abrir y bloquear, la misma
        //   ventana que sin comprobar nada; la respuesta lo dice.
        // - mismo_inodo FD: 0 mismo archivo, 1 distinto, 2 no se puede saber. Con /proc, el '-ef' de bash sobre /dev/fd/FD
        //   (bash lo resuelve en /proc/self/fd/FD). Sin /proc (chroot tipo jailkit, contenedores mínimos), '-ef' sería
        //   siempre falso: se compara dispositivo e inodo de los dos con stat de GNU coreutils ('-' es su entrada estándar,
        //   con fstat; otro stat podría tomar '-' por un nombre de archivo), python3 (os.fstat) o perl (stat del
        //   descriptor). Cada herramienta compara por su cuenta y sale con 0 (mismo) o 3 (distinto): cualquier otro código
        //   es que no funcionó, nunca «distinto». Que la ruta no exista (ENOENT) es «distinto»; cualquier otro fallo al
        //   consultarla (p. ej. sin permiso para atravesar un directorio) es «no se sabe». Por eso un '-ef' falso solo es
        //   «distinto» si la ruta existe; si no, decide la siguiente vía (el '-e' de bash no distingue ENOENT de EACCES), y
        //   sin ninguna, 2: una ruta borrada justo al tomar el candado, en un servidor con /proc pero sin stat de GNU,
        //   python3 ni perl, queda como 'sin_comprobar' en vez de reintentarse. stat de GNU se ejecuta con LC_ALL=C para
        //   reconocer ENOENT por su mensaje.
        // - bloquear FD MODO: flock(1) de util-linux con -E 75 para distinguir «ocupado» (75) de «no funciona» (otro
        //   código); si no, python3 o perl, que toman el flock sobre el descriptor heredado (el candado queda en la
        //   descripción compartida con el bash del script al salir ellos). Una herramienta que no funciona nunca cuenta como
        //   ocupado: se prueba la siguiente. Como último recurso, un flock sin -E (el de busybox, en Alpine sin python3 ni
        //   perl), que sale con 1 tanto si está ocupado como si no funciona: se distinguen porque ocupado no escribe nada,
        //   y solo se usa si antes, una vez por script, sobre un temporal propio, ha tomado un candado libre (0) y, con ese
        //   candado tomado por otra apertura, ha salido con 1 sin escribir nada. Si el sistema de archivos del destino no
        //   admite flock, ese flock escribe su error y cuenta como «no funciona».
        // - Limitaciones: otro programa que tome un flock sobre el archivo también impide escribirlo (el error lo dice). En
        //   NFS, flock se emula con candados fcntl (POSIX, por proceso): el exclusivo exige un descriptor abierto para
        //   escribir, así que con el de solo lectura falla como «no funciona» y se sigue sin exclusión ('ninguno', con el
        //   aviso), y el candado no se hereda igual entre procesos. Abrir el archivo para tomar el candado se ve en inotify
        //   aunque no se escriba nada: OPEN y CLOSE_NOWRITE con el descriptor de lectura, y OPEN y CLOSE_WRITE si se abrió
        //   para anexar (archivo con escritura pero sin lectura, p. ej. 0200, o el anexado), lo que puede despertar a un
        //   programa que vigile el archivo esperando escrituras.
        internal static readonly string DefinicionCandado = """
            bloquear() {
              local codigo
              flock -n "$2" -E 75 "$1" 2>/dev/null
              codigo=$?
              { [ "$codigo" = 0 ] || [ "$codigo" = 75 ]; } && return "$codigo"
              python3 -c 'if 1:
                import fcntl, sys
                modo = fcntl.LOCK_SH if sys.argv[2] == "-s" else fcntl.LOCK_EX
                try:
                    fcntl.flock(int(sys.argv[1]), modo | fcntl.LOCK_NB)
                except BlockingIOError:
                    sys.exit(75)' "$1" "$2" 2>/dev/null
              codigo=$?
              { [ "$codigo" = 0 ] || [ "$codigo" = 75 ]; } && return "$codigo"
              perl -MFcntl=:flock -e 'open(my $f, "<&=", $ARGV[0]) or exit 2; flock($f, ($ARGV[1] eq "-s" ? LOCK_SH : LOCK_EX) | LOCK_NB) and exit 0; exit($!{EWOULDBLOCK} ? 75 : 2)' "$1" "$2" 2>/dev/null
              codigo=$?
              { [ "$codigo" = 0 ] || [ "$codigo" = 75 ]; } && return "$codigo"
              bloquear_sin_e "$1" "$2"
            }
            bloquear_sin_e() {
              local prueba uno dos error codigo
              if [ -z "${flock_sin_e:-}" ]; then
                flock_sin_e=no
                if prueba=$(mktemp 2>/dev/null); then
                  if { exec {uno}<"$prueba" {dos}<"$prueba"; } 2>/dev/null; then
                    if flock -n -x "$uno" 2>/dev/null; then
                      error=$(flock -n -x "$dos" 2>&1)
                      codigo=$?
                      [ "$codigo" = 1 ] && [ -z "$error" ] && flock_sin_e=si
                    fi
                    exec {uno}<&- {dos}<&-
                  fi
                  rm -f -- "$prueba"
                fi
              fi
              [ "$flock_sin_e" = si ] || return 2
              error=$(flock -n "$2" "$1" 2>&1)
              codigo=$?
              [ "$codigo" = 0 ] && return 0
              [ "$codigo" = 1 ] && [ -z "$error" ] && return 75
              return 2
            }
            mismo_inodo() {
              local del_fd de_ruta
              if [ -e "/dev/fd/$1" ]; then
                [ "/dev/fd/$1" -ef "$destino" ] && return 0
                [ -e "$destino" ] && return 1
              fi
              case $(stat --version 2>/dev/null) in
                *'GNU coreutils'*)
                  if del_fd=$(stat -L -c '%d:%i' - <&"$1" 2>/dev/null) && [ -n "$del_fd" ]; then
                    if de_ruta=$(LC_ALL=C stat -L -c '%d:%i' -- "$destino" 2>&1); then
                      [ "$del_fd" = "$de_ruta" ]
                      return
                    fi
                    case $de_ruta in
                      *': No such file or directory') return 1 ;;
                    esac
                  fi ;;
              esac
              python3 -c 'if 1:
                import os, sys
                fd = os.fstat(int(sys.argv[1]))
                try:
                    ruta = os.stat(sys.argv[2])
                except FileNotFoundError:
                    sys.exit(3)
                sys.exit(0 if (fd.st_dev, fd.st_ino) == (ruta.st_dev, ruta.st_ino) else 3)' "$1" "$destino" 2>/dev/null
              case $? in
                0) return 0 ;;
                3) return 1 ;;
              esac
              perl -e 'open(my $f, "<&=", $ARGV[0]) or exit 2; my @fd = stat($f) or exit 2; my @ruta = stat($ARGV[1]) or exit($!{ENOENT} ? 3 : 2); exit($fd[0] == $ruta[0] && $fd[1] == $ruta[1] ? 0 : 3)' "$1" "$destino" 2>/dev/null
              case $? in
                0) return 0 ;;
                3) return 1 ;;
              esac
              return 2
            }
            tomar_candado() {
              local intento
              estado_candado=
              for intento in 1 2 3; do
                if [ -n "${anexar:-}" ]; then
                  { exec {candado}>>"$destino"; } || return 1
                elif ! { exec {candado}<"$destino"; } 2>/dev/null; then
                  [ -e "$destino" ] && { exec {candado}>>"$destino"; } 2>/dev/null || { estado_candado=sin_abrir; return 0; }
                fi
                bloquear "$candado" "$1"
                case $? in
                  0)
                    mismo_inodo "$candado"
                    case $? in
                      0) estado_candado=tomado; return 0 ;;
                      2) estado_candado=sin_comprobar; return 0 ;;
                    esac ;;
                  75)
                    estado_candado=ocupado
                    printf 'Error: hay otra escritura, reemplazo o restauración en curso sobre %s (otro proceso tiene tomado su candado, flock: otra operación sobre el archivo o un programa que lo tiene abierto con candado, como una aplicación .NET). No se ha modificado: reintenta cuando termine.\n' "$ruta" >&2
                    return 1 ;;
                  *) estado_candado=ninguno; return 0 ;;
                esac
                exec {candado}<&-
              done
              estado_candado=sustituido
              printf 'Error: %s se sustituyó por otro archivo cada vez que se tomaba su candado. No se ha modificado: reintenta.\n' "$ruta" >&2
              return 1
            }
            """.ReplaceLineEndings("\n");

        // Una FIFO, un socket o un dispositivo no se sustituyen por un archivo regular ni se abren: una FIFO sin lector
        // bloquearía hasta el timeout, /dev/zero no se acaba y en /dev/null se "escribiría" sin guardar nada.
        private const string ErrorNoRegular = """printf 'Error: %s no es un archivo regular (es una FIFO, un socket o un dispositivo): no se lee ni se escribe en él. No se ha modificado.\n' "$ruta" >&2; exit 1""";
        private static readonly UTF8Encoding Utf8Estricto = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // ssh_replace_file_content lee el archivo con 'base64', que parte en líneas de 76 caracteres + '\n':
        // cada línea de 77 bytes de salida codifica 57 bytes del archivo. Se reserva un margen para avisos (sudo, stderr).
        // Con MaxBytesSalidaPorPeticion por defecto (1 MiB) el archivo editable más grande es de 773 184 bytes (≈755 KiB).
        internal const int MargenSalidaBase64 = 4096;
        private const int BytesLineaBase64 = 77;
        private const int BytesArchivoPorLineaBase64 = 57;

        internal static long CalcularMaxBytesArchivoEditable(int maxBytesSalida)
        {
            long disponible = (long)maxBytesSalida - MargenSalidaBase64;
            if (disponible <= 0)
                return 0;

            // La última línea incompleta admite grupos de 4 caracteres (3 bytes) más su '\n'.
            long resto = disponible % BytesLineaBase64;
            long gruposFinales = resto > 0 ? (resto - 1) / 4 : 0;
            return (disponible / BytesLineaBase64 * BytesArchivoPorLineaBase64) + (gruposFinales * 3);
        }

        // Solo para pruebas: sustituye el timeout de todas las herramientas de archivo y búsqueda.
        internal TimeSpan? TimeoutForzado { get; init; }

        private const string AvisoGrepDisco = "Sugerencia de rendimiento: Has ejecutado 'grep' sobre el sistema de archivos vía ssh_exec. Utiliza la herramienta especializada 'ssh_search' para excluir automáticamente binarios (.pyc), directorios pesados (.venv, node_modules) y optimizar el consumo de contexto.";
        private const string AvisoFindDisco = "Sugerencia de rendimiento: Has ejecutado 'find' vía ssh_exec. Utiliza la herramienta especializada 'ssh_find' para localizar archivos por nombre/glob (y por tamaño con tamanoMin/tamanoMax, con el tamaño de cada uno y de mayor a menor) o 'ssh_list_dir' para explorar la topología de carpetas con poda automática.";
        private const string AvisoCdEncadenado = "Sugerencia de rendimiento: Has encadenado 'cd <ruta> &&' en ssh_exec. Utiliza 'ssh_set_cwd(destino, ruta)' para fijar el directorio de trabajo persistente de forma automática y operar con rutas relativas limpias.";

        [McpServerTool(Name = "ssh_exec", Destructive = true, OpenWorld = true)]
        [Description("Ejecuta comandos bash de sistema, compilación, despliegue o gestión de servicios y procesos (systemctl, pm2, git, docker, tests, curl). Admite tuberías para filtrar salidas de procesos (ej. 'pm2 list | grep online'). NO USAR para buscar patrones o cadenas en archivos (usa ssh_search), para localizar archivos (usa ssh_find), para explorar estructura de directorios (usa ssh_list_dir), para leer archivos (usa ssh_read_file) ni para editar (usa ssh_replace_file_content / ssh_write_file). Para fijar el directorio de trabajo continuo usa ssh_set_cwd. Si la salida puede ser grande (consultas SQL, logs, volcados, listados largos) usa SIEMPRE guardarSalida=true y después ssh_read_file o ssh_search sobre el archivo. La respuesta trae dos bloques: primero el control (JSON en una línea: id, estado, codigoSalida, siguienteByte, hasMasSalida, aviso; con guardarSalida, también archivo, lineas y bytes) y después la salida del comando tal cual (con guardarSalida, solo sus 5 primeras líneas).")]
        public async Task<CallToolResult> EjecutarAsync(
            [Description("usuario@servidor[:puerto]; el servidor puede ser un alias de ~/.ssh/config. Sin usuario se usa el User de ~/.ssh/config o, si no lo hay, el usuario local.")] string destino,
            [Description("Comando bash. Admite varias líneas, heredocs y comillas.")] string comando,
            [Description("Tiempo máximo de ejecución; al agotarse se envía Ctrl+C. Entre 1 y 3600.")] int timeoutSegundos = 300,
            [Description("Cuánto esperar el resultado antes de responder. Entre 0 y 120.")] int esperarSegundos = 20,
            [Description(DescripcionGuardarSalida)] bool guardarSalida = false,
            CancellationToken cancellationToken = default)
        {
            await ExigirEscrituraPermitidaAsync(gestor.ParsearDestino(destino), "ssh_exec", cancellationToken).ConfigureAwait(false);
            Peticion peticion = gestor.Ejecutar(destino, comando, TimeSpan.FromSeconds(timeoutSegundos), isGuardarSalida: guardarSalida, herramientaEscritura: "ssh_exec");
            await EsperarAsync(peticion.Finalizacion, esperarSegundos, cancellationToken).ConfigureAwait(false);
            return DosBloques(Resultado(peticion, 0));
        }

        private const string DescripcionGuardarSalida = "Pon true SIEMPRE que la salida pueda ser grande (consultas SQL, logs, volcados, listados largos): la salida completa (stdout y stderr, sin el límite de 1 MiB) se guarda en un archivo del servidor (/tmp/native_ssh-<usuario>/…, solo legible por ese usuario) y la respuesta trae solo el control (estado, codigoSalida, archivo, lineas, bytes) y las 5 primeras líneas. Después lee el archivo por tramos con ssh_read_file o busca en él con ssh_search; NUNCA lo vuelques entero con cat. NO lo uses con comandos interactivos: lo que el comando pregunte por su salida va al archivo y no lo verás (la contraseña de sudo sí se ve: va por el terminal). Los archivos se borran con ssh_reset_shell, ssh_disconnect o al cerrarse la sesión (un shell nuevo que se abra solo, tras un plazo agotado, una cancelación o una caída, no los borra); como mucho 100 archivos o 256 MiB por shell (por encima, el comando no se ejecuta y la respuesta lo dice).";

        [McpServerTool(Name = "ssh_output", ReadOnly = true, Idempotent = true)]
        [Description("Lee la salida de un comando a partir de 'desdeByte' (usa el 'siguienteByte' de la respuesta anterior). Si sigue en ejecución, espera hasta 'esperarSegundos' a que termine. Responde con los mismos dos bloques que ssh_exec: el control (JSON) y la salida tal cual.")]
        public async Task<CallToolResult> LeerSalidaAsync(
            [Description("Id devuelto por ssh_exec.")] string id,
            [Description("Posición desde la que leer.")] long desdeByte = 0,
            [Description("Cuánto esperar a que termine. Entre 0 y 120.")] int esperarSegundos = 0,
            CancellationToken cancellationToken = default)
        {
            Peticion peticion = gestor.BuscarPeticion(id);
            if (peticion.Herramienta is { } herramienta)
                throw new NativeSshException(MensajePeticionInterna(peticion, herramienta, "ssh_output no da su salida"));

            await EsperarAsync(peticion.Finalizacion, esperarSegundos, cancellationToken).ConfigureAwait(false);
            return DosBloques(Resultado(peticion, desdeByte));
        }

        [McpServerTool(Name = "ssh_input", Destructive = true)]
        [Description("Envía texto a la entrada estándar del comando en ejecución (respuesta a una pregunta, contraseña de sudo, 'y/n'...). Devuelve la salida producida desde el envío. Para contraseñas, tokens y cualquier otro secreto usa SIEMPRE secreto=true. Envía la entrada SOLO cuando el comando la esté pidiendo (mira su salida con ssh_output) y UNA línea por llamada. Lo que el comando no lea (líneas de más, Ctrl+D, o texto enviado antes de que lo pida o cuando ya no lee) NUNCA se ejecuta como orden: se DESCARTA entero y la respuesta del SIGUIENTE ssh_exec de este shell dice exactamente cuántas líneas se descartaron. Esa entrada se pierde: si el comando la necesitaba, vuelve a lanzarlo y envíala cuando la pida. NUNCA envíes Ctrl+C ('\\u0003'), Ctrl+\\ ('\\u001c') ni Ctrl+Z ('\\u001a'): se rechazan sin enviar nada; para interrumpir el comando usa SIEMPRE ssh_cancel.")]
        public async Task<CallToolResult> EnviarEntradaAsync(
            [Description("Id del comando en ejecución.")] string id,
            [Description("Texto a enviar. Obligatorio: para un Enter a secas, \"\" con enter=true.")] string texto,
            [Description("Añadir un salto de línea al final.")] bool enter = true,
            [Description("Cuánto esperar a que termine después de enviar. Entre 0 y 120.")] int esperarSegundos = 2,
            [Description("Pon true SIEMPRE que el texto sea una contraseña, un token o cualquier otro secreto: native_ssh no lo escribe en sus logs y, si aparece en la salida del comando (eco del terminal o un programa que lo imprime tal cual), lo sustituye por asteriscos en esta respuesta y en ssh_output. Solo reconoce el texto exacto: un eco transformado (p. ej. un programa que lo reescribe o lo recodifica) no se oculta, y si el programa intercala secuencias de control dentro del eco del secreto (colores o movimientos ANSI, BEL, retornos de carro), esas secuencias se quitan de la respuesta y el secreto puede llegar entero y limpio: no le pidas a un programa que muestre un secreto. Con secreto=true el texto debe ser UNA sola línea de caracteres imprimibles, SIN NINGÚN carácter de control (ni '\\n' o '\\r' en medio, ni tabulador, Escape, Ctrl+C, Ctrl+D...): si no, se rechaza sin enviar nada.")] bool secreto = false,
            CancellationToken cancellationToken = default)
        {
            Peticion peticion = gestor.BuscarPeticion(id);
            // El SDK enlaza un null tal cual (ValidadorArgumentos.IsDelTipo) y enviar "nada" con enter=true es un Enter a secas,
            // que acepta el valor por defecto de una pregunta como «[S/n]»: un null no responde nada, hay que pedirlo con "".
            // Antes que nada más (con secreto=true, el texto se codifica para registrarlo).
            if (texto is null)
                throw new NativeSshException("'texto' llegó como null: no se ha enviado nada y el comando sigue esperando. Para enviar un Enter a secas (p. ej. aceptar el valor por defecto de una pregunta), pasa texto: \"\" con enter: true.");

            // Un secreto con caracteres de control: lo que el comando no lea se descarta sin ejecutarse
            // (ConstructorComando.EscribirCola), pero un programa que lee línea a línea podría tomar una segunda línea como otra
            // respuesta y mostrarla o registrarla fuera del enmascarado, y un control que el terminal interpreta (Ctrl+C, Ctrl+U,
            // Ctrl+V...) cambia lo que llega, que ya no es el texto registrado. Se rechaza antes de registrarlo y de enviar nada.
            if (secreto && IsSecretoNoAdmitido(texto, enter))
                throw new NativeSshException(MensajeSecretoNoAdmitido);

            if (DescribirEntradaNoAdmitida(texto, enter) is { } rechazo)
                throw new NativeSshException(rechazo);

            await ExigirEscrituraPermitidaAsync(peticion.Destino, "ssh_input", cancellationToken).ConfigureAwait(false);
            // El secreto se registra antes de enviarlo: su eco llega después. Si la entrada se rechaza, queda registrado
            // (inocuo: solo enmascara). Nada de este método ni de la sesión pone el texto en un mensaje de error.
            if (secreto)
            {
                byte[] bytesSecreto = Encoding.UTF8.GetBytes(texto);
                try
                {
                    peticion.Salida.RegistrarSecreto(bytesSecreto);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytesSecreto);
                }
            }

            long desde = peticion.Salida.Longitud;
            // Se mira antes de enviar y otra vez tras el rechazo: si el shell se activa justo entre los dos, la entrada se
            // rechazó igualmente porque todavía se estaba abriendo. La señal del comando enviado se lee antes de intentarlo:
            // la sesión admite la entrada en cuanto el comando está escrito en el canal (incluso antes de marcar la señal),
            // así que un rechazo con la señal aún sin marcar es de un comando que todavía no se había enviado.
            bool isComandoEnviado = peticion.IsComandoEnviado;
            ShellEntradaEnum antes = ObservarShell(peticion);
            if (!gestor.EnviarEntrada(peticion, texto, enter, secreto))
            {
                ShellEntradaEnum despues = ObservarShell(peticion);
                throw new NativeSshException(MensajeEntradaNoEnviada(peticion, antes == ShellEntradaEnum.Inactivo ? antes : despues, isComandoEnviado));
            }

            await EsperarAsync(peticion.Finalizacion, esperarSegundos, cancellationToken).ConfigureAwait(false);
            // A una petición interna (p. ej. la contraseña que pide sudo en una escritura con sudo=true) la entrada se le envía,
            // pero la respuesta no lleva su salida (MensajePeticionInterna).
            if (peticion.Herramienta is { } herramienta)
                return new CallToolResult { Content = [new TextContentBlock { Text = MensajePeticionInterna(peticion, herramienta, $"entrada enviada{(secreto ? " como secreto" : string.Empty)} (estado: {peticion.Estado}), pero su salida no se muestra") }] };

            ResultadoComando resultado = Resultado(peticion, desde);
            return DosBloques(secreto ? resultado with { Aviso = resultado.Aviso is null ? AvisoEntradaSecreta : $"{resultado.Aviso} {AvisoEntradaSecreta}" } : resultado);
        }

        internal const string MensajeSecretoNoAdmitido = "No se envió la entrada: con secreto=true el texto debe ser UNA sola línea de caracteres imprimibles, sin ningún carácter de control (0x00-0x1F y 0x7F: saltos de línea '\\n' o '\\r' en medio, tabulador, Escape, Ctrl+C, Ctrl+D, Ctrl+U, Ctrl+V...). El salto final lo añade enter=true (con enter=false se admite un '\\n' o un '\\r' al final). Envía cada línea en su propia llamada a ssh_input, cuando el comando la pida, y los caracteres de control sin secreto=true.";

        // Con secreto=true, cualquier control C0 o DEL (CV6): saltos de línea, Ctrl+D (VEOF), Ctrl+C, Ctrl+U, Ctrl+V, NUL...
        // Con enter=false se admite un '\n' o un '\r' al final (el salto que no añade la herramienta).
        internal static bool IsSecretoNoAdmitido(ReadOnlySpan<char> texto, bool isEnter)
        {
            if (!isEnter && !texto.IsEmpty && texto[^1] is '\n' or '\r')
                texto = texto[..^1];
            return texto.IndexOfAnyInRange('\u0000', '\u001f') >= 0 || texto.Contains('\u007f');
        }

        internal const string MensajeSenalDelTerminal = "No se envió la entrada: lleva Ctrl+C ('\\u0003'), Ctrl+\\ ('\\u001c') o Ctrl+Z ('\\u001a'), y ssh_input NUNCA los envía: el terminal los convierte en señales y vacía lo pendiente, lo que puede abortar la línea con que el shell de ssh_exec espera el final del comando o borrar el comando antes de que el shell lo lea, y entonces lo que se enviara después se ejecutaría como orden (Ctrl+Z, además, dejaría el proceso detenido: no hay control de trabajos). Para interrumpir el comando usa ssh_cancel.";

        internal const string MensajeLnextFinal = "No se envió la entrada: termina en Ctrl+V ('\\u0016', VLNEXT) con enter=false, y el terminal tomaría como un carácter literal lo siguiente que le llegue, también el Ctrl+C con que ssh_cancel interrumpe el comando. Pon el Ctrl+V junto al carácter que debe acompañar, en la misma llamada.";

        // Lo que ssh_input no envía nunca, aunque no sea un secreto (null si se admite; el mensaje nunca lleva el texto):
        // - Ctrl+C (VINTR), Ctrl+\ (VQUIT) y Ctrl+Z (VSUSP), en cualquier sitio. Medido (tramo 1-quinquies del C): el comando
        //   va dentro de un 'eval' en la misma línea que su cola, y Ctrl+C aborta la línea entera, cola incluida, así que lo
        //   tecleado después lo ejecutaba bash como orden (y un secreto detrás salía sin enmascarar en el error de bash). Una
        //   trampa 'trap : INT' mantiene viva la línea, pero entonces ni Ctrl+C ni ssh_cancel paran un bucle del propio bash; un
        //   manejador que imprime el marcador y se manda SIGINT a sí mismo ('kill -INT $$') no aborta lo que hay dentro del
        //   'eval' (bash pierde la señal recibida durante una trampa) y ssh_cancel dejaba de parar los bucles. Además, los tres
        //   vacían la entrada pendiente del terminal: enviados justo después de ssh_exec, antes de que bash lea el envío
        //   entero, lo borran y lo tecleado después se ejecuta como orden (medido con Ctrl+C). Con Ctrl+Z, sin control de
        //   trabajos, el proceso queda además detenido. Ctrl+\ por sí solo no aborta la línea (bash ignora SIGQUIT; medido),
        //   pero comparte la carrera del vaciado. La interrupción la hace ssh_cancel (Ctrl+C y una línea de recuperación
        //   aparte, SesionShell.InterrumpirAsync). Coste: ssh_input no puede interrumpir un intérprete interactivo (python,
        //   psql) sin terminar el comando.
        // - Ctrl+V (VLNEXT) como último carácter enviado (enter=false): el terminal tomaría literal lo siguiente que llegue, y
        //   si es el Ctrl+C de ssh_cancel, no lo convertiría en señal.
        internal static string? DescribirEntradaNoAdmitida(ReadOnlySpan<char> texto, bool isEnter) =>
            texto.IndexOfAny('\u0003', '\u001c', '\u001a') >= 0 ? MensajeSenalDelTerminal
            : !isEnter && !texto.IsEmpty && texto[^1] == '\u0016' ? MensajeLnextFinal
            : null;

        internal const string AvisoEntradaSecreta = "Entrada enviada como secreto: no se ha escrito en los logs y sus apariciones en la salida (p. ej. el eco del terminal) se sustituyen por asteriscos.";

        // Lo que la sesión del destino deja ver de su shell respecto a una petición en ejecución (ver ShellEntradaEnum). Si el
        // comando ya se envió al shell lo dice la petición (Peticion.IsComandoEnviado).
        private ShellEntradaEnum ObservarShell(Peticion peticion)
        {
            foreach (SesionShell sesion in gestor.ListarSesiones())
            {
                if (sesion.Destino.ClaveSesion != peticion.Destino.ClaveSesion)
                    continue;

                bool isConElComando = ReferenceEquals(sesion.PeticionActual, peticion) && peticion.TiempoEjecucion < peticion.Timeout;
                return !sesion.IsShellActivo
                    ? ShellEntradaEnum.Inactivo
                    : !isConElComando
                        ? ShellEntradaEnum.Indeterminado
                        : sesion.IsShellInicializado ? ShellEntradaEnum.InicializadoConElComando : ShellEntradaEnum.ActivoConElComando;
            }

            return ShellEntradaEnum.Indeterminado;
        }

        // Por qué no se envió la entrada, con lo que la petición y su sesión dejan ver tras el rechazo (sin afirmar que el
        // comando no admite entrada: un comando en ejecución casi siempre la admite). La sesión rechaza la entrada si la
        // petición no está en ejecución, mientras su shell se abre o se inicializa (la petición ya está en ejecución, pero
        // el comando aún no se ha enviado al shell), durante una interrupción (Ctrl+C o el cierre del canal que la sigue), si
        // ya no es la que ocupa el shell o si no hay sesión. Un fallo de la escritura en el canal no es un rechazo: la sesión
        // lanza su propio error (pudo llegar una parte). Desde fuera se distinguen el estado, si se pidió cancelarla, si el
        // comando ya se había enviado al shell (isComandoEnviado, leída antes de intentarlo) y lo que se ve del shell
        // (ShellEntradaEnum): sin el comando enviado, el shell se está abriendo o inicializando (o, ya inicializado, el envío
        // del comando que acaba de empezar está en curso); con él enviado, su canal se cerró después o se está interrumpiendo
        // o cerrando (lo que queda se enumera).
        internal static string MensajeEntradaNoEnviada(Peticion peticion, ShellEntradaEnum shell, bool isComandoEnviado) => peticion.Estado switch
        {
            EstadoPeticionEnum.Encolado => $"No se envió la entrada: el comando {peticion.IdTexto} sigue encolado, esperando su turno en el shell de {peticion.Destino.Clave}. Envíala cuando esté en ejecución (ssh_status).",
            EstadoPeticionEnum.Ejecutando when peticion.IsCancelacionSolicitada => $"No se envió la entrada: el comando {peticion.IdTexto} se está cancelando (Ctrl+C enviado o cierre del canal del shell en curso, por ssh_cancel o ssh_reset_shell) y mientras tanto el shell no admite entrada.",
            EstadoPeticionEnum.Ejecutando when shell == ShellEntradaEnum.Inactivo && !isComandoEnviado => $"No se envió la entrada al comando {peticion.IdTexto}, que sigue en ejecución: el shell de {peticion.Destino.Clave} todavía se está abriendo (conexión, inicio de sesión o inicialización del shell) y el comando aún no se le ha enviado. {ReintentoTrasApertura}",
            EstadoPeticionEnum.Ejecutando when shell == ShellEntradaEnum.Inactivo => $"No se envió la entrada al comando {peticion.IdTexto}, que sigue en ejecución: el canal del shell de {peticion.Destino.Clave} se acaba de cerrar o se perdió la conexión después de enviarle el comando: el comando terminará como shell perdido y el siguiente abrirá un shell nuevo.",
            EstadoPeticionEnum.Ejecutando when shell == ShellEntradaEnum.InicializadoConElComando && !isComandoEnviado => $"No se envió la entrada al comando {peticion.IdTexto}, que sigue en ejecución: el shell de {peticion.Destino.Clave} ya está abierto e inicializado, pero el comando acaba de empezar y todavía no se le ha enviado. Reintenta en un momento; si sigue fallando, comprueba su estado con ssh_output o ssh_status.",
            EstadoPeticionEnum.Ejecutando when shell == ShellEntradaEnum.ActivoConElComando && !isComandoEnviado => $"No se envió la entrada al comando {peticion.IdTexto}, que sigue en ejecución: el shell de {peticion.Destino.Clave} tiene el canal abierto, pero aún se está inicializando (primer comando del destino, tras ssh_reset_shell o con un inicio de sesión lento) y el comando todavía no se le ha enviado. {ReintentoTrasApertura}",
            EstadoPeticionEnum.Ejecutando => $"No se envió la entrada al comando {peticion.IdTexto}, que sigue en ejecución: el shell no la admitió en este momento (se está interrumpiendo por su timeout, la sesión se está cerrando con ssh_disconnect o ya no es el comando que ocupa el shell). Comprueba su estado con ssh_output o ssh_status antes de reintentar.",
            EstadoPeticionEnum.Completado or EstadoPeticionEnum.Cancelado or EstadoPeticionEnum.TiempoAgotado or EstadoPeticionEnum.ShellPerdido or EstadoPeticionEnum.Error => $"No se envió la entrada: el comando {peticion.IdTexto} ya terminó (estado {peticion.Estado}).",
            _ => $"No se envió la entrada: el comando {peticion.IdTexto} está en un estado desconocido ({peticion.Estado})."
        };

        private const string ReintentoTrasApertura = "Reintenta en unos segundos (ssh_status muestra isShellActivo=true en cuanto el canal está abierto, aunque la inicialización puede tardar algo más); si sigue fallando, comprueba su estado con ssh_output o ssh_status.";

        [McpServerTool(Name = "ssh_cancel", Destructive = true, Idempotent = true)]
        [Description("Cancela un comando: si está encolado no llega a ejecutarse; si está en ejecución se envía Ctrl+C y, si no responde, se cierra el canal del shell (se pierden las variables exportadas).")]
        public async Task<CallToolResult> CancelarAsync(
            [Description("Id del comando.")] string id,
            CancellationToken cancellationToken = default)
        {
            Peticion peticion = gestor.BuscarPeticion(id);
            _ = peticion.SolicitarCancelacion();
            await EsperarAsync(peticion.Finalizacion, (int)Math.Ceiling(opciones.EsperaTrasCtrlC.TotalSeconds) + 2, cancellationToken).ConfigureAwait(false);
            return peticion.Herramienta is { } herramienta
                ? new CallToolResult { Content = [new TextContentBlock { Text = MensajePeticionInterna(peticion, herramienta, $"se pidió cancelarla (estado: {peticion.Estado}), pero su salida no se muestra") }] }
                : DosBloques(Resultado(peticion, 0));
        }

        // ssh_output, ssh_input y ssh_cancel sobre el id de una petición interna de una herramienta (Peticion.Herramienta): de
        // qué herramienta es y que su resultado lo da ella, nunca su salida (Bloque ST). ssh_input y ssh_cancel actúan igual
        // que con un comando (entrada, p. ej. la contraseña de sudo de una escritura con sudo=true; cancelación); el aviso de la petición sí (p. ej. el
        // de una escritura atascada en un servidor que no lee: solo ssh_disconnect la corta), que no lleva nada del archivo.
        internal static string MensajePeticionInterna(Peticion peticion, string herramienta, string accion) =>
            $"La petición {peticion.IdTexto} es interna de {herramienta} («{Recortar(peticion.Descripcion)}»): {accion}. Su resultado lo da la respuesta de {herramienta} (lo que hizo o, si falló, su error), no esta herramienta.{(peticion.Aviso is { Length: > 0 } aviso ? " " + aviso : string.Empty)}";

        [McpServerTool(Name = "ssh_status", ReadOnly = true, Idempotent = true)]
        [Description("Estado de las sesiones abiertas y de los últimos comandos (sin su salida; de las herramientas que van por el shell, «[herramienta] ruta», una entrada por llamada). isSinPerfil=true: los shells de esa sesión se abren sin ~/.profile ni ~/.bashrc (ssh_reset_shell con sinPerfil=true). Si hay destinos en modo solo lectura (configuración local NATIVE_SSH_SOLO_LECTURA, que no se puede cambiar desde aquí), 'soloLectura' lista sus entradas ('resolucionesSinTerminar', si aparece, los procesos que resolvían sus nombres y no murieron al matarlos: díselo al usuario) y cada sesión lleva isSoloLectura: con true, ssh_exec, ssh_input, ssh_write_file y ssh_replace_file_content se rechazan para ese destino, o al menos para el alias que lo hace de solo lectura, mientras la configuración de ssh de ese alias no cambie (usa las de lectura). Si cambió con la sesión abierta (otro HostName, User, Port o IdentityFile), una escritura con ese alias usa la sesión que corresponde a la configuración nueva y su resultado puede ser otro: vale lo que responda la herramienta de escritura, que lo comprueba en cada llamada. Si la configuración de ssh de ese alias pudo cambiar con la sesión abierta, NO te fíes de isSoloLectura para ese alias: atente a lo que responda la herramienta de escritura y, si la rechaza, no reintentes ni busques otra vía. Cuentan el destino de la sesión, la dirección a la que está conectada y sus alias (con un alias en 'destino', solo ese; sin 'destino', todos los alias con que se usó la sesión): una sesión conectada a una máquina de solo lectura lo sigue siendo aunque el nombre apunte ya a otra. Con usuario@servidor[:puerto] en 'destino' no cuenta ningún alias y solo lleva isSoloLectura la sesión que usaría una escritura con ese mismo texto (la de las claves de ese destino): dice exactamente si esa escritura se rechaza. Las otras sesiones listadas de ese destino son de otras claves (de un alias con otro IdentityFile): esa escritura no las usa y van sin isSoloLectura; para ellas, usa su alias en 'destino'. En los demás casos, sin isSoloLectura no se puede saber sin resolver un nombre y cada una de esas herramientas lo comprueba en cada llamada.")]
        public ResultadoEstado Estado(
            [Description("Filtrar por destino (opcional): un alias de ~/.ssh/config muestra solo las sesiones de ese alias con su usuario y puerto (usuario@alias o alias:puerto con otros son otro destino; 'aviso' nombra las suyas que siguen abiertas para otro usuario o puerto, con qué destino cerrarlas y qué más cerraría ese destino); usuario@servidor[:puerto], todas las de ese destino.")] string? destino = null)
        {
            DestinoSsh? filtro = string.IsNullOrWhiteSpace(destino) ? null : gestor.ParsearDestino(destino);
            bool isSinFiltro = filtro is null;
            DestinosSoloLectura soloLectura = opciones.SoloLectura;
            bool hasSoloLectura = !soloLectura.IsVacio;
            SesionShell[] seleccionadas = isSinFiltro ? gestor.ListarSesiones() : gestor.Seleccionar(filtro!.Value);
            string[][] alias = new string[seleccionadas.Length][];
            bool?[] isSoloLectura;
            using (PooledListRef<ConsultaSinRed> consultas = new())
            {
                for (int i = 0; i < seleccionadas.Length; i++)
                {
                    SesionShell s = seleccionadas[i];
                    alias[i] = gestor.AliasDe(s);
                    // Como una escritura con el texto del filtro: con un alias, cuenta ese texto pedido; con usuario@servidor:puerto,
                    // ninguno (no los alias con que se ha usado la sesión). Sin filtro, los alias con que se ha usado la sesión.
                    string[] textosPedidos = isSinFiltro ? alias[i] : filtro!.Value.Alias is { } aliasFiltro ? [aliasFiltro] : [];
                    consultas.Add(new ConsultaSinRed(s.Destino, s.Extremo, textosPedidos));
                }

                // Las entradas se leen con la configuración de ssh una vez para todas las sesiones.
                isSoloLectura = hasSoloLectura ? soloLectura.IsSoloLecturaSinRed(consultas.Span) : new bool?[seleccionadas.Length];
            }

            EstadoSesion[] sesiones = new EstadoSesion[seleccionadas.Length];
            for (int i = 0; i < seleccionadas.Length; i++)
            {
                SesionShell s = seleccionadas[i];
                // Con usuario@servidor:puerto se listan las sesiones de ese destino con cualquier lista de claves, pero una
                // escritura con ese texto solo usa la de sus claves (ClaveSesion): las demás van sin isSoloLectura.
                bool isOtrasClaves = filtro is { Alias: null } porServidor && !string.Equals(s.Destino.ClaveSesion, porServidor.ClaveSesion, StringComparison.Ordinal);
                sesiones[i] = new EstadoSesion(s.Destino.Clave, s.IsConectada, s.IsShellActivo, s.ComandosEjecutados, s.ConexionesAbiertas, s.ComandosEnCola, s.PeticionActual?.IdTexto, s.CanalesExecActivos, alias[i].Length == 0 ? null : alias[i], s.Destino.Identidades, isOtrasClaves ? null : isSoloLectura[i], s.IsSinPerfil ? true : null);
            }

            // ListarPeticiones ya viene ordenada de más reciente a más antigua: basta un contador. De las peticiones de una misma
            // llamada a una herramienta (Peticion.Grupo) sale solo la más reciente, con su estado: una entrada por llamada.
            using PooledListRef<ResumenPeticion> peticiones = new();
            HashSet<Guid> grupos = [];
            foreach (Peticion p in gestor.ListarPeticiones())
            {
                if (peticiones.Tamaño == MaxPeticionesEnEstado)
                    break;

                bool isDelFiltro = isSinFiltro || GestorSesiones.IsDelDestino(p, filtro!.Value);
                if (isDelFiltro && (p.Grupo is not { } grupo || grupos.Add(grupo)))
                    peticiones.Add(new ResumenPeticion(p.IdTexto, p.Destino.Clave, p.Estado, Codigo(p), Recortar(p.Descripcion), Math.Round(p.TiempoEjecucion.TotalSeconds, 1)));
            }

            string? aviso = filtro is { Alias: not null } conAlias ? TextoPistas(conAlias, gestor.BuscarPistasAlias(conAlias), gestor) : null;
            string[] sinTerminar = hasSoloLectura ? soloLectura.ResolucionesSinTerminar() : [];
            return new ResultadoEstado(sesiones, peticiones.Span.ToArray(), aviso, hasSoloLectura ? soloLectura.Entradas.ToArray() : null, sinTerminar.Length > 0 ? sinTerminar : null);
        }

        [McpServerTool(Name = "ssh_disconnect", Destructive = true, Idempotent = true)]
        [Description("Cierra la conexión con un destino. Cancela lo que esté en ejecución o en cola y olvida el directorio de trabajo fijado con ssh_set_cwd. BORRA del servidor todos los archivos de salida guardados con guardarSalida en las sesiones que cierra (de todos sus shells): léelos antes. La próxima herramienta que use ese destino abrirá una conexión nueva (cuenta para el límite de conexiones del servidor). Con un alias de ~/.ssh/config cierra solo las sesiones de ese alias con su usuario y puerto (usuario@alias o alias:puerto con otros son otro destino); con usuario@servidor[:puerto], todas las de ese destino. La respuesta dice cuáles cerró y, con un alias, qué otras sesiones suyas siguen abiertas, con qué destino cerrarlas y qué más cerraría ese destino. Lee lo que cerraría además antes de usarlo: si dice que ningún destino las cierra solas, cerrarlas cierra también las sesiones (y cancela los comandos) que nombra.")]
        public async Task<ResultadoAccion> DesconectarAsync(
            [Description("usuario@servidor[:puerto], o un alias de ~/.ssh/config. Usa el mismo destino que en ssh_exec: un alias con otro IdentityFile es otra sesión, con su propio directorio de trabajo.")] string destino)
        {
            DestinoSsh resuelto = gestor.ParsearDestino(destino);
            SesionCerrada[] cerradasConLimpieza = await gestor.DesconectarConLimpiezaAsync(resuelto).ConfigureAwait(false);
            DestinoSsh[] cerradas = new DestinoSsh[cerradasConLimpieza.Length];
            for (int i = 0; i < cerradas.Length; i++)
                cerradas[i] = cerradasConLimpieza[i].Destino;
            string mensaje = cerradas.Length switch
            {
                0 when resuelto.Alias is { } alias => $"No había conexión con '{alias}' ({resuelto.Clave}).",
                0 => "No había conexión con ese destino.",
                _ => DescribirDesconexion(cerradasConLimpieza)
            };
            // Las cerradas ya no están en el gestor: las pistas son de las que siguen abiertas.
            string? pistas = TextoPistas(resuelto, gestor.BuscarPistasAlias(resuelto), gestor);
            mensaje += DescribirBorradoAlDesconectar(cerradasConLimpieza);
            return new ResultadoAccion(cerradas.Length == 1 ? cerradas[0].Clave : resuelto.Clave, cerradas.Length > 0, pistas is null ? mensaje : $"{mensaje} {pistas}");
        }

        // ssh_disconnect: con una sesión cerrada, lo que pasó con sus archivos de guardarSalida; con varias, lo de cada una
        // que tenía algo que decir, precedido de cuál es.
        private static string DescribirBorradoAlDesconectar(ReadOnlySpan<SesionCerrada> cerradas)
        {
            if (cerradas.Length == 1)
                return DescribirBorradoAlDesconectar(cerradas[0].Limpieza);

            StringBuilder texto = new();
            foreach (SesionCerrada cerrada in cerradas)
            {
                string borrado = DescribirBorradoAlDesconectar(cerrada.Limpieza);
                if (borrado.Length > 0)
                    _ = texto.Append(" En la sesión ").Append(DescribirCerradas([cerrada.Destino], isConDestino: true)).Append(':').Append(borrado);
            }

            return texto.ToString();
        }

        // ssh_disconnect con alguna sesión cerrada: las que estaban conectadas (conexiones cerradas) y las que no llegaron a
        // conectarse (sesiones descartadas, sin conexión que cerrar), por separado. Varias del mismo usuario@servidor:puerto
        // solo pueden diferir en las claves (son sesiones distintas): entonces se nombran solo sus claves.
        private static string DescribirDesconexion(ReadOnlySpan<SesionCerrada> cerradas)
        {
            using PooledListRef<DestinoSsh> conectadas = new();
            using PooledListRef<DestinoSsh> sinConectar = new();
            foreach (SesionCerrada cerrada in cerradas)
                if (cerrada.IsSinConectar)
                    sinConectar.Add(cerrada.Destino);
                else
                    conectadas.Add(cerrada.Destino);

            DestinoSsh[] todas = [.. conectadas.Span, .. sinConectar.Span];
            bool isMismoDestino = todas.Length > 1 && IsMismoDestino(todas);
            string conDestino = isMismoDestino ? $" con {todas[0].Clave}" : string.Empty;
            string porClaves = isMismoDestino ? ", una por cada juego de claves" : string.Empty;
            string deConectadas = conectadas.Tamaño switch
            {
                0 => string.Empty,
                1 => $"Conexión cerrada{conDestino}: {DescribirCerradas(conectadas.Span, isConDestino: !isMismoDestino)}.",
                _ => $"Se cerraron {conectadas.Tamaño} conexiones{conDestino}{porClaves}: {DescribirCerradas(conectadas.Span, isConDestino: !isMismoDestino)}."
            };
            string deSinConectar = sinConectar.Tamaño switch
            {
                0 => string.Empty,
                1 => $"Sesión sin conectar descartada{conDestino}: {DescribirCerradas(sinConectar.Span, isConDestino: !isMismoDestino)}; no llegó a conectarse, así que no había conexión que cerrar.",
                _ => $"{sinConectar.Tamaño} sesiones sin conectar descartadas{conDestino}: {DescribirCerradas(sinConectar.Span, isConDestino: !isMismoDestino)}; no llegaron a conectarse, así que no había conexión que cerrar."
            };
            return deConectadas.Length > 0 && deSinConectar.Length > 0 ? $"{deConectadas} {deSinConectar}" : deConectadas + deSinConectar;
        }

        private static bool IsMismoDestino(DestinoSsh[] destinos)
        {
            foreach (DestinoSsh destino in destinos)
                if (!string.Equals(destino.Clave, destinos[0].Clave, StringComparison.Ordinal))
                    return false;

            return true;
        }

        // «usuario@servidor:puerto (claves)» o solo las claves de cada una, separadas por '; '.
        private static string DescribirCerradas(ReadOnlySpan<DestinoSsh> destinos, bool isConDestino)
        {
            StringBuilder texto = new();
            foreach (DestinoSsh destino in destinos)
            {
                if (texto.Length > 0)
                    _ = texto.Append("; ");
                if (isConDestino)
                    _ = texto.Append(destino.Clave).Append(" (");
                _ = destino.Identidades is null ? texto.Append("las claves de native_ssh") : texto.Append("IdentityFile ").AppendJoin(", ", destino.Identidades);
                if (isConDestino)
                    _ = texto.Append(')');
            }

            return texto.ToString();
        }

        // Con un alias, ssh_disconnect y ssh_status dicen qué otras sesiones suyas siguen abiertas (null si ninguna): las de
        // otro usuario o puerto y las que llegaron al tope de alias sin guardar este. Para cada grupo, el destino que las
        // cierra solas si lo hay; si no, lo que cerraría además cada destino posible (GestorSesiones.BuscarPistasAlias lo
        // calcula con el criterio de ssh_disconnect), o que ninguno las cierra y por qué. Nunca sugiere un destino que se rechazaría.
        private static string? TextoPistas(DestinoSsh resuelto, ReadOnlySpan<PistaAlias> pistas, GestorSesiones gestor)
        {
            if (resuelto.Alias is not { } alias || pistas.Length == 0)
                return null;

            StringBuilder texto = new();
            bool isPrimeraOtros = true;
            foreach (PistaAlias pista in pistas)
            {
                if (pista.IsEnElTope)
                    continue;

                _ = isPrimeraOtros
                    ? texto.Append($"Siguen abiertas sesiones de '{alias}' para otro usuario o puerto (su User o Port cambió con la sesión viva, o se abrieron con otro usuario o puerto): ")
                    : texto.Append("; ");
                isPrimeraOtros = false;
                AnadirPista(texto, pista, gestor, $"ssh_disconnect con '{alias}' la cierra cuando su configuración vuelva a ese usuario y puerto");
            }

            if (!isPrimeraOtros)
                _ = texto.Append(" (ssh_status sin destino las muestra todas).");

            bool isPrimeraTope = true;
            foreach (PistaAlias pista in pistas)
            {
                if (!pista.IsEnElTope)
                    continue;

                _ = isPrimeraTope
                    ? texto.Append(isPrimeraOtros ? "" : " ").Append($"Sesiones que ya recuerdan {GestorSesiones.MaxAliasPorSesion} alias y no guardaron '{alias}' (si buscas una abierta con este alias antes de cambiar su configuración, puede ser una de ellas): ")
                    : texto.Append("; ");
                isPrimeraTope = false;
                AnadirPista(texto, pista, gestor, null);
            }

            if (!isPrimeraTope)
                _ = texto.Append(" (ssh_status sin destino las muestra todas).");
            return texto.ToString();
        }

        // «viejas, que se cierra con X» si un candidato las cierra solas; si no, qué más cerraría cada uno que las alcanza, o
        // que ninguno las cierra (con el motivo y, si se da, cuándo las cierra el alias).
        private static void AnadirPista(StringBuilder texto, PistaAlias pista, GestorSesiones gestor, string? cierreConElAlias)
        {
            DescribirViejas(texto, pista.Viejas);
            if (pista.Exacto is { } exacto)
            {
                _ = texto.Append(", que se cierra con ").Append(exacto.Texto);
                return;
            }

            bool hasAlcanzan = false;
            foreach (CandidatoCierre candidato in pista.Candidatos)
                hasAlcanzan |= candidato.IsAlcanzaTodas;

            if (!hasAlcanzan)
            {
                // Solo de los candidatos se sabe: «no hay destino» sería falso si otro alias que la sesión recuerda la alcanza.
                _ = texto.Append(": no se cierra con ");
                for (int i = 0; i < pista.Candidatos.Length; i++)
                    _ = texto.Append(i == 0 ? "" : " ni con ").Append(pista.Candidatos[i].Texto);
                _ = texto.Append(" (");
                for (int i = 0; i < pista.Candidatos.Length; i++)
                {
                    CandidatoCierre candidato = pista.Candidatos[i];
                    _ = texto.Append(i == 0 ? "" : "; ").Append(candidato.Texto).Append(candidato.Error is { } error ? $" se rechaza: {error.TrimEnd('.')}" : " no la alcanza");
                }

                _ = texto.Append(')');
                if (cierreConElAlias is not null)
                    _ = texto.Append("; ").Append(cierreConElAlias);
                return;
            }

            bool isUnCandidato = pista.Candidatos.Length == 1;
            if (isUnCandidato)
                _ = texto.Append(": ").Append(pista.Candidatos[0].Texto).Append(" no la cierra sola: ");
            else
            {
                _ = texto.Append(": ni ");
                for (int i = 0; i < pista.Candidatos.Length; i++)
                    _ = texto.Append(i == 0 ? "" : " ni ").Append(pista.Candidatos[i].Texto);
                _ = texto.Append(" la cierran sola: ");
            }

            // Los dos cierran lo mismo (p. ej. el IdentityFile del alias pasó a ser el de otra sesión del mismo destino): una vez.
            if (!isUnCandidato && IsMismoCierre(pista.Candidatos))
            {
                DescribirAdemas(texto.Append("los dos cerrarían también "), pista.Candidatos[0], gestor);
                return;
            }

            for (int c = 0; c < pista.Candidatos.Length; c++)
            {
                CandidatoCierre candidato = pista.Candidatos[c];
                _ = texto.Append(c == 0 ? "" : "; ");
                if (!isUnCandidato)
                    _ = texto.Append(candidato.Texto).Append(' ');
                if (candidato.Error is { } error)
                    _ = texto.Append("se rechaza: ").Append(error.TrimEnd('.'));
                else if (!candidato.IsAlcanzaTodas)
                    _ = texto.Append("no la alcanza");
                else
                    DescribirAdemas(texto.Append("cerraría también "), candidato, gestor);
            }
        }

        private static bool IsMismoCierre(ReadOnlySpan<CandidatoCierre> candidatos)
        {
            SesionShell[] primero = candidatos[0].Ademas;
            foreach (CandidatoCierre candidato in candidatos)
            {
                if (!candidato.IsAlcanzaTodas || candidato.Ademas.Length != primero.Length)
                    return false;

                foreach (SesionShell sesion in candidato.Ademas)
                    if (!IsIncluida(primero, sesion))
                        return false;
            }

            return true;
        }

        private static bool IsIncluida(ReadOnlySpan<SesionShell> sesiones, SesionShell sesion)
        {
            foreach (SesionShell otra in sesiones)
                if (ReferenceEquals(otra, sesion))
                    return true;

            return false;
        }

        private static void DescribirAdemas(StringBuilder texto, CandidatoCierre candidato, GestorSesiones gestor)
        {
            for (int i = 0; i < candidato.Ademas.Length; i++)
                DescribirSesion(texto.Append(i == 0 ? "" : ", "), candidato.Ademas[i], gestor);
        }

        // Los usuario@servidor:puerto de las sesiones viejas, cada uno una vez, con sus comandos en curso.
        private static void DescribirViejas(StringBuilder texto, ReadOnlySpan<SesionShell> viejas)
        {
            using PooledListRef<string> claves = new();
            foreach (SesionShell sesion in viejas)
                if (claves.IndexOf(sesion.Destino.Clave) < 0)
                    claves.Add(sesion.Destino.Clave);

            for (int i = 0; i < claves.Tamaño; i++)
            {
                _ = texto.Append(i == 0 ? "" : ", ").Append(claves[i]);
                StringBuilder enCurso = new();
                foreach (SesionShell sesion in viejas)
                    if (string.Equals(sesion.Destino.Clave, claves[i], StringComparison.Ordinal))
                        DescribirEnCurso(enCurso, sesion);
                if (enCurso.Length > 0)
                    _ = texto.Append(" (").Append(enCurso).Append(')');
            }
        }

        // «usuario@servidor:puerto (claves; alias a, b; comando en curso id «comando»; N en cola)».
        private static void DescribirSesion(StringBuilder texto, SesionShell sesion, GestorSesiones gestor)
        {
            DestinoSsh destino = sesion.Destino;
            _ = texto.Append(destino.Clave).Append(" (");
            _ = destino.Identidades is null ? texto.Append("las claves de native_ssh") : texto.Append("IdentityFile ").AppendJoin(", ", destino.Identidades);
            string[] alias = gestor.AliasDe(sesion);
            if (alias.Length > 0)
                _ = texto.Append("; alias ").AppendJoin(", ", alias);
            StringBuilder enCurso = new();
            DescribirEnCurso(enCurso, sesion);
            _ = enCurso.Length > 0 ? texto.Append("; ").Append(enCurso).Append(')') : texto.Append(')');
        }

        private static void DescribirEnCurso(StringBuilder texto, SesionShell sesion)
        {
            if (sesion.PeticionActual is { } actual)
                _ = texto.Append(texto.Length > 0 ? "; " : "").Append($"comando en curso {actual.IdTexto} «{Recortar(actual.Descripcion)}»");
            int enCola = sesion.ComandosEnCola;
            if (enCola > 0)
                _ = texto.Append(texto.Length > 0 ? "; " : "").Append($"{enCola} en cola");
        }

        [McpServerTool(Name = "ssh_reset_shell", Destructive = true, Idempotent = true)]
        [Description("Descarta el shell actual (variables exportadas incluidas) y abre uno limpio en el próximo comando, sobre la misma conexión. Con sinPerfil=true lo abre SIN ~/.profile ni ~/.bashrc: úsalo cuando el login no termina (un perfil roto con un 'sleep', un 'read' o un 'exit') para repararlo con ssh_exec, ssh_write_file o ssh_replace_file_content. Cancela el comando en ejecución. Conserva el directorio de trabajo fijado con ssh_set_cwd. BORRA del servidor todos los archivos de salida guardados con guardarSalida en esta sesión (de todos sus shells): léelos antes.")]
        public ResultadoAccion ReiniciarShell(
            [Description("usuario@servidor[:puerto], o un alias de ~/.ssh/config. Usa el mismo destino que en ssh_exec: un alias con otro IdentityFile es otra sesión, con su propio directorio de trabajo.")] string destino,
            [Description("true: el shell nuevo (y los que se reabran solos) se abre sin perfil, con una petición 'exec' de 'bash --noediting --norc --noprofile' en lugar del login: sin ~/.profile ni ~/.bashrc, con el PATH por defecto de sshd y sin las variables, alias ni funciones del perfil (ssh_status lo muestra con isSinPerfil). Dura hasta otro ssh_reset_shell sin sinPerfil (vuelve al login) o ssh_disconnect. Si el servidor tiene un ForceCommand, este sustituye la petición y el shell es el de siempre. Por defecto false.")] bool sinPerfil = false)
        {
            DestinoSsh resuelto = gestor.ParsearDestino(destino);
            string clave = resuelto.Clave;
            LimpiezaGuardados? limpieza = gestor.ReiniciarShellConLimpieza(destino, sinPerfil, out bool isSesionNueva);
            if (limpieza is null)
                return new ResultadoAccion(clave, false, $"{NoHabiaSesion(resuelto)}.");

            string borrado = limpieza.Creados == 0
                ? ""
                : $" Se borran del servidor, por un canal aparte, los archivos de salida guardados con guardarSalida en esta sesión que sigan ahí (hasta {limpieza.Creados}: los que se crearon, menos los que ya se hubieran borrado); si el servidor no admite otro canal (MaxSessions) o el borrado falla, se dirá en el siguiente ssh_reset_shell y se volverán a intentar en él o con ssh_disconnect, que dice cuántos quedan.";
            // Sin sesión, sinPerfil la crea sin conectar (GestorSesiones.ReiniciarShellConLimpieza): no se descartó nada.
            string shell = isSesionNueva
                ? $"{NoHabiaSesion(resuelto)}: no se ha descartado ningún shell. Se ha preparado una sin conectar (ssh_status la muestra con isConectada=false e isSinPerfil=true), que se conectará con el próximo comando y abrirá su shell SIN PERFIL (sin ~/.profile ni ~/.bashrc; su entorno no es el del login: PATH por defecto, sin las variables, alias ni funciones del perfil). Lo usan ssh_exec, ssh_write_file y ssh_replace_file_content, y también los shells que se reabran solos, hasta ssh_reset_shell sin sinPerfil (vuelve al login) o ssh_disconnect."
                : sinPerfil
                    ? "El próximo comando usará un shell nuevo SIN PERFIL (sin ~/.profile ni ~/.bashrc; su entorno no es el del login: PATH por defecto, sin las variables, alias ni funciones del perfil). Lo usan ssh_exec, ssh_write_file y ssh_replace_file_content, y también los shells que se reabran solos, hasta ssh_reset_shell sin sinPerfil (vuelve al login) o ssh_disconnect."
                    : "El próximo comando usará un shell nuevo.";
            return new ResultadoAccion(clave, true, $"{shell}{borrado}{DescribirBorradoAnteriorFallido(limpieza)}");
        }

        // ssh_reset_shell sin sesión con las claves del destino: «No había sesión con ese destino» y, si hay otras del mismo
        // usuario@servidor:puerto con otras claves (otro alias con su IdentityFile, o las de native_ssh), cuáles: no se tocan.
        private string NoHabiaSesion(DestinoSsh destino)
        {
            SesionShell[] otras = gestor.OtrasSesionesDelDestino(destino);
            if (otras.Length == 0)
                return "No había sesión con ese destino";

            StringBuilder texto = new($"No había sesión con ese destino y esas claves ({DescribirCerradas([destino], isConDestino: false)}); ");
            bool isUnaOtra = otras.Length == 1;
            _ = texto.Append(isUnaOtra ? "hay otra de " : $"hay {otras.Length} más de ").Append(destino.Clave).Append(" con otras claves, que ").Append(isUnaOtra ? "no se toca: " : "no se tocan: ");
            for (int i = 0; i < otras.Length; i++)
            {
                if (i > 0)
                    _ = texto.Append("; ");
                DescribirSesion(texto, otras[i], gestor);
            }

            return texto.ToString();
        }

        // ssh_reset_shell: el borrado del anterior (en segundo plano) no se completó; sus shells están entre los de este.
        private static string DescribirBorradoAnteriorFallido(LimpiezaGuardados limpieza)
        {
            if (!limpieza.IsAnteriorFallido)
                return "";

            string motivo = limpieza.SinBorrarAnterior switch
            {
                0 => "sus salidas de guardarSalida sí se borraron, pero el directorio de algún shell no se pudo quitar: p. ej. sin permiso de escritura en el directorio que lo contiene (/tmp/native_ssh-<usuario>), y queda vacío, o con otras cosas que dejó un comando dentro",
                int quedan => $"no se pudieron borrar {quedan} archivo(s) de salida guardados con guardarSalida (p. ej. un directorio sin permiso de escritura: arréglalo con 'chmod u+w')",
                null => "sin conexión, el servidor no admitió otro canal, falló en el servidor o no se pudo listar algún directorio"
            };
            return $" Ojo: el borrado del ssh_reset_shell anterior no se completó ({motivo}); se vuelve a intentar ahora con el resto, y lo que no se pueda borrar lo dirá el siguiente ssh_reset_shell o ssh_disconnect.";
        }

        // ssh_disconnect: lo que contó el servidor al borrar (no lo que se creó: alguno pudo borrarse antes con 'rm'), lo que
        // quedó si algún directorio no se pudo borrar, o que no se pudo comprobar nada.
        private static string DescribirBorradoAlDesconectar(LimpiezaGuardados limpieza)
        {
            const string Quitar = "siguen en /tmp/native_ssh-<usuario> del servidor hasta que el barrido de 24 h de otra sesión los quite; bórralos con ssh_exec ('chmod -R u+w -- <directorio>' y 'rm -r -- <directorio>') si contienen datos sensibles.";
            bool isSinNada = limpieza.Creados == 0 && limpieza.Contados is null or 0 && !limpieza.IsFallida && !limpieza.IsSinComprobar;
            return isSinNada ? ""
                : limpieza.IsSinComprobar ? $" No se pudo comprobar en el servidor si quedaban archivos de salida guardados con guardarSalida en esta sesión (hasta {limpieza.Creados} creados) y no se ha borrado nada: el directorio /tmp/native_ssh-<usuario> no estaba, no era un directorio propio (o era un enlace) o no se pudo saber el usuario. Si lo moviste o lo sustituiste, revisa tú lo que contenga."
                : limpieza is { IsFallida: true, Contados: { } contados, Quedan: 0, Otros: { } otros } ? $"{DescribirBorradas(contados)}, pero el directorio de algún shell {(otros == 0 ? "quedó vacío y no se pudo quitar (p. ej. el directorio que lo contiene, /tmp/native_ssh-<usuario>, no tiene permiso de escritura): quítalo con ssh_exec ('chmod u+w' sobre ese directorio y 'rmdir' sobre el del shell)" : $"no se pudo borrar del todo: tiene {otros} entrada(s) que no son salidas de guardarSalida")}."
                : limpieza is { IsFallida: true, Contados: { } antes, Quedan: { } quedan } ? $" NO se pudieron borrar {quedan} de los {antes} archivo(s) de salida guardados con guardarSalida que quedaban de esta sesión{(antes > quedan ? $" (los otros {antes - quedan} sí se borraron)" : "")}: el directorio de su shell no se pudo borrar (p. ej. sin permiso de escritura); {Quitar}"
                : limpieza.IsFallida ? $" NO se pudieron borrar los archivos de salida guardados con guardarSalida en esta sesión (hasta {limpieza.Creados} creados; sin conexión o falló la limpieza, sin saber cuántos quedan): {Quitar}"
                : limpieza.Contados switch
                {
                    null => $" Se borraron del servidor los directorios de las salidas guardadas con guardarSalida en esta sesión (hasta {limpieza.Creados} archivo(s) creados; no se pudo saber cuántos quedaban).",
                    0 => " No quedaba en el servidor ningún archivo de salida guardado con guardarSalida en esta sesión (ya se habían borrado).",
                    int borrados => $" Se borraron del servidor los {borrados} archivo(s) de salida guardados con guardarSalida que quedaban de esta sesión."
                };
        }

        private static string DescribirBorradas(int contados) => contados == 0
            ? " No quedaba en el servidor ningún archivo de salida guardado con guardarSalida en esta sesión"
            : $" Se borraron del servidor los {contados} archivo(s) de salida guardados con guardarSalida que quedaban de esta sesión";

        // Script de ssh_read_file: cabecera '[Líneas X-Y de Total]' y el contenido tal cual. inicio < 0 = las últimas -inicio
        // líneas (tail -n); si no, de 'inicio' a 'fin' con head y tail, que no tocan los bytes: el sed de busybox añadía un
        // salto final que el archivo no tiene (Bloque S, P3). pipefail: un fallo de head (p. ej. de lectura) no queda tapado
        // por el código de tail.
        internal static string ConstruirComandoLectura(string ruta, int inicio, int fin)
        {
            string rutaEscapada = Escapar(ruta);
            // wc -l no cuenta una última línea sin '\n': se suma para que la cabecera cuadre con lo que imprimen head y tail.
            string lectura = inicio < 0
                ? PlantillaBash.Crear($$"""
                    ultimas={{-(long)inicio}}
                    inicio_efectivo=$(( total - ultimas + 1 ))
                    [ "$inicio_efectivo" -lt 1 ] && inicio_efectivo=1
                    if [ "$total" -eq 0 ]; then
                      echo "[Líneas 0-0 de 0]"
                    else
                      echo "[Líneas $inicio_efectivo-$total de $total]"
                    fi
                    tail -n {{-(long)inicio}} -- {{rutaEscapada}}
                    """)
                : PlantillaBash.Crear($$"""
                    if [ "$total" -eq 0 ]; then
                      echo "[Líneas 0-0 de 0]"
                    else
                      fin_efectivo=$(( total < {{fin}} ? total : {{fin}} ))
                      echo "[Líneas {{inicio}}-$fin_efectivo de $total]"
                    fi
                    set -o pipefail
                    head -n {{fin}} -- {{rutaEscapada}} | tail -n +{{inicio}}
                    """);
            return PlantillaBash.Crear($$"""
                (
                {{DefinicionSinAcceso}}
                if [ ! -e {{rutaEscapada}} ]; then
                  sin_acceso {{rutaEscapada}} || printf 'Error: el archivo no existe: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                if [ -d {{rutaEscapada}} ]; then
                  printf 'Error: la ruta es un directorio: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                # Una FIFO sin escritor bloquearía hasta el plazo y un dispositivo no es el contenido de un archivo. Los de
                # /proc y /sys sí son archivos regulares (de tamaño 0) y se leen.
                if [ ! -f {{rutaEscapada}} ]; then
                  printf 'Error: %s no es un archivo regular (es una FIFO, un socket o un dispositivo): no se lee.\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                if [ ! -r {{rutaEscapada}} ]; then
                  printf 'Error: sin permiso de lectura: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                total=$(wc -l < {{rutaEscapada}}) || exit 1
                if [ -s {{rutaEscapada}} ] && [ -n "$(tail -c 1 -- {{rutaEscapada}})" ]; then
                  total=$(( total + 1 ))
                fi
                {{lectura}}
                )
                """);
        }

        [McpServerTool(Name = "ssh_read_file", ReadOnly = true, Idempotent = true)]
        [Description("Va por un canal exec propio, no por el shell de ssh_exec: no espera a sus comandos y funciona aunque el login de ese shell falle. Lee un archivo del servidor remoto con soporte para rangos de líneas (1-indexed) o sus últimas N líneas (lineaInicio = -N, como 'tail -n N'). Devuelve el contenido en texto plano con saltos de línea reales (TextContent), sin envoltorios JSON, precedido de la cabecera '[Líneas X-Y de Total]'. El contenido llega tal cual, sin pasar por un terminal (conserva los \\r de CRLF y las secuencias de escape). Si no es UTF-8 válido, los bytes inválidos se sustituyen por U+FFFD y se añade al final la línea '[aviso: contenido no UTF-8; bytes inválidos sustituidos por U+FFFD]'. Si la salida supera el máximo por petición (1 MiB por defecto) devuelve un error en lugar de contenido parcial. Distingue en el error si el archivo no existe, si algún directorio de la ruta no se puede atravesar (y cuál) o si no hay permiso de lectura. Si la ruta no es un archivo regular (FIFO, socket o dispositivo) devuelve un error al momento; los archivos de /proc y /sys sí se leen.")]
        public async Task<string> LeerArchivoAsync(
            [Description("usuario@servidor[:puerto], o un alias de ~/.ssh/config. Usa el mismo destino que en ssh_exec: un alias con otro IdentityFile es otra sesión, con su propio directorio de trabajo.")] string destino,
            [Description("Ruta del archivo en el servidor remoto (absoluta o relativa a __NS_CWD).")] string ruta,
            [Description("Línea inicial (1-indexed, por defecto 1). Un valor negativo -N lee las últimas N líneas (como 'tail -n N') y no admite lineaFin.")] int lineaInicio = 1,
            [Description("Línea final (1-indexed, opcional). Si no se especifica, lee hasta 800 líneas. No se puede combinar con lineaInicio negativo.")] int? lineaFin = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(ruta))
                throw new NativeSshException("La ruta del archivo no puede estar vacía.");

            ExigirValorLiteral(ruta, "La ruta");
            bool isDesdeElFinal = lineaInicio < 0;
            if (isDesdeElFinal && lineaFin is not null)
                throw new NativeSshException($"lineaFin no se puede combinar con un lineaInicio negativo ({lineaInicio} = últimas {-(long)lineaInicio} líneas). Omite lineaFin o usa un rango positivo.");

            int inicio = Math.Max(1, lineaInicio);
            int fin = lineaFin ?? (inicio + 799);
            if (!isDesdeElFinal && fin < inicio)
                throw new NativeSshException($"La línea final ({fin}) no puede ser menor que la línea inicial ({inicio}).");

            string comando = ConstruirComandoLectura(ruta, isDesdeElFinal ? lineaInicio : inicio, fin);
            SalidaLectura resultado = await EjecutarLecturaAsync(destino, comando, TimeoutLecturaSegundos, "Lee un rango de líneas menor (lineaInicio/lineaFin).", cancellationToken).ConfigureAwait(false);
            if (resultado.Codigo != 0)
                throw new NativeSshException(resultado.Error("El comando de lectura falló"));

            // Nunca se sustituyen bytes en silencio: si el archivo no es UTF-8, se dice al final.
            string texto = resultado.Salida;
            return resultado.IsUtf8Valido
                ? texto
                : texto.EndsWith('\n') ? texto + AvisoContenidoNoUtf8 : $"{texto}\n{AvisoContenidoNoUtf8}";
        }

        [McpServerTool(Name = "ssh_write_file", Destructive = true)]
        [Description("Va por el shell persistente de ssh_exec, en su cola: espera a los comandos en curso y necesita que el login de ese shell termine (si el perfil está roto, ssh_reset_shell con sinPerfil=true). Escribe o anexa contenido en un archivo remoto mediante Base64 (inmune a problemas de escaping de bash). Por defecto no sobrescribe: la creación es exclusiva y, si el archivo existe o aparece mientras se escribe, falla sin tocarlo; el archivo nuevo se crea completo de una vez con los permisos por defecto (umask). overwrite=true: si la ruta es un enlace simbólico escribe en su destino y conserva el enlace; escribe un temporal en el mismo directorio, le copia todos los atributos del original (modo con los bits setuid y setgid, ACL, propietario, grupo, atributos extendidos y contexto SELinux, con cp de GNU coreutils; la fecha de modificación es la de la escritura) y, si comprueba que coinciden (stat; ACL y atributos extendidos, security.capability incluida, con getfattr; sin getfattr, las ACL con getfacl y ls, y de los demás atributos extendidos responde cp), lo renombra encima (atómico; si entretanto el destino se ha convertido en un directorio, falla sin meter nada dentro). Sobrescribe también los archivos de solo lectura propios (p. ej. 0444), y con sudo=true los de cualquier usuario, conservando su modo: la protección de solo lectura no impide la escritura, salvo si hay que escribir en el sitio (ver abajo): entonces, sin sudo, falla sin tocarlo. Si no puede copiar o comprobar todos los atributos (p. ej. archivo de otro usuario sin sudo, cp que no es de GNU coreutils, contexto SELinux que no se puede asignar), el archivo tiene enlaces duros o su directorio no tiene permiso de escritura (el temporal va entonces a $TMPDIR o /tmp), lo sobrescribe en el sitio (NO atómico; conserva inodo, enlaces duros, propietario, grupo, ACL, atributos extendidos y contexto SELinux; al escribir, el núcleo quita las capacidades, security.capability, y, salvo si escribe root (sudo=true), los bits setuid y setgid: se le devuelven y se comprueban, las capacidades solo con sudo=true y setfattr o setcap; lo que no se pueda devolver o comprobar lo dice la respuesta, con el valor original de las capacidades; sin getfattr ni getcap en el servidor no se puede saber si el archivo tenía capacidades y la respuesta lo avisa) tras hacer una copia de seguridad del original (en su directorio o en $TMPDIR o /tmp; sin ella no escribe): si la escritura falla o se interrumpe, restaura el original; la copia solo se borra cuando el archivo ha quedado escrito por completo o restaurado, y si no se puede restaurar se conservan la copia y el contenido nuevo y el error da sus rutas. Lo indica en la respuesta (isAtomica=false). append=true anexa al final con '>>' (no atómico). Antes de colocar el archivo o de anexar, el script comprueba que lo que le llegó tiene el tamaño y el sha256 enviados (sin sha256sum, el cksum; sin ninguno, solo el tamaño, y la respuesta lo dice); si no, falla sin tocar el archivo. Si el destino (resuelto) existe y no es un archivo regular (FIFO, socket o dispositivo), falla sin tocarlo, también con append. Si la escritura no termina (tiempo agotado, cancelación, ssh_disconnect o conexión perdida) o el script muere a mitad (p. ej. por SIGKILL), el error dice qué pasó con el archivo (no se llegó a modificar, se restauró, ya tiene el contenido nuevo o puede haber quedado a medias, con las rutas de la copia del original y del contenido nuevo) y el id de la petición para ver su salida con ssh_output; interrumpida la escritura en el sitio, el script restaura el original por su cuenta aunque ya no pueda confirmarlo. Mientras una escritura, un anexado o un reemplazo siguen en marcha sobre un archivo (también la restauración tras una interrupción), tienen tomado su candado (flock sobre el archivo, con flock, python3 o perl del servidor; sin ellos, con un flock sin -E como el de busybox si antes se comprueba que funciona) y las escrituras, anexados y reemplazos nuevos sobre él fallan al momento sin tocarlo: reintenta cuando termine (también fallan si otro programa tiene un flock sobre el archivo); el candado se toma también si el archivo tiene permiso de escritura pero no de lectura (se abre para anexar, sin truncarlo); sin ninguna de esas herramientas, o si el archivo no se puede abrir ni para leer ni para anexar, se escribe sin exclusión y la respuesta lo avisa, y si no se puede comprobar que el candado quedó sobre el archivo que está en la ruta (sin /proc en el servidor ni stat de GNU coreutils, python3 o perl, o si no se puede consultar la ruta), también. Devuelve ruta, bytes escritos, isAtomica y mensaje.")]
        public async Task<ResultadoEscritura> EscribirArchivoAsync(
            [Description("usuario@servidor[:puerto], o un alias de ~/.ssh/config. Usa el mismo destino que en ssh_exec: un alias con otro IdentityFile es otra sesión, con su propio directorio de trabajo.")] string destino,
            [Description("Ruta del archivo remoto (absoluta o relativa a __NS_CWD).")] string ruta,
            [Description("Contenido textual o datos a escribir.")] string contenido,
            [Description("Sobrescribir si el archivo ya existe (por defecto false). También sobrescribe archivos de solo lectura propios, conservando su modo.")] bool overwrite = false,
            [Description("Anexar al final del archivo existente (por defecto false).")] bool append = false,
            [Description("Ejecutar la escritura con sudo (por defecto false).")] bool sudo = false,
            CancellationToken cancellationToken = default)
        {
            await ExigirEscrituraPermitidaAsync(gestor.ParsearDestino(destino), "ssh_write_file", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(ruta))
                throw new NativeSshException("La ruta del archivo no puede estar vacía.");

            // El SDK enlaza un null tal cual (ValidadorArgumentos.IsDelTipo) y escribir "nada" vaciaría el archivo con
            // overwrite=true: un null nunca vacía un archivo, hay que pedirlo con "". Es el único texto obligatorio cuyo null
            // tiene un efecto destructivo (los demás dan el error de su herramienta sin tocar nada).
            if (contenido is null)
                throw new NativeSshException("'contenido' llegó como null: no se ha escrito nada. Para un archivo vacío pasa contenido: \"\".");

            if (overwrite && append)
                throw new NativeSshException("No se puede especificar 'overwrite=true' y 'append=true' simultáneamente.");

            ExigirValorLiteral(ruta, "La ruta");
            ExigirUtf16Valido(contenido ?? string.Empty, "El contenido");
            byte[] bytesUtf8 = Encoding.UTF8.GetBytes(contenido ?? string.Empty);
            string payloadBase64 = Convert.ToBase64String(bytesUtf8);
            string rutaEscapada = Escapar(ruta);
            string idMarcas = Guid.NewGuid().ToString("N");

            // Anexar: el fragmento se decodifica en un temporal propio y se verifica (verificar_contenido) antes de abrir el
            // archivo, que se crea si no existía: con el contenido desviado no se toca ni se crea. Se verifica el fragmento, no
            // el archivo tras anexarlo (otro proceso puede estar anexando a la vez y el candado no lo impide si no lo toma). El
            // archivo se abre antes de anunciar que se anexa, para que un fallo normal al abrirlo (permiso denegado) no se
            // describa como una muerte a mitad del anexado.
            string comando = append
                ? EnvolverEnBash(PlantillaBash.Crear($$"""
                    ruta=$1
                    tam_esperado=$2
                    sha_esperado=$3
                    cksum_esperado=$4
                    marca={{idMarcas}}
                    {{DefinicionAnunciar}}
                    {{DefinicionVerificarContenido}}
                    if [ -d "$ruta" ]; then
                      printf 'Error: la ruta es un directorio: %s\n' "$ruta" >&2
                      exit 1
                    fi
                    if [ -e "$ruta" ] && [ ! -f "$ruta" ]; then
                      {{ErrorNoRegular}}
                    fi
                    fragmento=$(command mktemp -- "${TMPDIR:-/tmp}/.ns_anexo.XXXXXX" 2>/dev/null) || {
                      printf 'Error: no se pudo crear el archivo temporal del fragmento en %s. No se ha anexado nada.\n' "${TMPDIR:-/tmp}" >&2
                      exit 1
                    }
                    trap 'command rm -f -- "$fragmento"' EXIT
                    base64 -d > "$fragmento" || exit 1
                    verificar_contenido "$fragmento" || { [ "$?" = 2 ] && exit {{CodigoSinVerificador}}; exit {{CodigoContenidoNoIntegro}}; }
                    anunciar verificacion "$verificacion"
                    {{DefinicionCandado}}
                    destino=$ruta
                    anexar=si
                    tomar_candado -x || exit 1
                    anunciar candado "$estado_candado"
                    anunciar fase anexando
                    command cat -- "$fragmento" >&"$candado" || exit 1
                    anunciar fase anexado
                    """), sudo, $"{rutaEscapada} {HuellaContenido(bytesUtf8)}", payloadBase64)
                : ConstruirComandoEscritura(rutaEscapada, bytesUtf8, sudo, overwrite, idMarcas);

            (Peticion peticion, InformeEscritura informe) = await EjecutarEscrituraAsync(destino, comando, idMarcas, ruta, "ssh_write_file", DescribirHerramienta("ssh_write_file", ruta), Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
            return peticion.CodigoSalida == CodigoContenidoNoIntegro
                ? throw new NativeSshException(MensajeContenidoNoIntegro(ruta, bytesUtf8.Length, append))
                : peticion.CodigoSalida == CodigoSinVerificador
                ? throw new NativeSshException(MensajeSinVerificador(ruta, append))
                : peticion.CodigoSalida == CodigoBashNoArranco && informe.IsSinAnuncios
                ? throw new NativeSshException(MensajeBashNoArranco(ruta, append))
                : peticion.CodigoSalida != 0
                ? throw new NativeSshException(informe.MensajeError($"Fallo al escribir en {ruta} (código {peticion.CodigoSalida}).", ruta, peticion.CodigoSalida, peticion.IdTexto))
                : append
                    ? new ResultadoEscritura(ruta, bytesUtf8.Length, null, false, $"Contenido anexado correctamente a: {ruta} ({bytesUtf8.Length} bytes).{informe.AvisoSinCandado()}{informe.AvisoVerificacion()}")
                    : new ResultadoEscritura(ruta, bytesUtf8.Length, null, informe.IsAtomica, $"Archivo escrito correctamente: {ruta} ({bytesUtf8.Length} bytes).{informe.AvisoExito()}");
        }

        [McpServerTool(Name = "ssh_search", ReadOnly = true, Idempotent = true)]
        [Description("Va por un canal exec propio, no por el shell de ssh_exec: no espera a sus comandos y funciona aunque el login de ese shell falle. Herramienta canónica y obligatoria para buscar cadenas de texto, funciones, clases o expresiones regulares en archivos del servidor remoto. Excluye automáticamente cachés, binarios (.pyc, .o), .git, node_modules y .venv, filtrando opcionalmente por extensión; las exclusiones pesan más que el filtro (con extension='lock' no se busca en ningún *.lock) y, como el filtro, se aplican a lo que hay dentro de la ruta, nunca a la ruta pedida (buscar en node_modules o en un archivo concreto lo busca ahí). Usa rg si el servidor lo tiene; si no, grep -P (PCRE) o, como último recurso, grep -E (ERE); sin grep de GNU (p. ej. busybox) recorre el árbol con find, con las mismas exclusiones, y busca con grep -E (ahí los binarios se omiten solo por extensión). La última línea indica el motor usado y, por tanto, la sintaxis de regex aplicada: '[motor: rg]' (regex de Rust, admite \\d y \\w, sin lookaround ni retroreferencias), '[motor: grep -P]' (PCRE) o '[motor: grep -E]' (ERE POSIX: usa [0-9] en lugar de \\d). Si la ruta indicada no existe o no se puede leer devuelve un error; lo ilegible dentro del árbol (directorios o archivos) se omite y la respuesta avisa de que pueden faltar coincidencias. Mucho más rápido y eficiente en tokens que grep.")]
        public async Task<string> BuscarAsync(
            [Description("usuario@servidor[:puerto], o un alias de ~/.ssh/config. Usa el mismo destino que en ssh_exec: un alias con otro IdentityFile es otra sesión, con su propio directorio de trabajo.")] string destino,
            [Description("Texto o patrón regex a buscar (sintaxis del motor indicado en la respuesta).")] string patron,
            [Description("Directorio o ruta donde buscar (por defecto '.' relativo a __NS_CWD).")] string ruta = ".",
            [Description("Filtro opcional por extensión o patrón glob (ej. 'py', 'ts', '*.json'); no se aplica si la ruta es un archivo.")] string? extension = null,
            [Description("Número máximo de coincidencias a devolver (por defecto 50, máx 200).")] int maxResultados = 50,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(patron))
                throw new NativeSshException("El patrón de búsqueda no puede estar vacío.");

            ExigirValorLiteral(patron, "El patrón");
            ExigirValorLiteral(ruta ?? string.Empty, "La ruta");
            ExigirValorLiteral(extension ?? string.Empty, "La extensión");
            int limite = Math.Clamp(maxResultados, 1, 200);
            string rutaEfectiva = string.IsNullOrEmpty(ruta) ? "." : ruta;
            string comando = ConstruirComandoBusqueda(patron, rutaEfectiva, extension, limite);
            SalidaLectura lectura = await EjecutarLecturaAsync(destino, comando, TimeoutBusquedaSegundos, "Probablemente hay líneas muy largas (p. ej. archivos minificados): refina el patrón, la extensión o la ruta.", cancellationToken).ConfigureAwait(false);
            return InterpretarBusqueda(lectura, patron, rutaEfectiva, limite);
        }

        private const string PrefijoMotor = "__NS_MOTOR:";
        private const string ExclusionDirectorios = ".git,node_modules,.venv,venv,__pycache__,dist,build,.next,.cache,bin,obj";
        private const string ExclusionArchivos = "'*.min.js','*.min.css','*.map','*.lock','package-lock.json','pnpm-lock.yaml'";

        // Sin grep de GNU no hay -I (omitir binarios por su contenido): en el recorrido con find se omiten por extensión.
        private const string ExclusionBinarios = "'*.pyc','*.pyo','*.o','*.so','*.a','*.class','*.jar','*.exe','*.dll','*.png','*.jpg','*.jpeg','*.gif','*.ico','*.pdf','*.zip','*.gz','*.tgz','*.xz','*.bz2','*.woff','*.woff2'";

        // Las mismas exclusiones para find: poda de los directorios y '! -name' de los archivos.
        private static readonly string PodaFind = "-type d \\( -name '" + ExclusionDirectorios.Replace(",", "' -o -name '", StringComparison.Ordinal) + "' \\) -prune";
        private static readonly string ExclusionArchivosFind = "! -name " + $"{ExclusionArchivos},{ExclusionBinarios}".Replace(",", " ! -name ", StringComparison.Ordinal);

        // Motor de búsqueda elegido en el servidor: rg si está instalado (regex de Rust), si no grep -P (PCRE) si el grep
        // lo admite, y como último recurso grep -E (ERE). Los tres con las mismas exclusiones, filtro de extensión y límite.
        // La primera línea anuncia el motor para que el LLM sepa qué sintaxis de regex se aplicó. Las exclusiones y -I de
        // grep son de GNU (se comprueba que las admite, no la versión): con otro grep (p. ej. busybox, que las rechaza con
        // su ayuda; Bloque S, P2) el árbol se recorre con find (poda de los mismos directorios, mismas exclusiones de
        // archivos y los binarios por extensión) y cada archivo se busca con grep -E; una regex inválida se detecta antes
        // con una línea de prueba (busybox no la compila sin datos que leer).
        // La ruta pedida nunca se excluye, en ninguno de los tres: las exclusiones y el filtro de extensión son para lo que
        // hay dentro de un directorio; un archivo pedido se busca siempre y sale como 'ruta:línea:texto'.
        // - Una ruta relativa que empieza por '-', '(', ')', '!' o ',' lleva './' delante en los tres motores (misma salida
        //   en todos): find la tomaría por una opción ('-d' es '-depth' en GNU) o un operador y, con su error callado,
        //   daba «sin coincidencias» o buscaba en todo el directorio actual.
        // - El grep de GNU aplica --exclude-dir, --exclude e --include también a las rutas de la línea de órdenes
        //   (compara el nombre, y lo que sigue a cada '/', con el patrón): un directorio pedido va con la barra final, con
        //   la que ningún patrón casa (sale igual, 'dir/archivo'), y un archivo pedido va sin esas opciones ni -r y con -H
        //   (con -r y un solo archivo, grep no da su nombre); si es un binario que casa (-I lo calla), lo dice como rg.
        // - rg y find ya buscan en la ruta pedida aunque se llame como un excluido. En rg manda el último glob que casa y
        //   un glob sin barra final casa con archivos y con directorios: van en este orden el filtro de extensión (o '*'),
        //   las exclusiones de archivos, esas mismas con la barra final como positivas (un directorio 'deps.lock' o
        //   'src.map' se recorre, como en grep de GNU, cuyo --exclude no se aplica a directorios, y en find, que solo
        //   excluye '-type f') y al final las de directorios, con la barra final de los globs de gitignore para que no
        //   excluyan un archivo que se llama igual (un script 'build' o 'bin'). Con el filtro detrás, '*' o '*.json'
        //   volvían a incluir .git o package-lock.json: las exclusiones pesan más que el filtro en los tres motores.
        // - En el recorrido con find, un error de find nunca es «sin coincidencias»: su stderr se filtra (con sus mensajes
        //   en inglés) y lo que grep -rs y rg callan se calla también (subdirectorio sin permiso o desaparecido a mitad,
        //   grep muerto por SIGPIPE, o por EPIPE si lo ignora, cuando head ya tiene bastantes líneas); cualquier otro
        //   error sale por stderr con el código 2. El código de find no sirve (es distinto de 0 si cualquier lote de grep
        //   no encontró nada): sin ese error sale con 0 y una salida vacía es «sin coincidencias». Si calló un
        //   subdirectorio sin permiso, sale con 2 sin stderr, como grep -rs y rg (la respuesta avisa de que pueden faltar
        //   coincidencias; A-undecies, AU3); un archivo ilegible lo calla el grep -s de cada lote y no se puede distinguir.
        internal static string ConstruirComandoBusqueda(string patron, string rutaEfectiva, string? extension, int limite)
        {
            string rutaMotor = rutaEfectiva[0] is '-' or '(' or ')' or '!' or ',' ? "./" + rutaEfectiva : rutaEfectiva;
            string rutaEscapada = Escapar(rutaMotor);
            string rutaPedidaEscapada = Escapar(rutaEfectiva);
            string directorioGrepEscapado = Escapar(rutaMotor.EndsWith('/') ? rutaMotor : rutaMotor + "/");
            string patronEscapado = Escapar(patron);
            // Sin filtro, rg recibe '*': con algún glob positivo (el que vuelve a admitir los directorios, abajo), rg omite
            // todo archivo que no case con ninguno.
            string filtroRg = "-g '*' ";
            string filtroGrep = string.Empty;
            string filtroFind = string.Empty;
            if (!string.IsNullOrEmpty(extension))
            {
                string filtro = extension.StartsWith("*.", StringComparison.Ordinal)
                    ? extension
                    : extension.StartsWith('.')
                        ? "*" + extension
                        : "*." + extension;
                string filtroEscapado = Escapar(filtro);
                filtroRg = $"-g {filtroEscapado} ";
                filtroGrep = $"--include={filtroEscapado} ";
                filtroFind = $"-name {filtroEscapado} ";
            }

            string archivosRg = ExclusionArchivos.Replace("'", string.Empty, StringComparison.Ordinal);
            string exclusionesRg = $"-g '!{{{archivosRg}}}' -g '{{{archivosRg}}}/' -g '!{{{ExclusionDirectorios}}}/'";
            string exclusionesGrep = $"--exclude-dir={{{ExclusionDirectorios}}} --exclude={{{ExclusionArchivos}}}";
            return PlantillaBash.Crear($$"""
                (
                {{DefinicionSinAcceso}}
                if [ ! -e {{rutaEscapada}} ]; then
                  sin_acceso {{rutaEscapada}} 0 {{rutaPedidaEscapada}} || printf 'Error: la ruta de búsqueda no existe: %s\n' {{rutaPedidaEscapada}} >&2
                  exit 1
                fi
                # La ruta pedida debe poder leerse (y recorrerse si es un directorio): los motores silencian los errores
                # de permisos y un "sin coincidencias" engañaría. Los archivos ilegibles dentro del árbol sí se omiten.
                if [ ! -r {{rutaEscapada}} ] || { [ -d {{rutaEscapada}} ] && [ ! -x {{rutaEscapada}} ]; }; then
                  printf 'Error: sin permiso de lectura en la ruta de búsqueda: %s\n' {{rutaPedidaEscapada}} >&2
                  exit 1
                fi
                # Código del motor, no de head; 141 (SIGPIPE) = head ya tenía suficientes líneas.
                if command -v rg >/dev/null 2>&1; then
                  printf '{{PrefijoMotor}}rg\n'
                  rg --no-config --line-number --no-heading --with-filename --color never --no-messages --no-ignore --hidden \
                    {{filtroRg}}{{exclusionesRg}} -m {{limite}} -e {{patronEscapado}} -- {{rutaEscapada}} | head -n {{limite + 1}}
                  rc=${PIPESTATUS[0]}
                elif printf 'x\n' | grep -q --exclude-dir=x --exclude=x --include=x -E x 2>/dev/null; then
                  sintaxis=E
                  printf '1\n' | grep -qP '\d' 2>/dev/null && sintaxis=P
                  printf '{{PrefijoMotor}}grep -%s\n' "$sintaxis"
                  if [ -d {{rutaEscapada}} ]; then
                    grep -rnIHs {{filtroGrep}}{{exclusionesGrep}} -m {{limite}} -"$sintaxis" -e {{patronEscapado}} -- {{directorioGrepEscapado}} | head -n {{limite + 1}}
                    rc=${PIPESTATUS[0]}
                  else
                    grep -nIHs -m {{limite}} -"$sintaxis" -e {{patronEscapado}} -- {{rutaEscapada}} | head -n {{limite + 1}}
                    rc=${PIPESTATUS[0]}
                    # -I calla un binario: pedido como ruta, se dice que casa (como rg) en vez de «sin coincidencias».
                    if [ "$rc" = 1 ] && grep -qs -"$sintaxis" -e {{patronEscapado}} -- {{rutaEscapada}}; then
                      printf '%s: binary file matches\n' {{rutaEscapada}}
                      rc=0
                    fi
                  fi
                else
                  printf '{{PrefijoMotor}}grep -E\n'
                  printf 'x\n' | grep -E -e {{patronEscapado}} >/dev/null
                  [ "$?" = 2 ] && exit 2
                  if [ -d {{rutaEscapada}} ]; then
                    # Mensajes de find en inglés para reconocerlos (LC_MESSAGES=C). Si el idioma lo fija LC_ALL, pasa a
                    # LANG: grep conserva así la codificación y el orden (LC_CTYPE, LC_COLLATE), que cambian lo que casa.
                    if [ -n "${LC_ALL-}" ]; then
                      export LANG=$LC_ALL
                      unset LC_ALL LC_CTYPE LC_COLLATE
                    fi
                    # Al filtro llegan los errores de find (directorios) y los del grep del -exec (archivos; sin -s, para que un
                    # archivo ilegible también se avise; hereda LC_MESSAGES=C de find). Sale con 2 si dejó pasar algún error,
                    # con 3 si solo calló un «Permission denied» y con 0 si no hubo nada (o solo lo que nunca es error:
                    # desaparecido a mitad o SIGPIPE).
                    { LC_MESSAGES=C find -H {{rutaEscapada}} -mindepth 1 {{PodaFind}} -o -type f {{filtroFind}}{{ExclusionArchivosFind}} \
                        -exec grep -nH -m {{limite}} -E -e {{patronEscapado}} -- {} + 2>&1 >&3 3>&- | {
                        ilegible=0 otro=0
                        while IFS= read -r linea || [ -n "$linea" ]; do
                          case $linea in
                            *': Permission denied') ilegible=1 ;;
                            *': No such file or directory'|*' terminated by signal 13'|*': Broken pipe') ;;
                            *) printf '%s\n' "$linea" >&2; otro=1 ;;
                          esac
                        done
                        [ "$otro" = 1 ] && exit 2
                        [ "$ilegible" = 1 ] && exit 3
                        exit 0
                      }
                    } 3>&1 | head -n {{limite + 1}}
                    # 2 sin stderr = algo ilegible callado, como grep -rs y rg.
                    [ "${PIPESTATUS[0]}" = 0 ] && rc=0 || rc=2
                  else
                    grep -nHs -m {{limite}} -E -e {{patronEscapado}} -- {{rutaEscapada}} | head -n {{limite + 1}}
                    rc=0
                  fi
                fi
                [ "$rc" -eq 141 ] && rc=0
                exit "$rc"
                )
                """);
        }

        // Códigos de rg y grep: 0 = coincidencias, 1 = ninguna, 2 = error (con -s/--no-messages, archivos ilegibles o regex inválida).
        private static string InterpretarBusqueda(SalidaLectura lectura, string patron, string rutaEfectiva, int limite)
        {
            int codigo = lectura.Codigo;
            string salida = lectura.Salida;
            string motor = "desconocido";
            string resultados = salida;
            if (salida.StartsWith(PrefijoMotor, StringComparison.Ordinal))
            {
                int finLinea = salida.IndexOf('\n', StringComparison.Ordinal);
                bool isSinResultados = finLinea < 0;
                motor = isSinResultados ? salida[PrefijoMotor.Length..] : salida[PrefijoMotor.Length..finLinea];
                resultados = isSinResultados ? string.Empty : salida[(finLinea + 1)..];
            }

            resultados = QuitarSaltoFinal(resultados);

            string pie = $"[motor: {motor}]";
            string errores = lectura.Errores.Trim();
            bool isVacio = string.IsNullOrEmpty(resultados);
            bool hasErrores = !string.IsNullOrEmpty(errores);
            // Sin resultados y con stderr es un error (ruta inexistente, regex inválida...).
            if (codigo != 0 && isVacio && hasErrores)
                throw new NativeSshException(motor == "desconocido" ? errores : $"{errores}\n{pie}");

            // Un 2 con stderr y resultados: el motor falló en parte del árbol (p. ej. un error de E/S de find en el recorrido
            // sin grep de GNU). Los resultados valen, pero pueden faltar coincidencias: se dice, con el error. Un 2 sin
            // stderr (con o sin resultados) es un error que -s/--no-messages calla, como un archivo o directorio ilegible:
            // también pueden faltar, y se dice sin detalle (A-undecies, AU3).
            string avisoErrores = codigo != 2 ? string.Empty
                : hasErrores ? $"[Aviso: la búsqueda terminó con errores, así que pueden faltar coincidencias: {AcotarErrores(errores)}]\n"
                : AvisoCodigoDosSinDetalle + "\n";

            // 0 sin resultados: el recorrido con find sin grep de GNU, que no da un código de «sin coincidencias».
            if (codigo is 0 or 1 or 2 && isVacio)
                return $"No se encontraron coincidencias para el patrón: '{patron}' en '{rutaEfectiva}'.\n\n{avisoErrores}{pie}";

            if (codigo is not (0 or 2))
                throw new NativeSshException(lectura.Error("Error en búsqueda"));
            string[] lineas = resultados.Split('\n');
            string avisoLimite = $"[Límite alcanzado: se muestran {limite} coincidencias. Refina el patrón o subdirectorio para acotar la búsqueda.]";
            return lineas.Length > limite
                ? $"{string.Join('\n', lineas.AsSpan(0, limite))}\n\n{avisoLimite}\n{avisoErrores}{pie}"
                : lineas.Length == limite
                    ? $"{resultados}\n\n{avisoLimite}\n{avisoErrores}{pie}"
                    : $"{resultados}\n\n{avisoErrores}{pie}";
        }

        private const int MaxCaracteresErroresBusqueda = 1000;

        private const string AvisoCodigoDosSinDetalle = "[Aviso: el motor terminó con código 2 sin mostrar el error (calla así los archivos o directorios que no puede leer, p. ej. por permisos), así que pueden faltar coincidencias.]";

        // El stderr de la búsqueda en un aviso, acotado a su principio sin partir un par sustituto (un emoji, p. ej.).
        private static string AcotarErrores(string errores) =>
            errores.Length <= MaxCaracteresErroresBusqueda ? errores : string.Concat(errores.AsSpan(0, char.IsHighSurrogate(errores[MaxCaracteresErroresBusqueda - 1]) ? MaxCaracteresErroresBusqueda - 1 : MaxCaracteresErroresBusqueda), "…");

        private static string Escapar(string valor) => "'" + valor.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        // Solo se quita el salto de línea que cierra la última línea de un listado: los blancos y separadores al principio
        // del primer nombre (o línea encontrada) y al final del último son datos.
        private static string QuitarSaltoFinal(string salida) => salida.EndsWith('\n') ? salida[..^1] : salida;

        // Fin de los listados de ssh_find y ssh_list_dir: lee los nombres que da 'find -print0' (ya ordenados con 'sort -z'),
        // les quita el "./" inicial y escribe como mucho 'maximo', terminados en NUL. Un nombre con '\n' sigue siendo uno
        // solo: con 'sed | sort | head -n' se partía en dos líneas, que además se ordenaban por separado.
        private static string LimitarNombres(int maximo) =>
            PlantillaBash.Crear($$"""{ n=0; while [ "$n" -lt {{maximo}} ] && IFS= read -r -d '' nombre; do printf '%s\0' "${nombre#./}"; n=$((n + 1)); done; }""");

        // Como LimitarNombres, para ssh_list_dir con incluirArchivos=true (RC7): un directorio sale con '/' al final; un enlace
        // simbólico, aunque lleve a un directorio, no ([ -L ] mira el último componente: lo que hay dentro de un enlace a un
        // directorio sí se marca). La barra va dentro del nombre, así que un nombre escapado como cadena JSON la lleva dentro.
        private static string LimitarNombresMarcandoDirectorios(int maximo) =>
            PlantillaBash.Crear($$"""{ n=0; while [ "$n" -lt {{maximo}} ] && IFS= read -r -d '' nombre; do barra=; [ -d "$nombre" ] && [ ! -L "$nombre" ] && barra=/; printf '%s%s\0' "${nombre#./}" "$barra"; n=$((n + 1)); done; }""");

        // Nombres de un listado terminados en NUL (el último NUL no deja un nombre vacío detrás).
        private static string[] SepararNombres(string salida) =>
            salida.Length == 0 ? [] : (salida.EndsWith('\0') ? salida[..^1] : salida).Split('\0');

        // Los 'cantidad' primeros nombres, uno por línea (EscaparNombre).
        private static string UnirNombres(ReadOnlySpan<string> nombres, int cantidad)
        {
            StringBuilder listado = new();
            int total = Math.Min(nombres.Length, cantidad);
            for (int i = 0; i < total; i++)
            {
                if (i > 0)
                    _ = listado.Append('\n');
                EscaparNombre(listado, nombres[i]);
            }

            return listado.ToString();
        }

        // Un nombre de un listado en una sola línea: tal cual, salvo si contiene caracteres de control (categoría Cc:
        // U+0000–U+001F y U+007F–U+009F, como '\n', '\r', '\t' o NEL), los separadores de línea y de párrafo de Unicode
        // (U+2028 y U+2029, que no son de control pero muchos lectores toman como un salto) o empieza por comillas dobles o
        // por '[' (así ningún nombre imita las líneas '[Límite alcanzado: …]' y '[montajes_no_explorados: …]' que añade
        // native_ssh); entonces va entre comillas dobles y escapado como una cadena JSON (\n, \r, \t, \f, \b, \uXXXX, \" y \\),
        // que se puede pegar tal cual como cadena JSON. Un nombre sin escapar nunca empieza por comillas, así que no hay ambigüedad;
        // una barra invertida o unas comillas en medio (p. ej. las unidades de systemd con \x2d) no cambian el nombre.
        private static void EscaparNombre(StringBuilder listado, string nombre)
        {
            ReadOnlySpan<char> caracteres = nombre;
            bool hasControl = caracteres.ContainsAnyInRange('\u0000', '\u001f') || caracteres.ContainsAnyInRange('\u007f', '\u009f') || caracteres.ContainsAnyInRange('\u2028', '\u2029');
            if (!hasControl && !nombre.StartsWith('"') && !nombre.StartsWith('['))
            {
                _ = listado.Append(nombre);
                return;
            }

            _ = listado.Append('"');
            foreach (char c in nombre)
                _ = c switch
                {
                    '"' => listado.Append("\\\""),
                    '\\' => listado.Append("\\\\"),
                    '\n' => listado.Append("\\n"),
                    '\r' => listado.Append("\\r"),
                    '\t' => listado.Append("\\t"),
                    '\f' => listado.Append("\\f"),
                    '\b' => listado.Append("\\b"),
                    _ when char.IsControl(c) || c is '\u2028' or '\u2029' => listado.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)),
                    _ => listado.Append(c)
                };
            _ = listado.Append('"');
        }

        // Un valor que va dentro del script debe llegar al servidor tal cual. Un NUL no cabe en un argumento y bash lo
        // descarta al decodificar el script con '$(... | base64 -d)': la ruta o el patrón serían otros. Se rechaza antes de
        // enviar nada, igual que un texto que no es UTF-16 válido (ExigirUtf16Valido).
        private static void ExigirValorLiteral(string valor, string nombre)
        {
            if (valor.Contains('\0', StringComparison.Ordinal))
                throw new NativeSshException($"{nombre} contiene un carácter NUL (\\0), que no puede formar parte de una ruta ni de un patrón.");

            ExigirUtf16Valido(valor, nombre);
        }

        // Un sustituto UTF-16 suelto no tiene representación en UTF-8: al codificarlo se cambiaría por U+FFFD y la ruta, el
        // patrón o el contenido llegarían cambiados. Se rechaza antes de enviar nada.
        private static void ExigirUtf16Valido(string valor, string nombre)
        {
            try
            {
                _ = Utf8Estricto.GetByteCount(valor);
            }
            catch (EncoderFallbackException)
            {
                throw new NativeSshException($"{nombre} contiene un carácter UTF-16 inválido (un sustituto suelto) que no se puede representar en UTF-8: llegaría cambiado por U+FFFD. No se ha enviado nada.");
            }
        }

        [McpServerTool(Name = "ssh_list_dir", ReadOnly = true, Idempotent = true)]
        [Description("Va por un canal exec propio, no por el shell de ssh_exec: no espera a sus comandos y funciona aunque el login de ese shell falle. Herramienta canónica y obligatoria para explorar la topología jerárquica de carpetas de un proyecto con poda automática de dependencias y profundidad controlada. Sustituye a 'ls -la' y 'tree'. Si la ruta no existe, no se puede atravesar o no se puede listar, devuelve un error que lo indica (nunca un listado vacío). Cada nombre ocupa una línea; con incluirArchivos=true, los directorios llevan '/' al final (la ruta con la '/' sirve igual en otra herramienta). Si un nombre contiene caracteres de control (salto de línea, retorno de carro, tabulador...) o los separadores de línea y de párrafo de Unicode (U+2028, U+2029), o empieza por comillas dobles o por '[', sale entre comillas dobles y escapado como una cadena JSON (el salto de línea como \\n, la barra invertida como \\\\), que se puede usar tal cual como cadena JSON de la ruta en otra herramienta; los demás nombres salen tal cual.")]
        public async Task<string> ListarDirectoriosAsync(
            [Description("usuario@servidor[:puerto], o un alias de ~/.ssh/config. Usa el mismo destino que en ssh_exec: un alias con otro IdentityFile es otra sesión, con su propio directorio de trabajo.")] string destino,
            [Description("Ruta base donde explorar (por defecto '.' relativo a __NS_CWD).")] string ruta = ".",
            [Description("Profundidad máxima de recursión (por defecto 3, entre 1 y 10).")] int profundidadMax = 3,
            [Description("Número máximo de directorios a devolver (por defecto 100, máx 200).")] int maxResultados = 100,
            [Description("Si es true, incluye también los archivos junto a los directorios (sustituye completamente a 'ls' o 'tree') y cada directorio sale con '/' al final ('src/'; un enlace simbólico a un directorio no lleva '/': es un enlace, ssh_stat lo dice). Por defecto false (solo directorios, sin '/').")] bool incluirArchivos = false,
            CancellationToken cancellationToken = default)
        {
            ExigirValorLiteral(ruta ?? string.Empty, "La ruta");
            int profundidad = Math.Clamp(profundidadMax, 1, 10);
            int limite = Math.Clamp(maxResultados, 1, 200);
            string rutaEfectiva = string.IsNullOrEmpty(ruta) ? "." : ruta;
            string rutaEscapada = Escapar(rutaEfectiva);
            string filtroTipo = incluirArchivos ? string.Empty : "-type d ";

            string comando = PlantillaBash.Crear($$"""
                (
                {{DefinicionSinAcceso}}
                if [ ! -e {{rutaEscapada}} ]; then
                  sin_acceso {{rutaEscapada}} || printf 'Error: la ruta de exploración no existe: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                if [ ! -d {{rutaEscapada}} ]; then
                  printf 'Error: la ruta no es un directorio: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                # find descarta sus errores: sin poder leer y recorrer el directorio, "No se encontraron" engañaría.
                if [ ! -r {{rutaEscapada}} ] || [ ! -x {{rutaEscapada}} ]; then
                  printf 'Error: sin permiso para listar el directorio: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                cd -- {{rutaEscapada}} && \
                find -L . -maxdepth {{profundidad}} -mindepth 1 \
                  \( -name __pycache__ -o -name .cache \) -prune \
                  -o \( -name .git -o -name node_modules -o -name .venv -o -name venv -o -name dist -o -name build -o -name .next -o -name bin -o -name obj \) -print0 -prune \
                  -o {{filtroTipo}}-print0 2>/dev/null | sort -z | {{(incluirArchivos ? LimitarNombresMarcandoDirectorios(limite + 1) : LimitarNombres(limite + 1))}}
                )
                """);

            SalidaLectura lectura = await EjecutarLecturaAsync(destino, comando, TimeoutBusquedaSegundos, "Refina la ruta o reduce la profundidad.", cancellationToken).ConfigureAwait(false);
            if (lectura.Codigo != 0)
                throw new NativeSshException(lectura.Error("Error al listar directorios"));

            string[] nombres = SepararNombres(lectura.Salida);
            string etiquetaVacio = incluirArchivos ? "elementos" : "subdirectorios";
            if (nombres.Length == 0)
                return $"No se encontraron {etiquetaVacio} en '{rutaEfectiva}'.";

            string listado = UnirNombres(nombres, limite);
            string etiquetaLimite = incluirArchivos ? "elementos" : "directorios";
            return nombres.Length > limite
                ? $"{listado}\n\n[Límite alcanzado: se muestran {limite} {etiquetaLimite} (profundidad máx: {profundidad}). Refina la ruta o profundidad para acotar.]"
                : listado;
        }

        [McpServerTool(Name = "ssh_find", ReadOnly = true, Idempotent = true)]
        [Description("Va por un canal exec propio, no por el shell de ssh_exec: no espera a sus comandos y funciona aunque el login de ese shell falle. Herramienta canónica y obligatoria para localizar archivos o carpetas por nombre o patrón glob (ej. '*.service', 'docker-compose*.yml', '.env*') y archivos por tamaño (tamanoMin/tamanoMax: cada resultado con su tamaño, de mayor a menor; p. ej. para liberar espacio en disco). Poda automáticamente carpetas pesadas (.git, node_modules, .venv, bin, obj) y devuelve rutas relativas limpias. Mucho más rápido que find en shell. Nunca entra en los sistemas de ficheros virtuales del kernel que haya por debajo de la ruta (/proc, /sys, /dev, cgroup, debugfs, tracefs, autofs...; con ruta='/' es lo que evita recorrer el sistema una vez por proceso a través de /proc), salvo el que contenga la propia ruta pedida (ruta='/proc' sí busca en /proc). Con cruzarMontajes=true (por defecto) recorre los demás sistemas de ficheros montados por debajo de la ruta; con cruzarMontajes=false no sale del sistema de ficheros de la ruta (como 'find -xdev'; también deja fuera los subvolúmenes btrfs, que tienen otro dispositivo). Si algún montaje quedó sin recorrer, la última línea de la respuesta es '[montajes_no_explorados: /proc, /sys, …]' (sin repetidos ni montajes anidados, con tope): para buscar dentro de uno de ellos, repite ssh_find con ruta igual a ese montaje. Si la ruta no existe, no se puede atravesar o no se puede recorrer, o si find no termina bien (p. ej. no llega a ejecutarse), devuelve un error que lo indica (nunca un «no se encontraron» engañoso). Cada nombre ocupa una línea: si contiene caracteres de control (salto de línea, retorno de carro, tabulador...) o los separadores de línea y de párrafo de Unicode (U+2028, U+2029), o empieza por comillas dobles o por '[', sale entre comillas dobles y escapado como una cadena JSON (el salto de línea como \\n, la barra invertida como \\\\), que se puede usar tal cual como cadena JSON de la ruta en otra herramienta; los demás nombres salen tal cual.")]
        public async Task<string> BuscarPorNombreAsync(
            [Description("usuario@servidor[:puerto], o un alias de ~/.ssh/config. Usa el mismo destino que en ssh_exec: un alias con otro IdentityFile es otra sesión, con su propio directorio de trabajo.")] string destino,
            [Description("Patrón de nombre o glob a buscar (ej. '*.service', 'docker-compose.yml', '*.conf').")] string patron,
            [Description("Ruta base donde buscar (por defecto '.' relativo a __NS_CWD).")] string ruta = ".",
            [Description("Tipo de elemento a buscar: 'file' (archivos), 'dir' o 'directory' (directorios) o 'all' (ambos), sin distinguir mayúsculas. Por defecto (o vacío), 'file'. Otro valor, también con blancos alrededor, da error.")] string tipo = "file",
            [Description("Profundidad máxima de recursión (por defecto 10, entre 1 y 20).")] int profundidadMax = 10,
            [Description("Número máximo de resultados a devolver (por defecto 100, máx 200).")] int maxResultados = 100,
            [Description("true (por defecto): entra también en los demás sistemas de ficheros montados por debajo de la ruta (nunca en los virtuales del kernel). false: no sale del sistema de ficheros de la ruta, como 'find -xdev'; úsalo para ir montaje por montaje con la línea '[montajes_no_explorados: …]'.")] bool cruzarMontajes = true,
            [Description("Tamaño mínimo, inclusivo: un entero de bytes o con unidad k, M o G (potencias de 1024), p. ej. '50M' (como 'find -size'). Solo con tipo='file'. Con tamanoMin o tamanoMax cada resultado sale como '<tamaño><tabulador><ruta>' y ordenados de mayor a menor (con el tope, los más grandes): úsalo para liberar espacio en disco.")] string? tamanoMin = null,
            [Description("Tamaño máximo, inclusivo, con el mismo formato que tamanoMin (p. ej. '0' para los archivos vacíos). Solo con tipo='file'; también ordena de mayor a menor y muestra el tamaño.")] string? tamanoMax = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(patron))
                throw new NativeSshException("El patrón de búsqueda no puede estar vacío.");

            ExigirValorLiteral(patron, "El patrón");
            ExigirValorLiteral(ruta ?? string.Empty, "La ruta");
            int profundidad = Math.Clamp(profundidadMax, 1, 20);
            int limite = Math.Clamp(maxResultados, 1, 200);
            string rutaEfectiva = string.IsNullOrEmpty(ruta) ? "." : ruta;
            // Sin tipo, 'file'; sin distinguir mayúsculas y con 'directory' como sinónimo de 'dir' (lo dice la descripción); un
            // tipo desconocido es un error, no una búsqueda de archivos.
            string filtroTipo = (string.IsNullOrEmpty(tipo) ? "file" : tipo.ToLowerInvariant()) switch
            {
                "file" => "-type f ",
                "dir" or "directory" => "-type d ",
                "all" => string.Empty,
                _ => throw new NativeSshException($"Tipo '{tipo}' no válido: usa 'file', 'dir' (o 'directory') o 'all', sin distinguir mayúsculas.")
            };

            long? minBytes = LeerTamano(tamanoMin, "tamanoMin");
            long? maxBytes = LeerTamano(tamanoMax, "tamanoMax");
            bool isConTamano = minBytes is not null || maxBytes is not null;
            if (isConTamano && filtroTipo != "-type f ")
                throw new NativeSshException("tamanoMin y tamanoMax solo se admiten con tipo='file' (el tamaño de un directorio es el de su entrada, no el de su contenido; para eso, 'du' con ssh_exec).");

            if (minBytes > maxBytes)
                throw new NativeSshException($"tamanoMin ({tamanoMin}) es mayor que tamanoMax ({tamanoMax}): ningún archivo puede cumplir los dos.");

            string idMarca = Guid.NewGuid().ToString("N");
            string comando = ConstruirComandoBusquedaNombre(patron, rutaEfectiva, filtroTipo, profundidad, limite, cruzarMontajes, idMarca, minBytes, maxBytes);
            SalidaLectura lectura = await EjecutarLecturaAsync(destino, comando, TimeoutBusquedaSegundos, "Refina la ruta, el patrón o la profundidad.", cancellationToken).ConfigureAwait(false);
            return lectura.Codigo != 0
                ? throw new NativeSshException(lectura.Error("Error en búsqueda por nombre"))
                : InterpretarBusquedaNombre(lectura.Salida, idMarca, patron, rutaEfectiva, profundidad, limite, cruzarMontajes, isConTamano);
        }

        // tamanoMin/tamanoMax de ssh_find (RC9): un entero de bytes o con unidad k, M o G (potencias de 1024), sin blancos, signo
        // ni decimales; null si no se pidió. Se convierte a bytes aquí: el find de busybox no admite M ni G y redondea k
        // (medido: '-size +1k' no da un archivo de 1024 bytes), así que el script usa siempre bytes ('c').
        private static long? LeerTamano(string? tamano, string nombre)
        {
            if (string.IsNullOrEmpty(tamano))
                return null;

            ReadOnlySpan<char> cifras = tamano;
            long multiplicador = cifras[^1] switch
            {
                'k' => 1024L,
                'M' => 1024L * 1024L,
                'G' => 1024L * 1024L * 1024L,
                _ => 1L
            };
            if (multiplicador > 1)
                cifras = cifras[..^1];

            bool isValido = cifras.Length is > 0 and <= 15 && !cifras.ContainsAnyExceptInRange('0', '9');
            return isValido && long.Parse(cifras, NumberStyles.None, CultureInfo.InvariantCulture) is var numero && numero <= long.MaxValue / multiplicador
                ? numero * multiplicador
                : throw new NativeSshException($"{nombre} '{tamano}' no es válido: un entero de bytes o con unidad k, M o G (potencias de 1024) pegada, sin blancos, signo ni decimales; p. ej. '500', '64k', '50M' o '2G'.");
        }

        // Tipos de los sistemas de ficheros virtuales del kernel que ssh_find nunca recorre por debajo de la ruta (C13): con
        // 'find -L', /proc/<pid>/cwd y /proc/<pid>/task/<tid>/cwd llevan al directorio de trabajo de cada proceso y el árbol
        // se recorría una vez por proceso (find solo salta los enlaces a un antecesor). autofs, además, montaría lo que vigila.
        private const string TiposVirtuales = "proc sysfs devtmpfs devpts cgroup cgroup2 debugfs tracefs securityfs pstore bpf mqueue hugetlbfs configfs fusectl binfmt_misc autofs efivarfs selinuxfs nsfs rpc_pipefs";

        private const string PrefijoMarcaMontaje = "__NS_MONTAJE_";
        private const string PrefijoMarcaTotalMontajes = "__NS_TOTAL_MONTAJES_";
        private const string PrefijoMarcaSinTabla = "__NS_SIN_TABLA_";
        private const string PrefijoMarcaFindFallo = "__NS_FIND_FALLO_";
        private const int MaxMontajesNoExplorados = 20;

        // explorar_montajes CRUZAR PROFUNDIDAD: con el directorio actual en la raíz de la búsqueda (ya resuelta en 'raiz', sin
        // enlaces), lee /proc/self/mountinfo y, para cada punto de montaje estrictamente por debajo de 'raiz' y a menos de
        // PROFUNDIDAD niveles (más abajo find no entra de todos modos), deja en 'poda' los argumentos de find que podan los
        // virtuales (por su ruta relativa, con los comodines de -path escapados) y en 'no_explorados' los que no se
        // recorrerán: los virtuales y, con CRUZAR=0, los demás directorios montados cuyo dispositivo (stat, como compara
        // 'find -xdev') no es el de la raíz (un bind del mismo sistema de ficheros sí se recorre). Un montaje que contiene a
        // la raíz no está por debajo: ruta='/proc' no se poda. En mountinfo el punto de montaje va con el espacio, el
        // tabulador, el salto de línea y la barra invertida en octal (\040, \011, \012, \134), que printf %b de bash
        // decodifica (no hay otras secuencias con '\': la barra invertida misma va como \134). El tipo es el primer campo tras el separador ' - ' (las opciones no llevan
        // espacios). Solo cuentan los directorios (un contenedor monta también archivos sueltos, como /dev/null): se mira
        // con -d cada montaje dentro de la profundidad que se recorre (find lo consultaría igual al recorrer su padre); el
        // dispositivo (stat %d, por lotes en comparar_dispositivos) solo con CRUZAR=0 y para los no virtuales. /dev/fd, si
        // es un enlace (a /proc/self/fd), se poda siempre por su ruta: no es un montaje y no sale en la línea.
        // Sin /proc/self/mountinfo (busybox sin /proc, chroot): se podan /proc, /sys y /dev por su ruta y 'sin_tabla' lo
        // anota (con CRUZAR=0 no se puede saber qué otros montajes quedan fuera).
        // La tabla se lee de una vez con cat y se parte en líneas sin 'read': con 'read' directamente sobre
        // /proc/self/mountinfo, cada lectura vuelve a generar la tabla desde el principio (read lee poco y retrocede), con
        // coste cuadrático (medido: 5 000 montajes, más de 20 s solo en leerla). Sin <(…) ni tuberías hacia el bucle: la
        // sustitución de procesos necesita /dev/fd y un bucle al final de una tubería no deja sus variables. En la tabla
        // ninguna línea está vacía ni lleva saltos o espacios dentro de un campo (van en octal), así que se parte por saltos
        // de línea y por espacios (con set -f, sin expandir comodines). En 'no_explorados' cada punto va como en la tabla
        // (con el octal) para ordenarlo y quitar repetidos y anidados (escribir_no_explorados).
        private static readonly string DefinicionMontajes = $$"""
            montaje() {
              local punto=$1 tipo=$2 crudo=$3 rel nivel
              case $raiz in
                /) [ "$punto" = / ] && return 0; rel=.$punto ;;
                *) case $punto in
                     "$raiz"/*) rel=.${punto#"$raiz"} ;;
                     *) return 0 ;;
                   esac ;;
              esac
              nivel=${rel//[!\/]/}
              [ "${#nivel}" -lt "$profundidad_montajes" ] || return 0
              [ -d "$punto" ] || return 0
              case " {{TiposVirtuales}} " in
                *" $tipo "*)
                  podar "$rel"
                  no_explorados+=("$crudo")
                  return 0 ;;
              esac
              [ "$cruzar_montajes" = 1 ] && return 0
              candidatos+=("$punto")
              candidatos_crudo+=("$crudo")
            }
            # Con CRUZAR=0, el dispositivo de cada directorio montado no virtual, con un stat por cada 500 (uno por montaje
            # eran miles de procesos con miles de montajes); si alguno del lote falla, ese lote se repite uno a uno. Un
            # montaje cuyo dispositivo no se puede leer cuenta como no explorado.
            comparar_dispositivos() {
              local i j n lote dispositivos IFS=$' \n'
              for ((i = 0; i < ${#candidatos[@]}; i += 500)); do
                lote=("${candidatos[@]:i:500}")
                n=${#lote[@]}
                dispositivos=($(stat -L -c %d -- "${lote[@]}" 2>/dev/null))
                if [ "${#dispositivos[@]}" != "$n" ]; then
                  dispositivos=()
                  for ((j = 0; j < n; j++)); do
                    dispositivos+=("$(stat -L -c %d -- "${lote[j]}" 2>/dev/null)")
                  done
                fi
                for ((j = 0; j < n; j++)); do
                  [ -n "${dispositivos[j]}" ] && [ "${dispositivos[j]}" = "$dispositivo_raiz" ] && continue
                  no_explorados+=("${candidatos_crudo[i + j]}")
                done
              done
            }
            podar() {
              local glob
              glob=${1//\\/\\\\}; glob=${glob//\*/\\*}; glob=${glob//\?/\\?}; glob=${glob//\[/\\[}
              poda+=(-o -path "$glob")
            }
            explorar_montajes() {
              local punto crudo tipo linea lineas campos tabla IFS
              cruzar_montajes=$1 profundidad_montajes=$2
              poda=() no_explorados=() candidatos=() candidatos_crudo=() sin_tabla=
              dispositivo_raiz=$(stat -L -c %d . 2>/dev/null)
              if [ -r /proc/self/mountinfo ]; then
                tabla=$(cat /proc/self/mountinfo)
                set -f
                IFS=$'\n'
                lineas=($tabla)
                IFS=' '
                for linea in "${lineas[@]}"; do
                  tipo=${linea#* - }
                  tipo=${tipo%% *}
                  campos=($linea)
                  crudo=${campos[4]}
                  printf -v punto '%b' "$crudo"
                  montaje "$punto" "$tipo" "$crudo"
                done
                set +f
              else
                sin_tabla=1
                for punto in /proc /sys /dev; do
                  [ -d "$punto" ] && montaje "$punto" proc "$punto"
                done
              fi
              comparar_dispositivos
              # /dev/fd lleva a /proc/self/fd, donde el descriptor de un directorio abierto por find vuelve a un directorio
              # ya recorrido (el find de busybox no detecta ese bucle): con /dev sin podar (un tmpfs, como en los
              # contenedores), se poda el enlace.
              case $raiz in
                /) [ -L /dev/fd ] && podar ./dev/fd ;;
                /dev) [ -L /dev/fd ] && podar ./fd ;;
              esac
              [ "${#poda[@]}" = 0 ] || poda[0]='('
              [ "${#poda[@]}" = 0 ] || poda+=(')')
            }
            # escribir_no_explorados MARCA MARCA_TOTAL: los montajes no recorridos, sin repetidos ni los que están dentro de
            # otro de la lista (tampoco se recorren), cada uno con MARCA delante y como mucho {{MaxMontajesNoExplorados}}, y después
            # MARCA_TOTAL con cuántos hay: la salida no crece con el número de montajes. Se ordenan con '/' cambiada por un
            # espacio (en la tabla el espacio va en octal), así cada montaje queda justo delante de los que tiene dentro y basta
            # compararlo con el último que se conservó.
            escribir_no_explorados() {
              [ "${#no_explorados[@]}" = 0 ] && return 0
              printf '%s\n' "${no_explorados[@]}" | tr / ' ' | LC_ALL=C sort | tr ' ' / | {
                local crudo punto ultimo= total=0
                while IFS= read -r crudo; do
                  if [ -n "$ultimo" ]; then
                    case $crudo in "$ultimo"|"$ultimo"/*) continue ;; esac
                  fi
                  ultimo=$crudo
                  total=$((total + 1))
                  [ "$total" -le {{MaxMontajesNoExplorados}} ] || continue
                  printf -v punto '%b' "$crudo"
                  printf '%s%s\0' "$1" "$punto"
                done
                printf '%s%s\0' "$2" "$total"
              }
            }
            """.ReplaceLineEndings("\n");

        // Script de ssh_find. Tras resolver la raíz (pwd -P) y los montajes (explorar_montajes), find recorre '.' con la poda
        // de los virtuales delante ('\( -path ./proc -o … \)', «imprimir pero podar» como los directorios pesados) y, con
        // cruzarMontajes=false, -xdev. Los montajes no recorridos salen detrás de los nombres, cada uno como un nombre más
        // con la marca '__NS_MONTAJE_<id>__' (id aleatorio por petición: ningún archivo la puede imitar; solo los 20 primeros)
        // seguidos de '__NS_TOTAL_MONTAJES_<id>__<n>', y '__NS_SIN_TABLA_<id>__' si no se pudo leer la tabla de montajes.
        // Con tamaño (minBytes/maxBytes, RC9): '-size +Nc'/'-size -Nc' en bytes (estrictos en GNU y busybox, de ahí el ±1 para
        // que sean inclusivos) y cada resultado sale como '<bytes>\t<ruta>': con el -printf de GNU find, o, si find no lo tiene
        // (busybox), con un 'stat -L -c %s' por resultado (un archivo que desaparece entretanto se omite). Se ordenan TODOS
        // por tamaño, de mayor a menor, antes del tope: lo que se muestra son de verdad los más grandes.
        internal static string ConstruirComandoBusquedaNombre(string patron, string rutaEfectiva, string filtroTipo, int profundidad, int limite, bool isCruzarMontajes, string idMarca, long? minBytes = null, long? maxBytes = null)
        {
            string rutaEscapada = Escapar(rutaEfectiva);
            string patronEscapado = Escapar(patron);
            string xdev = isCruzarMontajes ? string.Empty : "-xdev ";
            bool isConTamano = minBytes is not null || maxBytes is not null;
            string filtroTamano = (minBytes is > 0 ? $"-size +{minBytes - 1}c " : string.Empty) + (maxBytes is { } maximo ? $"-size -{maximo + 1}c " : string.Empty);
            string imprimir = isConTamano ? "\"${imprimir[@]}\"" : "-print0";
            string elegirImpresion = isConTamano
                ? """imprimir=(-print0); con_stat=1; if find . -maxdepth 0 -printf '' >/dev/null 2>&1; then imprimir=(-printf '%s\t%p\0'); con_stat=; fi"""
                : string.Empty;
            string ordenar = isConTamano
                ? $"{{ if [ -n \"$con_stat\" ]; then while IFS= read -r -d '' n; do t=$(stat -L -c %s -- \"$n\" 2>/dev/null) && printf '%s\\t%s\\0' \"$t\" \"$n\"; done; else cat; fi; }} | sort -z -t \"$(printf '\\t')\" -k1,1nr | {LimitarNombres(limite + 1)}"
                : $"sort -z | {LimitarNombres(limite + 1)}";
            return PlantillaBash.Crear($$"""
                (
                {{DefinicionSinAcceso}}
                if [ ! -e {{rutaEscapada}} ]; then
                  sin_acceso {{rutaEscapada}} || printf 'Error: la ruta de búsqueda no existe: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                # find descarta sus errores: sin poder leer y recorrer el directorio, "No se encontraron" engañaría.
                if [ -d {{rutaEscapada}} ] && { [ ! -r {{rutaEscapada}} ] || [ ! -x {{rutaEscapada}} ]; }; then
                  printf 'Error: sin permiso para recorrer la ruta de búsqueda: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                cd -- {{rutaEscapada}} || exit 1
                {{SustitucionLiteral("raiz", "pwd -P")}}
                {{DefinicionMontajes}}
                explorar_montajes {{(isCruzarMontajes ? 1 : 0)}} {{profundidad}}
                {{elegirImpresion}}
                [ "${#poda[@]}" = 0 ] || poda+=(\( {{filtroTipo}}-name {{patronEscapado}} {{filtroTamano}}{{imprimir}} -prune -o -prune \) -o)
                # Los nombres van a la salida (fd 7); el stderr de find y su código, a 'error_find' (solo los últimos 1 000
                # bytes: con miles de avisos no crece).
                exec 7>&1
                error_find=$( { { find -L . {{xdev}}-maxdepth {{profundidad}} -mindepth 1 "${poda[@]}" \
                  \( -name .git -o -name node_modules -o -name .venv -o -name venv -o -name __pycache__ -o -name dist -o -name build -o -name .next -o -name bin -o -name obj -o -name .cache \) \
                  \( {{filtroTipo}}-name {{patronEscapado}} {{filtroTamano}}{{imprimir}} -prune -o -prune \) \
                  -o {{filtroTipo}}-name {{patronEscapado}} {{filtroTamano}}{{imprimir}} 2>&8 | {{ordenar}} >&7
                  printf '\n%s' "${PIPESTATUS[0]}" >&8; } 8>&1 | tail -c 1000; } )
                exec 7>&-
                codigo_find=${error_find##*$'\n'}
                # Código 1 son avisos (un directorio sin permiso, un bucle de enlaces): find recorrió el resto y la lista vale.
                # Con 2 o más (no se pudo ejecutar, p. ej. E2BIG con miles de podas; error fatal; señal) la lista no es fiable.
                [ "$codigo_find" -le 1 ] 2>/dev/null || printf '{{PrefijoMarcaFindFallo}}{{idMarca}}__%s %s\0' "$codigo_find" "${error_find%$'\n'*}"
                escribir_no_explorados '{{PrefijoMarcaMontaje}}{{idMarca}}__' '{{PrefijoMarcaTotalMontajes}}{{idMarca}}__'
                [ -z "$sin_tabla" ] || printf '{{PrefijoMarcaSinTabla}}{{idMarca}}__\0'
                )
                """);
        }

        // Respuesta de ssh_find: los nombres encontrados (con su límite) y, al final, la línea de los montajes no recorridos.
        // isConTamano (RC9): cada nombre llega como '<bytes>\t<ruta>' y sale como '<tamaño legible>\t<ruta>', en el orden recibido.
        internal static string InterpretarBusquedaNombre(string salida, string idMarca, string patron, string rutaEfectiva, int profundidad, int limite, bool isCruzarMontajes, bool isConTamano = false)
        {
            string marcaMontaje = $"{PrefijoMarcaMontaje}{idMarca}__";
            string marcaTotal = $"{PrefijoMarcaTotalMontajes}{idMarca}__";
            string marcaSinTabla = $"{PrefijoMarcaSinTabla}{idMarca}__";
            string marcaFindFallo = $"{PrefijoMarcaFindFallo}{idMarca}__";
            using PooledListRef<string> nombres = new();
            using PooledListRef<string> montajes = new();
            int totalMontajes = 0;
            bool isSinTabla = false;
            foreach (string nombre in SepararNombres(salida))
                if (nombre.StartsWith(marcaMontaje, StringComparison.Ordinal))
                    montajes.Add(nombre[marcaMontaje.Length..]);
                else if (nombre.StartsWith(marcaTotal, StringComparison.Ordinal) && int.TryParse(nombre.AsSpan(marcaTotal.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int total))
                    totalMontajes = total;
                else if (nombre == marcaSinTabla)
                    isSinTabla = true;
                else if (nombre.StartsWith(marcaFindFallo, StringComparison.Ordinal))
                    throw new NativeSshException(MensajeFindFallido(nombre[marcaFindFallo.Length..]));
                else
                    nombres.Add(nombre);

            string lineaMontajes = LineaMontajesNoExplorados(montajes.Span, isSinTabla && !isCruzarMontajes, totalMontajes);
            string pie = lineaMontajes.Length == 0 ? string.Empty : "\n\n" + lineaMontajes;
            string listado = isConTamano ? UnirNombresConTamano(nombres.Span, limite) : UnirNombres(nombres.Span, limite);
            return nombres.Tamaño == 0
                ? $"No se encontraron elementos que coincidan con '{patron}' en '{rutaEfectiva}'.{pie}"
                : nombres.Tamaño > limite
                    ? $"{listado}\n\n[Límite alcanzado: se muestran {limite} resultados (profundidad máx: {profundidad}). Refina la ruta o patrón para acotar.]{pie}"
                    : listado + pie;
        }

        // Los 'cantidad' primeros '<bytes>\t<ruta>' como '<tamaño legible>\t<ruta>', uno por línea (la ruta, como EscaparNombre;
        // sin el './' inicial del -printf de GNU find).
        private static string UnirNombresConTamano(ReadOnlySpan<string> nombres, int cantidad)
        {
            StringBuilder listado = new();
            int total = Math.Min(nombres.Length, cantidad);
            for (int i = 0; i < total; i++)
            {
                string nombre = nombres[i];
                int tabulador = nombre.IndexOf('\t', StringComparison.Ordinal);
                long bytes = 0;
                bool isConBytes = tabulador > 0 && long.TryParse(nombre.AsSpan(0, tabulador), NumberStyles.None, CultureInfo.InvariantCulture, out bytes);
                string ruta = isConBytes ? nombre[(tabulador + 1)..] : nombre;
                _ = listado.Append(i > 0 ? "\n" : "").Append(isConBytes ? FormatearTamano(bytes) : "?").Append('\t');
                EscaparNombre(listado, ruta.StartsWith("./", StringComparison.Ordinal) ? ruta[2..] : ruta);
            }

            return listado.ToString();
        }

        // 'codigoYError': el código de find, un espacio y el final de su stderr.
        private static string MensajeFindFallido(string codigoYError)
        {
            int espacio = codigoYError.IndexOf(' ', StringComparison.Ordinal);
            bool isSinError = espacio < 0;
            string codigo = isSinError ? codigoYError : codigoYError[..espacio];
            string error = isSinError ? string.Empty : codigoYError[(espacio + 1)..].Trim();
            string detalle = error.Length == 0 ? "sin mensaje" : error;
            return $"Error en búsqueda por nombre: find no terminó bien (código {codigo}) y su lista no es fiable, así que no se devuelve ninguna: {detalle}. Si dice que la lista de argumentos es demasiado larga, hay miles de sistemas de ficheros virtuales montados por debajo de la ruta (cada uno se poda en la línea de órdenes de find): repite ssh_find con una ruta más concreta.";
        }

        // '[montajes_no_explorados: …]' con los montajes ordenados, sin repetidos (un punto puede estar montado varias veces) ni
        // los que están dentro de otro de la lista (no se recorren igualmente), con tope. Vacía si no hay ninguno y la tabla de
        // montajes se pudo leer. Cada ruta va como un nombre de los listados (EscaparNombre). 'totalMontajes' es cuántos contó
        // el script (que solo manda los primeros): el «(+N más)» sale de él si es mayor que los recibidos. Ordena 'montajes'
        // en su sitio.
        internal static string LineaMontajesNoExplorados(Span<string> montajes, bool isSinTablaSinCruzar, int totalMontajes = 0)
        {
            montajes.Sort(StringComparer.Ordinal);
            using PooledListRef<string> superiores = new();
            foreach (string montaje in montajes)
            {
                bool isDentroDeOtro = false;
                foreach (string superior in superiores)
                    if (montaje == superior || superior == "/" || montaje.StartsWith(superior + "/", StringComparison.Ordinal))
                    {
                        isDentroDeOtro = true;
                        break;
                    }

                if (!isDentroDeOtro)
                    superiores.Add(montaje);
            }

            bool isSinSuperiores = superiores.Tamaño == 0;
            if (isSinSuperiores && !isSinTablaSinCruzar)
                return string.Empty;

            StringBuilder linea = new("[montajes_no_explorados: ");
            int mostrados = Math.Min(superiores.Tamaño, MaxMontajesNoExplorados);
            for (int i = 0; i < mostrados; i++)
            {
                if (i > 0)
                    _ = linea.Append(", ");
                EscaparNombre(linea, superiores[i]);
            }

            int restantes = Math.Max(superiores.Tamaño, totalMontajes) - mostrados;
            if (restantes > 0)
                _ = linea.Append(CultureInfo.InvariantCulture, $" (+{restantes} más)");
            if (isSinTablaSinCruzar)
                _ = linea.Append(isSinSuperiores ? string.Empty : "; ").Append("no se pudo leer /proc/self/mountinfo: puede haber otros sistemas de ficheros montados por debajo de la ruta que no se recorrieron");
            return linea.Append(']').ToString();
        }

        [McpServerTool(Name = "ssh_stat", ReadOnly = true, Idempotent = true)]
        [Description("Va por un canal exec propio, no por el shell de ssh_exec: no espera a sus comandos y funciona aunque el login de ese shell falle. Devuelve metadatos de un archivo o carpeta en formato estructurado (existe, tipo, tamaño en bytes, tamaño legible, permisos en octal, última modificación, propietario, grupo y, si es un enlace simbólico, destinoEnlace) sin seguir el enlace. El tipo es uno de estos valores fijos, iguales en cualquier servidor e idioma: 'archivo regular', 'directorio', 'enlace simbólico', 'fifo', 'socket', 'dispositivo de caracteres', 'dispositivo de bloques' o 'desconocido'. Úsala antes de leer archivos potencialmente masivos y en lugar de 'ls -l' para ver permisos y dueño. existe=false solo si la ruta no existe; si algún directorio de la ruta no se puede atravesar, devuelve un error.")]
        public async Task<ResultadoMetadatos> ConsultarMetadatosAsync(
            [Description("usuario@servidor[:puerto], o un alias de ~/.ssh/config. Usa el mismo destino que en ssh_exec: un alias con otro IdentityFile es otra sesión, con su propio directorio de trabajo.")] string destino,
            [Description("Ruta del archivo o carpeta a inspeccionar (absoluta o relativa a __NS_CWD).")] string ruta,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(ruta))
                throw new NativeSshException("La ruta no puede estar vacía.");

            ExigirValorLiteral(ruta, "La ruta");
            string rutaEscapada = Escapar(ruta);

            string comando = PlantillaBash.Crear($$"""
                (
                {{DefinicionSinAcceso}}
                if [ ! -e {{rutaEscapada}} ] && [ ! -L {{rutaEscapada}} ]; then
                  sin_acceso {{rutaEscapada}} && exit 1
                  echo "NO_EXISTE"
                  exit 0
                fi
                # El tipo no sale de 'stat' (su texto depende del idioma del servidor y de la implementación): es uno de
                # unos valores fijos.
                if [ -L {{rutaEscapada}} ]; then tipo='enlace simbólico'
                elif [ -d {{rutaEscapada}} ]; then tipo=directorio
                elif [ -f {{rutaEscapada}} ]; then tipo='archivo regular'
                elif [ -p {{rutaEscapada}} ]; then tipo=fifo
                elif [ -S {{rutaEscapada}} ]; then tipo=socket
                elif [ -c {{rutaEscapada}} ]; then tipo='dispositivo de caracteres'
                elif [ -b {{rutaEscapada}} ]; then tipo='dispositivo de bloques'
                else tipo=desconocido
                fi
                printf '%s|' "$tipo"
                LC_ALL=C stat -c '%s|%a|%y|%U|%G' -- {{rutaEscapada}} 2>/dev/null || LC_ALL=C stat -f '%z|%Lp|%Sm|%Su|%Sg' -- {{rutaEscapada}} 2>/dev/null
                if [ -L {{rutaEscapada}} ]; then
                  enlace=$(readlink -- {{rutaEscapada}}; printf x)
                  printf 'ENLACE:%s' "${enlace%x}"
                fi
                )
                """);

            SalidaLectura lectura = await EjecutarLecturaAsync(destino, comando, TimeoutMetadatosSegundos, "Revisa la ruta.", cancellationToken).ConfigureAwait(false);
            // Sin Trim ni Split por líneas sobre la salida completa: el destino del enlace puede contener o terminar en
            // saltos de línea, '\r' o espacios. Primera línea = stat; resto = "ENLACE:" + destino + el '\n' de readlink.
            string salida = lectura.Salida;
            if (lectura.Codigo != 0)
                throw new NativeSshException(lectura.Error($"Error al consultar metadatos de '{ruta}'"));

            if (salida.Trim() == "NO_EXISTE")
                return new ResultadoMetadatos(ruta, false, "El archivo o directorio no existe.");

            int finStat = salida.IndexOf('\n', StringComparison.Ordinal);
            bool hasResto = finStat >= 0;
            string[] partes = (hasResto ? salida[..finStat] : salida).Split('|');
            if (partes.Length < 4)
                throw new NativeSshException(lectura.Error($"Error al consultar metadatos de '{ruta}'"));

            string tipo = partes[0].Trim();
            _ = long.TryParse(partes[1].Trim(), CultureInfo.InvariantCulture, out long tamanoBytes);
            string tamanoHumano = FormatearTamano(tamanoBytes);
            string permisos = partes[2].Trim();
            string ultimaModificacion = partes[3].Trim();
            string? propietario = partes.Length > 4 ? partes[4].Trim() : null;
            string? grupo = partes.Length > 5 ? partes[5].Trim() : null;
            const string PrefijoEnlace = "ENLACE:";
            string resto = hasResto ? salida[(finStat + 1)..] : string.Empty;
            string? destinoEnlace = null;
            if (resto.StartsWith(PrefijoEnlace, StringComparison.Ordinal))
            {
                string conSalto = resto[PrefijoEnlace.Length..];
                destinoEnlace = conSalto.EndsWith('\n') ? conSalto[..^1] : conSalto;
            }

            return new ResultadoMetadatos(ruta, true, null, tipo, tamanoBytes, tamanoHumano, permisos, ultimaModificacion, propietario, grupo, destinoEnlace);
        }

        private static string FormatearTamano(long bytes) =>
            bytes < 1024
                ? $"{bytes} B"
                : bytes < 1024L * 1024L
                    ? (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KiB"
                    : bytes < 1024L * 1024L * 1024L
                        ? (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MiB"
                        : (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " GiB";

        [McpServerTool(Name = "ssh_set_cwd", Destructive = true)]
        [Description("Herramienta canónica y obligatoria para anclar el espacio de trabajo persistente (__NS_CWD) al inicio de la inspección de un repositorio. Valida en remoto que la ruta exista y sea un directorio, y posiciona automáticamente todos los comandos siguientes (ssh_exec y las rutas relativas de las demás herramientas) sin necesidad de encadenar 'cd dir &&'. Se mantiene aunque el shell se recree (ssh_reset_shell, shell perdido) hasta ssh_disconnect. Para volver a $HOME pasa ruta \"\" (vacía): ruta es obligatoria y sin un texto no se cambia nada.")]
        public async Task<ResultadoDirectorioTrabajo> FijarDirectorioAsync(
            [Description("usuario@servidor[:puerto], o un alias de ~/.ssh/config. Usa el mismo destino que en ssh_exec: un alias con otro IdentityFile es otra sesión, con su propio directorio de trabajo.")] string destino,
            [Description("Ruta remota a establecer como espacio de trabajo persistente. Obligatoria: para restablecer a $HOME (~), pásala vacía (\"\").")] string ruta,
            CancellationToken cancellationToken = default)
        {
            DestinoSsh resuelto = gestor.ParsearDestino(destino);
            string clave = resuelto.Clave;
            string claveSesion = resuelto.ClaveSesion;
            // El SDK enlaza un null tal cual (ValidadorArgumentos.IsDelTipo): un null no restablece el directorio, hay que
            // pedirlo con "".
            if (ruta is null)
                throw new NativeSshException("'ruta' llegó como null: no se ha cambiado el directorio de trabajo. Para restablecerlo a $HOME (~) a propósito, pasa ruta: \"\".");

            SesionShell? sesionInicial = SesionDe(claveSesion);
            string? guardado = sesionInicial?.DirectorioTrabajo;
            if (ruta.Length == 0)
            {
                // Con '&&': si 'unset' falla (p. ej. __NS_CWD de solo lectura), el shell sigue en el anterior y el código
                // no es el de 'pwd' (A-undecies, AU2).
                const string ComandoReset = "unset __NS_CWD && export __NS_CWD=\"\" && pwd";
                Peticion peticionReset = await EjecutarHerramientaAsync(destino, ComandoReset, "ssh_set_cwd", DescribirHerramienta("ssh_set_cwd", DirectorioHome), Guid.NewGuid(), TimeoutMetadatosSegundos, p => ConsecuenciaCwd(clave, sesionInicial, guardado, p, DirectorioHome, claveSesion), cancellationToken).ConfigureAwait(false);
                ExigirCompletada(peticionReset, p => ConsecuenciaCwd(clave, sesionInicial, guardado, p, DirectorioHome, claveSesion));

                if (peticionReset.CodigoSalida != 0)
                {
                    string errorReset = ReconstruirSalida(peticionReset, out _).Trim();
                    throw new NativeSshException($"{(string.IsNullOrWhiteSpace(errorReset) ? $"Error al restablecer el directorio (código {peticionReset.CodigoSalida})." : errorReset)} {ConsecuenciaCwd(clave, sesionInicial, guardado, claveSesion: claveSesion)}");
                }

                // Solo ahora, con el shell ya en $HOME: si el comando no llegó o no terminó, C# no cambia (A-decies, AD1).
                gestor.FijarDirectorioTrabajo(destino, null);
                return new ResultadoDirectorioTrabajo(clave, null, "Directorio de trabajo persistente restablecido a $HOME (~).");
            }

            ExigirValorLiteral(ruta, "La ruta");
            string idMarca = Guid.NewGuid().ToString("N");
            // El directorio resuelto vuelve por el PTY, donde LimpiarAnsi quitaría el '\r' de un CRLF (y los ESC y BEL) y Trim
            // los blancos finales (NEL, LS, PS...): viaja en base64 en una línea marcada con un id aleatorio por petición.
            string comandoValidacion = PlantillaBash.Crear($$"""
                (
                {{DefinicionSinAcceso}}
                ruta={{Escapar(ruta)}}
                if [ ! -e "$ruta" ]; then
                  sin_acceso "$ruta" || printf 'Error: la ruta no existe: %s\n' "$ruta" >&2
                  exit 1
                fi
                if [ ! -d "$ruta" ]; then
                  printf 'Error: la ruta no es un directorio: %s\n' "$ruta" >&2
                  exit 1
                fi
                if [ ! -x "$ruta" ]; then
                  printf 'Error: sin permiso para entrar en el directorio: %s\n' "$ruta" >&2
                  exit 1
                fi
                cd -- "$ruta" || exit 1
                {{SustitucionLiteral("real", "pwd -P")}}
                printf '{{PrefijoMarcaDirectorio}}%s__ %s\n' {{idMarca}} "$(printf '%s' "$real" | base64 | tr -d '\n')"
                )
                """);

            // La validación va en un subshell: falle como falle, no cambia nada.
            Guid grupo = Guid.NewGuid();
            Peticion peticionValidacion = await EjecutarHerramientaAsync(destino, comandoValidacion, "ssh_set_cwd", DescribirHerramienta("ssh_set_cwd", ruta, "validación"), grupo, TimeoutMetadatosSegundos, p => ConsecuenciaCwd(clave, sesionInicial, guardado, p, claveSesion: claveSesion), cancellationToken).ConfigureAwait(false);
            ExigirCompletada(peticionValidacion, p => ConsecuenciaCwd(clave, sesionInicial, guardado, p, claveSesion: claveSesion));

            if (peticionValidacion.CodigoSalida != 0)
            {
                string salidaError = ReconstruirSalida(peticionValidacion, out _).Trim();
                throw new NativeSshException($"{(string.IsNullOrWhiteSpace(salidaError) ? $"Error al validar el directorio (código {peticionValidacion.CodigoSalida})." : salidaError)} {ConsecuenciaCwd(clave, sesionInicial, guardado, claveSesion: claveSesion)}");
            }

            string pwdReal = ExtraerDirectorioMarcado(ReconstruirSalida(peticionValidacion, out _), idMarca);
            if (Encoding.UTF8.GetByteCount(pwdReal) > SesionShell.MaxBytesDirectorioTrabajo)
                throw new NativeSshException($"La ruta del directorio de trabajo supera {SesionShell.MaxBytesDirectorioTrabajo} bytes. {ConsecuenciaCwd(clave, sesionInicial, guardado, claveSesion: claveSesion)}");

            // La sesión que validó: si al fallar el export ya hay otra, la primera se cerró entre medias.
            SesionShell? sesionValidacion = SesionDe(claveSesion);
            string comandoExport = $"export __NS_CWD={Escapar(pwdReal)}";
            Peticion peticionExport = await EjecutarHerramientaAsync(destino, comandoExport, "ssh_set_cwd", DescribirHerramienta("ssh_set_cwd", ruta, "export"), grupo, TimeoutMetadatosSegundos, p => ConsecuenciaCwd(clave, sesionValidacion, guardado, p, pwdReal, claveSesion), cancellationToken).ConfigureAwait(false);
            ExigirCompletada(peticionExport, p => ConsecuenciaCwd(clave, sesionValidacion, guardado, p, pwdReal, claveSesion));

            if (peticionExport.CodigoSalida != 0)
            {
                string errorExport = ReconstruirSalida(peticionExport, out _).Trim();
                throw new NativeSshException($"{(string.IsNullOrWhiteSpace(errorExport) ? $"Error al fijar __NS_CWD (código {peticionExport.CodigoSalida})." : errorExport)} {ConsecuenciaCwd(clave, sesionValidacion, guardado, claveSesion: claveSesion)}");
            }

            // Fuente de verdad en C# (la reciben los canales exec y se reexporta si el shell se recrea), fijada solo cuando
            // el shell ya lo ha exportado: si el comando no llegó o no terminó, las herramientas de lectura y un shell
            // nuevo siguen en el anterior y el error lo dice (A-decies, AD1).
            gestor.FijarDirectorioTrabajo(destino, pwdReal);
            return new ResultadoDirectorioTrabajo(clave, pwdReal, $"Directorio de trabajo persistente fijado a: {pwdReal}");
        }

        private const string DirectorioHome = "$HOME (~)";

        // Sesión del destino ahora, por DestinoSsh.ClaveSesion (null = no hay: nunca se abrió o ssh_disconnect la cerró).
        private SesionShell? SesionDe(string claveSesion)
        {
            foreach (SesionShell sesion in gestor.ListarSesiones())
                if (sesion.Destino.ClaveSesion == claveSesion)
                    return sesion;

            return null;
        }

        // Qué quedó del directorio de trabajo tras un fallo de ssh_set_cwd, calculado después del fallo y no antes
        // (A-undecies, AU1): ssh_disconnect a mitad saca la sesión con su directorio y la próxima conexión empieza en $HOME,
        // y otra llamada puede haberlo cambiado entre medias. 'guardado' es el directorio de C# al empezar (null = $HOME) y
        // 'sesionEsperada', la sesión que había (null si aún no había ninguna). Si nada de eso pasó, C# no ha cambiado:
        // - sin 'nuevo' (la validación, que va en un subshell, o un comando que el shell rechazó con un código distinto de
        //   0) o terminado sin llegar al shell (IsNoEnviadaAlShell, IsInterrumpidaSinEnviar): todo sigue en el anterior,
        //   con certeza;
        // - shell perdido: el nuevo se inicializa con el directorio de C#, el anterior;
        // - sin terminar con el shell vivo (plazo, cancelación): ese shell puede haber pasado ya a 'nuevo'.
        // 'clave' es lo que se muestra (usuario@servidor:puerto); 'claveSesion', la de la sesión si difiere (IdentityFile).
        internal string ConsecuenciaCwd(string clave, SesionShell? sesionEsperada, string? guardado, Peticion? peticion = null, string? nuevo = null, string? claveSesion = null)
        {
            SesionShell? sesion = SesionDe(claveSesion ?? clave);
            if (sesion is null)
                return $"La sesión con {clave} se cerró mientras tanto (ssh_disconnect) y con ella se olvidó el directorio de trabajo: la próxima herramienta abrirá una conexión nueva en {DirectorioHome}.";

            string? actual = sesion.DirectorioTrabajo;
            string actualVisible = actual ?? DirectorioHome;
            if (sesionEsperada is not null && !ReferenceEquals(sesion, sesionEsperada))
                return $"La sesión con {clave} se cerró mientras tanto (ssh_disconnect) y ya se abrió otra: el directorio de trabajo es {actualVisible}, el de esa sesión nueva.";

            bool isInterrumpidaSinEnviar = peticion is not null && IsInterrumpidaSinEnviar(peticion);
            bool isSinEfectoEnElShell = nuevo is null || peticion is null || isInterrumpidaSinEnviar || IsNoEnviadaAlShell(peticion);
            bool isShellPerdido = peticion?.Estado == EstadoPeticionEnum.ShellPerdido;
            if (actual != guardado)
                return isSinEfectoEnElShell || isShellPerdido
                    ? $"Mientras tanto otra llamada cambió el directorio de trabajo: ahora es {actualVisible}."
                    : $"Mientras tanto otra llamada cambió el directorio de trabajo a {actualVisible} (lo usan las herramientas de lectura y un shell nuevo), pero el shell actual de ssh_exec puede haber pasado ya a {nuevo}: repite ssh_set_cwd.";

            string anterior = guardado ?? DirectorioHome;
            return isInterrumpidaSinEnviar
                ? $"El comando no llegó a enviarse al shell{(peticion!.TiempoEjecucion == TimeSpan.Zero ? " (seguía en la cola)" : string.Empty)}: el directorio de trabajo sigue siendo {anterior}."
                : isSinEfectoEnElShell
                    ? $"El directorio de trabajo sigue siendo {anterior}."
                    : isShellPerdido
                        ? $"El directorio de trabajo sigue siendo {anterior}: lo usan las herramientas de lectura y el shell nuevo se abrirá en él."
                        : ConsecuenciaCwdIncierta(anterior, nuevo!);
        }

        // Interrumpida (ssh_cancel, el plazo o el token) y ya terminada sin que el comando llegara a escribirse en el shell:
        // en la cola (SolicitarCancelacion la finaliza bajo el mismo candado que la marca como en ejecución) o mientras se
        // abría el shell. Solo con la petición terminada: mientras sigue en ejecución, la sesión aún puede enviarlo (entre
        // su última comprobación del token y la escritura). El estado se lee antes que la señal, que la sesión marca antes de
        // finalizarla. Tampoco cuenta si la escritura del comando ya había empezado (IsEnvioIniciado): pudo quedarse a medias,
        // tanto si terminó en ShellPerdido como en Cancelado porque el cierre de la sesión (ssh_disconnect) la cortó.
        internal static bool IsInterrumpidaSinEnviar(Peticion peticion) =>
            peticion.Estado is EstadoPeticionEnum.Cancelado or EstadoPeticionEnum.TiempoAgotado && !peticion.IsComandoEnviado && !peticion.IsEnvioIniciado;

        private static string ConsecuenciaCwdIncierta(string anterior, string nuevo) =>
            $"El directorio de trabajo sigue siendo {anterior} para las herramientas de lectura y para un shell nuevo, pero el shell actual de ssh_exec puede haber pasado ya a {nuevo}: repite ssh_set_cwd (o usa ssh_reset_shell para recrearlo en {anterior}).";

        private const string PrefijoMarcaDirectorio = "__NS_CWD_";

        // Directorio que resolvió la validación de ssh_set_cwd: el base64 de la única línea "__NS_CWD_<id>__ <base64>" de la
        // salida (nada ajeno puede imitar el id). Debe ser una ruta absoluta en UTF-8 válido: si no, no se fija nada.
        internal static string ExtraerDirectorioMarcado(string salida, string idMarca)
        {
            string marca = $"{PrefijoMarcaDirectorio}{idMarca}__ ";
            string? base64 = null;
            int marcas = 0;
            foreach (string linea in salida.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
                if (linea.StartsWith(marca, StringComparison.Ordinal))
                {
                    marcas++;
                    base64 = linea[marca.Length..];
                }

            const string SinCambios = "No se ha cambiado el directorio de trabajo.";
            if (marcas != 1 || base64 is null)
                throw new NativeSshException($"La validación del directorio no devolvió su ruta resuelta (marcas: {marcas}); ¿se mezcló otra salida del terminal? {SinCambios}");

            string directorio;
            try
            {
                directorio = Utf8Estricto.GetString(Convert.FromBase64String(base64));
            }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
            {
                throw new NativeSshException($"La ruta resuelta del directorio no es base64 de un texto UTF-8 válido (los nombres que no son UTF-8 no se pueden fijar). {SinCambios}");
            }

            return directorio.StartsWith('/')
                ? directorio
                : throw new NativeSshException($"La ruta resuelta del directorio no es absoluta: '{directorio}'. {SinCambios}");
        }

        [McpServerTool(Name = "ssh_replace_file_content", Destructive = true)]
        [Description("Va por el shell persistente de ssh_exec, en su cola: espera a los comandos en curso y necesita que el login de ese shell termine (si el perfil está roto, ssh_reset_shell con sinPerfil=true). Edita quirúrgicamente un bloque de texto exacto dentro de un archivo remoto sin reescribir todo el archivo a mano. Busca 'targetContent' en todo el archivo o, si se indican lineaInicio y lineaFin, estrictamente dentro de ese rango de líneas, y lo sustituye por 'replacementContent' (obligatorio: para borrar el texto pásalo vacío, \"\"; si falta, no se escribe nada). Exige una coincidencia única salvo allowMultiple=true. Los saltos de línea '\\r\\n' y '\\n' de targetContent y replacementContent se adaptan a los del archivo (CRLF o LF); cualquier otro carácter, incluidos FF, NEL, LS, PS y un '\\r' suelto, se busca y se escribe tal cual. Solo edita texto UTF-8 válido (conserva el BOM y los saltos de línea CRLF/LF) de hasta ≈755 KiB con la configuración por defecto: rechaza sin escribir nada los archivos binarios, con otra codificación o más grandes. La escritura tiene las mismas garantías que ssh_write_file con overwrite=true: si la ruta es un enlace simbólico edita su destino y conserva el enlace; temporal en el mismo directorio con todos los atributos del original (modo con los bits setuid y setgid, ACL, propietario, grupo, atributos extendidos y contexto SELinux, copiados y comprobados) renombrado encima (atómico); si no puede copiar o comprobar todos los atributos, hay enlaces duros o el directorio no tiene permiso de escritura, sobrescribe en el sitio (NO atómico; conserva inodo, enlaces duros, propietario, grupo, ACL, atributos extendidos y contexto SELinux, y devuelve y comprueba lo que el núcleo quita al escribir: las capacidades, security.capability, solo con sudo=true, y, salvo si escribe root, los bits setuid y setgid; lo que no se pueda devolver o comprobar lo dice la respuesta; con copia de seguridad previa que restaura si la escritura falla o se interrumpe y que solo se borra cuando el archivo ha quedado escrito por completo o restaurado; si además es de solo lectura, sin sudo falla sin tocarlo) y lo indica en la respuesta. Si la lectura o la escritura no terminan (tiempo agotado, cancelación, ssh_disconnect o conexión perdida) o el script muere a mitad (p. ej. por SIGKILL), el error dice qué pasó con el archivo y el id de la petición para ver su salida con ssh_output; interrumpida la escritura en el sitio, el script restaura el original por su cuenta aunque ya no pueda confirmarlo. Mientras una escritura, un anexado o un reemplazo siguen en marcha sobre un archivo (también la restauración tras una interrupción), tienen tomado su candado (flock sobre el archivo, con flock, python3 o perl del servidor; sin ellos, con un flock sin -E como el de busybox si antes se comprueba que funciona) y las escrituras, anexados y reemplazos nuevos sobre él fallan al momento sin tocarlo: reintenta cuando termine (también fallan si otro programa tiene un flock sobre el archivo); el candado se toma también si el archivo tiene permiso de escritura pero no de lectura (se abre para anexar, sin truncarlo); sin ninguna de esas herramientas, o si el archivo no se puede abrir ni para leer ni para anexar, se escribe sin exclusión y la respuesta lo avisa, y si no se puede comprobar que el candado quedó sobre el archivo que está en la ruta (sin /proc en el servidor ni stat de GNU coreutils, python3 o perl, o si no se puede consultar la ruta), también. Si el destino no es un archivo regular (FIFO, socket o dispositivo), falla sin leerlo ni tocarlo. Justo antes de escribir comprueba que el archivo sigue siendo el que leyó (sha256sum o, si no lo hay, cksum): si otro proceso lo cambió o lo borró entretanto, no escribe y devuelve un error (vuelve a intentarlo). Para varias ediciones del mismo archivo usa SIEMPRE 'ediciones' en una sola llamada (en lugar de targetContent y replacementContent, que no se combinan con ella): se aplican en orden, cada una buscada en todo el archivo tras aplicar las anteriores (sin lineaInicio ni lineaFin), y todas o ninguna: si una no se encuentra o aparece varias veces sin su allowMultiple=true, no se escribe nada y el error dice cuál; la escritura es una sola, con las mismas garantías. Devuelve ruta, bytes (tamaño final del archivo), reemplazos (el total), isAtomica y mensaje.")]
        public async Task<ResultadoEscritura> ReemplazarContenidoArchivoAsync(
            [Description("usuario@servidor[:puerto], o un alias de ~/.ssh/config. Usa el mismo destino que en ssh_exec: un alias con otro IdentityFile es otra sesión, con su propio directorio de trabajo.")] string destino,
            [Description("Ruta del archivo remoto (absoluta o relativa a __NS_CWD).")] string ruta,
            [Description("Texto exacto a reemplazar (incluyendo espacios e indentación). Obligatorio salvo con 'ediciones'.")] string? targetContent = null,
            [Description("Nuevo texto que reemplazará a targetContent. Obligatorio salvo con 'ediciones' (para borrar targetContent, pásalo vacío: \"\"); si falta, no se escribe nada.")] string? replacementContent = null,
            [Description("Línea inicial (1-indexed) del rango donde buscar targetContent. Opcional: sin lineaInicio ni lineaFin se busca en todo el archivo; si se indica, lineaFin es obligatoria.")] int? lineaInicio = null,
            [Description("Línea final (1-indexed, inclusive) del rango donde buscar targetContent. Opcional: sin lineaInicio ni lineaFin se busca en todo el archivo; si se indica, lineaInicio es obligatoria.")] int? lineaFin = null,
            [Description("Si es true, permite reemplazar si targetContent aparece múltiples veces en el archivo o el rango (reemplaza todas). Por defecto false (falla si hay ambigüedad).")] bool allowMultiple = false,
            [Description("Leer y escribir con sudo (por defecto false).")] bool sudo = false,
            [Description("Varias ediciones del mismo archivo en una llamada, en orden y todas o ninguna: lista de {targetContent, replacementContent, allowMultiple (opcional, por defecto false)}; replacementContent es obligatorio en cada una (\"\" para borrar su targetContent). Cada una se busca en todo el archivo tras aplicar las anteriores. No se combina con targetContent, replacementContent, lineaInicio, lineaFin ni allowMultiple.")] EdicionTexto[]? ediciones = null,
            CancellationToken cancellationToken = default)
        {
            await ExigirEscrituraPermitidaAsync(gestor.ParsearDestino(destino), "ssh_replace_file_content", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(ruta))
                throw new NativeSshException("La ruta del archivo no puede estar vacía.");

            bool hasEdiciones = ediciones is { Length: > 0 };
            if (hasEdiciones && (targetContent is not null || replacementContent is not null || lineaInicio is not null || lineaFin is not null || allowMultiple))
                throw new NativeSshException("Usa 'ediciones' o targetContent/replacementContent (con lineaInicio, lineaFin y allowMultiple), no los dos a la vez: con 'ediciones', cada edición lleva su targetContent, su replacementContent y su allowMultiple. No se ha enviado nada.");

            if (!hasEdiciones && string.IsNullOrEmpty(targetContent))
                throw new NativeSshException("El texto a reemplazar (targetContent) no puede estar vacío.");

            // Sin replacementContent no se toma "" (borraría targetContent en silencio): para borrar hay que pedirlo.
            if (!hasEdiciones && replacementContent is null)
                throw new NativeSshException("Falta replacementContent: es obligatorio (para borrar targetContent, pásalo vacío: \"\"). No se ha enviado nada.");

            for (int i = 0; hasEdiciones && i < ediciones!.Length; i++)
            {
                EdicionTexto? edicion = ediciones[i];
                if (string.IsNullOrEmpty(edicion?.TargetContent))
                    throw new NativeSshException($"Edición {i + 1} de {ediciones.Length}: su targetContent no puede estar vacío. No se ha enviado nada.");

                if (edicion.ReplacementContent is null)
                    throw new NativeSshException($"Edición {i + 1} de {ediciones.Length}: falta su replacementContent; es obligatorio (para borrar su targetContent, pásalo vacío: \"\"). No se ha enviado nada.");

                ExigirUtf16Valido(edicion.TargetContent, $"El targetContent de la edición {i + 1}");
                ExigirUtf16Valido(edicion.ReplacementContent, $"El replacementContent de la edición {i + 1}");
            }

            bool hasInicio = lineaInicio is not null;
            bool hasFin = lineaFin is not null;
            if (hasInicio != hasFin)
                throw new NativeSshException("Indica lineaInicio y lineaFin juntos (rango de líneas) u omite los dos para buscar en todo el archivo.");

            bool isConRango = hasInicio;
            if (isConRango && lineaInicio < 1)
                throw new NativeSshException("La línea inicial debe ser mayor o igual a 1.");

            if (isConRango && lineaFin < lineaInicio)
                throw new NativeSshException($"La línea final ({lineaFin}) no puede ser menor que la línea inicial ({lineaInicio}).");

            ExigirValorLiteral(ruta, "La ruta");
            ExigirUtf16Valido(targetContent ?? string.Empty, "targetContent");
            ExigirUtf16Valido(replacementContent ?? string.Empty, "replacementContent");
            string rutaEscapada = Escapar(ruta);
            long maxBytesEditable = CalcularMaxBytesArchivoEditable(opciones.MaxBytesSalidaPorPeticion);
            string idMarcas = Guid.NewGuid().ToString("N");
            // Con sudo=true todas las comprobaciones van con sudo: sin él, un archivo en un directorio que el usuario no
            // puede atravesar parece no existir. El base64 se imprime entre marcadores (ver ExtraerArchivoEntreMarcadores)
            // y se lee entero antes del primero, así que el stderr de cat y base64 nunca cae entre ellos. Los marcadores se
            // componen con printf para que su texto literal no aparezca en el propio script. Cada error conocido sale con su
            // código (CodigoErrorLectura) y sin texto: el error de la herramienta lo construye native_ssh con la ruta
            // (MensajeErrorLecturaReplace), nunca con la salida de la lectura, que puede ser el archivo.
            string comandoLectura = EnvolverEnBash(PlantillaBash.Crear($$"""
                ruta=$1
                {{DefinicionSinAcceso}}
                if [ ! -e "$ruta" ]; then
                  sin_acceso "$ruta" >/dev/null 2>&1 && exit {{(int)CodigoErrorLecturaEnum.SinAcceso}}
                  exit {{(int)CodigoErrorLecturaEnum.NoExiste}}
                fi
                [ -d "$ruta" ] && exit {{(int)CodigoErrorLecturaEnum.Directorio}}
                [ -f "$ruta" ] || exit {{(int)CodigoErrorLecturaEnum.NoRegular}}
                [ -r "$ruta" ] || exit {{(int)CodigoErrorLecturaEnum.SinPermisoLectura}}
                {{DefinicionCandado}}
                destino=$ruta
                if ! tomar_candado -s 2>/dev/null; then
                  [ "$estado_candado" = sustituido ] && exit {{(int)CodigoErrorLecturaEnum.Sustituido}}
                  exit {{(int)CodigoErrorLecturaEnum.Candado}}
                fi
                tam=$(stat -L -c %s -- "$ruta" 2>/dev/null || stat -L -f %z -- "$ruta" 2>/dev/null)
                [ -z "$tam" ] && exit {{(int)CodigoErrorLecturaEnum.SinTamano}}
                [ "$tam" -gt {{maxBytesEditable}} ] && exit {{(int)CodigoErrorLecturaEnum.DemasiadoGrande}}
                b64=$(cat -- "$ruta" | base64) || exit {{(int)CodigoErrorLecturaEnum.NoLeido}}
                printf '{{PrefijoMarcaBase64}}%s_%s__\n' INICIO {{idMarcas}}
                printf '%s\n' "$b64"
                printf '{{PrefijoMarcaBase64}}%s_%s__ %s\n' FIN {{idMarcas}} "$tam"
                """), sudo, rutaEscapada);

            string sinModificar = $"No se ha modificado '{ruta}'.";
            Guid grupo = Guid.NewGuid();
            Peticion peticionLectura = await EjecutarHerramientaAsync(destino, comandoLectura, "ssh_replace_file_content", DescribirHerramienta("ssh_replace_file_content", ruta, "lectura"), grupo, TimeoutLecturaSegundos, _ => sinModificar, cancellationToken).ConfigureAwait(false);
            ExigirCompletada(peticionLectura, _ => sinModificar, isConSalida: false);

            if (peticionLectura.CodigoSalida != 0)
                throw new NativeSshException(MensajeErrorLecturaReplace(peticionLectura.CodigoSalida, ruta, sudo, maxBytesEditable));

            string salidaLectura = ReconstruirSalidaCompleta(peticionLectura, $"No se ha escrito nada en '{ruta}'.");
            byte[] bytesLeidos = ExtraerArchivoEntreMarcadores(salidaLectura, idMarcas, ruta);
            string textoOriginal = DecodificarArchivoUtf8(bytesLeidos, ruta);

            string separador = textoOriginal.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            (string textoModificado, int ocurrencias) = hasEdiciones
                ? AplicarEdiciones(textoOriginal, ediciones!, separador, ruta)
                : AplicarReemplazo(textoOriginal, targetContent!, replacementContent!, separador, lineaInicio, lineaFin, allowMultiple, ruta);
            byte[] bytesModificados = Encoding.UTF8.GetBytes(textoModificado);

            string idEscritura = Guid.NewGuid().ToString("N");
            string comandoEscritura = ConstruirComandoEscritura(rutaEscapada, bytesModificados, sudo, isSobrescribir: true, idEscritura, bytesLeidos);

            (Peticion peticionEscritura, InformeEscritura informe) = await EjecutarEscrituraAsync(destino, comandoEscritura, idEscritura, ruta, "ssh_replace_file_content", DescribirHerramienta("ssh_replace_file_content", ruta, "escritura"), grupo, cancellationToken).ConfigureAwait(false);
            if (peticionEscritura.CodigoSalida == CodigoContenidoNoIntegro)
                throw new NativeSshException(MensajeContenidoNoIntegro(ruta, bytesModificados.Length, isAnexar: false));

            if (peticionEscritura.CodigoSalida == CodigoSinVerificador)
                throw new NativeSshException(MensajeSinVerificador(ruta, isAnexar: false));

            if (peticionEscritura.CodigoSalida == CodigoBashNoArranco && informe.IsSinAnuncios)
                throw new NativeSshException(MensajeBashNoArranco(ruta, isAnexar: false));

            string cuantas = hasEdiciones ? $"{ediciones!.Length} ediciones, " : string.Empty;
            return peticionEscritura.CodigoSalida != 0
                ? throw new NativeSshException(informe.MensajeError($"Error al escribir el archivo modificado en {ruta} (código {peticionEscritura.CodigoSalida}).", ruta, peticionEscritura.CodigoSalida, peticionEscritura.IdTexto))
                : new ResultadoEscritura(ruta, bytesModificados.Length, ocurrencias, informe.IsAtomica, $"Archivo modificado quirúrgicamente con éxito: {ruta} ({cuantas}{ocurrencias} reemplazo(s) realizado(s)).{informe.AvisoExito()}");
        }

        // Una sola edición (targetContent), en todo el archivo o en el rango de líneas: el texto nuevo y las apariciones
        // reemplazadas.
        private static (string Texto, int Ocurrencias) AplicarReemplazo(string textoOriginal, string targetContent, string replacementContent, string separador, int? lineaInicio, int? lineaFin, bool allowMultiple, string ruta)
        {
            bool isConRango = lineaInicio is not null;
            string targetNormalizado = AdaptarSaltos(targetContent, separador);
            string replacementNormalizado = AdaptarSaltos(replacementContent, separador);

            string[] lineas = textoOriginal.Split(separador);
            bool hasSaltoFinal = textoOriginal.EndsWith(separador, StringComparison.Ordinal);
            int totalLineas = hasSaltoFinal ? Math.Max(0, lineas.Length - 1) : lineas.Length;

            int inicio = lineaInicio ?? 1;
            int fin = lineaFin ?? Math.Max(1, totalLineas);
            if (isConRango && inicio > totalLineas)
                throw new NativeSshException($"La línea inicial ({inicio}) excede el total de líneas del archivo ({totalLineas}).");

            string ambito = isConRango ? $"el rango de líneas {inicio}-{fin}" : "todo el archivo";
            int idxInicio = inicio - 1;
            int idxFin = Math.Max(idxInicio, Math.Min(fin, totalLineas) - 1);

            string textoAntes = idxInicio > 0 ? string.Join(separador, lineas[..idxInicio]) + separador : string.Empty;
            string textoRango = string.Join(separador, lineas[idxInicio..(idxFin + 1)]);
            string textoDespues = (idxFin + 1 < lineas.Length) ? separador + string.Join(separador, lineas[(idxFin + 1)..]) : string.Empty;

            if (!textoRango.Contains(targetNormalizado, StringComparison.Ordinal)
                && (textoRango + separador).Contains(targetNormalizado, StringComparison.Ordinal)
                && textoDespues.StartsWith(separador, StringComparison.Ordinal))
            {
                textoRango += separador;
                textoDespues = textoDespues[separador.Length..];
            }

            int ocurrencias = ContarApariciones(textoRango, targetNormalizado);
            if (ocurrencias == 0)
                throw new NativeSshException(isConRango
                    ? $"No se encontró 'targetContent' en {ambito} de '{ruta}'. Revisa la indentación y el rango exacto."
                    : $"No se encontró 'targetContent' en {ambito} '{ruta}'. Revisa la indentación y los saltos de línea exactos.");

            if (ocurrencias > 1 && !allowMultiple)
                throw new NativeSshException($"Se encontraron {ocurrencias} apariciones de 'targetContent' en {ambito}. Especifica 'allowMultiple=true', amplía targetContent con contexto o acota con lineaInicio/lineaFin.");

            string textoRangoModificado = allowMultiple
                ? textoRango.Replace(targetNormalizado, replacementNormalizado, StringComparison.Ordinal)
                : string.Concat(textoRango.AsSpan(0, textoRango.IndexOf(targetNormalizado, StringComparison.Ordinal)), replacementNormalizado, textoRango.AsSpan(textoRango.IndexOf(targetNormalizado, StringComparison.Ordinal) + targetNormalizado.Length));

            return (textoAntes + textoRangoModificado + textoDespues, ocurrencias);
        }

        // Varias ediciones (CI5): en orden, cada una en todo el texto que dejaron las anteriores; si una no se encuentra o es
        // ambigua sin su allowMultiple, error sin escribir nada (todas o ninguna). El total de apariciones reemplazadas.
        private static (string Texto, int Ocurrencias) AplicarEdiciones(string textoOriginal, EdicionTexto[] ediciones, string separador, string ruta)
        {
            const string NadaEscrito = "No se ha modificado el archivo: con 'ediciones' se aplican todas o ninguna.";
            string texto = textoOriginal;
            int total = 0;
            for (int i = 0; i < ediciones.Length; i++)
            {
                EdicionTexto edicion = ediciones[i];
                string target = AdaptarSaltos(edicion.TargetContent, separador);
                string reemplazo = AdaptarSaltos(edicion.ReplacementContent!, separador);
                int ocurrencias = ContarApariciones(texto, target);
                string cual = $"Edición {i + 1} de {ediciones.Length}";
                if (ocurrencias == 0)
                    throw new NativeSshException($"{cual}: no se encontró su 'targetContent' en '{ruta}' (se busca en todo el archivo, tras aplicar las ediciones anteriores). Revisa la indentación y los saltos de línea exactos. {NadaEscrito}");

                if (ocurrencias > 1 && !edicion.AllowMultiple)
                    throw new NativeSshException($"{cual}: se encontraron {ocurrencias} apariciones de su 'targetContent' en todo el archivo. Pon allowMultiple=true en esa edición o amplía su targetContent con contexto. {NadaEscrito}");

                int posicion = texto.IndexOf(target, StringComparison.Ordinal);
                texto = edicion.AllowMultiple
                    ? texto.Replace(target, reemplazo, StringComparison.Ordinal)
                    : string.Concat(texto.AsSpan(0, posicion), reemplazo, texto.AsSpan(posicion + target.Length));
                total += ocurrencias;
            }

            return (texto, total);
        }

        private static int ContarApariciones(string texto, string buscado)
        {
            int ocurrencias = 0;
            int pos = 0;
            while ((pos = texto.IndexOf(buscado, pos, StringComparison.Ordinal)) != -1)
            {
                ocurrencias++;
                pos += buscado.Length;
            }

            return ocurrencias;
        }

        // Solo los saltos '\r\n' y '\n' del texto se adaptan al separador del archivo: FF, NEL, LS, PS y un '\r' suelto son
        // contenido y se conservan (ReplaceLineEndings los convertiría en saltos).
        internal static string AdaptarSaltos(string texto, string separador)
        {
            string conLf = texto.Replace("\r\n", "\n", StringComparison.Ordinal);
            return separador == "\n" ? conLf : conLf.Replace("\n", separador, StringComparison.Ordinal);
        }

        // Script común de ssh_write_file (sin append) y ssh_replace_file_content:
        // - todo el script (comprobaciones incluidas) se ejecuta con el mismo privilegio: con sudo=true, un único 'sudo bash';
        // - un enlace simbólico se resuelve (readlink -f) y se escribe en su destino, nunca se sustituye el enlace;
        // - el temporal se crea con mktemp (0600) en el directorio del destino resuelto o, si el directorio no admite
        //   escritura y el archivo existe, en ${TMPDIR:-/tmp}, y se sobrescribe en el sitio;
        // - un destino que existe y no es un archivo regular (FIFO, socket, dispositivo) se rechaza sin tocarlo;
        // - archivo existente: el temporal recibe todos los atributos del original (conservar_atributos) y se renombra
        //   encima; si no se pueden copiar y comprobar (propietario ajeno sin sudo, cp que no es de GNU, contexto SELinux
        //   que no se puede asignar...) o hay enlaces duros, se escribe en el sitio (no atómico);
        // - ssh_replace_file_content (contenidoLeido): justo antes de colocar, el destino debe seguir siendo el que se leyó
        //   ($3 = sha256, $4 = suma y bytes de cksum); si cambió o desapareció, no se escribe;
        // - archivo nuevo: permisos por la umask del usuario ($2); sin sobrescribir, creación exclusiva con ln (o noclobber);
        // - anuncia (InformeEscritura, con idMarcas) las rutas del temporal y de la copia de seguridad en cuanto las crea y
        //   cada fase de la colocación, para que un error por tiempo agotado o cancelación diga qué pasó con el archivo.
        private static string ConstruirComandoEscritura(string rutaEscapada, byte[] contenido, bool sudo, bool isSobrescribir, string idMarcas, byte[]? contenidoLeido = null)
        {
            const string ErrorYaExiste = """printf 'Error: el archivo ya existe: %s. Especifica overwrite=true para sobrescribirlo o append=true para anexar.\n' "$ruta" >&2; exit 1""";

            string comprobarNoExiste = isSobrescribir
                ? string.Empty
                : PlantillaBash.Crear($$"""
                    if [ -e "$ruta" ] || [ -L "$ruta" ]; then
                      {{ErrorYaExiste}}
                    fi
                    """);
            // Sin sobrescribir nunca se mira si el destino existe para decidir: ln falla si apareció entretanto.
            string colocar = isSobrescribir
                ? PlantillaBash.Crear($$"""
                    {{DefinicionConservarAtributos}}
                    {{DefinicionSinCambios}}
                    {{DefinicionRenombrar}}
                    if [ -e "$destino" ]; then
                      en_sitio=$fuera
                      [ "$(stat -c '%h' -- "$destino" 2>/dev/null)" = 1 ] || en_sitio=1
                      [ "$en_sitio" = 1 ] || conservar_atributos "$destino" "$tmp" || en_sitio=1
                      sin_cambios || exit 1
                      if [ "$en_sitio" = 0 ]; then
                        anunciar fase colocando
                        renombrar || exit 1
                        anunciar fase colocado
                      else
                        if [ ! -w "$destino" ]; then
                          printf 'Error: %s no tiene permiso de escritura (¿solo lectura?) y habría que sobrescribirlo en el sitio, porque tiene enlaces duros, su directorio no admite escritura o no se pueden copiar a un temporal todos sus atributos (propietario, grupo, modo, ACL, atributos extendidos, contexto SELinux). No se ha modificado: dale permiso de escritura o usa sudo=true.\n' "$ruta" >&2
                          exit 1
                        fi
                        escribir_en_sitio || exit 1
                      fi
                    else
                      sin_cambios || exit 1
                      chmod -- "$modo_nuevo" "$tmp" || exit 1
                      anunciar fase colocando
                      renombrar || exit 1
                      anunciar fase colocado
                    fi
                    """)
                : PlantillaBash.Crear($$"""
                    {{DefinicionEnlazar}}
                    chmod -- "$modo_nuevo" "$tmp" || exit 1
                    anunciar fase colocando
                    if enlazar; then
                      anunciar fase colocado
                    else
                      if [ -e "$destino" ] || [ -L "$destino" ]; then
                        {{ErrorYaExiste}}
                      fi
                      anunciar fase creando
                      (set -C; cat -- "$tmp" > "$destino") || exit 1
                      anunciar fase creado
                    fi
                    """);
            const string ErrorSinTemporal = """printf 'Error: no se pudo crear el archivo temporal en %s (¿existe el directorio y hay permiso de escritura?).\n' "$dir" >&2; exit 1""";
            // Directorio sin permiso de escritura: un archivo que ya existe aún se puede sobrescribir en el sitio, con el
            // temporal fuera (fuera=1). Sin sobrescribir hace falta el directorio (ln).
            string temporalFuera = isSobrescribir
                ? PlantillaBash.Crear($$"""
                    if [ -e "$destino" ] && tmp=$(mktemp -- "${TMPDIR:-/tmp}/.ns_escritura.XXXXXX" 2>/dev/null); then
                      fuera=1
                    else
                      tmp=
                      {{ErrorSinTemporal}}
                    fi
                    """)
                : ErrorSinTemporal;

            string script = PlantillaBash.Crear($$"""
                ruta=$1
                modo_nuevo=$2
                sha_leido=$3
                cksum_leido=$4
                tam_esperado=$5
                sha_esperado=$6
                cksum_esperado=$7
                marca={{idMarcas}}
                {{DefinicionAnunciar}}
                {{DefinicionVerificarContenido}}
                if [ -d "$ruta" ]; then
                  printf 'Error: la ruta es un directorio: %s\n' "$ruta" >&2
                  exit 1
                fi
                {{comprobarNoExiste}}
                destino=$ruta
                if [ -L "$ruta" ]; then
                  {{SustitucionLiteral("destino", "readlink -f -- \"$ruta\"")}}
                  if [ -z "$destino" ]; then
                    printf 'Error: no se pudo resolver el enlace simbólico: %s\n' "$ruta" >&2
                    exit 1
                  fi
                fi
                if [ -e "$destino" ] && [ ! -f "$destino" ]; then
                  {{ErrorNoRegular}}
                fi
                {{SustitucionLiteral("dir", "dirname -- \"$destino\"")}}
                {{SustitucionLiteral("base", "basename -- \"$destino\"")}}
                {{DefinicionAcortarNombre}}
                acortar_nombre
                {{DefinicionCandado}}
                if [ -e "$destino" ]; then
                  tomar_candado -x || exit 1
                  anunciar candado "$estado_candado"
                fi
                tmp=
                copia=
                fuera=0
                fase=
                {{DefinicionPrivilegios}}
                {{DefinicionEscrituraEnElSitio}}
                trap limpiar EXIT
                trap 'interrumpir 129' HUP
                trap 'interrumpir 130' INT
                trap 'interrumpir 143' TERM
                if ! tmp=$(mktemp -- "$dir/.$corto.tmp.XXXXXX" 2>/dev/null); then
                  {{temporalFuera}}
                fi
                anunciar_ruta tmp "$tmp"
                base64 -d > "$tmp" || exit 1
                verificar_contenido "$tmp" || { [ "$?" = 2 ] && exit {{CodigoSinVerificador}}; exit {{CodigoContenidoNoIntegro}}; }
                anunciar verificacion "$verificacion"
                {{colocar}}
                """);
            // La umask se toma del shell del usuario: bajo sudo sería la unión con la de sudoers.
            const string ModoPorUmask = "\"$(printf '%o' $(( 0666 & ~0$(umask) )))\"";
            string huella = contenidoLeido is null
                ? "'' ''"
                : $"{Convert.ToHexStringLower(SHA256.HashData(contenidoLeido))} '{CksumPosix.Calcular(contenidoLeido).ToString(CultureInfo.InvariantCulture)} {contenidoLeido.Length.ToString(CultureInfo.InvariantCulture)}'";
            return EnvolverEnBash(script, sudo, $"{rutaEscapada} {ModoPorUmask} {huella} {HuellaContenido(contenido)}", Convert.ToBase64String(contenido));
        }

        // acortar_nombre: deja en 'corto' el nombre del destino ('base') recortado a 242 bytes, para que el temporal
        // ".<corto>.tmp.XXXXXX" y la copia ".<corto>.orig.XXXXXX" quepan en NAME_MAX (255 bytes) con cualquier nombre válido.
        // Con LC_ALL=C las longitudes y los recortes de bash son en bytes; tras recortar se quita un carácter UTF-8 que haya
        // quedado partido al final (bytes de continuación 10xxxxxx sin todos los que pide su byte inicial).
        private static readonly string DefinicionAcortarNombre = """
            acortar_nombre() {
              local LC_ALL=C
              local i c
              corto=$base
              [ "${#corto}" -le 242 ] && return 0
              corto=${corto:0:242}
              for (( i = 1; i <= 4; i++ )); do
                c=${corto: -i:1}
                case $c in
                  [$'\x80'-$'\xbf']) continue ;;
                  [$'\xc0'-$'\xdf']) [ "$i" -lt 2 ] && corto=${corto:0:${#corto}-i} ;;
                  [$'\xe0'-$'\xef']) [ "$i" -lt 3 ] && corto=${corto:0:${#corto}-i} ;;
                  [$'\xf0'-$'\xf7']) [ "$i" -lt 4 ] && corto=${corto:0:${#corto}-i} ;;
                esac
                break
              done
              return 0
            }
            """.ReplaceLineEndings("\n");

        // renombrar y enlazar colocan el temporal en el destino con 'mv -T' y 'ln -T' (GNU coreutils), que tratan el destino
        // como un archivo y nunca meten el temporal dentro de un directorio. Si entre las comprobaciones y la colocación el
        // destino se ha convertido en un directorio, 'mv -T' falla; si se ha convertido en un enlace simbólico (también a un
        // directorio), 'mv -T' sustituye el enlace por el archivo nuevo, sin tocar el directorio. 'ln -T' falla en los dos
        // casos (el destino ya existe). Si mv o ln no admiten -T (p. ej. busybox o BSD), se comprueba justo antes que el
        // destino no es un directorio ni un enlace a uno (mv) o que no existe (ln); queda una ventana entre esa comprobación
        // y la colocación que solo -T cierra.
        private static readonly string DefinicionRenombrar = """
            renombrar() {
              if mv -T --help >/dev/null 2>&1; then
                mv -f -T -- "$tmp" "$destino" && return 0
              elif [ ! -d "$destino" ]; then
                mv -f -- "$tmp" "$destino" && return 0
              fi
              [ -d "$destino" ] && printf 'Error: %s se ha convertido en un directorio mientras se escribía: no se ha colocado el contenido nuevo.\n' "$ruta" >&2
              return 1
            }
            """.ReplaceLineEndings("\n");

        private static readonly string DefinicionEnlazar = """
            enlazar() {
              if ln -T --help >/dev/null 2>&1; then
                ln -T -- "$tmp" "$destino" 2>/dev/null
              else
                [ ! -e "$destino" ] && [ ! -L "$destino" ] && ln -- "$tmp" "$destino" 2>/dev/null
              fi
            }
            """.ReplaceLineEndings("\n");

        // sin_cambios: con la huella de lo que leyó ssh_replace_file_content ($sha_leido, $cksum_leido), el destino debe seguir
        // siendo ese archivo: se compara el sha256sum de su contenido (o, si no hay sha256sum, la salida de cksum: suma y
        // bytes); si cambió o ya no existe, no se escribe. Sin huella (ssh_write_file) no compara nada; sin ninguna de las
        // dos herramientas, no se puede comprobar y no se escribe.
        // verificar_contenido ARCHIVO: que lo decodificado en ARCHIVO sea lo que mandó native_ssh, antes de colocarlo o
        // anexarlo: el tamaño ($tam_esperado, con wc) y, con lo que haya en el servidor, el sha256 ($sha_esperado) o, sin
        // sha256sum, el cksum POSIX ($cksum_esperado: "suma bytes"); sin ninguno de los dos, solo el tamaño. Deja en
        // 'verificacion' cuál se comprobó (sha256, cksum o tamano) y devuelve 1 si no coincide, o 2 si no hay wc (no se
        // puede comprobar nada). El contenido llega al script
        // por la tubería de EnvolverEnBash desde el shell PTY, donde una función o un alias del usuario (p. ej. printf) puede
        // desviarlo: sin esto, un archivo vacío se daba por escrito.
        internal static readonly string DefinicionVerificarContenido = """
            verificar_contenido() {
              local tam suma
              verificacion=
              command -v wc >/dev/null 2>&1 || return 2
              tam=$(command wc -c < "$1") || return 1
              [ "$(( tam ))" = "$tam_esperado" ] || return 1
              if command -v sha256sum >/dev/null 2>&1; then
                suma=$(command sha256sum < "$1") || return 1
                [ "${suma%% *}" = "$sha_esperado" ] || return 1
                verificacion=sha256
              elif command -v cksum >/dev/null 2>&1; then
                suma=$(command cksum < "$1") || return 1
                [ "$suma" = "$cksum_esperado" ] || return 1
                verificacion=cksum
              else
                verificacion=tamano
              fi
              return 0
            }
            """.ReplaceLineEndings("\n");

        // Código con el que salen las escrituras cuando lo decodificado no es lo que se mandó (verificar_contenido).
        internal const int CodigoContenidoNoIntegro = 90;

        // Código con el que salen las escrituras cuando el servidor no tiene wc y no se puede comprobar lo decodificado.
        internal const int CodigoSinVerificador = 91;

        // Código con el que sale bash cuando no llega a ejecutar el script: en modo POSIX no arranca con una función exportada
        // que se llama como una orden interna especial (set, unset, export, exec, eval, trap, times...). Los scripts de
        // escritura y de lectura no salen nunca con él por su cuenta.
        internal const int CodigoBashNoArranco = 2;

        // El error de una escritura (o de la lectura previa de replace) que bash no llegó a ejecutar: no se hizo nada.
        internal static string MensajeBashNoArranco(string ruta, bool isAnexar) =>
            $"Error: bash no llegó a ejecutar el script de native_ssh (salió con el código 2 sin empezarlo): lo habitual es que el shell de la sesión exporte una función con el nombre de una orden interna especial de bash (set, unset, export, exec, eval, trap, times...), con la que bash en modo POSIX no arranca. {(isAnexar ? $"No se ha anexado nada a '{ruta}'." : $"No se ha modificado '{ruta}'.")} Quítala (unset -f y export -n con su nombre) o usa ssh_reset_shell, y vuelve a intentarlo.";

        // El error de una escritura que no pudo comprobar lo decodificado porque el servidor no tiene wc: no se hizo nada.
        internal static string MensajeSinVerificador(string ruta, bool isAnexar) =>
            $"Error: no se pudo comprobar que el contenido llegó íntegro al servidor: no tiene 'wc' (de coreutils o busybox), con el que native_ssh comprueba el tamaño de lo que llega antes de escribirlo. {(isAnexar ? $"No se ha anexado nada a '{ruta}'." : $"No se ha modificado '{ruta}'.")}";

        // La huella de lo que se manda, para verificar_contenido: "<bytes> <sha256> '<cksum> <bytes>'".
        private static string HuellaContenido(byte[] contenido) =>
            $"{contenido.Length.ToString(CultureInfo.InvariantCulture)} {Convert.ToHexStringLower(SHA256.HashData(contenido))} '{CksumPosix.Calcular(contenido).ToString(CultureInfo.InvariantCulture)} {contenido.Length.ToString(CultureInfo.InvariantCulture)}'";

        // El error de una escritura cuyo contenido no llegó íntegro al script: no se tocó el archivo.
        internal static string MensajeContenidoNoIntegro(string ruta, int bytes, bool isAnexar) =>
            $"Error: el contenido no llegó íntegro al script de escritura en el servidor (no tenía los {bytes} bytes enviados, o su sha256 no coincidía): algo en el shell de la sesión, como una función o un alias (p. ej. de printf o de command), alteró lo que se le pasó. {(isAnexar ? $"No se ha anexado nada a '{ruta}'." : $"No se ha modificado '{ruta}'.")} Revisa las funciones y alias del shell (p. ej. con ssh_reset_shell, o con sinPerfil=true si vienen del perfil) y vuelve a intentarlo.";

        private static readonly string DefinicionSinCambios = """
            sin_cambios() {
              [ -z "$sha_leido" ] && return 0
              local actual
              if [ -f "$destino" ] && command -v sha256sum >/dev/null 2>&1; then
                actual=$(sha256sum < "$destino") && [ "${actual%% *}" = "$sha_leido" ] && return 0
              elif [ -f "$destino" ] && command -v cksum >/dev/null 2>&1; then
                actual=$(cksum < "$destino") && [ "$actual" = "$cksum_leido" ] && return 0
              elif [ -f "$destino" ]; then
                printf 'Error: no se puede comprobar que %s no cambió desde la lectura (faltan sha256sum y cksum). No se ha modificado.\n' "$ruta" >&2
                return 1
              fi
              printf 'Error: el archivo %s cambió desde la lectura; vuelve a intentarlo. No se ha modificado.\n' "$ruta" >&2
              return 1
            }
            """.ReplaceLineEndings("\n");

        // anunciar CLAVE [VALOR...] y anunciar_ruta CLAVE RUTA: una línea "__NS_W_<marca>__ ..." en stdout para quien ejecuta
        // el script (InformeEscritura); la ruta va en base64 porque puede tener saltos de línea o bytes de control. El
        // marcador se compone con printf: su texto completo no aparece en el script. Un fallo al escribir el anuncio (p. ej.
        // con el terminal ya cerrado) no cambia el flujo: devuelven siempre 0.
        private static readonly string DefinicionAnunciar = PlantillaBash.Crear($$"""
            anunciar() {
              printf '{{InformeEscritura.PrefijoMarca}}%s__ %s\n' "$marca" "$*" 2>/dev/null
              return 0
            }
            anunciar_ruta() {
              printf '{{InformeEscritura.PrefijoMarca}}%s__ %s %s\n' "$marca" "$1" "$(printf '%s' "$2" | base64 | tr -d '\n')" 2>/dev/null
              return 0
            }
            """);

        // leer_privilegios y restaurar_privilegios: escribir en un archivo hace que el núcleo le quite el bit setuid y el
        // setgid (este, si el grupo tiene ejecución; salvo a quien tiene CAP_FSETID) y su security.capability (siempre,
        // también a root). Se miran antes de escribir en el sitio y se devuelven después, también tras restaurar el
        // original: los bits con chmod (el propietario no puede poner setgid si no es del grupo del archivo: chmod lo ignora
        // en silencio) y las capacidades con setfattr (bytes exactos, leídos con getfattr) o, si no hay getfattr, con setcap
        // (texto de getcap); escribirlas exige privilegios (sudo=true). Todo se comprueba. 'perdidos' queda con lo que no se
        // pudo devolver (setuid, setgid, capacidades) o comprobar (capacidades?: sin getfattr ni getcap no se puede saber si
        // el archivo las tenía, sea o no ejecutable); la capacidad original se anuncia para poder devolverla a mano.
        private static readonly string DefinicionPrivilegios = """
            leer_capacidad() {
              local salida
              case $cap_herramienta in
                getfattr) getfattr --only-values -n security.capability -- "$destino_abs" 2>/dev/null | od -An -tx1 | tr -d ' \n' ;;
                getcap)
                  salida=$(getcap -- "$destino_abs" 2>/dev/null)
                  salida=${salida#"$destino_abs"}
                  salida=${salida# = }
                  printf '%s' "${salida# }" ;;
              esac
              return 0
            }
            leer_privilegios() {
              suid=0
              sgid=0
              cap=
              cap_herramienta=
              cap_desconocida=0
              case $destino in
                /*) destino_abs=$destino ;;
                *) destino_abs=$PWD/$destino ;;
              esac
              [ -u "$destino" ] && suid=1
              [ -g "$destino" ] && sgid=1
              if command -v getfattr >/dev/null 2>&1; then
                cap_herramienta=getfattr
              elif command -v getcap >/dev/null 2>&1; then
                cap_herramienta=getcap
              else
                cap_desconocida=1
              fi
              cap=$(leer_capacidad)
              if [ -n "$cap" ] && [ "$cap_herramienta" = getfattr ]; then
                anunciar_ruta capacidad "setfattr -n security.capability -v 0x$cap"
              elif [ -n "$cap" ]; then
                anunciar_ruta capacidad "setcap '$cap'"
              fi
              return 0
            }
            restaurar_privilegios() {
              perdidos=
              if [ "$suid" = 1 ] && [ ! -u "$destino" ]; then
                chmod -- u+s "$destino" 2>/dev/null
                [ -u "$destino" ] || perdidos=$perdidos,setuid
              fi
              if [ "$sgid" = 1 ] && [ ! -g "$destino" ]; then
                chmod -- g+s "$destino" 2>/dev/null
                [ -g "$destino" ] || perdidos=$perdidos,setgid
              fi
              if [ -n "$cap" ]; then
                if [ "$(leer_capacidad)" != "$cap" ]; then
                  case $cap_herramienta in
                    getfattr) setfattr -n security.capability -v "0x$cap" -- "$destino_abs" 2>/dev/null ;;
                    getcap) setcap "$cap" "$destino_abs" 2>/dev/null ;;
                  esac
                fi
                [ "$(leer_capacidad)" = "$cap" ] || perdidos=$perdidos,capacidades
              elif [ "$cap_desconocida" = 1 ]; then
                perdidos=$perdidos,capacidades?
              fi
              perdidos=${perdidos#,}
              return 0
            }
            """.ReplaceLineEndings("\n");

        // escribir_en_sitio: sobrescribe "$destino" con "$tmp" sin dejarlo nunca truncado sin copia. Antes copia el original
        // a una copia de seguridad (mktemp, 0600) en su directorio o, si no admite escritura, en ${TMPDIR:-/tmp}; si no puede,
        // no toca nada. 'fase' sigue el estado del destino: '' (intacto), escribiendo, escrito, restaurando, restaurado o
        // fallido (no se pudo restaurar). Si la escritura falla, restaura la copia. La trampa de salida 'limpiar' es lo único
        // que borra la copia y el temporal, y solo con la fase '', escrito o restaurado: si el script se interrumpe
        // escribiendo o restaurando, la trampa vuelve a restaurar y solo borra si lo consigue. Si la restauración falla, el
        // error da las rutas de la copia y del temporal, que se conservan.
        // Interrupciones (Ctrl+C del timeout o de la cancelación, cierre del canal del shell, caída de la conexión, kill):
        // HUP, INT y TERM tienen trampa ('interrumpir'), que ignora las siguientes y sale por la trampa de salida; 'limpiar'
        // también las ignora desde su primera orden, aunque se llegue a ella por un 'exit' normal. Una señal ignorada se
        // hereda: el 'cat' que restaura tampoco muere con ellas. Sin esto, la trampa casi nunca restauraba tras cerrarse el
        // canal o caer la conexión: al colgarse el terminal, el grupo del script recibe varios SIGHUP casi a la vez (el que
        // el shell interactivo reenvía a sus trabajos al recibir el suyo, el que el núcleo manda al grupo de primer plano
        // cuando sale el líder de la sesión y, si el líder es otro proceso, como el shell de login que ejecuta el
        // ForceCommand, el que este reenvía a su vez); bash, sin trampa para SIGHUP, ejecuta la trampa de salida dentro de
        // su manejo de una señal mortal, y una segunda señal mortal en ese momento lo mata al instante (kill_shell en su
        // manejador, termsig_sighandler; medido con bash 5.3), a menudo antes de la primera orden de la trampa, y si no, el
        // siguiente SIGHUP mataba el 'cat' que restauraba. Tras la interrupción el script sigue hasta dejar el archivo
        // restaurado (o conservar la copia) aunque ya no haya terminal: solo SIGKILL lo detiene, y una restauración colgada
        // (p. ej. un sistema de archivos que no responde) sigue ahí hasta que la E/S termine.
        private static readonly string DefinicionEscrituraEnElSitio = """
            interrumpir() {
              trap '' HUP INT TERM
              exit "$1"
            }
            restaurar() {
              fase=restaurando
              anunciar fase restaurando
              if cat -- "$copia" > "$destino"; then
                restaurar_privilegios
                fase=restaurado
                anunciar fase restaurado "${perdidos:--}"
                printf 'Error: %s de %s; se ha restaurado su contenido original.\n' "$1" "$ruta" >&2
              else
                fase=fallido
                anunciar fase fallido
                printf 'Error: %s de %s y no se pudo restaurar su contenido original: la copia de seguridad del original está en %s y el contenido nuevo en %s (no se han borrado).\n' "$1" "$ruta" "$copia" "$tmp" >&2
              fi
              return 0
            }
            limpiar() {
              trap '' HUP INT TERM
              case $fase in
                escribiendo|restaurando) restaurar 'se interrumpió la escritura en el sitio' ;;
              esac
              if [ "$fase" = fallido ]; then
                anunciar conservados
                return 0
              fi
              rm -f -- "$tmp" "$copia" 2>/dev/null
              if { [ -n "$tmp" ] && [ -e "$tmp" ]; } || { [ -n "$copia" ] && [ -e "$copia" ]; }; then
                anunciar restos
              else
                anunciar limpio
              fi
              return 0
            }
            escribir_en_sitio() {
              copia=$(mktemp -- "$dir/.$corto.orig.XXXXXX" 2>/dev/null) || copia=$(mktemp -- "${TMPDIR:-/tmp}/.ns_original.XXXXXX" 2>/dev/null) || {
                copia=
                printf 'Error: no se pudo crear la copia de seguridad de %s (ni en su directorio ni en %s): no se sobrescribe en el sitio sin ella. No se ha modificado.\n' "$ruta" "${TMPDIR:-/tmp}" >&2
                return 1
              }
              anunciar_ruta copia "$copia"
              # Sin permiso de lectura (p. ej. 0200 sin sudo) el mensaje propio lo explica, sin la línea cruda de cat.
              if [ ! -r "$destino" ]; then
                printf 'Error: no se pudo hacer la copia de seguridad de %s: no tiene permiso de lectura (solo de escritura) y no se sobrescribe en el sitio sin ella. No se ha modificado: dale permiso de lectura o usa sudo=true.\n' "$ruta" >&2
                return 1
              fi
              if ! cat -- "$destino" > "$copia"; then
                printf 'Error: no se pudo hacer la copia de seguridad de %s: no se sobrescribe en el sitio sin ella. No se ha modificado.\n' "$ruta" >&2
                return 1
              fi
              leer_privilegios
              fase=escribiendo
              anunciar fase escribiendo
              if cat -- "$tmp" > "$destino"; then
                restaurar_privilegios
                fase=escrito
                anunciar fase escrito "${perdidos:--}"
                return 0
              fi
              restaurar 'falló la escritura en el sitio'
              return 1
            }
            """.ReplaceLineEndings("\n");

        // conservar_atributos ORIGINAL TEMPORAL: copia al temporal todos los atributos del original —modo y ACL, propietario
        // y grupo, atributos extendidos de todos los espacios de nombres y contexto SELinux— con cp de GNU coreutils, sin
        // tocar su contenido ni sus fechas (la de modificación queda la de la escritura), y comprueba que coinciden. Si algo
        // no se puede copiar o no coincide, devuelve 1 y el archivo se sobrescribe en el sitio, que lo conserva todo.
        // - cp falla si no puede copiar un atributo pedido explícitamente, salvo el propietario sin privilegios, que ignora:
        //   propietario, grupo y modo se comparan con stat.
        // - Sin SELinux en el núcleo, 'cp --preserve=context' falla al instante: se repite sin 'context'. El contexto se
        //   compara con stat %C ('?' y código 1 sin SELinux: se compara solo el texto).
        // - ACL y atributos extendidos: con getfattr, todos los espacios de nombres (ACL incluidas); si no, las ACL con
        //   getfacl y el indicador de ACL de 'ls -ld' (el temporal hereda la ACL por defecto del directorio y cp no se la
        //   quita si el original no tiene). Sin getfattr, de los demás atributos extendidos responde cp.
        // Sin 'grep -q': con pipefail, su salida anticipada mataría a getfattr por SIGPIPE.
        internal static readonly string DefinicionConservarAtributos = """
            volcar_atributos() {
              getfattr --absolute-names -d -m - -e hex -- "$1" 2>/dev/null | grep -v '^# file: ' | sort
            }
            mismas_acl_y_atributos() {
              if command -v getfattr >/dev/null 2>&1; then
                [ "$(volcar_atributos "$1")" = "$(volcar_atributos "$2")" ]
                return
              fi
              if command -v getfacl >/dev/null 2>&1; then
                [ "$(getfacl -c -p -- "$1" 2>/dev/null)" = "$(getfacl -c -p -- "$2" 2>/dev/null)" ] || return 1
              fi
              [ "$(ls -ld -- "$1" 2>/dev/null | cut -c 11)" = "$(ls -ld -- "$2" 2>/dev/null | cut -c 11)" ]
            }
            conservar_atributos() {
              cp --attributes-only --preserve=mode,ownership,xattr,context -- "$1" "$2" 2>/dev/null \
                || cp --attributes-only --preserve=mode,ownership,xattr -- "$1" "$2" 2>/dev/null \
                || return 1
              local original
              original=$(stat -c '%u:%g:%a' -- "$1" 2>/dev/null) && [ -n "$original" ] || return 1
              [ "$original" = "$(stat -c '%u:%g:%a' -- "$2" 2>/dev/null)" ] || return 1
              [ "$(stat -c %C -- "$1" 2>/dev/null)" = "$(stat -c %C -- "$2" 2>/dev/null)" ] || return 1
              mismas_acl_y_atributos "$1" "$2"
            }
            """.ReplaceLineEndings("\n");

        // $(cmd) quita TODOS los saltos de línea finales, también los que forman parte de un nombre de archivo ("victima\n"
        // acabaría en "victima"). Se añade un centinela 'x' tras la salida y se quita después junto con el único '\n' que
        // añade el propio comando. Requiere bash ($'\n'): los scripts se ejecutan siempre con EnvolverEnBash.
        private static string SustitucionLiteral(string variable, string comando) =>
            $"{variable}=$({comando}; printf x); {variable}=${{{variable}%x}}; {variable}=${{{variable}%$'\\n'}}";

        // Ejecuta 'script' con bash ("$1", "$2"... = 'argumentos', ya escapados), con 'sudo bash' si sudo=true, de modo
        // que todas sus comprobaciones (test -e/-d/-L, readlink, stat...) usan el mismo privilegio que la escritura.
        // - 'script' es solo código, construido con PlantillaBash; los valores del usuario van en 'argumentos' y se copian
        //   tal cual (no se normaliza nada después de interpolarlos).
        // - El cd a __NS_CWD se hace fuera de sudo: env_reset descarta __NS_CWD y cambia HOME, pero sudo conserva el cwd.
        // - El contenido (base64) llega por stdin, nunca como argumento: un argumento no puede pasar de 128 KiB (MAX_ARG_STRLEN).
        //   sudo pide la contraseña por /dev/tty, no por stdin.
        // - Es siempre bash (no el shell de login), así que el script puede usar sintaxis de bash como $'\n'.
        // - El script se pone a salvo de las funciones que hereda (las exportadas con 'export -f' en el shell persistente, que
        //   'bash -c' importaría: un 'printf' que sale con otro código o un 'base64' que cambia lo que decodifica): bash
        //   arranca en modo POSIX (--posix, que además no lee BASH_ENV), donde 'unset' (orden interna especial) va antes
        //   que cualquier función, quita 'declare' y después todas las que haya, y vuelve al modo normal. Una función
        //   heredada con el nombre de una orden interna especial (unset, set, export...) hace que bash en modo POSIX no
        //   arranque (código 2): falla sin ejecutar nada. Medido con bash 5.3: con printf, declare, command y builtin
        //   exportadas, el script ve 0 funciones y el printf de verdad.
        // - Fuera del script, en el shell persistente del usuario, no hay forma completa de evitar sus funciones: printf,
        //   sudo y bash se llaman con 'command' (salta una función con su nombre, no una función 'command'), y lo que llega
        //   por la tubería lo comprueban las escrituras (DefinicionVerificarContenido).
        // - Tampoco hereda las opciones del shell de la sesión: SHELLOPTS y BASHOPTS exportados (noclobber, errexit, nounset,
        //   xtrace, noexec; failglob...) y PS4 (que con xtrace ejecutaría lo que lleve con $(…) antes de cada orden) se quitan
        //   del entorno al lanzarlo (env -u; con noexec heredado no se ejecutaría ni el prólogo), y el prólogo deja además
        //   xtrace, verbose, noclobber, errexit y nounset desactivadas.
        private const string PrologoSinFunciones = """
            unset -f declare
            for __ns_f in $(declare -F); do case $__ns_f in declare|-f) ;; *) unset -f -- "$__ns_f" ;; esac; done
            unset __ns_f
            set +o posix +o xtrace +o verbose +o noclobber +o errexit +o nounset
            set -o pipefail

            """;

        private static string EnvolverEnBash(string script, bool sudo, string argumentos, string? payloadBase64 = null)
        {
            string entrada = payloadBase64 is null ? string.Empty : $"command printf '%s' '{payloadBase64}' | ";
            string programa = sudo ? "command env -u SHELLOPTS -u BASHOPTS -u PS4 sudo bash --posix" : "command env -u SHELLOPTS -u BASHOPTS -u PS4 bash --posix";
            return PlantillaBash.Crear($$"""
                (
                cd "${__NS_CWD:-$HOME}" 2>/dev/null || cd ~
                set -o pipefail
                {{entrada}}{{programa}} -c {{Escapar(PrologoSinFunciones.ReplaceLineEndings("\n") + script)}} ns {{argumentos}}
                )
                """);
        }

        private const string PrefijoMarcaBase64 = "__NS_B64_";
        private static readonly SearchValues<char> CaracteresBase64 = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=");

        // La lectura previa de ssh_replace_file_content va por el PTY, donde el stderr (avisos de sudo, trabajos en segundo
        // plano...) se mezcla con stdout: un aviso de solo letras se colaría como base64 válido. El script imprime el base64
        // entre una línea "__NS_B64_INICIO_<id>__" y otra "__NS_B64_FIN_<id>__ <tamaño según stat>", con un id aleatorio
        // por petición que nada ajeno puede imitar (y el base64 no contiene '_'). Solo se decodifica lo que hay entre ellas:
        // cada marcador debe aparecer exactamente una vez y en orden, entre ellos solo puede haber base64 y lo decodificado
        // debe medir lo que dijo stat (así tampoco pasa una línea ajena de solo letras ni un archivo que cambió al leerlo).
        // Los marcadores caben de sobra en MargenSalidaBase64.
        internal static byte[] ExtraerArchivoEntreMarcadores(string salida, string idMarcas, string ruta)
        {
            string marcaInicio = $"{PrefijoMarcaBase64}INICIO_{idMarcas}__";
            string marcaFin = $"{PrefijoMarcaBase64}FIN_{idMarcas}__ ";
            string[] lineas = salida.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
            int idxInicio = -1;
            int idxFin = -1;
            int inicios = 0;
            int fines = 0;
            for (int i = 0; i < lineas.Length; i++)
                if (lineas[i] == marcaInicio)
                {
                    inicios++;
                    idxInicio = i;
                }
                else if (lineas[i].StartsWith(marcaFin, StringComparison.Ordinal))
                {
                    fines++;
                    idxFin = i;
                }

            string sinEscribir = $"No se ha escrito nada en '{ruta}'.";
            if (inicios != 1 || fines != 1 || idxFin < idxInicio)
                throw new NativeSshException($"La lectura de '{ruta}' no devolvió su contenido entre los marcadores esperados (inicio: {inicios}, fin: {fines}); ¿se interrumpió o se mezcló otra salida del terminal? {sinEscribir}");

            if (!long.TryParse(lineas[idxFin].AsSpan(marcaFin.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long tamanoEsperado))
                throw new NativeSshException($"La lectura de '{ruta}' no indicó el tamaño del archivo en su marcador final. {sinEscribir}");

            string contenido = string.Concat(lineas.AsSpan(idxInicio + 1, idxFin - idxInicio - 1));
            if (contenido.AsSpan().ContainsAnyExcept(CaracteresBase64))
                throw new NativeSshException($"La lectura de '{ruta}' tiene texto ajeno al base64 entre sus marcadores (¿avisos de sudo o de otro proceso mezclados con la salida?). {sinEscribir}");

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(contenido);
            }
            catch (FormatException)
            {
                throw new NativeSshException($"La lectura de '{ruta}' no devolvió base64 válido entre sus marcadores (¿avisos de sudo o de otro proceso mezclados con la salida?). {sinEscribir}");
            }

            return bytes.LongLength != tamanoEsperado
                ? throw new NativeSshException($"Se leyeron {bytes.LongLength} bytes de '{ruta}', pero stat indicó {tamanoEsperado}: el archivo cambió mientras se leía o se mezcló otra salida del terminal. {sinEscribir}")
                : bytes;
        }

        // Los errores conocidos de la lectura previa de ssh_replace_file_content: el script sale con estos códigos, sin texto.
        internal enum CodigoErrorLecturaEnum
        {
            NoExiste = 80,
            SinAcceso = 81,
            Directorio = 82,
            NoRegular = 83,
            SinPermisoLectura = 84,
            Candado = 85,
            SinTamano = 86,
            DemasiadoGrande = 87,
            NoLeido = 88,
            Sustituido = 89
        }

        // El error de la lectura previa de ssh_replace_file_content que terminó con un código distinto de 0. Nunca lleva su
        // salida: a partir del marcador de inicio es el archivo en base64, y un 'printf' redefinido en el shell puede dejar
        // el base64 (o el archivo en claro) sin marcadores. Para los errores conocidos, el mensaje que native_ssh construye
        // con el código y la ruta; para cualquier otro código, el código (con sudo=true, también si sudo no llegó a ejecutar
        // la lectura).
        internal static string MensajeErrorLecturaReplace(int codigo, string ruta, bool isSudo, long maxBytesEditable) => codigo == CodigoBashNoArranco ? MensajeBashNoArranco(ruta, isAnexar: false) : (CodigoErrorLecturaEnum)codigo switch
        {
            CodigoErrorLecturaEnum.NoExiste => $"Error: el archivo no existe: {ruta}",
            CodigoErrorLecturaEnum.SinAcceso => $"Error: sin permiso para acceder a la ruta: {ruta} (algún directorio de la ruta no se puede atravesar; prueba con sudo=true).",
            CodigoErrorLecturaEnum.Directorio => $"Error: la ruta es un directorio: {ruta}",
            CodigoErrorLecturaEnum.NoRegular => $"Error: {ruta} no es un archivo regular (es una FIFO, un socket o un dispositivo): no se lee ni se escribe en él. No se ha modificado.",
            CodigoErrorLecturaEnum.SinPermisoLectura => $"Error: sin permiso de lectura: {ruta} (prueba con sudo=true).",
            CodigoErrorLecturaEnum.Candado => $"Error: hay otra escritura, reemplazo o restauración en curso sobre {ruta} (otro proceso tiene tomado su candado, flock: otra operación sobre el archivo o un programa que lo tiene abierto con candado, como una aplicación .NET). No se ha modificado: reintenta cuando termine.",
            CodigoErrorLecturaEnum.Sustituido => $"Error: {ruta} se sustituyó por otro archivo cada vez que se tomaba su candado. No se ha modificado: reintenta.",
            CodigoErrorLecturaEnum.SinTamano => $"Error: no se pudo obtener el tamaño de: {ruta}",
            CodigoErrorLecturaEnum.DemasiadoGrande => $"Error: el archivo {ruta} supera el máximo editable con ssh_replace_file_content ({maxBytesEditable} bytes). No se ha modificado.",
            CodigoErrorLecturaEnum.NoLeido => $"Error: no se pudo leer {ruta} (cat o base64 fallaron). No se ha modificado.",
            _ => $"Error al leer el archivo para reemplazo (código {codigo}{(isSudo ? "; con sudo=true, también si sudo no llegó a ejecutar la lectura: contraseña incorrecta o sin permiso de sudo" : string.Empty)}). No se muestra la salida de la lectura, que puede ser el archivo. No se ha modificado '{ruta}'."
        };

        // Solo se edita lo que sobrevive a bytes → UTF-8 → bytes: UTF-8 estricto (el BOM se conserva como U+FEFF) y sin NUL.
        private static string DecodificarArchivoUtf8(byte[] bytes, string ruta)
        {
            string texto;
            try
            {
                texto = Utf8Estricto.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new NativeSshException($"'{ruta}' no es texto UTF-8 válido (¿otra codificación, p. ej. Latin-1, o un archivo binario?): ssh_replace_file_content no lo edita para no corromperlo. No se ha escrito nada.");
            }

            return texto.Contains('\0', StringComparison.Ordinal)
                ? throw new NativeSshException($"'{ruta}' parece un archivo binario (contiene bytes NUL): ssh_replace_file_content no lo edita. No se ha escrito nada.")
                : texto;
        }

        // Nunca devuelve la cola como si fuera el contenido completo: si se descartó el principio, error.
        private string ReconstruirSalidaCompleta(Peticion peticion, string consejo)
        {
            string texto = ReconstruirSalida(peticion, out bool hasDatosDescartados);
            return hasDatosDescartados
                ? throw new NativeSshException($"La salida superó el máximo de {opciones.MaxBytesSalidaPorPeticion} bytes por petición y se descartó su principio. {consejo}")
                : texto;
        }

        private string ReconstruirSalida(Peticion peticion, out bool hasDatosDescartados)
        {
            StringBuilder sb = new();
            long desde = 0;
            hasDatosDescartados = false;
            while (true)
            {
                PaginaSalida pagina = PaginadorSalida.Leer(peticion.Salida, desde, opciones.MaxBytesPorRespuesta, peticion.Estado.IsFinal());
                hasDatosDescartados |= pagina.HasDatosDescartados;
                _ = sb.Append(pagina.Texto);
                if (!pagina.HasMas)
                    break;
                desde = pagina.Siguiente;
            }

            return sb.ToString();
        }


        // El aviso del estado final con el de las líneas tecleadas descartadas, si ninguna respuesta lo ha dado aún.
        private static string? AvisoConTecleado(Peticion peticion) => peticion.TryTomarAvisoTecleado()
            ? Unir(peticion.Aviso, SesionShell.DescribirTecleadoDescartado(peticion.LineasTecleadoDescartadas, peticion.IdComandoQueNoLeyo))
            : peticion.Aviso;

        // Los avisos de las peticiones anteriores del shell enlazadas (Peticion.AnteriorConTecleadoDescartado) que ninguna
        // respuesta ha dado aún, del más antiguo al más reciente. El de una herramienta no cita su id (Peticion.IsHerramienta).
        // No dicen «de este shell»: la petición enlazada puede ser de un shell anterior (ssh_reset_shell). Recorrido iterativo
        // y con tope (Peticion.MaxAvisosTecleadoEnlazados): de una cadena más larga solo se dan los avisos de las más
        // recientes (antes era recursivo y una cadena de decenas de miles desbordaba la pila del servidor).
        internal static string? AvisosTecleadoDeAnteriores(Peticion? anterior)
        {
            if (anterior is null)
                return null;

            Peticion[] enlazadas = new Peticion[Peticion.MaxAvisosTecleadoEnlazados];
            int cuantas = 0;
            for (Peticion? actual = anterior; actual is not null && cuantas < enlazadas.Length; actual = actual.AnteriorConTecleadoDescartado)
                enlazadas[cuantas++] = actual;

            string? avisos = null;
            for (int i = cuantas - 1; i >= 0; i--)
            {
                Peticion enlazada = enlazadas[i];
                if (!enlazada.TryTomarAvisoTecleado())
                    continue;

                string quien = enlazada.IdComandoQueNoLeyo is { } idQueNoLeyo ? $"el comando {idQueNoLeyo}" : $"el comando anterior a {(enlazada.IsHerramienta ? "ella" : "él")}";
                string lineas = SesionShell.DescribirLineas(enlazada.LineasTecleadoDescartadas);
                avisos = Unir(avisos, enlazada.IsHerramienta
                    ? $"Una herramienta de archivo o ssh_set_cwd anterior avisa de que {quien} no leyó {lineas} de entrada tecleada con ssh_input: se descartaron sin ejecutarse al llegar ella (ninguna se ejecutó como orden)."
                    : $"El final del comando {enlazada.IdTexto}, que aún no se ha leído, avisa de que {quien} no leyó {lineas} de entrada tecleada con ssh_input: se descartaron sin ejecutarse (ninguna se ejecutó como orden).");
            }

            return avisos;
        }

        // El resumen de los avisos que la compactación soltó por el tope (Peticion.CompactarAvisosTecleadoPendientes), una sola
        // vez: la cuenta exacta de comandos y de líneas, sin detallarlos.
        internal static string? DescribirAvisosOmitidos(AvisosTecleadoOmitidos? omitidos)
        {
            if (omitidos is null || !omitidos.TryTomar(out int peticiones, out int lineas, out bool isAlMenos))
                return null;

            string comandos = peticiones == 1 ? "otro comando anterior no leyó" : $"otros {peticiones} comandos anteriores no leyeron";
            string cuantas = isAlMenos ? $"{lineas} o más líneas" : SesionShell.DescribirLineas(lineas);
            return $"Además, {comandos} {cuantas} de entrada tecleada con ssh_input: se descartaron sin ejecutarse (ninguna se ejecutó como orden). No se detallan uno por uno: cada respuesta detalla como mucho {Peticion.MaxAvisosTecleadoEnlazados}.";
        }

        // Respuesta de ssh_exec, ssh_output, ssh_input y ssh_cancel en dos bloques de texto MCP, siempre los dos y en este orden:
        // el control (ControlComando, JSON compacto en una línea) y la salida tal cual, sin escapar (vacía si no hay). Así el
        // modelo ve saltos de línea reales y, si el cliente vuelca la respuesta a un archivo (agy lo hace por encima de unos
        // pocos KB), el archivo tiene líneas reales y empieza por el control. Solo tipos del SDK y el contexto JSON generado:
        // sin reflexión (Native AOT).
        internal static CallToolResult DosBloques(ResultadoComando resultado) => new()
        {
            Content =
            [
                new TextContentBlock { Text = JsonSerializer.Serialize(resultado.Control(), TipoControl) },
                new TextContentBlock { Text = resultado.Salida }
            ]
        };

        private static JsonTypeInfo<ControlComando> TipoControl => field ??= (JsonTypeInfo<ControlComando>)ContextoJsonNativeSsh.OpcionesHerramientas.GetTypeInfo(typeof(ControlComando));

        private ResultadoComando Resultado(Peticion peticion, long desdeByte)
        {
            // Se lee el estado antes que la salida: si ya era final, la salida está completa.
            EstadoPeticionEnum estado = peticion.Estado;
            TimeSpan? tiempoSinSalida = peticion.TiempoSinSalida;
            PaginaSalida pagina = PaginadorSalida.Leer(peticion.Salida, desdeByte, opciones.MaxBytesPorRespuesta, estado.IsFinal(), isComoTerminal: true);

            // El aviso de las líneas tecleadas descartadas sale una sola vez: en el final de este comando o, si nadie lo ha leído
            // aún (p. ej. se lanzó con esperarSegundos=0 y después otro ssh_exec), en la primera respuesta de un comando
            // posterior del mismo shell, aunque en medio haya herramientas de archivo o ssh_set_cwd (Peticion.TryTomarAvisoTecleado).
            // Dados los avisos de las enlazadas, el enlace se suelta: no se retienen ni se vuelven a recorrer.
            string? avisoFinal = estado.IsFinal() ? AvisoConTecleado(peticion) : peticion.Aviso;
            Peticion? anterior = peticion.AnteriorConTecleadoDescartado;
            avisoFinal = Unir(avisoFinal, AvisosTecleadoDeAnteriores(anterior));
            if (anterior is not null)
                peticion.SoltarAnteriorConTecleadoDescartado(anterior);
            avisoFinal = Unir(avisoFinal, DescribirAvisosOmitidos(peticion.AvisosTecleadoOmitidos));

            string? avisoAntipatron = DetectarAvisoAntipatron(peticion.Comando);
            if (avisoAntipatron is not null)
                avisoFinal = Unir(avisoFinal, avisoAntipatron);

            ResultadoComando resultado = new(
                peticion.IdTexto,
                peticion.Destino.Clave,
                estado,
                Codigo(peticion),
                pagina.Texto,
                pagina.Desde,
                pagina.Siguiente,
                pagina.HasMas,
                pagina.HasDatosDescartados,
                Math.Round(peticion.TiempoEjecucion.TotalSeconds, 1),
                tiempoSinSalida is { } sinSalida ? Math.Round(sinSalida.TotalSeconds, 1) : null,
                avisoFinal);
            return peticion.InformeGuardado is { } informe ? ConSalidaGuardada(resultado, peticion, informe, estado) : resultado;
        }

        // Cuándo se borra el archivo de guardarSalida (SesionShell: los directorios de todos los shells de la sesión).
        internal const string ReglaBorradoGuardado = "Se borra con ssh_reset_shell, ssh_disconnect o al cerrarse la sesión; un shell nuevo que se abra solo (tras un plazo agotado, una cancelación o una caída del shell) no lo borra.";

        // ssh_exec con guardarSalida (SalidaGuardada): el control lleva el archivo, sus líneas y sus bytes, y el bloque de salida
        // sus primeras líneas en lugar de la salida. Lo que el comando escribió directamente en el terminal (no está en el
        // archivo: la petición de contraseña de sudo, un programa que escribe en /dev/tty) va detrás, separado. Las marcas del
        // envoltorio nunca están en la salida (FiltroMarcasGuardado). La vista previa la leyó la sesión por un canal exec al
        // terminar el comando, y no la hay si se envió un secreto con ssh_input (el archivo no pasa por el enmascarado).
        private static ResultadoComando ConSalidaGuardada(ResultadoComando resultado, Peticion peticion, InformeGuardado informe, EstadoPeticionEnum estado)
        {
            string terminal = resultado.Salida;
            string? aviso;
            string vista = string.Empty;
            if (informe.Error is not null)
                aviso = null;
            else if (informe.IsTerminado && (informe.Lineas is null || informe.Bytes is null))
                aviso = $"El comando terminó, pero su archivo {informe.Archivo} ya no estaba al contar sus líneas y sus bytes: lo borró o lo movió el propio comando u otro proceso, o lo borró el barrido de restos antiguos (sin cambios en 24 h) de otra sesión de native_ssh. No hay recuento ni vista previa.";
            else if (informe.IsTerminado)
            {
                bool isSecreto = peticion.Salida.HasSecretos;
                vista = isSecreto ? string.Empty : SalidaGuardada.TextoVistaPrevia(informe);
                long lineas = informe.Lineas ?? 0;
                bool isSinVista = isSecreto || (lineas > 0 && informe.VistaPrevia is null);
                string cuenta = $"{lineas} líneas, {informe.Bytes} bytes; aquí solo {(isSinVista ? "el control" : $"sus {Math.Min(lineas, SalidaGuardada.LineasVistaPrevia)} primeras líneas")}";
                aviso = $"Salida completa guardada en {informe.Archivo} ({cuenta}). Léela por tramos con ssh_read_file (lineaInicio/lineaFin) o busca en ella con ssh_search (ruta = el archivo); NO la vuelques entera con cat. {ReglaBorradoGuardado}";
                if (isSecreto)
                    aviso += " Sin vista previa: se envió un secreto con ssh_input y el archivo no pasa por el enmascarado (tiene lo que imprimió el programa).";
                else if (isSinVista)
                    aviso += $" Sin vista previa: no se pudo leer el principio del archivo ({informe.MotivoSinVistaPrevia ?? "motivo desconocido"}).";
            }
            else if (!estado.IsFinal())
                aviso = informe.Archivo is { } archivo
                    ? $"Sigue en ejecución y su salida se está guardando en {archivo}: cuando termine, ssh_output (con este id) da sus líneas, sus bytes y sus primeras líneas. Mientras tanto, el bloque de salida solo lleva lo que el comando escribe directamente en el terminal (p. ej. la petición de contraseña de sudo). {ReglaBorradoGuardado}"
                    : "Su salida irá a un archivo del servidor (guardarSalida), pero el comando aún no ha empezado: ssh_output (con este id) dará la ruta.";
            else
                aviso = informe.Archivo is not { } archivo ? null
                    : estado == EstadoPeticionEnum.Cancelado
                    ? $"El comando no terminó normalmente (estado {estado}): lo que escribió hasta entonces está en {archivo}, sin recuento de líneas ni vista previa (si lo canceló ssh_reset_shell o ssh_disconnect, ya se borró con su directorio); léelo con ssh_read_file. {ReglaBorradoGuardado}"
                    : $"El comando no terminó normalmente (estado {estado}): lo que escribió hasta entonces está en {archivo}, sin recuento de líneas ni vista previa; léelo con ssh_read_file. {ReglaBorradoGuardado}";

            string salida = terminal.Length == 0 ? vista
                : vista.Length == 0 ? terminal
                : $"{vista}{(vista.EndsWith('\n') ? "" : "\n")}--- escrito directamente en el terminal (no está en el archivo) ---\n{terminal}";
            return resultado with
            {
                Salida = salida,
                // Primero lo de guardarSalida (la ruta y cómo leerla) y después lo demás (sugerencias de rendimiento incluidas): con
                // varios avisos, la ruta quedaba al final de cientos de caracteres (RC8).
                Aviso = Unir(aviso, resultado.Aviso),
                Archivo = informe.Archivo,
                Lineas = informe.Lineas,
                Bytes = informe.Bytes
            };
        }

        private static string? Unir(string? primero, string? segundo) =>
            primero is null ? segundo : segundo is null ? primero : $"{primero} {segundo}";

        private static int? Codigo(Peticion peticion) => peticion.IsFinalizada ? peticion.CodigoSalida : null;

        // Descripción de una petición interna de una herramienta (Peticion.Descripcion): «[herramienta] ruta» y, si la llamada
        // hace varias, la fase. Nunca el script ni el contenido.
        private static string DescribirHerramienta(string herramienta, string ruta, string? fase = null) =>
            fase is null ? $"[{herramienta}] {ruta}" : $"[{herramienta}] {ruta} ({fase})";

        private static string Recortar(string comando)
        {
            string linea = comando.ReplaceLineEndings(" ");
            return linea.Length <= MaxCaracteresComando ? linea : string.Concat(linea.AsSpan(0, MaxCaracteresComando - 1), "…");
        }

        // consecuencia: lo que no llegó a pasar si la operación no termina (p. ej. "No se ha modificado ..."), para el error.
        // 'consecuencia' se calcula con la petición ya cancelada (si seguía en la cola, ya no se ejecutará).
        // descripcion y grupo: lo que ssh_status muestra de la petición y la llamada a la que pertenece (Peticion.Descripcion).
        private async Task<Peticion> EjecutarHerramientaAsync(string destino, string comando, string herramienta, string descripcion, Guid grupo, int timeoutSegundos, Func<Peticion, string>? consecuencia, CancellationToken cancellationToken)
        {
            TimeSpan timeout = TimeoutForzado ?? TimeSpan.FromSeconds(timeoutSegundos);
            Peticion peticion = gestor.Ejecutar(destino, comando, timeout, herramienta: herramienta, descripcion: descripcion, grupo: grupo);
            try
            {
                await peticion.Finalizacion.WaitAsync(timeout + opciones.EsperaTrasCtrlC + MargenEsperaHerramienta, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Sigue encolada o el shell no respondió al Ctrl+C: se cancela para no bloquear la cola.
                EstadoPeticionEnum estado = peticion.Estado;
                _ = peticion.SolicitarCancelacion();
                throw new NativeSshException($"La operación no terminó en {timeout.TotalSeconds:0} s (estado: {estado}); se ha cancelado la petición {peticion.IdTexto}.{(consecuencia is null ? string.Empty : " " + consecuencia(peticion))}");
            }
            catch (OperationCanceledException ex)
            {
                _ = peticion.SolicitarCancelacion();
                if (consecuencia is null)
                    throw;

                throw new OperationCanceledException($"Se canceló la operación (petición {peticion.IdTexto}). {consecuencia(peticion)}", ex, cancellationToken);
            }

            return peticion;
        }

        // Escrituras (ssh_write_file, ssh_replace_file_content) por el shell PTY. Si no terminan (timeout, cancelación con
        // ssh_cancel, ssh_disconnect o el token, conexión perdida), se cancela la petición y se espera a que el script atienda
        // el Ctrl+C (su trampa de salida restaura el original o conserva las copias) para que el error diga qué pasó con el
        // archivo según lo que el script anunció (InformeEscritura), con el id de la petición. Con el token
        // cancelado se lanza OperationCanceledException con ese mismo mensaje. Si terminan, quien llama mira el código.
        // herramienta: la que escribe (Peticion.HerramientaEscritura); si la sesión la rechaza por el modo solo lectura, el
        // error es ese rechazo.
        private async Task<(Peticion Peticion, InformeEscritura Informe)> EjecutarEscrituraAsync(string destino, string comando, string idMarcas, string ruta, string herramienta, string descripcion, Guid grupo, CancellationToken cancellationToken)
        {
            TimeSpan timeout = TimeoutForzado ?? TimeSpan.FromSeconds(TimeoutEscrituraSegundos);
            Peticion peticion = gestor.Ejecutar(destino, comando, timeout, herramienta: herramienta, herramientaEscritura: herramienta, descripcion: descripcion, grupo: grupo);
            bool isSinTerminar = false;
            bool isCancelada = false;
            try
            {
                await peticion.Finalizacion.WaitAsync(timeout + opciones.EsperaTrasCtrlC + MargenEsperaHerramienta, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                isSinTerminar = true;
            }
            catch (OperationCanceledException)
            {
                isCancelada = true;
            }

            if (isSinTerminar || isCancelada)
            {
                _ = peticion.SolicitarCancelacion();
                await EsperarFinalizacionAsync(peticion).ConfigureAwait(false);
            }

            // Si se descartó el principio de la salida, pueden faltar anuncios: el informe no da nada por confirmado.
            InformeEscritura informe = InformeEscritura.Leer(ReconstruirSalida(peticion, out bool hasDatosDescartados), idMarcas, isSalidaCompleta: !hasDatosDescartados);
            EstadoPeticionEnum estado = peticion.Estado;
            if (estado == EstadoPeticionEnum.Completado && !isCancelada)
                return (peticion, informe);

            if (peticion.IsRechazadaSoloLectura)
                throw new NativeSshException($"La escritura en '{ruta}' no se hizo y '{ruta}' no se ha modificado. {peticion.Aviso}");

            if (IsNoEnviadaAlShell(peticion))
                throw new NativeSshException($"La escritura en '{ruta}' no se hizo: {NoEnviadaAlShell} '{ruta}' no se ha modificado. {peticion.Aviso}");

            // Interrumpida y terminada sin que el comando llegara al shell (en la cola o mientras se abría el shell): no se
            // ejecutó nada, con certeza (IsInterrumpidaSinEnviar). Que la petición saliera de la cola (TiempoEjecucion > 0)
            // no dice que el script empezara: la apertura del shell puede durar segundos. Si la escritura del comando ya había
            // empezado, va al camino incierto aunque falte la señal: pudo quedarse a medias, tanto si la petición terminó en
            // ShellPerdido (falló la escritura) como en Cancelado (el cierre de la sesión, ssh_disconnect, la cortó;
            // SesionShell.BucleComandosAsync).
            string mensaje = informe.MensajeInterrupcion(ruta, estado, peticion.IdTexto, AvisoConTecleado(peticion), isEjecutada: !IsInterrumpidaSinEnviar(peticion));
            throw isCancelada ? new OperationCanceledException(mensaje, cancellationToken) : new NativeSshException(mensaje);
        }

        // Tras cancelar: el Ctrl+C llega al script y su trampa de salida termina antes de que el shell responda (o, si no
        // responde a tiempo, se cierra el canal). Acotado: si aun así no termina, el error dirá lo anunciado hasta entonces.
        private async Task EsperarFinalizacionAsync(Peticion peticion)
        {
            try
            {
                await peticion.Finalizacion.WaitAsync(opciones.EsperaTrasCtrlC + MargenEsperaHerramienta).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Sigue sin terminar.
            }
        }

        internal const string AvisoContenidoNoUtf8 = "[aviso: contenido no UTF-8; bytes inválidos sustituidos por U+FFFD]";

        // IsUtf8Valido: stdout era UTF-8 válido (si no, Salida lleva U+FFFD en lugar de los bytes inválidos).
        private readonly record struct SalidaLectura(int Codigo, string Salida, string Errores, bool IsUtf8Valido = true)
        {
            // Mensaje de error: stderr del script ("Error: ..."), o stdout si no lo hubo, o el código.
            public string Error(string contexto) =>
                !string.IsNullOrWhiteSpace(Errores) ? Errores.Trim()
                : !string.IsNullOrWhiteSpace(Salida) ? Salida.Trim()
                : $"{contexto} (código {Codigo}).";
        }

        // Herramientas de solo lectura: canal exec sobre la conexión de la sesión, sin cola, sin PTY ni limpieza ANSI
        // (la salida llega tal cual, \r incluidos). Salida por encima del tope o sin código = error, nunca parcial.
        private async Task<SalidaLectura> EjecutarLecturaAsync(string destino, string script, int timeoutSegundos, string consejo, CancellationToken cancellationToken)
        {
            TimeSpan timeout = TimeoutForzado ?? TimeSpan.FromSeconds(timeoutSegundos);
            ResultadoCanalExec resultado = await gestor.EjecutarExecAsync(destino, script, timeout, cancellationToken).ConfigureAwait(false);
            if (resultado.HasExcedidoTope)
                throw new NativeSshException($"La salida superó el máximo de {opciones.MaxBytesSalidaPorPeticion} bytes por petición; no se devuelve contenido parcial. {consejo}");

            string salida = Encoding.UTF8.GetString(resultado.Salida.Span);
            string errores = Encoding.UTF8.GetString(resultado.Errores.Span);
            return resultado.CodigoSalida switch
            {
                null => throw new NativeSshException(resultado.SenalSalida is { Length: > 0 } senal
                    ? $"La operación terminó por la señal {senal} sin código de salida."
                    : "La operación terminó sin código de salida (¿se cerró el canal?)."),
                CodigoTimeoutRemoto or CodigoTimeoutRemotoKill => throw new NativeSshException($"La operación no terminó en {timeout.TotalSeconds:0.#} s (tiempo agotado); se detuvo en el servidor."),
                int codigo => new SalidaLectura(codigo, salida, errores, Utf8.IsValid(resultado.Salida.Span))
            };
        }

        // Códigos de 'timeout' en el servidor (ConstructorComandoExec): 124 al agotarse, 137 si hubo que matarlo con KILL.
        private const int CodigoTimeoutRemoto = 124;
        private const int CodigoTimeoutRemotoKill = 137;

        // El comando no llegó al shell: la sesión finaliza la petición en Error sin haberlo escrito en el canal
        // (Peticion.IsComandoEnviado) cuando falla la conexión o la apertura e inicialización del shell (en un servidor sin
        // bash, con el aviso de RespuestaSinBash, que ya dice que no se ejecutó nada y que ninguna herramienta funcionará),
        // cuando la sesión ya está cerrada o con un error interno antes de enviarlo. Entonces no se ejecutó nada y el error no
        // da el id de la petición ni remite a ssh_stat o ssh_read_file (no hay nada que comprobar; sin bash, tampoco
        // funcionan). Es el único sitio que lo decide para ExigirCompletada, EjecutarEscrituraAsync y ConsecuenciaCwd (estas
        // dos añaden IsInterrumpidaSinEnviar). El estado se lee antes que la señal, que la sesión marca antes de finalizarla.
        internal static bool IsNoEnviadaAlShell(Peticion peticion) =>
            peticion.Estado == EstadoPeticionEnum.Error && !peticion.IsComandoEnviado;

        private const string NoEnviadaAlShell = "el comando no llegó a ejecutarse (no se envió al shell de ssh_exec: no se pudo abrir —conexión, inicio de sesión o inicialización— o la sesión ya estaba cerrada).";

        // La sesión finaliza la petición (TiempoAgotado tras el Ctrl+C, Cancelado al cerrarse la sesión, ShellPerdido) antes de
        // que venza la espera de EjecutarHerramientaAsync: el error lleva también el id (el de ssh_status) y, con isConSalida, lo
        // que escribió el script (sin las líneas de sus marcadores), ya que ssh_output no da la salida de una petición interna.
        // isConSalida=false en la lectura previa de ssh_replace_file_content: su salida es el archivo en base64 (si termina con
        // un código distinto de 0, el error lo da MensajeErrorLecturaReplace).
        // 'consecuencia' solo se calcula si la petición no se completó.
        private void ExigirCompletada(Peticion peticion, Func<Peticion, string>? consecuencia = null, bool isConSalida = true)
        {
            if (IsNoEnviadaAlShell(peticion))
                throw new NativeSshException($"La operación no se hizo: {NoEnviadaAlShell} {peticion.Aviso}{(consecuencia is null ? string.Empty : " " + consecuencia(peticion))}");

            EstadoPeticionEnum estado = peticion.Estado;
            if (estado != EstadoPeticionEnum.Completado)
                throw new NativeSshException($"La operación no se completó (estado: {estado}; petición {peticion.IdTexto}).{(AvisoConTecleado(peticion) is { Length: > 0 } aviso ? " " + aviso : string.Empty)}{(consecuencia is null ? string.Empty : " " + consecuencia(peticion))}{(isConSalida ? SalidaSinMarcadores(peticion) : string.Empty)}");
        }

        // La salida de una petición interna de ssh_set_cwd para su error, sin las líneas de los marcadores del script; vacía si
        // no escribió nada más.
        private string SalidaSinMarcadores(Peticion peticion)
        {
            StringBuilder texto = new();
            foreach (string linea in ReconstruirSalida(peticion, out _).Split('\n'))
                if (!linea.Contains("__NS_", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(linea))
                    _ = texto.Append(texto.Length == 0 ? string.Empty : "\n").Append(linea.TrimEnd());

            return texto.Length == 0 ? string.Empty : InformeEscritura.DescribirSalidaDelScript(texto.ToString());
        }

        private static async Task EsperarAsync(Task tarea, int segundos, CancellationToken cancellationToken)
        {
            int acotados = Math.Clamp(segundos, 0, MaxSegundosEspera);
            if (acotados == 0 || tarea.IsCompleted)
                return;

            try
            {
                await tarea.WaitAsync(TimeSpan.FromSeconds(acotados), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Sigue en ejecución: se devuelve el estado parcial.
            }
        }
    }
}
