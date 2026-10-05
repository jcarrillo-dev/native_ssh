using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
        private const string MarcaEscrituraNoAtomica = "__NS_ESCRITURA_NO_ATOMICA__";
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
            if (!gestor.EnviarEntrada(peticion, texto, enter))
                throw new NativeSshException($"El comando {peticion.IdTexto} no admite entrada: está en estado {peticion.Estado}.");

            await EsperarAsync(peticion.Finalizacion, esperarSegundos, cancellationToken).ConfigureAwait(false);
            return Resultado(peticion, desde);
        }

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

        [McpServerTool(Name = "ssh_read_file", ReadOnly = true, Idempotent = true)]
        [Description("Lee un archivo del servidor remoto con soporte para rangos de líneas (1-indexed) o sus últimas N líneas (lineaInicio = -N, como 'tail -n N'). Devuelve el contenido en texto plano con saltos de línea reales (TextContent), sin envoltorios JSON, precedido de la cabecera '[Líneas X-Y de Total]'. El contenido llega tal cual, sin pasar por un terminal (conserva los \\r de CRLF y las secuencias de escape). Si la salida supera el máximo por petición (1 MiB por defecto) devuelve un error en lugar de contenido parcial. Se ejecuta en un canal exec propio sobre la misma conexión SSH: no espera a los comandos de ssh_exec en curso.")]
        public async Task<string> LeerArchivoAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Ruta del archivo en el servidor remoto (absoluta o relativa a __NS_CWD).")] string ruta,
            [Description("Línea inicial (1-indexed, por defecto 1). Un valor negativo -N lee las últimas N líneas (como 'tail -n N') y no admite lineaFin.")] int lineaInicio = 1,
            [Description("Línea final (1-indexed, opcional). Si no se especifica, lee hasta 800 líneas. No se puede combinar con lineaInicio negativo.")] int? lineaFin = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ruta))
                throw new NativeSshException("La ruta del archivo no puede estar vacía.");

            bool isDesdeElFinal = lineaInicio < 0;
            if (isDesdeElFinal && lineaFin is not null)
                throw new NativeSshException($"lineaFin no se puede combinar con un lineaInicio negativo ({lineaInicio} = últimas {-(long)lineaInicio} líneas). Omite lineaFin o usa un rango positivo.");

            int inicio = Math.Max(1, lineaInicio);
            int fin = lineaFin ?? (inicio + 799);
            if (!isDesdeElFinal && fin < inicio)
                throw new NativeSshException($"La línea final ({fin}) no puede ser menor que la línea inicial ({inicio}).");

            string rutaEscapada = "'" + ruta.Replace("'", "'\\''") + "'";
            // wc -l no cuenta una última línea sin '\n': se suma para que la cabecera cuadre con lo que imprimen sed y tail.
            string lectura = isDesdeElFinal
                ? $$"""
                    ultimas={{-(long)lineaInicio}}
                    inicio_efectivo=$(( total - ultimas + 1 ))
                    [ "$inicio_efectivo" -lt 1 ] && inicio_efectivo=1
                    if [ "$total" -eq 0 ]; then
                      echo "[Líneas 0-0 de 0]"
                    else
                      echo "[Líneas $inicio_efectivo-$total de $total]"
                    fi
                    tail -n {{-(long)lineaInicio}} -- {{rutaEscapada}}
                    """
                : $$"""
                    if [ "$total" -eq 0 ]; then
                      echo "[Líneas 0-0 de 0]"
                    else
                      fin_efectivo=$(( total < {{fin}} ? total : {{fin}} ))
                      echo "[Líneas {{inicio}}-$fin_efectivo de $total]"
                    fi
                    sed -n '{{inicio}},{{fin}}p' -- {{rutaEscapada}}
                    """;
            string comando = $$"""
                (
                if [ ! -e {{rutaEscapada}} ]; then
                  printf 'Error: el archivo no existe: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                if [ -d {{rutaEscapada}} ]; then
                  printf 'Error: la ruta es un directorio: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                total=$(wc -l < {{rutaEscapada}}) || exit 1
                if [ -s {{rutaEscapada}} ] && [ -n "$(tail -c 1 -- {{rutaEscapada}})" ]; then
                  total=$(( total + 1 ))
                fi
                {{lectura}}
                )
                """.ReplaceLineEndings("\n");

            SalidaLectura resultado = await EjecutarLecturaAsync(destino, comando, TimeoutLecturaSegundos, "Lee un rango de líneas menor (lineaInicio/lineaFin).", cancellationToken).ConfigureAwait(false);
            return resultado.Codigo != 0
                ? throw new NativeSshException(resultado.Error("El comando de lectura falló"))
                : resultado.Salida;
        }

        [McpServerTool(Name = "ssh_write_file", Destructive = true)]
        [Description("Escribe o anexa contenido en un archivo remoto mediante Base64 (inmune a problemas de escaping de bash). Por defecto no sobrescribe: la creación es exclusiva y, si el archivo existe o aparece mientras se escribe, falla sin tocarlo; el archivo nuevo se crea completo de una vez con los permisos por defecto (umask). overwrite=true: si la ruta es un enlace simbólico escribe en su destino y conserva el enlace; escribe un temporal en el mismo directorio que recibe modo, propietario y grupo del original y lo renombra encima (atómico; no conserva ACL ni atributos extendidos). Si no puede conservar propietario o modo (p. ej. archivo de otro usuario sin sudo) o el archivo tiene enlaces duros, lo sobrescribe en el sitio (NO atómico; conserva inodo, modo, propietario y enlaces duros) y lo indica en la respuesta (isAtomica=false). append=true anexa al final con '>>' (no atómico). Devuelve ruta, bytes escritos, isAtomica y mensaje.")]
        public async Task<ResultadoEscritura> EscribirArchivoAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Ruta del archivo remoto (absoluta o relativa a __NS_CWD).")] string ruta,
            [Description("Contenido textual o datos a escribir.")] string contenido,
            [Description("Sobrescribir si el archivo ya existe (por defecto false).")] bool overwrite = false,
            [Description("Anexar al final del archivo existente (por defecto false).")] bool append = false,
            [Description("Ejecutar la escritura con sudo (por defecto false).")] bool sudo = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ruta))
                throw new NativeSshException("La ruta del archivo no puede estar vacía.");

            if (overwrite && append)
                throw new NativeSshException("No se puede especificar 'overwrite=true' y 'append=true' simultáneamente.");

            byte[] bytesUtf8 = Encoding.UTF8.GetBytes(contenido ?? string.Empty);
            string payloadBase64 = Convert.ToBase64String(bytesUtf8);
            string rutaEscapada = "'" + ruta.Replace("'", "'\\''") + "'";

            string comando = append
                ? EnvolverEnBash("""
                    ruta=$1
                    if [ -d "$ruta" ]; then
                      printf 'Error: la ruta es un directorio: %s\n' "$ruta" >&2
                      exit 1
                    fi
                    base64 -d >> "$ruta"
                    """, sudo, rutaEscapada, payloadBase64)
                : ConstruirComandoEscritura(rutaEscapada, payloadBase64, sudo, overwrite);

            Peticion peticion = await EjecutarHerramientaAsync(destino, comando, TimeoutEscrituraSegundos, cancellationToken).ConfigureAwait(false);
            ExigirCompletada(peticion);

            string textoCompleto = ReconstruirSalida(peticion, out _);
            if (peticion.CodigoSalida != 0)
                throw new NativeSshException(string.IsNullOrWhiteSpace(textoCompleto) ? $"Fallo al escribir en {ruta} (código {peticion.CodigoSalida})." : textoCompleto.Trim());

            bool isAtomica = !append && !IsEscrituraNoAtomica(textoCompleto);
            return append
                ? new ResultadoEscritura(ruta, bytesUtf8.Length, null, false, $"Contenido anexado correctamente a: {ruta} ({bytesUtf8.Length} bytes).")
                : new ResultadoEscritura(ruta, bytesUtf8.Length, null, isAtomica, $"Archivo escrito correctamente: {ruta} ({bytesUtf8.Length} bytes).{AvisoEscrituraNoAtomica(textoCompleto)}");
        }

        [McpServerTool(Name = "ssh_search", ReadOnly = true, Idempotent = true)]
        [Description("Herramienta canónica y obligatoria para buscar cadenas de texto, funciones, clases o expresiones regulares en archivos del servidor remoto. Excluye automáticamente cachés, binarios (.pyc, .o), .git, node_modules y .venv, filtrando opcionalmente por extensión. Usa rg si el servidor lo tiene; si no, grep -P (PCRE) o, como último recurso, grep -E (ERE). La última línea indica el motor usado y, por tanto, la sintaxis de regex aplicada: '[motor: rg]' (regex de Rust, admite \\d y \\w, sin lookaround ni retroreferencias), '[motor: grep -P]' (PCRE) o '[motor: grep -E]' (ERE POSIX: usa [0-9] en lugar de \\d). Mucho más rápido y eficiente en tokens que grep. Se ejecuta en un canal exec propio sobre la misma conexión SSH: no espera a los comandos de ssh_exec en curso.")]
        public async Task<string> BuscarAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Texto o patrón regex a buscar (sintaxis del motor indicado en la respuesta).")] string patron,
            [Description("Directorio o ruta donde buscar (por defecto '.' relativo a __NS_CWD).")] string ruta = ".",
            [Description("Filtro opcional por extensión o patrón glob (ej. 'py', 'ts', '*.json').")] string? extension = null,
            [Description("Número máximo de coincidencias a devolver (por defecto 50, máx 200).")] int maxResultados = 50,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(patron))
                throw new NativeSshException("El patrón de búsqueda no puede estar vacío.");

            int limite = Math.Clamp(maxResultados, 1, 200);
            string rutaEfectiva = string.IsNullOrWhiteSpace(ruta) ? "." : ruta;
            string comando = ConstruirComandoBusqueda(patron, rutaEfectiva, extension, limite);
            SalidaLectura lectura = await EjecutarLecturaAsync(destino, comando, TimeoutBusquedaSegundos, "Probablemente hay líneas muy largas (p. ej. archivos minificados): refina el patrón, la extensión o la ruta.", cancellationToken).ConfigureAwait(false);
            return InterpretarBusqueda(lectura, patron, rutaEfectiva, limite);
        }

        private const string PrefijoMotor = "__NS_MOTOR:";
        private const string ExclusionDirectorios = ".git,node_modules,.venv,venv,__pycache__,dist,build,.next,.cache,bin,obj";
        private const string ExclusionArchivos = "'*.min.js','*.min.css','*.map','*.lock','package-lock.json','pnpm-lock.yaml'";

        // Motor de búsqueda elegido en el servidor: rg si está instalado (regex de Rust), si no grep -P (PCRE) si el grep
        // lo admite, y como último recurso grep -E (ERE). Los tres con las mismas exclusiones, filtro de extensión y límite.
        // La primera línea anuncia el motor para que el LLM sepa qué sintaxis de regex se aplicó.
        internal static string ConstruirComandoBusqueda(string patron, string rutaEfectiva, string? extension, int limite)
        {
            string rutaEscapada = Escapar(rutaEfectiva);
            string patronEscapado = Escapar(patron);
            string filtroRg = string.Empty;
            string filtroGrep = string.Empty;
            if (!string.IsNullOrWhiteSpace(extension))
            {
                string extensionLimpia = extension.Trim();
                string filtro = extensionLimpia.StartsWith("*.", StringComparison.Ordinal)
                    ? extensionLimpia
                    : extensionLimpia.StartsWith('.')
                        ? "*" + extensionLimpia
                        : "*." + extensionLimpia;
                string filtroEscapado = Escapar(filtro);
                filtroRg = $"-g {filtroEscapado} ";
                filtroGrep = $"--include={filtroEscapado} ";
            }

            string exclusionesRg = $"-g '!{{{ExclusionDirectorios}}}' -g '!{{{ExclusionArchivos.Replace("'", string.Empty, StringComparison.Ordinal)}}}'";
            string exclusionesGrep = $"--exclude-dir={{{ExclusionDirectorios}}} --exclude={{{ExclusionArchivos}}}";
            return $$"""
                (
                if [ ! -e {{rutaEscapada}} ]; then
                  printf 'Error: la ruta de búsqueda no existe: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                # Código del motor, no de head; 141 (SIGPIPE) = head ya tenía suficientes líneas.
                if command -v rg >/dev/null 2>&1; then
                  printf '{{PrefijoMotor}}rg\n'
                  rg --no-config --line-number --no-heading --with-filename --color never --no-messages --no-ignore --hidden \
                    {{exclusionesRg}} {{filtroRg}}-m {{limite}} -e {{patronEscapado}} -- {{rutaEscapada}} | head -n {{limite + 1}}
                  rc=${PIPESTATUS[0]}
                elif printf '1\n' | grep -qP '\d' 2>/dev/null; then
                  printf '{{PrefijoMotor}}grep -P\n'
                  grep -rnIs {{filtroGrep}}{{exclusionesGrep}} -m {{limite}} -P -e {{patronEscapado}} -- {{rutaEscapada}} | head -n {{limite + 1}}
                  rc=${PIPESTATUS[0]}
                else
                  printf '{{PrefijoMotor}}grep -E\n'
                  grep -rnIs {{filtroGrep}}{{exclusionesGrep}} -m {{limite}} -E -e {{patronEscapado}} -- {{rutaEscapada}} | head -n {{limite + 1}}
                  rc=${PIPESTATUS[0]}
                fi
                [ "$rc" -eq 141 ] && rc=0
                exit "$rc"
                )
                """.ReplaceLineEndings("\n");
        }

        // Códigos de rg y grep: 0 = coincidencias, 1 = ninguna, 2 = error (con -s/--no-messages, archivos ilegibles o regex inválida).
        private static string InterpretarBusqueda(SalidaLectura lectura, string patron, string rutaEfectiva, int limite)
        {
            int codigo = lectura.Codigo;
            string salida = lectura.Salida.Trim();
            string motor = "desconocido";
            string resultados = salida;
            if (salida.StartsWith(PrefijoMotor, StringComparison.Ordinal))
            {
                int finLinea = salida.IndexOf('\n', StringComparison.Ordinal);
                motor = (finLinea < 0 ? salida[PrefijoMotor.Length..] : salida[PrefijoMotor.Length..finLinea]).Trim();
                resultados = finLinea < 0 ? string.Empty : salida[(finLinea + 1)..].Trim();
            }

            string pie = $"[motor: {motor}]";
            string errores = lectura.Errores.Trim();
            bool isVacio = string.IsNullOrEmpty(resultados);
            bool hasErrores = !string.IsNullOrEmpty(errores);
            // Sin resultados y con stderr es un error (ruta inexistente, regex inválida...). Un 2 sin stderr solo indica
            // archivos ilegibles (silenciados con -s/--no-messages): no hubo coincidencias.
            if (codigo != 0 && isVacio && hasErrores)
                throw new NativeSshException(motor == "desconocido" ? errores : $"{errores}\n{pie}");

            if (codigo is 1 or 2 && isVacio)
                return $"No se encontraron coincidencias para el patrón: '{patron}' en '{rutaEfectiva}'.\n\n{pie}";

            if (codigo is not (0 or 2))
                throw new NativeSshException(lectura.Error("Error en búsqueda"));

            string[] lineas = resultados.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string avisoLimite = $"[Límite alcanzado: se muestran {limite} coincidencias. Refina el patrón o subdirectorio para acotar la búsqueda.]";
            return lineas.Length > limite
                ? $"{string.Join('\n', lineas.AsSpan(0, limite))}\n\n{avisoLimite}\n{pie}"
                : lineas.Length == limite
                    ? $"{resultados}\n\n{avisoLimite}\n{pie}"
                    : $"{resultados}\n\n{pie}";
        }

        private static string Escapar(string valor) => "'" + valor.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        [McpServerTool(Name = "ssh_list_dir", ReadOnly = true, Idempotent = true)]
        [Description("Herramienta canónica y obligatoria para explorar la topología jerárquica de carpetas de un proyecto con poda automática de dependencias y profundidad controlada. Sustituye a 'ls -la' y 'tree'. Se ejecuta en un canal exec propio sobre la misma conexión SSH: no espera a los comandos de ssh_exec en curso.")]
        public async Task<string> ListarDirectoriosAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Ruta base donde explorar (por defecto '.' relativo a __NS_CWD).")] string ruta = ".",
            [Description("Profundidad máxima de recursión (por defecto 3, entre 1 y 10).")] int profundidadMax = 3,
            [Description("Número máximo de directorios a devolver (por defecto 100, máx 200).")] int maxResultados = 100,
            [Description("Si es true, incluye también archivos inmediatos junto a los directorios (sustituyendo completamente a 'ls' o 'tree'). Por defecto false.")] bool incluirArchivos = false,
            CancellationToken cancellationToken = default)
        {
            int profundidad = Math.Clamp(profundidadMax, 1, 10);
            int limite = Math.Clamp(maxResultados, 1, 200);
            string rutaEfectiva = string.IsNullOrWhiteSpace(ruta) ? "." : ruta;
            string rutaEscapada = "'" + rutaEfectiva.Replace("'", "'\\''") + "'";
            string filtroTipo = incluirArchivos ? string.Empty : "-type d ";

            string comando = $$"""
                (
                if [ ! -e {{rutaEscapada}} ]; then
                  printf 'Error: la ruta de exploración no existe: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                if [ ! -d {{rutaEscapada}} ]; then
                  printf 'Error: la ruta no es un directorio: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                cd -- {{rutaEscapada}} && \
                find -L . -maxdepth {{profundidad}} -mindepth 1 \
                  \( -name __pycache__ -o -name .cache \) -prune \
                  -o \( -name .git -o -name node_modules -o -name .venv -o -name venv -o -name dist -o -name build -o -name .next -o -name bin -o -name obj \) -print -prune \
                  -o {{filtroTipo}}-print 2>/dev/null | sed 's|^\./||' | sort | head -n {{limite + 1}}
                )
                """.ReplaceLineEndings("\n");

            SalidaLectura lectura = await EjecutarLecturaAsync(destino, comando, TimeoutBusquedaSegundos, "Refina la ruta o reduce la profundidad.", cancellationToken).ConfigureAwait(false);
            if (lectura.Codigo != 0)
                throw new NativeSshException(lectura.Error("Error al listar directorios"));

            string salida = lectura.Salida.Trim();

            string etiquetaVacio = incluirArchivos ? "elementos" : "subdirectorios";
            if (string.IsNullOrEmpty(salida))
                return $"No se encontraron {etiquetaVacio} en '{rutaEfectiva}'.";

            string[] lineas = salida.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lineas.Length > limite)
            {
                string salidaTruncada = string.Join('\n', lineas.AsSpan(0, limite));
                string etiquetaLimite = incluirArchivos ? "elementos" : "directorios";
                return $"{salidaTruncada}\n\n[Límite alcanzado: se muestran {limite} {etiquetaLimite} (profundidad máx: {profundidad}). Refina la ruta o profundidad para acotar.]";
            }

            return salida;
        }

        [McpServerTool(Name = "ssh_find", ReadOnly = true, Idempotent = true)]
        [Description("Herramienta canónica y obligatoria para localizar archivos o carpetas por nombre o patrón glob (ej. '*.service', 'docker-compose*.yml', '.env*'). Poda automáticamente carpetas pesadas (.git, node_modules, .venv, bin, obj) y devuelve rutas relativas limpias. Mucho más rápido que find en shell. Se ejecuta en un canal exec propio sobre la misma conexión SSH: no espera a los comandos de ssh_exec en curso.")]
        public async Task<string> BuscarPorNombreAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Patrón de nombre o glob a buscar (ej. '*.service', 'docker-compose.yml', '*.conf').")] string patron,
            [Description("Ruta base donde buscar (por defecto '.' relativo a __NS_CWD).")] string ruta = ".",
            [Description("Tipo de elemento a buscar: 'file' (archivos), 'dir' (directorios) o 'all' (ambos). Por defecto 'file'.")] string tipo = "file",
            [Description("Profundidad máxima de recursión (por defecto 10, entre 1 y 20).")] int profundidadMax = 10,
            [Description("Número máximo de resultados a devolver (por defecto 100, máx 200).")] int maxResultados = 100,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(patron))
                throw new NativeSshException("El patrón de búsqueda no puede estar vacío.");

            int profundidad = Math.Clamp(profundidadMax, 1, 20);
            int limite = Math.Clamp(maxResultados, 1, 200);
            string rutaEfectiva = string.IsNullOrWhiteSpace(ruta) ? "." : ruta;
            string rutaEscapada = "'" + rutaEfectiva.Replace("'", "'\\''") + "'";
            string patronEscapado = "'" + patron.Replace("'", "'\\''") + "'";
            string filtroTipo = tipo.ToLowerInvariant() switch
            {
                "dir" or "directory" => "-type d ",
                "all" => string.Empty,
                _ => "-type f "
            };

            string comando = $$"""
                (
                if [ ! -e {{rutaEscapada}} ]; then
                  printf 'Error: la ruta de búsqueda no existe: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                cd -- {{rutaEscapada}} && \
                find -L . -maxdepth {{profundidad}} -mindepth 1 \
                  \( -name .git -o -name node_modules -o -name .venv -o -name venv -o -name __pycache__ -o -name dist -o -name build -o -name .next -o -name bin -o -name obj -o -name .cache \) \
                  \( {{filtroTipo}}-name {{patronEscapado}} -print -prune -o -prune \) \
                  -o {{filtroTipo}}-name {{patronEscapado}} -print 2>/dev/null | sed 's|^\./||' | sort | head -n {{limite + 1}}
                )
                """.ReplaceLineEndings("\n");

            SalidaLectura lectura = await EjecutarLecturaAsync(destino, comando, TimeoutBusquedaSegundos, "Refina la ruta, el patrón o la profundidad.", cancellationToken).ConfigureAwait(false);
            if (lectura.Codigo != 0)
                throw new NativeSshException(lectura.Error("Error en búsqueda por nombre"));

            string salida = lectura.Salida.Trim();

            if (string.IsNullOrEmpty(salida))
                return $"No se encontraron elementos que coincidan con '{patron}' en '{rutaEfectiva}'.";

            string[] lineas = salida.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lineas.Length > limite)
            {
                string salidaTruncada = string.Join('\n', lineas.AsSpan(0, limite));
                return $"{salidaTruncada}\n\n[Límite alcanzado: se muestran {limite} resultados (profundidad máx: {profundidad}). Refina la ruta o patrón para acotar.]";
            }

            return salida;
        }

        [McpServerTool(Name = "ssh_stat", ReadOnly = true, Idempotent = true)]
        [Description("Devuelve metadatos de un archivo o carpeta en formato estructurado (existe, tipo, tamaño en bytes, tamaño legible, permisos en octal, última modificación, propietario, grupo y, si es un enlace simbólico, destinoEnlace) sin seguir el enlace. Úsala antes de leer archivos potencialmente masivos y en lugar de 'ls -l' para ver permisos y dueño. Se ejecuta en un canal exec propio sobre la misma conexión SSH: no espera a los comandos de ssh_exec en curso.")]
        public async Task<ResultadoMetadatos> ConsultarMetadatosAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Ruta del archivo o carpeta a inspeccionar (absoluta o relativa a __NS_CWD).")] string ruta,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ruta))
                throw new NativeSshException("La ruta no puede estar vacía.");

            string rutaEscapada = "'" + ruta.Replace("'", "'\\''") + "'";

            string comando = $$"""
                (
                if [ ! -e {{rutaEscapada}} ] && [ ! -L {{rutaEscapada}} ]; then
                  echo "NO_EXISTE"
                  exit 0
                fi
                stat -c '%F|%s|%a|%y|%U|%G' -- {{rutaEscapada}} 2>/dev/null || stat -f '%HT|%z|%Lp|%Sm|%Su|%Sg' -- {{rutaEscapada}} 2>/dev/null
                if [ -L {{rutaEscapada}} ]; then
                  enlace=$(readlink -- {{rutaEscapada}}; printf x)
                  printf 'ENLACE:%s' "${enlace%x}"
                fi
                )
                """.ReplaceLineEndings("\n");

            SalidaLectura lectura = await EjecutarLecturaAsync(destino, comando, TimeoutMetadatosSegundos, "Revisa la ruta.", cancellationToken).ConfigureAwait(false);
            // Sin Trim ni Split por líneas sobre la salida completa: el destino del enlace puede contener o terminar en
            // saltos de línea, '\r' o espacios. Primera línea = stat; resto = "ENLACE:" + destino + el '\n' de readlink.
            string salida = lectura.Salida;
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
            if (string.IsNullOrWhiteSpace(ruta))
            {
                gestor.FijarDirectorioTrabajo(destino, null);
                string comandoReset = "unset __NS_CWD; export __NS_CWD=\"\"; pwd";
                Peticion peticionReset = await EjecutarHerramientaAsync(destino, comandoReset, TimeoutMetadatosSegundos, cancellationToken).ConfigureAwait(false);
                ExigirCompletada(peticionReset);

                if (peticionReset.CodigoSalida != 0)
                {
                    string errorReset = ReconstruirSalida(peticionReset, out _).Trim();
                    throw new NativeSshException(string.IsNullOrWhiteSpace(errorReset) ? $"Error al restablecer el directorio (código {peticionReset.CodigoSalida})." : errorReset);
                }

                return new ResultadoDirectorioTrabajo(gestor.ParsearDestino(destino).Clave, null, "Directorio de trabajo persistente restablecido a $HOME (~).");
            }

            string rutaEscapada = "'" + ruta.Replace("'", "'\\''") + "'";
            string comandoValidacion = $$"""
                (
                if [ ! -e {{rutaEscapada}} ]; then
                  printf 'Error: la ruta no existe: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                if [ ! -d {{rutaEscapada}} ]; then
                  printf 'Error: la ruta no es un directorio: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                cd -- {{rutaEscapada}} && pwd -P
                )
                """.ReplaceLineEndings("\n");

            Peticion peticionValidacion = await EjecutarHerramientaAsync(destino, comandoValidacion, TimeoutMetadatosSegundos, cancellationToken).ConfigureAwait(false);
            ExigirCompletada(peticionValidacion);

            if (peticionValidacion.CodigoSalida != 0)
            {
                string salidaError = ReconstruirSalida(peticionValidacion, out _).Trim();
                throw new NativeSshException(string.IsNullOrWhiteSpace(salidaError) ? $"Error al validar el directorio (código {peticionValidacion.CodigoSalida})." : salidaError);
            }

            string pwdReal = ReconstruirSalida(peticionValidacion, out _).Trim();
            // Fuente de verdad en C#: la reciben los canales exec y se reexporta si el shell se recrea.
            gestor.FijarDirectorioTrabajo(destino, pwdReal);
            string pwdRealEscapado = "'" + pwdReal.Replace("'", "'\\''") + "'";
            string comandoExport = $"export __NS_CWD={pwdRealEscapado}";
            Peticion peticionExport = await EjecutarHerramientaAsync(destino, comandoExport, TimeoutMetadatosSegundos, cancellationToken).ConfigureAwait(false);
            ExigirCompletada(peticionExport);

            if (peticionExport.CodigoSalida != 0)
            {
                string errorExport = ReconstruirSalida(peticionExport, out _).Trim();
                throw new NativeSshException(string.IsNullOrWhiteSpace(errorExport) ? $"Error al fijar __NS_CWD (código {peticionExport.CodigoSalida})." : errorExport);
            }

            return new ResultadoDirectorioTrabajo(gestor.ParsearDestino(destino).Clave, pwdReal, $"Directorio de trabajo persistente fijado a: {pwdReal}");
        }

        [McpServerTool(Name = "ssh_replace_file_content", Destructive = true)]
        [Description("Edita quirúrgicamente un bloque de texto exacto dentro de un archivo remoto sin reescribir todo el archivo a mano. Busca 'targetContent' en todo el archivo o, si se indican lineaInicio y lineaFin, estrictamente dentro de ese rango de líneas, y lo sustituye por 'replacementContent'. Exige una coincidencia única salvo allowMultiple=true. Solo edita texto UTF-8 válido (conserva el BOM y los saltos de línea CRLF/LF) de hasta ≈755 KiB con la configuración por defecto: rechaza sin escribir nada los archivos binarios, con otra codificación o más grandes. La escritura tiene las mismas garantías que ssh_write_file con overwrite=true: si la ruta es un enlace simbólico edita su destino y conserva el enlace; temporal en el mismo directorio con modo, propietario y grupo del original renombrado encima (atómico; no conserva ACL ni atributos extendidos); si no puede conservar propietario o modo, o hay enlaces duros, sobrescribe en el sitio (NO atómico; conserva inodo, modo, propietario y enlaces duros) y lo indica en la respuesta. Devuelve ruta, bytes (tamaño final del archivo), reemplazos, isAtomica y mensaje.")]
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
            if (string.IsNullOrWhiteSpace(ruta))
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

            string rutaEscapada = "'" + ruta.Replace("'", "'\\''") + "'";
            long maxBytesEditable = CalcularMaxBytesArchivoEditable(opciones.MaxBytesSalidaPorPeticion);
            // Con sudo=true todas las comprobaciones van con sudo: sin él, un archivo en un directorio que el usuario no
            // puede atravesar parece no existir.
            string comandoLectura = EnvolverEnBash($$"""
                ruta=$1
                if [ ! -e "$ruta" ]; then
                  printf 'Error: el archivo no existe: %s\n' "$ruta" >&2
                  exit 1
                fi
                if [ -d "$ruta" ]; then
                  printf 'Error: la ruta es un directorio: %s\n' "$ruta" >&2
                  exit 1
                fi
                tam=$(stat -L -c %s -- "$ruta" 2>/dev/null || stat -L -f %z -- "$ruta" 2>/dev/null)
                if [ -z "$tam" ]; then
                  printf 'Error: no se pudo obtener el tamaño de: %s\n' "$ruta" >&2
                  exit 1
                fi
                if [ "$tam" -gt {{maxBytesEditable}} ]; then
                  printf 'Error: el archivo %s ocupa %s bytes y supera el máximo editable con ssh_replace_file_content ({{maxBytesEditable}} bytes). No se ha modificado.\n' "$ruta" "$tam" >&2
                  exit 1
                fi
                cat -- "$ruta" | base64
                """, sudo, rutaEscapada);

            Peticion peticionLectura = await EjecutarHerramientaAsync(destino, comandoLectura, TimeoutLecturaSegundos, cancellationToken).ConfigureAwait(false);
            ExigirCompletada(peticionLectura);

            if (peticionLectura.CodigoSalida != 0)
            {
                string salidaError = ReconstruirSalida(peticionLectura, out _).Trim();
                throw new NativeSshException(string.IsNullOrWhiteSpace(salidaError) ? $"Error al leer el archivo para reemplazo (código {peticionLectura.CodigoSalida})." : salidaError);
            }

            string salidaBase64 = ReconstruirSalidaCompleta(peticionLectura, $"No se ha escrito nada en '{ruta}'.").Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal).Trim();
            string textoOriginal = DecodificarArchivoUtf8(salidaBase64, ruta);

            string separador = textoOriginal.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string targetNormalizado = targetContent.ReplaceLineEndings(separador);
            string replacementNormalizado = (replacementContent ?? string.Empty).ReplaceLineEndings(separador);

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

            string comandoEscritura = ConstruirComandoEscritura(rutaEscapada, payloadBase64, sudo, isSobrescribir: true);

            Peticion peticionEscritura = await EjecutarHerramientaAsync(destino, comandoEscritura, TimeoutEscrituraSegundos, cancellationToken).ConfigureAwait(false);
            ExigirCompletada(peticionEscritura);

            string salidaEscritura = ReconstruirSalida(peticionEscritura, out _).Trim();
            return peticionEscritura.CodigoSalida != 0
                ? throw new NativeSshException(string.IsNullOrWhiteSpace(salidaEscritura) ? $"Error al escribir el archivo modificado en {ruta} (código {peticionEscritura.CodigoSalida})." : salidaEscritura)
                : new ResultadoEscritura(ruta, bytesModificados.Length, ocurrencias, !IsEscrituraNoAtomica(salidaEscritura), $"Archivo modificado quirúrgicamente con éxito: {ruta} ({ocurrencias} reemplazo(s) realizado(s)).{AvisoEscrituraNoAtomica(salidaEscritura)}");
        }

        // Script común de ssh_write_file (sin append) y ssh_replace_file_content:
        // - todo el script (comprobaciones incluidas) se ejecuta con el mismo privilegio: con sudo=true, un único 'sudo bash';
        // - un enlace simbólico se resuelve (readlink -f) y se escribe en su destino, nunca se sustituye el enlace;
        // - el temporal se crea con mktemp (0600) en el directorio del destino resuelto;
        // - archivo existente: el temporal recibe propietario/grupo y modo (chown/chmod --reference) y se renombra encima;
        //   si no se puede (propietario ajeno sin sudo, sin --reference) o hay enlaces duros, se escribe en el sitio (no atómico);
        // - archivo nuevo: permisos por la umask del usuario ($2); sin sobrescribir, creación exclusiva con ln (o noclobber).
        private static string ConstruirComandoEscritura(string rutaEscapada, string payloadBase64, bool sudo, bool isSobrescribir)
        {
            const string ErrorYaExiste = """printf 'Error: el archivo ya existe: %s. Especifica overwrite=true para sobrescribirlo o append=true para anexar.\n' "$ruta" >&2; exit 1""";

            string comprobarNoExiste = isSobrescribir
                ? string.Empty
                : $$"""
                    if [ -e "$ruta" ] || [ -L "$ruta" ]; then
                      {{ErrorYaExiste}}
                    fi
                    """;
            // Sin sobrescribir nunca se mira si el destino existe para decidir: ln falla si apareció entretanto.
            string colocar = isSobrescribir
                ? $$"""
                    if [ -e "$destino" ]; then
                      en_sitio=0
                      chown --reference="$destino" -- "$tmp" 2>/dev/null || en_sitio=1
                      chmod --reference="$destino" -- "$tmp" 2>/dev/null || en_sitio=1
                      [ "$(stat -c '%u:%g' -- "$tmp" 2>/dev/null)" = "$(stat -c '%u:%g' -- "$destino" 2>/dev/null)" ] || en_sitio=1
                      [ "$(stat -c '%h' -- "$destino" 2>/dev/null)" = 1 ] || en_sitio=1
                      if [ "$en_sitio" = 0 ]; then
                        mv -f -- "$tmp" "$destino" || exit 1
                      else
                        cat -- "$tmp" > "$destino" || exit 1
                        echo '{{MarcaEscrituraNoAtomica}}'
                      fi
                    else
                      chmod "$modo_nuevo" -- "$tmp" || exit 1
                      mv -f -- "$tmp" "$destino" || exit 1
                    fi
                    """
                : $$"""
                    chmod "$modo_nuevo" -- "$tmp" || exit 1
                    if ! ln -- "$tmp" "$destino" 2>/dev/null; then
                      if [ -e "$destino" ] || [ -L "$destino" ]; then
                        {{ErrorYaExiste}}
                      fi
                      (set -C; cat -- "$tmp" > "$destino") || exit 1
                    fi
                    """;

            string script = $$"""
                ruta=$1
                modo_nuevo=$2
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
                {{SustitucionLiteral("dir", "dirname -- \"$destino\"")}}
                {{SustitucionLiteral("base", "basename -- \"$destino\"")}}
                tmp=$(mktemp -- "$dir/.$base.tmp.XXXXXX") || {
                  printf 'Error: no se pudo crear el archivo temporal en %s (¿existe el directorio y hay permiso de escritura?).\n' "$dir" >&2
                  exit 1
                }
                trap 'rm -f -- "$tmp" 2>/dev/null' EXIT
                base64 -d > "$tmp" || exit 1
                {{colocar}}
                """;
            // La umask se toma del shell del usuario: bajo sudo sería la unión con la de sudoers.
            const string ModoPorUmask = "\"$(printf '%o' $(( 0666 & ~0$(umask) )))\"";
            return EnvolverEnBash(script, sudo, $"{rutaEscapada} {ModoPorUmask}", payloadBase64);
        }

        // $(cmd) quita TODOS los saltos de línea finales, también los que forman parte de un nombre de archivo ("victima\n"
        // acabaría en "victima"). Se añade un centinela 'x' tras la salida y se quita después junto con el único '\n' que
        // añade el propio comando. Requiere bash ($'\n'): los scripts se ejecutan siempre con EnvolverEnBash.
        private static string SustitucionLiteral(string variable, string comando) =>
            $"{variable}=$({comando}; printf x); {variable}=${{{variable}%x}}; {variable}=${{{variable}%$'\\n'}}";

        // Ejecuta 'script' con bash ("$1", "$2"... = 'argumentos', ya escapados), con 'sudo bash' si sudo=true, de modo
        // que todas sus comprobaciones (test -e/-d/-L, readlink, stat...) usan el mismo privilegio que la escritura.
        // - El cd a __NS_CWD se hace fuera de sudo: env_reset descarta __NS_CWD y cambia HOME, pero sudo conserva el cwd.
        // - El contenido (base64) llega por stdin, nunca como argumento: un argumento no puede pasar de 128 KiB (MAX_ARG_STRLEN).
        //   sudo pide la contraseña por /dev/tty, no por stdin.
        // - Es siempre bash (no el shell de login), así que el script puede usar sintaxis de bash como $'\n'.
        private static string EnvolverEnBash(string script, bool sudo, string argumentos, string? payloadBase64 = null)
        {
            string entrada = payloadBase64 is null ? string.Empty : $"printf '%s' '{payloadBase64}' | ";
            string programa = sudo ? "sudo bash" : "bash";
            return $$"""
                (
                cd "${__NS_CWD:-$HOME}" 2>/dev/null || cd ~
                set -o pipefail
                {{entrada}}{{programa}} -c {{Escapar("set -o pipefail\n" + script.ReplaceLineEndings("\n"))}} ns {{argumentos}}
                )
                """.ReplaceLineEndings("\n");
        }

        private static bool IsEscrituraNoAtomica(string salida) => salida.Contains(MarcaEscrituraNoAtomica, StringComparison.Ordinal);

        private static string AvisoEscrituraNoAtomica(string salida) =>
            IsEscrituraNoAtomica(salida)
                ? " Escritura no atómica: no se podía conservar el propietario o el modo del original (o tiene enlaces duros), así que se sobrescribió en el sitio conservando inodo, modo, propietario y enlaces duros."
                : string.Empty;

        // Solo se edita lo que sobrevive a bytes → UTF-8 → bytes: UTF-8 estricto (el BOM se conserva como U+FEFF) y sin NUL.
        private static string DecodificarArchivoUtf8(string salidaBase64, string ruta)
        {
            string texto;
            try
            {
                texto = Utf8Estricto.GetString(Convert.FromBase64String(salidaBase64));
            }
            catch (FormatException)
            {
                throw new NativeSshException($"La lectura de '{ruta}' no devolvió base64 válido (¿avisos de sudo o del shell mezclados con la salida?). No se ha escrito nada.");
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

        private async Task<Peticion> EjecutarHerramientaAsync(string destino, string comando, int timeoutSegundos, CancellationToken cancellationToken)
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
                throw new NativeSshException($"La operación no terminó en {timeout.TotalSeconds:0} s (estado: {estado}); se ha cancelado la petición {peticion.IdTexto}.");
            }
            catch (OperationCanceledException)
            {
                _ = peticion.SolicitarCancelacion();
                throw;
            }

            return peticion;
        }

        private readonly record struct SalidaLectura(int Codigo, string Salida, string Errores)
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
                int codigo => new SalidaLectura(codigo, salida, errores)
            };
        }

        // Códigos de 'timeout' en el servidor (ConstructorComandoExec): 124 al agotarse, 137 si hubo que matarlo con KILL.
        private const int CodigoTimeoutRemoto = 124;
        private const int CodigoTimeoutRemotoKill = 137;

        private static void ExigirCompletada(Peticion peticion)
        {
            EstadoPeticionEnum estado = peticion.Estado;
            if (estado != EstadoPeticionEnum.Completado)
                throw new NativeSshException(peticion.Aviso is { Length: > 0 } aviso
                    ? $"La operación no se completó (estado: {estado}). {aviso}"
                    : $"La operación no se completó (estado: {estado}).");
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
