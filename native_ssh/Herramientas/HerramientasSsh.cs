using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using ModelContextProtocol.Server;
using NativeSsh.Comun;
using NativeSsh.Sesion;

namespace NativeSsh.Herramientas
{
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
        // tiene, da el error y devuelve 1 sin haber tocado nada.
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
        //   ocupado: se prueba la siguiente. El flock de busybox no tiene -E y no se usa.
        // - Limitaciones: en NFS, flock se emula con candados fcntl por proceso; otro programa que tome un flock sobre el
        //   archivo también impide escribirlo (el error lo dice).
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
                    printf 'Error: hay otra escritura, reemplazo o restauración en curso sobre %s (otro proceso tiene tomado su candado, flock: otra operación sobre el archivo o un programa que lo tiene abierto con candado, como una aplicación .NET). No se ha modificado: reintenta cuando termine.\n' "$ruta" >&2
                    return 1 ;;
                  *) estado_candado=ninguno; return 0 ;;
                esac
                exec {candado}<&-
              done
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
        private const string AvisoFindDisco = "Sugerencia de rendimiento: Has ejecutado 'find' vía ssh_exec. Utiliza la herramienta especializada 'ssh_find' para localizar archivos por nombre/glob o 'ssh_list_dir' para explorar la topología de carpetas con poda automática.";
        private const string AvisoCdEncadenado = "Sugerencia de rendimiento: Has encadenado 'cd <ruta> &&' en ssh_exec. Utiliza 'ssh_set_cwd(destino, ruta)' para fijar el directorio de trabajo persistente de forma automática y operar con rutas relativas limpias.";

        [McpServerTool(Name = "ssh_exec", Destructive = true, OpenWorld = true)]
        [Description("Ejecuta comandos bash de sistema, compilación, despliegue o gestión de servicios y procesos (systemctl, pm2, git, docker, tests, curl). Admite tuberías para filtrar salidas de procesos (ej. 'pm2 list | grep online'). NO USAR para buscar patrones o cadenas en archivos (usa ssh_search), para localizar archivos (usa ssh_find), para explorar estructura de directorios (usa ssh_list_dir), para leer archivos (usa ssh_read_file) ni para editar (usa ssh_replace_file_content / ssh_write_file). Para fijar el directorio de trabajo continuo usa ssh_set_cwd.")]
        public async Task<ResultadoComando> EjecutarAsync(
            [Description("usuario@servidor[:puerto]. Sin usuario se usa el usuario local.")] string destino,
            [Description("Comando bash. Admite varias líneas, heredocs y comillas.")] string comando,
            [Description("Tiempo máximo de ejecución; al agotarse se envía Ctrl+C. Entre 1 y 3600.")] int timeoutSegundos = 300,
            [Description("Cuánto esperar el resultado antes de responder. Entre 0 y 120.")] int esperarSegundos = 20,
            CancellationToken cancellationToken = default)
        {
            Peticion peticion = gestor.Ejecutar(destino, comando, TimeSpan.FromSeconds(timeoutSegundos));
            await EsperarAsync(peticion.Finalizacion, esperarSegundos, cancellationToken).ConfigureAwait(false);
            return Resultado(peticion, 0);
        }

        [McpServerTool(Name = "ssh_output", ReadOnly = true, Idempotent = true)]
        [Description("Lee la salida de un comando a partir de 'desdeByte' (usa el 'siguienteByte' de la respuesta anterior). Si sigue en ejecución, espera hasta 'esperarSegundos' a que termine.")]
        public async Task<ResultadoComando> LeerSalidaAsync(
            [Description("Id devuelto por ssh_exec.")] string id,
            [Description("Posición desde la que leer.")] long desdeByte = 0,
            [Description("Cuánto esperar a que termine. Entre 0 y 120.")] int esperarSegundos = 0,
            CancellationToken cancellationToken = default)
        {
            Peticion peticion = gestor.BuscarPeticion(id);
            await EsperarAsync(peticion.Finalizacion, esperarSegundos, cancellationToken).ConfigureAwait(false);
            return Resultado(peticion, desdeByte);
        }

        [McpServerTool(Name = "ssh_input", Destructive = true)]
        [Description("Envía texto a la entrada estándar del comando en ejecución (respuesta a una pregunta, contraseña de sudo, 'y/n'...). Devuelve la salida producida desde el envío.")]
        public async Task<ResultadoComando> EnviarEntradaAsync(
            [Description("Id del comando en ejecución.")] string id,
            [Description("Texto a enviar.")] string texto,
            [Description("Añadir un salto de línea al final.")] bool enter = true,
            [Description("Cuánto esperar a que termine después de enviar. Entre 0 y 120.")] int esperarSegundos = 2,
            CancellationToken cancellationToken = default)
        {
            Peticion peticion = gestor.BuscarPeticion(id);
            long desde = peticion.Salida.Longitud;
            // Se mira antes de enviar y otra vez tras el rechazo: si el shell se activa justo entre los dos, la entrada se
            // rechazó igualmente porque todavía se estaba abriendo.
            bool isShellInactivo = IsShellInactivo(peticion.Destino.Clave);
            if (!gestor.EnviarEntrada(peticion, texto, enter))
                throw new NativeSshException(MensajeEntradaNoEnviada(peticion, isShellInactivo || IsShellInactivo(peticion.Destino.Clave)));

            await EsperarAsync(peticion.Finalizacion, esperarSegundos, cancellationToken).ConfigureAwait(false);
            return Resultado(peticion, desde);
        }

        // Sesión del destino con su shell sin abrir (ssh_status lo muestra como shellActivo=false): se está conectando,
        // iniciando sesión o inicializando el shell (primer comando, tras ssh_reset_shell o tras perderlo), o el canal se
        // acaba de cerrar o perder. Sin sesión, false: no hay shell que se esté abriendo.
        private bool IsShellInactivo(string clave)
        {
            foreach (SesionShell sesion in gestor.ListarSesiones())
                if (sesion.Destino.Clave == clave)
                    return !sesion.IsShellActivo;

            return false;
        }

        // Por qué no se envió la entrada, con lo que la petición y su sesión dejan ver tras el rechazo (sin afirmar que el
        // comando no admite entrada: un comando en ejecución casi siempre la admite). La sesión rechaza la entrada si la
        // petición no está en ejecución, mientras su shell se abre (la petición ya está en ejecución, pero el comando aún no
        // se ha enviado al shell), durante una interrupción (Ctrl+C o el cierre del canal que la sigue), si ya no es la que
        // ocupa el shell o si no hay sesión; y devuelve false si la escritura en el canal falla. Desde fuera se distinguen el
        // estado, si se pidió cancelarla y si el shell está activo: el resto se enumera.
        internal static string MensajeEntradaNoEnviada(Peticion peticion, bool isShellInactivo) => peticion.Estado switch
        {
            EstadoPeticionEnum.Encolado => $"No se envió la entrada: el comando {peticion.IdTexto} sigue encolado, esperando su turno en el shell de {peticion.Destino.Clave}. Envíala cuando esté en ejecución (ssh_status).",
            EstadoPeticionEnum.Ejecutando when peticion.IsCancelacionSolicitada => $"No se envió la entrada: el comando {peticion.IdTexto} se está cancelando (Ctrl+C enviado o cierre del canal del shell en curso, por ssh_cancel o ssh_reset_shell) y mientras tanto el shell no admite entrada.",
            EstadoPeticionEnum.Ejecutando when isShellInactivo => $"No se envió la entrada al comando {peticion.IdTexto}, que sigue en ejecución: el shell de {peticion.Destino.Clave} todavía no está activo (se está abriendo: conexión, inicio de sesión o inicialización del shell, y el comando aún no se le ha enviado; o su canal se acaba de cerrar o se perdió la conexión). Reintenta cuando ssh_status muestre el shell activo (isShellActivo=true) con este comando en curso.",
            EstadoPeticionEnum.Ejecutando => $"No se envió la entrada al comando {peticion.IdTexto}, que sigue en ejecución: el shell no la admitió en este momento (se está interrumpiendo por su timeout, la sesión se está cerrando con ssh_disconnect, ya no es el comando que ocupa el shell o falló la escritura en el canal, y entonces pudo llegar una parte). Comprueba su estado con ssh_output o ssh_status antes de reintentar.",
            EstadoPeticionEnum.Completado or EstadoPeticionEnum.Cancelado or EstadoPeticionEnum.TiempoAgotado or EstadoPeticionEnum.ShellPerdido or EstadoPeticionEnum.Error => $"No se envió la entrada: el comando {peticion.IdTexto} ya terminó (estado {peticion.Estado}).",
            _ => $"No se envió la entrada: el comando {peticion.IdTexto} está en un estado desconocido ({peticion.Estado})."
        };

        [McpServerTool(Name = "ssh_cancel", Destructive = true, Idempotent = true)]
        [Description("Cancela un comando: si está encolado no llega a ejecutarse; si está en ejecución se envía Ctrl+C y, si no responde, se cierra el canal del shell (se pierden las variables exportadas).")]
        public async Task<ResultadoComando> CancelarAsync(
            [Description("Id del comando.")] string id,
            CancellationToken cancellationToken = default)
        {
            Peticion peticion = gestor.BuscarPeticion(id);
            _ = peticion.SolicitarCancelacion();
            await EsperarAsync(peticion.Finalizacion, (int)Math.Ceiling(opciones.EsperaTrasCtrlC.TotalSeconds) + 2, cancellationToken).ConfigureAwait(false);
            return Resultado(peticion, 0);
        }

        [McpServerTool(Name = "ssh_status", ReadOnly = true, Idempotent = true)]
        [Description("Estado de las sesiones abiertas y de los últimos comandos (sin su salida).")]
        public ResultadoEstado Estado(
            [Description("Filtrar por destino (opcional).")] string? destino = null)
        {
            string? clave = string.IsNullOrWhiteSpace(destino) ? null : gestor.ParsearDestino(destino).Clave;
            bool isSinFiltro = clave is null;
            List<EstadoSesion> sesiones = [];
            foreach (SesionShell s in gestor.ListarSesiones())
                if (isSinFiltro || s.Destino.Clave == clave)
                    sesiones.Add(new EstadoSesion(s.Destino.Clave, s.IsConectada, s.IsShellActivo, s.ComandosEjecutados, s.ConexionesAbiertas, s.ComandosEnCola, s.PeticionActual?.IdTexto, s.CanalesExecActivos));

            // ListarPeticiones ya viene ordenada de más reciente a más antigua: basta un contador.
            List<ResumenPeticion> peticiones = [];
            foreach (Peticion p in gestor.ListarPeticiones())
            {
                if (peticiones.Count == MaxPeticionesEnEstado)
                    break;

                if (isSinFiltro || p.Destino.Clave == clave)
                    peticiones.Add(new ResumenPeticion(p.IdTexto, p.Destino.Clave, p.Estado, Codigo(p), Recortar(p.Comando), Math.Round(p.TiempoEjecucion.TotalSeconds, 1)));
            }

            return new ResultadoEstado(sesiones, peticiones);
        }

        [McpServerTool(Name = "ssh_disconnect", Destructive = true, Idempotent = true)]
        [Description("Cierra la conexión con un destino. Cancela lo que esté en ejecución o en cola y olvida el directorio de trabajo fijado con ssh_set_cwd. La próxima herramienta que use ese destino abrirá una conexión nueva (cuenta para el límite de conexiones del servidor).")]
        public async Task<ResultadoAccion> DesconectarAsync(
            [Description("usuario@servidor[:puerto].")] string destino)
        {
            string clave = gestor.ParsearDestino(destino).Clave;
            bool isHecho = await gestor.DesconectarAsync(destino).ConfigureAwait(false);
            return new ResultadoAccion(clave, isHecho, isHecho ? "Conexión cerrada." : "No había conexión con ese destino.");
        }

        [McpServerTool(Name = "ssh_reset_shell", Destructive = true, Idempotent = true)]
        [Description("Descarta el shell actual (variables exportadas incluidas) y abre uno limpio en el próximo comando, sobre la misma conexión. Cancela el comando en ejecución. Conserva el directorio de trabajo fijado con ssh_set_cwd.")]
        public ResultadoAccion ReiniciarShell(
            [Description("usuario@servidor[:puerto].")] string destino)
        {
            string clave = gestor.ParsearDestino(destino).Clave;
            bool isHecho = gestor.ReiniciarShell(destino);
            return new ResultadoAccion(clave, isHecho, isHecho ? "El próximo comando usará un shell nuevo." : "No había sesión con ese destino.");
        }

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
        [Description("Lee un archivo del servidor remoto con soporte para rangos de líneas (1-indexed) o sus últimas N líneas (lineaInicio = -N, como 'tail -n N'). Devuelve el contenido en texto plano con saltos de línea reales (TextContent), sin envoltorios JSON, precedido de la cabecera '[Líneas X-Y de Total]'. El contenido llega tal cual, sin pasar por un terminal (conserva los \\r de CRLF y las secuencias de escape). Si no es UTF-8 válido, los bytes inválidos se sustituyen por U+FFFD y se añade al final la línea '[aviso: contenido no UTF-8; bytes inválidos sustituidos por U+FFFD]'. Si la salida supera el máximo por petición (1 MiB por defecto) devuelve un error en lugar de contenido parcial. Distingue en el error si el archivo no existe, si algún directorio de la ruta no se puede atravesar (y cuál) o si no hay permiso de lectura. Si la ruta no es un archivo regular (FIFO, socket o dispositivo) devuelve un error al momento; los archivos de /proc y /sys sí se leen. Se ejecuta en un canal exec propio sobre la misma conexión SSH: no espera a los comandos de ssh_exec en curso.")]
        public async Task<string> LeerArchivoAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
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
        [Description("Escribe o anexa contenido en un archivo remoto mediante Base64 (inmune a problemas de escaping de bash). Por defecto no sobrescribe: la creación es exclusiva y, si el archivo existe o aparece mientras se escribe, falla sin tocarlo; el archivo nuevo se crea completo de una vez con los permisos por defecto (umask). overwrite=true: si la ruta es un enlace simbólico escribe en su destino y conserva el enlace; escribe un temporal en el mismo directorio, le copia todos los atributos del original (modo con los bits setuid y setgid, ACL, propietario, grupo, atributos extendidos y contexto SELinux, con cp de GNU coreutils; la fecha de modificación es la de la escritura) y, si comprueba que coinciden (stat; ACL y atributos extendidos, security.capability incluida, con getfattr; sin getfattr, las ACL con getfacl y ls, y de los demás atributos extendidos responde cp), lo renombra encima (atómico; si entretanto el destino se ha convertido en un directorio, falla sin meter nada dentro). Sobrescribe también los archivos de solo lectura propios (p. ej. 0444), y con sudo=true los de cualquier usuario, conservando su modo: la protección de solo lectura no impide la escritura, salvo si hay que escribir en el sitio (ver abajo): entonces, sin sudo, falla sin tocarlo. Si no puede copiar o comprobar todos los atributos (p. ej. archivo de otro usuario sin sudo, cp que no es de GNU coreutils, contexto SELinux que no se puede asignar), el archivo tiene enlaces duros o su directorio no tiene permiso de escritura (el temporal va entonces a $TMPDIR o /tmp), lo sobrescribe en el sitio (NO atómico; conserva inodo, enlaces duros, propietario, grupo, ACL, atributos extendidos y contexto SELinux; al escribir, el núcleo quita las capacidades, security.capability, y, salvo si escribe root (sudo=true), los bits setuid y setgid: se le devuelven y se comprueban, las capacidades solo con sudo=true y setfattr o setcap; lo que no se pueda devolver o comprobar lo dice la respuesta, con el valor original de las capacidades; sin getfattr ni getcap en el servidor no se puede saber si el archivo tenía capacidades y la respuesta lo avisa) tras hacer una copia de seguridad del original (en su directorio o en $TMPDIR o /tmp; sin ella no escribe): si la escritura falla o se interrumpe, restaura el original; la copia solo se borra cuando el archivo ha quedado escrito por completo o restaurado, y si no se puede restaurar se conservan la copia y el contenido nuevo y el error da sus rutas. Lo indica en la respuesta (isAtomica=false). append=true anexa al final con '>>' (no atómico). Si el destino (resuelto) existe y no es un archivo regular (FIFO, socket o dispositivo), falla sin tocarlo, también con append. Si la escritura no termina (tiempo agotado, cancelación, ssh_disconnect o conexión perdida) o el script muere a mitad (p. ej. por SIGKILL), el error dice qué pasó con el archivo (no se llegó a modificar, se restauró, ya tiene el contenido nuevo o puede haber quedado a medias, con las rutas de la copia del original y del contenido nuevo) y el id de la petición para ver su salida con ssh_output; interrumpida la escritura en el sitio, el script restaura el original por su cuenta aunque ya no pueda confirmarlo. Mientras una escritura, un anexado o un reemplazo siguen en marcha sobre un archivo (también la restauración tras una interrupción), tienen tomado su candado (flock sobre el archivo, con flock, python3 o perl del servidor) y las escrituras, anexados y reemplazos nuevos sobre él fallan al momento sin tocarlo: reintenta cuando termine (también fallan si otro programa tiene un flock sobre el archivo); el candado se toma también si el archivo tiene permiso de escritura pero no de lectura (se abre para anexar, sin truncarlo); sin ninguna de esas herramientas, o si el archivo no se puede abrir ni para leer ni para anexar, se escribe sin exclusión y la respuesta lo avisa, y si no se puede comprobar que el candado quedó sobre el archivo que está en la ruta (sin /proc en el servidor ni stat de GNU coreutils, python3 o perl, o si no se puede consultar la ruta), también. Devuelve ruta, bytes escritos, isAtomica y mensaje.")]
        public async Task<ResultadoEscritura> EscribirArchivoAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Ruta del archivo remoto (absoluta o relativa a __NS_CWD).")] string ruta,
            [Description("Contenido textual o datos a escribir.")] string contenido,
            [Description("Sobrescribir si el archivo ya existe (por defecto false). También sobrescribe archivos de solo lectura propios, conservando su modo.")] bool overwrite = false,
            [Description("Anexar al final del archivo existente (por defecto false).")] bool append = false,
            [Description("Ejecutar la escritura con sudo (por defecto false).")] bool sudo = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(ruta))
                throw new NativeSshException("La ruta del archivo no puede estar vacía.");

            if (overwrite && append)
                throw new NativeSshException("No se puede especificar 'overwrite=true' y 'append=true' simultáneamente.");

            ExigirValorLiteral(ruta, "La ruta");
            ExigirUtf16Valido(contenido ?? string.Empty, "El contenido");
            byte[] bytesUtf8 = Encoding.UTF8.GetBytes(contenido ?? string.Empty);
            string payloadBase64 = Convert.ToBase64String(bytesUtf8);
            string rutaEscapada = Escapar(ruta);
            string idMarcas = Guid.NewGuid().ToString("N");

            // Anexar: el archivo se abre (y se crea si no existía) antes de anunciar que se anexa, para que un fallo normal al
            // abrirlo (permiso denegado) no se describa como una muerte a mitad del anexado.
            string comando = append
                ? EnvolverEnBash(PlantillaBash.Crear($$"""
                    ruta=$1
                    marca={{idMarcas}}
                    {{DefinicionAnunciar}}
                    if [ -d "$ruta" ]; then
                      printf 'Error: la ruta es un directorio: %s\n' "$ruta" >&2
                      exit 1
                    fi
                    if [ -e "$ruta" ] && [ ! -f "$ruta" ]; then
                      {{ErrorNoRegular}}
                    fi
                    {{DefinicionCandado}}
                    destino=$ruta
                    anexar=si
                    tomar_candado -x || exit 1
                    anunciar candado "$estado_candado"
                    anunciar fase anexando
                    base64 -d >&"$candado" || exit 1
                    anunciar fase anexado
                    """), sudo, rutaEscapada, payloadBase64)
                : ConstruirComandoEscritura(rutaEscapada, payloadBase64, sudo, overwrite, idMarcas);

            (Peticion peticion, InformeEscritura informe) = await EjecutarEscrituraAsync(destino, comando, idMarcas, ruta, cancellationToken).ConfigureAwait(false);
            return peticion.CodigoSalida != 0
                ? throw new NativeSshException(informe.MensajeError($"Fallo al escribir en {ruta} (código {peticion.CodigoSalida}).", ruta, peticion.CodigoSalida, peticion.IdTexto))
                : append
                    ? new ResultadoEscritura(ruta, bytesUtf8.Length, null, false, $"Contenido anexado correctamente a: {ruta} ({bytesUtf8.Length} bytes).{informe.AvisoSinCandado()}")
                    : new ResultadoEscritura(ruta, bytesUtf8.Length, null, informe.IsAtomica, $"Archivo escrito correctamente: {ruta} ({bytesUtf8.Length} bytes).{informe.AvisoExito()}");
        }

        [McpServerTool(Name = "ssh_search", ReadOnly = true, Idempotent = true)]
        [Description("Herramienta canónica y obligatoria para buscar cadenas de texto, funciones, clases o expresiones regulares en archivos del servidor remoto. Excluye automáticamente cachés, binarios (.pyc, .o), .git, node_modules y .venv, filtrando opcionalmente por extensión. Usa rg si el servidor lo tiene; si no, grep -P (PCRE) o, como último recurso, grep -E (ERE); sin grep de GNU (p. ej. busybox) recorre el árbol con find, con las mismas exclusiones, y busca con grep -E (ahí los binarios se omiten solo por extensión). La última línea indica el motor usado y, por tanto, la sintaxis de regex aplicada: '[motor: rg]' (regex de Rust, admite \\d y \\w, sin lookaround ni retroreferencias), '[motor: grep -P]' (PCRE) o '[motor: grep -E]' (ERE POSIX: usa [0-9] en lugar de \\d). Si la ruta indicada no existe o no se puede leer devuelve un error; los archivos ilegibles dentro del árbol se omiten. Mucho más rápido y eficiente en tokens que grep. Se ejecuta en un canal exec propio sobre la misma conexión SSH: no espera a los comandos de ssh_exec en curso.")]
        public async Task<string> BuscarAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Texto o patrón regex a buscar (sintaxis del motor indicado en la respuesta).")] string patron,
            [Description("Directorio o ruta donde buscar (por defecto '.' relativo a __NS_CWD).")] string ruta = ".",
            [Description("Filtro opcional por extensión o patrón glob (ej. 'py', 'ts', '*.json').")] string? extension = null,
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
        // archivos y los binarios por extensión) y cada archivo se busca con grep -E. Ahí el código de find no sirve (es
        // distinto de 0 si cualquier lote de grep no encontró nada): sale con 0 y una salida vacía es «sin coincidencias»;
        // una regex inválida se detecta antes con una línea de prueba (busybox no la compila sin datos que leer).
        internal static string ConstruirComandoBusqueda(string patron, string rutaEfectiva, string? extension, int limite)
        {
            string rutaEscapada = Escapar(rutaEfectiva);
            string patronEscapado = Escapar(patron);
            string filtroRg = string.Empty;
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

            string exclusionesRg = $"-g '!{{{ExclusionDirectorios}}}' -g '!{{{ExclusionArchivos.Replace("'", string.Empty, StringComparison.Ordinal)}}}'";
            string exclusionesGrep = $"--exclude-dir={{{ExclusionDirectorios}}} --exclude={{{ExclusionArchivos}}}";
            return PlantillaBash.Crear($$"""
                (
                {{DefinicionSinAcceso}}
                if [ ! -e {{rutaEscapada}} ]; then
                  sin_acceso {{rutaEscapada}} || printf 'Error: la ruta de búsqueda no existe: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                # La ruta pedida debe poder leerse (y recorrerse si es un directorio): los motores silencian los errores
                # de permisos y un "sin coincidencias" engañaría. Los archivos ilegibles dentro del árbol sí se omiten.
                if [ ! -r {{rutaEscapada}} ] || { [ -d {{rutaEscapada}} ] && [ ! -x {{rutaEscapada}} ]; }; then
                  printf 'Error: sin permiso de lectura en la ruta de búsqueda: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                # Código del motor, no de head; 141 (SIGPIPE) = head ya tenía suficientes líneas.
                if command -v rg >/dev/null 2>&1; then
                  printf '{{PrefijoMotor}}rg\n'
                  rg --no-config --line-number --no-heading --with-filename --color never --no-messages --no-ignore --hidden \
                    {{exclusionesRg}} {{filtroRg}}-m {{limite}} -e {{patronEscapado}} -- {{rutaEscapada}} | head -n {{limite + 1}}
                  rc=${PIPESTATUS[0]}
                elif printf 'x\n' | grep -q --exclude-dir=x --exclude=x --include=x -E x 2>/dev/null; then
                  if printf '1\n' | grep -qP '\d' 2>/dev/null; then
                    printf '{{PrefijoMotor}}grep -P\n'
                    grep -rnIs {{filtroGrep}}{{exclusionesGrep}} -m {{limite}} -P -e {{patronEscapado}} -- {{rutaEscapada}} | head -n {{limite + 1}}
                    rc=${PIPESTATUS[0]}
                  else
                    printf '{{PrefijoMotor}}grep -E\n'
                    grep -rnIs {{filtroGrep}}{{exclusionesGrep}} -m {{limite}} -E -e {{patronEscapado}} -- {{rutaEscapada}} | head -n {{limite + 1}}
                    rc=${PIPESTATUS[0]}
                  fi
                else
                  printf '{{PrefijoMotor}}grep -E\n'
                  printf 'x\n' | grep -E -e {{patronEscapado}} >/dev/null
                  [ "$?" = 2 ] && exit 2
                  if [ -d {{rutaEscapada}} ]; then
                    find -H {{rutaEscapada}} -mindepth 1 {{PodaFind}} -o -type f {{filtroFind}}{{ExclusionArchivosFind}} \
                      -exec grep -nHs -m {{limite}} -E -e {{patronEscapado}} -- {} + 2>/dev/null | head -n {{limite + 1}}
                  else
                    grep -nHs -m {{limite}} -E -e {{patronEscapado}} -- {{rutaEscapada}} | head -n {{limite + 1}}
                  fi
                  rc=0
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
                motor = finLinea < 0 ? salida[PrefijoMotor.Length..] : salida[PrefijoMotor.Length..finLinea];
                resultados = finLinea < 0 ? string.Empty : salida[(finLinea + 1)..];
            }

            resultados = QuitarSaltoFinal(resultados);

            string pie = $"[motor: {motor}]";
            string errores = lectura.Errores.Trim();
            bool isVacio = string.IsNullOrEmpty(resultados);
            bool hasErrores = !string.IsNullOrEmpty(errores);
            // Sin resultados y con stderr es un error (ruta inexistente, regex inválida...). Un 2 sin stderr solo indica
            // archivos ilegibles (silenciados con -s/--no-messages): no hubo coincidencias.
            if (codigo != 0 && isVacio && hasErrores)
                throw new NativeSshException(motor == "desconocido" ? errores : $"{errores}\n{pie}");

            // 0 sin resultados: el recorrido con find sin grep de GNU, que no da un código de «sin coincidencias».
            if (codigo is 0 or 1 or 2 && isVacio)
                return $"No se encontraron coincidencias para el patrón: '{patron}' en '{rutaEfectiva}'.\n\n{pie}";

            if (codigo is not (0 or 2))
                throw new NativeSshException(lectura.Error("Error en búsqueda"));

            string[] lineas = resultados.Split('\n');
            string avisoLimite = $"[Límite alcanzado: se muestran {limite} coincidencias. Refina el patrón o subdirectorio para acotar la búsqueda.]";
            return lineas.Length > limite
                ? $"{string.Join('\n', lineas.AsSpan(0, limite))}\n\n{avisoLimite}\n{pie}"
                : lineas.Length == limite
                    ? $"{resultados}\n\n{avisoLimite}\n{pie}"
                    : $"{resultados}\n\n{pie}";
        }

        private static string Escapar(string valor) => "'" + valor.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        // Solo se quita el salto de línea que cierra la última línea de un listado: los blancos y separadores al principio
        // del primer nombre (o línea encontrada) y al final del último son datos.
        private static string QuitarSaltoFinal(string salida) => salida.EndsWith('\n') ? salida[..^1] : salida;

        // Fin de los listados de ssh_find y ssh_list_dir: lee los nombres que da 'find -print0' (ya ordenados con 'sort -z'),
        // les quita el "./" inicial y escribe como mucho 'maximo', terminados en NUL. Un nombre con '\n' sigue siendo uno
        // solo: con 'sed | sort | head -n' se partía en dos líneas, que además se ordenaban por separado.
        private static string LimitarNombres(int maximo) =>
            PlantillaBash.Crear($$"""{ n=0; while [ "$n" -lt {{maximo}} ] && IFS= read -r -d '' nombre; do printf '%s\0' "${nombre#./}"; n=$((n + 1)); done; }""");

        // Nombres de un listado terminados en NUL (el último NUL no deja un nombre vacío detrás).
        private static string[] SepararNombres(string salida) =>
            salida.Length == 0 ? [] : (salida.EndsWith('\0') ? salida[..^1] : salida).Split('\0');

        // Los 'cantidad' primeros nombres, uno por línea (EscaparNombre).
        private static string UnirNombres(string[] nombres, int cantidad)
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
        // (U+2028 y U+2029, que no son de control pero muchos lectores toman como un salto) o empieza por comillas dobles;
        // entonces va entre comillas dobles y escapado como una cadena JSON (\n, \r, \t, \f, \b, \uXXXX, \" y \\), que se
        // puede pegar tal cual como cadena JSON. Un nombre sin escapar nunca empieza por comillas, así que no hay ambigüedad;
        // una barra invertida o unas comillas en medio (p. ej. las unidades de systemd con \x2d) no cambian el nombre.
        private static void EscaparNombre(StringBuilder listado, string nombre)
        {
            ReadOnlySpan<char> caracteres = nombre;
            bool hasControl = caracteres.ContainsAnyInRange('\u0000', '\u001f') || caracteres.ContainsAnyInRange('\u007f', '\u009f') || caracteres.ContainsAnyInRange('\u2028', '\u2029');
            if (!hasControl && !nombre.StartsWith('"'))
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
        [Description("Herramienta canónica y obligatoria para explorar la topología jerárquica de carpetas de un proyecto con poda automática de dependencias y profundidad controlada. Sustituye a 'ls -la' y 'tree'. Si la ruta no existe, no se puede atravesar o no se puede listar, devuelve un error que lo indica (nunca un listado vacío). Cada nombre ocupa una línea: si contiene caracteres de control (salto de línea, retorno de carro, tabulador...) o los separadores de línea y de párrafo de Unicode (U+2028, U+2029), o empieza por comillas dobles, sale entre comillas dobles y escapado como una cadena JSON (el salto de línea como \\n, la barra invertida como \\\\), que se puede usar tal cual como cadena JSON de la ruta en otra herramienta; los demás nombres salen tal cual. Se ejecuta en un canal exec propio sobre la misma conexión SSH: no espera a los comandos de ssh_exec en curso.")]
        public async Task<string> ListarDirectoriosAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Ruta base donde explorar (por defecto '.' relativo a __NS_CWD).")] string ruta = ".",
            [Description("Profundidad máxima de recursión (por defecto 3, entre 1 y 10).")] int profundidadMax = 3,
            [Description("Número máximo de directorios a devolver (por defecto 100, máx 200).")] int maxResultados = 100,
            [Description("Si es true, incluye también archivos inmediatos junto a los directorios (sustituyendo completamente a 'ls' o 'tree'). Por defecto false.")] bool incluirArchivos = false,
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
                  -o {{filtroTipo}}-print0 2>/dev/null | sort -z | {{LimitarNombres(limite + 1)}}
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
        [Description("Herramienta canónica y obligatoria para localizar archivos o carpetas por nombre o patrón glob (ej. '*.service', 'docker-compose*.yml', '.env*'). Poda automáticamente carpetas pesadas (.git, node_modules, .venv, bin, obj) y devuelve rutas relativas limpias. Mucho más rápido que find en shell. Si la ruta no existe, no se puede atravesar o no se puede recorrer, devuelve un error que lo indica (nunca un «no se encontraron» engañoso). Cada nombre ocupa una línea: si contiene caracteres de control (salto de línea, retorno de carro, tabulador...) o los separadores de línea y de párrafo de Unicode (U+2028, U+2029), o empieza por comillas dobles, sale entre comillas dobles y escapado como una cadena JSON (el salto de línea como \\n, la barra invertida como \\\\), que se puede usar tal cual como cadena JSON de la ruta en otra herramienta; los demás nombres salen tal cual. Se ejecuta en un canal exec propio sobre la misma conexión SSH: no espera a los comandos de ssh_exec en curso.")]
        public async Task<string> BuscarPorNombreAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Patrón de nombre o glob a buscar (ej. '*.service', 'docker-compose.yml', '*.conf').")] string patron,
            [Description("Ruta base donde buscar (por defecto '.' relativo a __NS_CWD).")] string ruta = ".",
            [Description("Tipo de elemento a buscar: 'file' (archivos), 'dir' o 'directory' (directorios) o 'all' (ambos), sin distinguir mayúsculas. Por defecto (o vacío), 'file'. Otro valor, también con blancos alrededor, da error.")] string tipo = "file",
            [Description("Profundidad máxima de recursión (por defecto 10, entre 1 y 20).")] int profundidadMax = 10,
            [Description("Número máximo de resultados a devolver (por defecto 100, máx 200).")] int maxResultados = 100,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(patron))
                throw new NativeSshException("El patrón de búsqueda no puede estar vacío.");

            ExigirValorLiteral(patron, "El patrón");
            ExigirValorLiteral(ruta ?? string.Empty, "La ruta");
            int profundidad = Math.Clamp(profundidadMax, 1, 20);
            int limite = Math.Clamp(maxResultados, 1, 200);
            string rutaEfectiva = string.IsNullOrEmpty(ruta) ? "." : ruta;
            string rutaEscapada = Escapar(rutaEfectiva);
            string patronEscapado = Escapar(patron);
            // Sin tipo, 'file'; sin distinguir mayúsculas y con 'directory' como sinónimo de 'dir' (lo dice la descripción); un
            // tipo desconocido es un error, no una búsqueda de archivos.
            string filtroTipo = (string.IsNullOrEmpty(tipo) ? "file" : tipo.ToLowerInvariant()) switch
            {
                "file" => "-type f ",
                "dir" or "directory" => "-type d ",
                "all" => string.Empty,
                _ => throw new NativeSshException($"Tipo '{tipo}' no válido: usa 'file', 'dir' (o 'directory') o 'all', sin distinguir mayúsculas.")
            };

            string comando = PlantillaBash.Crear($$"""
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
                cd -- {{rutaEscapada}} && \
                find -L . -maxdepth {{profundidad}} -mindepth 1 \
                  \( -name .git -o -name node_modules -o -name .venv -o -name venv -o -name __pycache__ -o -name dist -o -name build -o -name .next -o -name bin -o -name obj -o -name .cache \) \
                  \( {{filtroTipo}}-name {{patronEscapado}} -print0 -prune -o -prune \) \
                  -o {{filtroTipo}}-name {{patronEscapado}} -print0 2>/dev/null | sort -z | {{LimitarNombres(limite + 1)}}
                )
                """);

            SalidaLectura lectura = await EjecutarLecturaAsync(destino, comando, TimeoutBusquedaSegundos, "Refina la ruta, el patrón o la profundidad.", cancellationToken).ConfigureAwait(false);
            if (lectura.Codigo != 0)
                throw new NativeSshException(lectura.Error("Error en búsqueda por nombre"));

            string[] nombres = SepararNombres(lectura.Salida);
            if (nombres.Length == 0)
                return $"No se encontraron elementos que coincidan con '{patron}' en '{rutaEfectiva}'.";

            string listado = UnirNombres(nombres, limite);
            return nombres.Length > limite
                ? $"{listado}\n\n[Límite alcanzado: se muestran {limite} resultados (profundidad máx: {profundidad}). Refina la ruta o patrón para acotar.]"
                : listado;
        }

        [McpServerTool(Name = "ssh_stat", ReadOnly = true, Idempotent = true)]
        [Description("Devuelve metadatos de un archivo o carpeta en formato estructurado (existe, tipo, tamaño en bytes, tamaño legible, permisos en octal, última modificación, propietario, grupo y, si es un enlace simbólico, destinoEnlace) sin seguir el enlace. El tipo es uno de estos valores fijos, iguales en cualquier servidor e idioma: 'archivo regular', 'directorio', 'enlace simbólico', 'fifo', 'socket', 'dispositivo de caracteres', 'dispositivo de bloques' o 'desconocido'. Úsala antes de leer archivos potencialmente masivos y en lugar de 'ls -l' para ver permisos y dueño. existe=false solo si la ruta no existe; si algún directorio de la ruta no se puede atravesar, devuelve un error. Se ejecuta en un canal exec propio sobre la misma conexión SSH: no espera a los comandos de ssh_exec en curso.")]
        public async Task<ResultadoMetadatos> ConsultarMetadatosAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
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
        [Description("Herramienta canónica y obligatoria para anclar el espacio de trabajo persistente (__NS_CWD) al inicio de la inspección de un repositorio. Valida en remoto que la ruta exista y sea un directorio, y posiciona automáticamente todos los comandos siguientes (ssh_exec y las rutas relativas de las demás herramientas) sin necesidad de encadenar 'cd dir &&'. Se mantiene aunque el shell se recree (ssh_reset_shell, shell perdido) hasta ssh_disconnect. Pasa vacío/null para volver a $HOME.")]
        public async Task<ResultadoDirectorioTrabajo> FijarDirectorioAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Ruta remota a establecer como espacio de trabajo persistente. Si es null o vacía, restablece a $HOME (~).")] string? ruta = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(ruta))
            {
                gestor.FijarDirectorioTrabajo(destino, null);
                string comandoReset = "unset __NS_CWD; export __NS_CWD=\"\"; pwd";
                Peticion peticionReset = await EjecutarHerramientaAsync(destino, comandoReset, TimeoutMetadatosSegundos, null, cancellationToken).ConfigureAwait(false);
                ExigirCompletada(peticionReset);

                if (peticionReset.CodigoSalida != 0)
                {
                    string errorReset = ReconstruirSalida(peticionReset, out _).Trim();
                    throw new NativeSshException(string.IsNullOrWhiteSpace(errorReset) ? $"Error al restablecer el directorio (código {peticionReset.CodigoSalida})." : errorReset);
                }

                return new ResultadoDirectorioTrabajo(gestor.ParsearDestino(destino).Clave, null, "Directorio de trabajo persistente restablecido a $HOME (~).");
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

            Peticion peticionValidacion = await EjecutarHerramientaAsync(destino, comandoValidacion, TimeoutMetadatosSegundos, null, cancellationToken).ConfigureAwait(false);
            ExigirCompletada(peticionValidacion);

            if (peticionValidacion.CodigoSalida != 0)
            {
                string salidaError = ReconstruirSalida(peticionValidacion, out _).Trim();
                throw new NativeSshException(string.IsNullOrWhiteSpace(salidaError) ? $"Error al validar el directorio (código {peticionValidacion.CodigoSalida})." : salidaError);
            }

            string pwdReal = ExtraerDirectorioMarcado(ReconstruirSalida(peticionValidacion, out _), idMarca);
            // Fuente de verdad en C#: la reciben los canales exec y se reexporta si el shell se recrea.
            gestor.FijarDirectorioTrabajo(destino, pwdReal);
            string comandoExport = $"export __NS_CWD={Escapar(pwdReal)}";
            Peticion peticionExport = await EjecutarHerramientaAsync(destino, comandoExport, TimeoutMetadatosSegundos, null, cancellationToken).ConfigureAwait(false);
            ExigirCompletada(peticionExport);

            if (peticionExport.CodigoSalida != 0)
            {
                string errorExport = ReconstruirSalida(peticionExport, out _).Trim();
                throw new NativeSshException(string.IsNullOrWhiteSpace(errorExport) ? $"Error al fijar __NS_CWD (código {peticionExport.CodigoSalida})." : errorExport);
            }

            return new ResultadoDirectorioTrabajo(gestor.ParsearDestino(destino).Clave, pwdReal, $"Directorio de trabajo persistente fijado a: {pwdReal}");
        }

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
        [Description("Edita quirúrgicamente un bloque de texto exacto dentro de un archivo remoto sin reescribir todo el archivo a mano. Busca 'targetContent' en todo el archivo o, si se indican lineaInicio y lineaFin, estrictamente dentro de ese rango de líneas, y lo sustituye por 'replacementContent'. Exige una coincidencia única salvo allowMultiple=true. Los saltos de línea '\\r\\n' y '\\n' de targetContent y replacementContent se adaptan a los del archivo (CRLF o LF); cualquier otro carácter, incluidos FF, NEL, LS, PS y un '\\r' suelto, se busca y se escribe tal cual. Solo edita texto UTF-8 válido (conserva el BOM y los saltos de línea CRLF/LF) de hasta ≈755 KiB con la configuración por defecto: rechaza sin escribir nada los archivos binarios, con otra codificación o más grandes. La escritura tiene las mismas garantías que ssh_write_file con overwrite=true: si la ruta es un enlace simbólico edita su destino y conserva el enlace; temporal en el mismo directorio con todos los atributos del original (modo con los bits setuid y setgid, ACL, propietario, grupo, atributos extendidos y contexto SELinux, copiados y comprobados) renombrado encima (atómico); si no puede copiar o comprobar todos los atributos, hay enlaces duros o el directorio no tiene permiso de escritura, sobrescribe en el sitio (NO atómico; conserva inodo, enlaces duros, propietario, grupo, ACL, atributos extendidos y contexto SELinux, y devuelve y comprueba lo que el núcleo quita al escribir: las capacidades, security.capability, solo con sudo=true, y, salvo si escribe root, los bits setuid y setgid; lo que no se pueda devolver o comprobar lo dice la respuesta; con copia de seguridad previa que restaura si la escritura falla o se interrumpe y que solo se borra cuando el archivo ha quedado escrito por completo o restaurado; si además es de solo lectura, sin sudo falla sin tocarlo) y lo indica en la respuesta. Si la lectura o la escritura no terminan (tiempo agotado, cancelación, ssh_disconnect o conexión perdida) o el script muere a mitad (p. ej. por SIGKILL), el error dice qué pasó con el archivo y el id de la petición para ver su salida con ssh_output; interrumpida la escritura en el sitio, el script restaura el original por su cuenta aunque ya no pueda confirmarlo. Mientras una escritura, un anexado o un reemplazo siguen en marcha sobre un archivo (también la restauración tras una interrupción), tienen tomado su candado (flock sobre el archivo, con flock, python3 o perl del servidor) y las escrituras, anexados y reemplazos nuevos sobre él fallan al momento sin tocarlo: reintenta cuando termine (también fallan si otro programa tiene un flock sobre el archivo); el candado se toma también si el archivo tiene permiso de escritura pero no de lectura (se abre para anexar, sin truncarlo); sin ninguna de esas herramientas, o si el archivo no se puede abrir ni para leer ni para anexar, se escribe sin exclusión y la respuesta lo avisa, y si no se puede comprobar que el candado quedó sobre el archivo que está en la ruta (sin /proc en el servidor ni stat de GNU coreutils, python3 o perl, o si no se puede consultar la ruta), también. Si el destino no es un archivo regular (FIFO, socket o dispositivo), falla sin leerlo ni tocarlo. Justo antes de escribir comprueba que el archivo sigue siendo el que leyó (sha256sum o, si no lo hay, cksum): si otro proceso lo cambió o lo borró entretanto, no escribe y devuelve un error (vuelve a intentarlo). Devuelve ruta, bytes (tamaño final del archivo), reemplazos, isAtomica y mensaje.")]
        public async Task<ResultadoEscritura> ReemplazarContenidoArchivoAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Ruta del archivo remoto (absoluta o relativa a __NS_CWD).")] string ruta,
            [Description("Texto exacto a reemplazar (incluyendo espacios e indentación).")] string targetContent,
            [Description("Nuevo texto que reemplazará a targetContent.")] string replacementContent,
            [Description("Línea inicial (1-indexed) del rango donde buscar targetContent. Opcional: sin lineaInicio ni lineaFin se busca en todo el archivo; si se indica, lineaFin es obligatoria.")] int? lineaInicio = null,
            [Description("Línea final (1-indexed, inclusive) del rango donde buscar targetContent. Opcional: sin lineaInicio ni lineaFin se busca en todo el archivo; si se indica, lineaInicio es obligatoria.")] int? lineaFin = null,
            [Description("Si es true, permite reemplazar si targetContent aparece múltiples veces en el archivo o el rango (reemplaza todas). Por defecto false (falla si hay ambigüedad).")] bool allowMultiple = false,
            [Description("Leer y escribir con sudo (por defecto false).")] bool sudo = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(ruta))
                throw new NativeSshException("La ruta del archivo no puede estar vacía.");

            if (string.IsNullOrEmpty(targetContent))
                throw new NativeSshException("El texto a reemplazar (targetContent) no puede estar vacío.");

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
            ExigirUtf16Valido(targetContent, "targetContent");
            ExigirUtf16Valido(replacementContent ?? string.Empty, "replacementContent");
            string rutaEscapada = Escapar(ruta);
            long maxBytesEditable = CalcularMaxBytesArchivoEditable(opciones.MaxBytesSalidaPorPeticion);
            string idMarcas = Guid.NewGuid().ToString("N");
            // Con sudo=true todas las comprobaciones van con sudo: sin él, un archivo en un directorio que el usuario no
            // puede atravesar parece no existir. El base64 se imprime entre marcadores (ver ExtraerArchivoEntreMarcadores)
            // y se lee entero antes del primero, así que el stderr de cat y base64 nunca cae entre ellos. Los marcadores se
            // componen con printf para que su texto literal no aparezca en el propio script.
            string comandoLectura = EnvolverEnBash(PlantillaBash.Crear($$"""
                ruta=$1
                {{DefinicionSinAcceso}}
                if [ ! -e "$ruta" ]; then
                  sin_acceso "$ruta" || printf 'Error: el archivo no existe: %s\n' "$ruta" >&2
                  exit 1
                fi
                if [ -d "$ruta" ]; then
                  printf 'Error: la ruta es un directorio: %s\n' "$ruta" >&2
                  exit 1
                fi
                if [ ! -f "$ruta" ]; then
                  {{ErrorNoRegular}}
                fi
                if [ ! -r "$ruta" ]; then
                  printf 'Error: sin permiso de lectura: %s (prueba con sudo=true).\n' "$ruta" >&2
                  exit 1
                fi
                {{DefinicionCandado}}
                destino=$ruta
                tomar_candado -s || exit 1
                tam=$(stat -L -c %s -- "$ruta" 2>/dev/null || stat -L -f %z -- "$ruta" 2>/dev/null)
                if [ -z "$tam" ]; then
                  printf 'Error: no se pudo obtener el tamaño de: %s\n' "$ruta" >&2
                  exit 1
                fi
                if [ "$tam" -gt {{maxBytesEditable}} ]; then
                  printf 'Error: el archivo %s ocupa %s bytes y supera el máximo editable con ssh_replace_file_content ({{maxBytesEditable}} bytes). No se ha modificado.\n' "$ruta" "$tam" >&2
                  exit 1
                fi
                b64=$(cat -- "$ruta" | base64) || exit 1
                printf '{{PrefijoMarcaBase64}}%s_%s__\n' INICIO {{idMarcas}}
                printf '%s\n' "$b64"
                printf '{{PrefijoMarcaBase64}}%s_%s__ %s\n' FIN {{idMarcas}} "$tam"
                """), sudo, rutaEscapada);

            string sinModificar = $"No se ha modificado '{ruta}'.";
            Peticion peticionLectura = await EjecutarHerramientaAsync(destino, comandoLectura, TimeoutLecturaSegundos, sinModificar, cancellationToken).ConfigureAwait(false);
            ExigirCompletada(peticionLectura, sinModificar);

            if (peticionLectura.CodigoSalida != 0)
            {
                string salidaError = ReconstruirSalida(peticionLectura, out _).Trim();
                throw new NativeSshException(string.IsNullOrWhiteSpace(salidaError) ? $"Error al leer el archivo para reemplazo (código {peticionLectura.CodigoSalida})." : salidaError);
            }

            string salidaLectura = ReconstruirSalidaCompleta(peticionLectura, $"No se ha escrito nada en '{ruta}'.");
            byte[] bytesLeidos = ExtraerArchivoEntreMarcadores(salidaLectura, idMarcas, ruta);
            string textoOriginal = DecodificarArchivoUtf8(bytesLeidos, ruta);

            string separador = textoOriginal.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string targetNormalizado = AdaptarSaltos(targetContent, separador);
            string replacementNormalizado = AdaptarSaltos(replacementContent ?? string.Empty, separador);

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

            int ocurrencias = 0;
            int pos = 0;
            while ((pos = textoRango.IndexOf(targetNormalizado, pos, StringComparison.Ordinal)) != -1)
            {
                ocurrencias++;
                pos += targetNormalizado.Length;
            }

            if (ocurrencias == 0)
                throw new NativeSshException(isConRango
                    ? $"No se encontró 'targetContent' en {ambito} de '{ruta}'. Revisa la indentación y el rango exacto."
                    : $"No se encontró 'targetContent' en {ambito} '{ruta}'. Revisa la indentación y los saltos de línea exactos.");

            if (ocurrencias > 1 && !allowMultiple)
                throw new NativeSshException($"Se encontraron {ocurrencias} apariciones de 'targetContent' en {ambito}. Especifica 'allowMultiple=true', amplía targetContent con contexto o acota con lineaInicio/lineaFin.");

            string textoRangoModificado = allowMultiple
                ? textoRango.Replace(targetNormalizado, replacementNormalizado, StringComparison.Ordinal)
                : string.Concat(textoRango.AsSpan(0, textoRango.IndexOf(targetNormalizado, StringComparison.Ordinal)), replacementNormalizado, textoRango.AsSpan(textoRango.IndexOf(targetNormalizado, StringComparison.Ordinal) + targetNormalizado.Length));

            string textoModificado = textoAntes + textoRangoModificado + textoDespues;
            byte[] bytesModificados = Encoding.UTF8.GetBytes(textoModificado);
            string payloadBase64 = Convert.ToBase64String(bytesModificados);

            string idEscritura = Guid.NewGuid().ToString("N");
            string comandoEscritura = ConstruirComandoEscritura(rutaEscapada, payloadBase64, sudo, isSobrescribir: true, idEscritura, bytesLeidos);

            (Peticion peticionEscritura, InformeEscritura informe) = await EjecutarEscrituraAsync(destino, comandoEscritura, idEscritura, ruta, cancellationToken).ConfigureAwait(false);
            return peticionEscritura.CodigoSalida != 0
                ? throw new NativeSshException(informe.MensajeError($"Error al escribir el archivo modificado en {ruta} (código {peticionEscritura.CodigoSalida}).", ruta, peticionEscritura.CodigoSalida, peticionEscritura.IdTexto))
                : new ResultadoEscritura(ruta, bytesModificados.Length, ocurrencias, informe.IsAtomica, $"Archivo modificado quirúrgicamente con éxito: {ruta} ({ocurrencias} reemplazo(s) realizado(s)).{informe.AvisoExito()}");
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
        private static string ConstruirComandoEscritura(string rutaEscapada, string payloadBase64, bool sudo, bool isSobrescribir, string idMarcas, byte[]? contenidoLeido = null)
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
                marca={{idMarcas}}
                {{DefinicionAnunciar}}
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
                {{colocar}}
                """);
            // La umask se toma del shell del usuario: bajo sudo sería la unión con la de sudoers.
            const string ModoPorUmask = "\"$(printf '%o' $(( 0666 & ~0$(umask) )))\"";
            string huella = contenidoLeido is null
                ? "'' ''"
                : $"{Convert.ToHexStringLower(SHA256.HashData(contenidoLeido))} '{CksumPosix.Calcular(contenidoLeido).ToString(CultureInfo.InvariantCulture)} {contenidoLeido.Length.ToString(CultureInfo.InvariantCulture)}'";
            return EnvolverEnBash(script, sudo, $"{rutaEscapada} {ModoPorUmask} {huella}", payloadBase64);
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
        private static string EnvolverEnBash(string script, bool sudo, string argumentos, string? payloadBase64 = null)
        {
            string entrada = payloadBase64 is null ? string.Empty : $"printf '%s' '{payloadBase64}' | ";
            string programa = sudo ? "sudo bash" : "bash";
            return PlantillaBash.Crear($$"""
                (
                cd "${__NS_CWD:-$HOME}" 2>/dev/null || cd ~
                set -o pipefail
                {{entrada}}{{programa}} -c {{Escapar("set -o pipefail\n" + script)}} ns {{argumentos}}
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
            {
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


        private ResultadoComando Resultado(Peticion peticion, long desdeByte)
        {
            // Se lee el estado antes que la salida: si ya era final, la salida está completa.
            EstadoPeticionEnum estado = peticion.Estado;
            TimeSpan? tiempoSinSalida = peticion.TiempoSinSalida;
            PaginaSalida pagina = PaginadorSalida.Leer(peticion.Salida, desdeByte, opciones.MaxBytesPorRespuesta, estado.IsFinal());

            string? avisoFinal = peticion.Aviso;
            string? avisoAntipatron = DetectarAvisoAntipatron(peticion.Comando);
            if (avisoAntipatron is not null)
                avisoFinal = avisoFinal is null ? avisoAntipatron : $"{avisoFinal} {avisoAntipatron}";

            return new ResultadoComando(
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
        }

        [GeneratedRegex(@"(?:^|\s)(?:--recursive|-[a-zA-Z]*[rR][a-zA-Z]*)\b", RegexOptions.CultureInvariant)]
        private static partial Regex RegexFlagsRecursivosGrep();

        private static string? DetectarAvisoAntipatron(string comando)
        {
            if (string.IsNullOrWhiteSpace(comando))
                return null;

            AnalizarComando(comando, out bool hasCd, out bool hasFind, out bool hasGrep);

            if (!hasCd && !hasFind && !hasGrep)
                return null;

            List<string> avisos = [];
            if (hasGrep)
                avisos.Add(AvisoGrepDisco);
            if (hasFind)
                avisos.Add(AvisoFindDisco);
            if (hasCd)
                avisos.Add(AvisoCdEncadenado);

            return string.Join(" ", avisos);
        }

        private static void AnalizarComando(string comando, out bool hasCd, out bool hasFind, out bool hasGrep)
        {
            hasCd = false;
            hasFind = false;
            hasGrep = false;

            ReadOnlySpan<char> span = comando.AsSpan();
            int longitud = span.Length;
            int inicioSegmento = 0;
            string separadorPrevio = string.Empty;

            char comilla = '\0';
            int i = 0;

            while (i < longitud)
            {
                char c = span[i];

                if (comilla != '\0')
                {
                    if (c == '\\' && i + 1 < longitud)
                    {
                        i += 2;
                        continue;
                    }
                    if (c == comilla)
                        comilla = '\0';
                    i++;
                    continue;
                }

                if (c is '\'' or '"')
                {
                    comilla = c;
                    i++;
                    continue;
                }

                string? separadorDetectado = null;
                int avance = 0;

                if (c == '&' && i + 1 < longitud && span[i + 1] == '&')
                {
                    separadorDetectado = "&&";
                    avance = 2;
                }
                else if (c == '|' && i + 1 < longitud && span[i + 1] == '|')
                {
                    separadorDetectado = "||";
                    avance = 2;
                }
                else if (c == '|' && i + 1 < longitud && span[i + 1] == '&')
                {
                    separadorDetectado = "|&";
                    avance = 2;
                }
                else if (c == '|')
                {
                    separadorDetectado = "|";
                    avance = 1;
                }
                else if (c == ';')
                {
                    separadorDetectado = ";";
                    avance = 1;
                }
                else if (c == '\n')
                {
                    separadorDetectado = "\n";
                    avance = 1;
                }
                else if (c == '\r')
                {
                    separadorDetectado = "\n";
                    avance = (i + 1 < longitud && span[i + 1] == '\n') ? 2 : 1;
                }

                if (separadorDetectado is not null)
                {
                    string segmento = span[inicioSegmento..i].ToString();
                    ProcesarSegmento(segmento, separadorPrevio, separadorDetectado, ref hasCd, ref hasFind, ref hasGrep);
                    separadorPrevio = separadorDetectado;
                    i += avance;
                    inicioSegmento = i;
                }
                else
                {
                    i++;
                }
            }

            if (inicioSegmento < longitud)
            {
                string ultimoSegmento = span[inicioSegmento..longitud].ToString();
                ProcesarSegmento(ultimoSegmento, separadorPrevio, string.Empty, ref hasCd, ref hasFind, ref hasGrep);
            }
        }

        private static void ProcesarSegmento(
            string segmento,
            string separadorPrevio,
            string separadorSiguiente,
            ref bool hasCd,
            ref bool hasFind,
            ref bool hasGrep)
        {
            string segLimpio = segmento.Trim();
            if (string.IsNullOrEmpty(segLimpio))
                return;

            if (!hasCd && separadorSiguiente == "&&" && EsComandoCd(segLimpio))
                hasCd = true;

            if (!hasFind && separadorPrevio != "|" && separadorPrevio != "|&" && EsComandoFind(segLimpio))
                hasFind = true;

            if (!hasGrep && EsComandoGrep(segLimpio))
            {
                bool isRgrep = EsComandoRgrep(segLimpio);
                bool hasFlagsRecursivos = isRgrep || RegexFlagsRecursivosGrep().IsMatch(segLimpio);
                if (hasFlagsRecursivos || (separadorPrevio != "|" && separadorPrevio != "|&"))
                    hasGrep = true;
            }
        }

        private static bool EsComandoRgrep(string texto)
        {
            ReadOnlySpan<char> span = texto.AsSpan().TrimStart(" \t(");
            if (span.StartsWith("sudo", StringComparison.Ordinal) && span.Length > 4 && char.IsWhiteSpace(span[4]))
                span = span[4..].TrimStart(" \t");

            if (span.StartsWith("rgrep", StringComparison.Ordinal))
            {
                if (span.Length == 5)
                    return true;
                char c = span[5];
                return char.IsWhiteSpace(c);
            }
            return false;
        }

        private static bool EsComandoCd(string texto)
        {
            ReadOnlySpan<char> span = texto.AsSpan().TrimStart(" \t(");
            if (span.StartsWith("cd", StringComparison.Ordinal))
            {
                if (span.Length == 2)
                    return true;
                char c = span[2];
                return char.IsWhiteSpace(c) || c is '/' or '.' or '~' or '$';
            }
            return false;
        }

        private static bool EsComandoFind(string texto)
        {
            ReadOnlySpan<char> span = texto.AsSpan().TrimStart(" \t(");
            if (span.StartsWith("sudo", StringComparison.Ordinal) && span.Length > 4 && char.IsWhiteSpace(span[4]))
                span = span[4..].TrimStart(" \t");

            if (span.StartsWith("find", StringComparison.Ordinal))
            {
                if (span.Length == 4)
                    return true;
                char c = span[4];
                return char.IsWhiteSpace(c);
            }
            return false;
        }

        private static bool EsComandoGrep(string texto)
        {
            ReadOnlySpan<char> span = texto.AsSpan().TrimStart(" \t(");
            if (span.StartsWith("sudo", StringComparison.Ordinal) && span.Length > 4 && char.IsWhiteSpace(span[4]))
                span = span[4..].TrimStart(" \t");

            if (span.StartsWith("grep", StringComparison.Ordinal))
            {
                if (span.Length == 4)
                    return true;
                char c = span[4];
                return char.IsWhiteSpace(c);
            }

            if (span.StartsWith("egrep", StringComparison.Ordinal) || span.StartsWith("fgrep", StringComparison.Ordinal) || span.StartsWith("rgrep", StringComparison.Ordinal))
            {
                if (span.Length == 5)
                    return true;
                char c = span[5];
                return char.IsWhiteSpace(c);
            }

            return false;
        }

        private static int? Codigo(Peticion peticion) => peticion.IsFinalizada ? peticion.CodigoSalida : null;

        private static string Recortar(string comando)
        {
            string linea = comando.ReplaceLineEndings(" ");
            return linea.Length <= MaxCaracteresComando ? linea : string.Concat(linea.AsSpan(0, MaxCaracteresComando - 1), "…");
        }

        // consecuencia: lo que no llegó a pasar si la operación no termina (p. ej. "No se ha modificado ..."), para el error.
        private async Task<Peticion> EjecutarHerramientaAsync(string destino, string comando, int timeoutSegundos, string? consecuencia, CancellationToken cancellationToken)
        {
            TimeSpan timeout = TimeoutForzado ?? TimeSpan.FromSeconds(timeoutSegundos);
            Peticion peticion = gestor.Ejecutar(destino, comando, timeout);
            try
            {
                await peticion.Finalizacion.WaitAsync(timeout + opciones.EsperaTrasCtrlC + MargenEsperaHerramienta, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Sigue encolada o el shell no respondió al Ctrl+C: se cancela para no bloquear la cola.
                EstadoPeticionEnum estado = peticion.Estado;
                _ = peticion.SolicitarCancelacion();
                throw new NativeSshException($"La operación no terminó en {timeout.TotalSeconds:0} s (estado: {estado}); se ha cancelado la petición {peticion.IdTexto}.{(consecuencia is null ? string.Empty : " " + consecuencia)}");
            }
            catch (OperationCanceledException ex)
            {
                _ = peticion.SolicitarCancelacion();
                if (consecuencia is null)
                    throw;

                throw new OperationCanceledException($"Se canceló la operación (petición {peticion.IdTexto}). {consecuencia}", ex, cancellationToken);
            }

            return peticion;
        }

        // Escrituras (ssh_write_file, ssh_replace_file_content) por el shell PTY. Si no terminan (timeout, cancelación con
        // ssh_cancel, ssh_disconnect o el token, conexión perdida), se cancela la petición y se espera a que el script atienda
        // el Ctrl+C (su trampa de salida restaura el original o conserva las copias) para que el error diga qué pasó con el
        // archivo según lo que el script anunció (InformeEscritura), con el id de la petición para ssh_output. Con el token
        // cancelado se lanza OperationCanceledException con ese mismo mensaje. Si terminan, quien llama mira el código.
        private async Task<(Peticion Peticion, InformeEscritura Informe)> EjecutarEscrituraAsync(string destino, string comando, string idMarcas, string ruta, CancellationToken cancellationToken)
        {
            TimeSpan timeout = TimeoutForzado ?? TimeSpan.FromSeconds(TimeoutEscrituraSegundos);
            Peticion peticion = gestor.Ejecutar(destino, comando, timeout);
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

            // Una petición cancelada en la cola nunca empezó a ejecutarse (no tiene instante de inicio).
            string mensaje = informe.MensajeInterrupcion(ruta, estado, peticion.IdTexto, peticion.Aviso, isEjecutada: peticion.TiempoEjecucion > TimeSpan.Zero);
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

        // La sesión finaliza la petición (TiempoAgotado tras el Ctrl+C, Cancelado al cerrarse la sesión, ShellPerdido) antes de
        // que venza la espera de EjecutarHerramientaAsync: el error lleva también el id, para ver su salida con ssh_output.
        private static void ExigirCompletada(Peticion peticion, string? consecuencia = null)
        {
            EstadoPeticionEnum estado = peticion.Estado;
            if (estado != EstadoPeticionEnum.Completado)
                throw new NativeSshException($"La operación no se completó (estado: {estado}; petición {peticion.IdTexto}, su salida completa con ssh_output).{(peticion.Aviso is { Length: > 0 } aviso ? " " + aviso : string.Empty)}{(consecuencia is null ? string.Empty : " " + consecuencia)}");
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
