using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using JCarrillo.AOT.Core.Extensiones.ValueLINQ;
using ModelContextProtocol.Server;
using NativeSsh.Comun;
using NativeSsh.Protocolo;
using NativeSsh.Sesion;

namespace NativeSsh.Herramientas
{
    [McpServerToolType]
    public sealed class HerramientasSsh(GestorSesiones gestor, OpcionesNativeSsh opciones)
    {
        public const int MaxSegundosEspera = 120;
        private const int MaxPeticionesEnEstado = 20;
        private const int MaxCaracteresComando = 120;

        [McpServerTool(Name = "ssh_exec", Destructive = true, OpenWorld = true)]
        [Description("Ejecuta un comando bash en un shell persistente del destino (una conexión SSH por destino; los comandos se encolan en orden). Las variables exportadas se conservan entre comandos. Tras cada comando, el shell se restablece automáticamente a 'cd ${__NS_CWD:-$HOME}': para fijar un directorio de trabajo continuo entre múltiples comandos usa ssh_set_cwd o exporta 'export __NS_CWD=/ruta' (y omite la ruta en ssh_set_cwd o usa 'unset __NS_CWD' para volver a $HOME). Si el comando no termina en 'esperarSegundos', devuelve estado Ejecutando: usa ssh_output con el id para seguir, ssh_input si espera una respuesta, o ssh_cancel.")]
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
            List<EstadoSesion> sesiones = [];
            foreach (SesionShell s in gestor.ListarSesiones())
                if (clave is null || s.Destino.Clave == clave)
                    sesiones.Add(new EstadoSesion(s.Destino.Clave, s.IsConectada, s.IsShellActivo, s.ComandosEjecutados, s.ConexionesAbiertas, s.ComandosEnCola, s.PeticionActual?.IdTexto));

            List<Peticion> candidatas = [];
            foreach (Peticion p in gestor.ListarPeticiones())
                if (clave is null || p.Destino.Clave == clave)
                    candidatas.Add(p);

            List<ResumenPeticion> peticiones = [];
            Peticion[] arrayPeticiones = [.. candidatas];
            foreach (Peticion p in arrayPeticiones.ToValueQuery().Delay().Take(MaxPeticionesEnEstado))
                peticiones.Add(new ResumenPeticion(p.IdTexto, p.Destino.Clave, p.Estado, Codigo(p), Recortar(p.Comando), Math.Round(p.TiempoEjecucion.TotalSeconds, 1)));

            return new ResultadoEstado(sesiones, peticiones);
        }

        [McpServerTool(Name = "ssh_disconnect", Destructive = true, Idempotent = true)]
        [Description("Cierra la conexión con un destino. Cancela lo que esté en ejecución o en cola. La próxima ssh_exec abrirá una conexión nueva (cuenta para el límite de conexiones del servidor).")]
        public async Task<ResultadoAccion> DesconectarAsync(
            [Description("usuario@servidor[:puerto].")] string destino)
        {
            string clave = gestor.ParsearDestino(destino).Clave;
            bool isHecho = await gestor.DesconectarAsync(destino).ConfigureAwait(false);
            return new ResultadoAccion(clave, isHecho, isHecho ? "Conexión cerrada." : "No había conexión con ese destino.");
        }

        [McpServerTool(Name = "ssh_reset_shell", Destructive = true, Idempotent = true)]
        [Description("Descarta el shell actual (variables exportadas incluidas) y abre uno limpio en el próximo comando, sobre la misma conexión. Cancela el comando en ejecución.")]
        public ResultadoAccion ReiniciarShell(
            [Description("usuario@servidor[:puerto].")] string destino)
        {
            string clave = gestor.ParsearDestino(destino).Clave;
            bool isHecho = gestor.ReiniciarShell(destino);
            return new ResultadoAccion(clave, isHecho, isHecho ? "El próximo comando usará un shell nuevo." : "No había sesión con ese destino.");
        }

        [McpServerTool(Name = "ssh_read_file", ReadOnly = true, Idempotent = true)]
        [Description("Lee un archivo del servidor remoto con soporte para rangos de líneas (1-indexed). Devuelve el contenido en texto plano con saltos de línea reales (TextContent), sin envoltorios JSON.")]
        public async Task<string> LeerArchivoAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Ruta del archivo en el servidor remoto (absoluta o relativa a __NS_CWD).")] string ruta,
            [Description("Línea inicial (1-indexed, por defecto 1).")] int lineaInicio = 1,
            [Description("Línea final (1-indexed, opcional). Si no se especifica, lee hasta 800 líneas.")] int? lineaFin = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ruta))
                throw new NativeSshException("La ruta del archivo no puede estar vacía.");

            int inicio = Math.Max(1, lineaInicio);
            int fin = lineaFin ?? (inicio + 799);
            if (fin < inicio)
                throw new NativeSshException($"La línea final ({fin}) no puede ser menor que la línea inicial ({inicio}).");

            string rutaEscapada = "'" + ruta.Replace("'", "'\\''") + "'";
            string comando = $$"""
                (
                cd "${__NS_CWD:-$HOME}" 2>/dev/null || cd ~;
                if [ ! -e {{rutaEscapada}} ]; then
                  printf 'Error: el archivo no existe: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                if [ -d {{rutaEscapada}} ]; then
                  printf 'Error: la ruta es un directorio: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                total=$(wc -l < {{rutaEscapada}})
                if [ "$total" -eq 0 ]; then
                  echo "[Líneas 0-0 de 0]"
                else
                  fin_efectivo=$(( total < {{fin}} ? total : {{fin}} ))
                  echo "[Líneas {{inicio}}-$fin_efectivo de $total]"
                fi
                sed -n '{{inicio}},{{fin}}p' -- {{rutaEscapada}}
                )
                """.ReplaceLineEndings("\n");

            Peticion peticion = gestor.Ejecutar(destino, comando, TimeSpan.FromSeconds(60));
            await EsperarAsync(peticion.Finalizacion, 20, cancellationToken).ConfigureAwait(false);

            string textoCompleto = ReconstruirSalida(peticion);
            return peticion.CodigoSalida != 0 && peticion.Estado == EstadoPeticionEnum.Completado
                ? throw new NativeSshException(string.IsNullOrWhiteSpace(textoCompleto) ? $"El comando de lectura falló con código {peticion.CodigoSalida}." : textoCompleto.Trim())
                : textoCompleto;
        }

        [McpServerTool(Name = "ssh_write_file", Destructive = true)]
        [Description("Escribe o anexa contenido en un archivo remoto de forma segura mediante Base64 (inmune a problemas de escaping de bash). Por defecto no sobreescribe si el archivo ya existe.")]
        public async Task<string> EscribirArchivoAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Ruta del archivo remoto (absoluta o relativa a __NS_CWD).")] string ruta,
            [Description("Contenido textual o datos a escribir.")] string contenido,
            [Description("Sobrescribir si el archivo ya existe (por defecto false).")] bool overwrite = false,
            [Description("Anexar al final del archivo existente (por defecto false).")] bool append = false,
            [Description("Ejecutar escritura con permisos elevados usando sudo tee (por defecto false).")] bool sudo = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ruta))
                throw new NativeSshException("La ruta del archivo no puede estar vacía.");

            if (overwrite && append)
                throw new NativeSshException("No se puede especificar 'overwrite=true' y 'append=true' simultáneamente.");

            byte[] bytesUtf8 = Encoding.UTF8.GetBytes(contenido ?? string.Empty);
            string payloadBase64 = Convert.ToBase64String(bytesUtf8);
            string rutaEscapada = "'" + ruta.Replace("'", "'\\''") + "'";

            string accionEscritura = append
                ? (sudo
                    ? $"printf '%s' '{payloadBase64}' | base64 -d | sudo tee -a -- {rutaEscapada} >/dev/null"
                    : $"printf '%s' '{payloadBase64}' | base64 -d >> {rutaEscapada}")
                : (sudo
                    ? $"printf '%s' '{payloadBase64}' | base64 -d | sudo tee -- \"$tmp\" >/dev/null && sudo mv -f -- \"$tmp\" {rutaEscapada}"
                    : $"printf '%s' '{payloadBase64}' | base64 -d > \"$tmp\" && mv -f -- \"$tmp\" {rutaEscapada}");

            string comando = append
                ? $$"""
                    (
                    cd "${__NS_CWD:-$HOME}" 2>/dev/null || cd ~;
                    if [ -d {{rutaEscapada}} ]; then
                      printf 'Error: la ruta es un directorio: %s\n' {{rutaEscapada}} >&2
                      exit 1
                    fi
                    {{accionEscritura}}
                    )
                    """.ReplaceLineEndings("\n")
                : $$"""
                    (
                    cd "${__NS_CWD:-$HOME}" 2>/dev/null || cd ~;
                    if [ -d {{rutaEscapada}} ]; then
                      printf 'Error: la ruta es un directorio: %s\n' {{rutaEscapada}} >&2
                      exit 1
                    fi
                    {{(!overwrite ? $$"""
                    if [ -e {{rutaEscapada}} ]; then
                      printf 'Error: el archivo ya existe: %s. Especifica overwrite=true para sobrescribirlo o append=true para anexar.\n' {{rutaEscapada}} >&2
                      exit 1
                    fi
                    """ : string.Empty)}}
                    dir=$(dirname -- {{rutaEscapada}})
                    tmp="${dir}/.$(basename -- {{rutaEscapada}}).tmp.$$"
                    trap 'rm -f -- "$tmp" 2>/dev/null' EXIT;
                    {{accionEscritura}}
                    )
                    """.ReplaceLineEndings("\n");

            Peticion peticion = gestor.Ejecutar(destino, comando, TimeSpan.FromSeconds(60));
            await EsperarAsync(peticion.Finalizacion, 20, cancellationToken).ConfigureAwait(false);

            string textoCompleto = ReconstruirSalida(peticion);
            return peticion.CodigoSalida != 0 && peticion.Estado == EstadoPeticionEnum.Completado
                ? throw new NativeSshException(string.IsNullOrWhiteSpace(textoCompleto) ? $"Fallo al escribir en {ruta} (código {peticion.CodigoSalida})." : textoCompleto.Trim())
                : peticion.Estado != EstadoPeticionEnum.Completado
                    ? throw new NativeSshException($"La operación no se completó (estado: {peticion.Estado}).")
                    : append
                        ? $"Contenido anexado correctamente a: {ruta} ({bytesUtf8.Length} bytes)."
                        : $"Archivo escrito correctamente: {ruta} ({bytesUtf8.Length} bytes).";
        }

        [McpServerTool(Name = "ssh_search", ReadOnly = true, Idempotent = true)]
        [Description("Busca patrones de texto o regex en archivos del servidor remoto, ignorando automáticamente carpetas ruidosas (.git, node_modules, .venv, etc.) y limitando los resultados para no saturar el contexto.")]
        public async Task<string> BuscarAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Texto o patrón regex a buscar.")] string patron,
            [Description("Directorio o ruta donde buscar (por defecto '.' relativo a __NS_CWD).")] string ruta = ".",
            [Description("Filtro opcional por extensión o patrón glob (ej. 'py', 'ts', '*.json').")] string? extension = null,
            [Description("Número máximo de coincidencias a devolver (por defecto 50, máx 200).")] int maxResultados = 50,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(patron))
                throw new NativeSshException("El patrón de búsqueda no puede estar vacío.");

            int limite = Math.Clamp(maxResultados, 1, 200);
            string rutaEfectiva = string.IsNullOrWhiteSpace(ruta) ? "." : ruta;
            string rutaEscapada = "'" + rutaEfectiva.Replace("'", "'\\''") + "'";
            string patronEscapado = "'" + patron.Replace("'", "'\\''") + "'";

            string includeFlag = string.Empty;
            if (!string.IsNullOrWhiteSpace(extension))
            {
                string extensionLimpia = extension.Trim();
                string filtro = extensionLimpia.StartsWith("*.", StringComparison.Ordinal)
                    ? extensionLimpia
                    : extensionLimpia.StartsWith('.')
                        ? "*" + extensionLimpia
                        : "*." + extensionLimpia;
                string filtroEscapado = "'" + filtro.Replace("'", "'\\''") + "'";
                includeFlag = $"--include={filtroEscapado} \\\n  ";
            }

            string comando = $$"""
                (
                cd "${__NS_CWD:-$HOME}" 2>/dev/null || cd ~;
                if [ ! -e {{rutaEscapada}} ]; then
                  printf 'Error: la ruta de búsqueda no existe: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                grep -rnIs \
                  {{includeFlag}}--exclude-dir={.git,node_modules,.venv,venv,__pycache__,dist,build,.next,.cache,bin,obj} \
                  --exclude={'*.min.js','*.min.css','*.map','*.lock','package-lock.json','pnpm-lock.yaml'} \
                  -m {{limite}} \
                  -E -e {{patronEscapado}} \
                  -- {{rutaEscapada}} 2>&1
                )
                """.ReplaceLineEndings("\n");

            Peticion peticion = gestor.Ejecutar(destino, comando, TimeSpan.FromSeconds(60));
            await EsperarAsync(peticion.Finalizacion, 20, cancellationToken).ConfigureAwait(false);

            if (peticion.Estado != EstadoPeticionEnum.Completado)
                throw new NativeSshException($"La operación no se completó (estado: {peticion.Estado}).");

            string salida = ReconstruirSalida(peticion).Trim();
            if ((peticion.CodigoSalida == 1 || (peticion.CodigoSalida == 2 && !salida.StartsWith("grep:", StringComparison.OrdinalIgnoreCase))) && string.IsNullOrEmpty(salida))
                return $"No se encontraron coincidencias para el patrón: '{patron}' en '{rutaEfectiva}'.";

            if (peticion.CodigoSalida == 2 && salida.StartsWith("grep:", StringComparison.OrdinalIgnoreCase))
                throw new NativeSshException(salida);

            if (peticion.CodigoSalida is not (0 or 2))
                throw new NativeSshException(string.IsNullOrWhiteSpace(salida) ? $"Error en búsqueda (código {peticion.CodigoSalida})." : salida);

            string[] lineas = salida.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lineas.Length > limite)
            {
                string salidaTruncada = string.Join('\n', lineas.AsSpan(0, limite));
                return $"{salidaTruncada}\n\n[Límite alcanzado: se muestran {limite} coincidencias. Refina el patrón o subdirectorio para acotar la búsqueda.]";
            }
            return lineas.Length == limite
                ? $"{salida}\n\n[Límite alcanzado: se muestran {limite} coincidencias. Refina el patrón o subdirectorio para acotar la búsqueda.]"
                : salida;
        }

        [McpServerTool(Name = "ssh_list_dir", ReadOnly = true, Idempotent = true)]
        [Description("Explora la estructura topológica de carpetas y subcarpetas en rutas relativas limpias (ej. ['src', 'src/services', 'src/ui']), excluyendo automáticamente carpetas ruidosas (.git, node_modules, .venv, dist, build, bin, obj, etc.). Permite entender la arquitectura completa de un proyecto en una sola llamada sin saturar el contexto.")]
        public async Task<string> ListarDirectoriosAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Ruta base donde explorar (por defecto '.' relativo a __NS_CWD).")] string ruta = ".",
            [Description("Profundidad máxima de recursión (por defecto 3, entre 1 y 10).")] int profundidadMax = 3,
            [Description("Número máximo de directorios a devolver (por defecto 100, máx 200).")] int maxResultados = 100,
            CancellationToken cancellationToken = default)
        {
            int profundidad = Math.Clamp(profundidadMax, 1, 10);
            int limite = Math.Clamp(maxResultados, 1, 200);
            string rutaEfectiva = string.IsNullOrWhiteSpace(ruta) ? "." : ruta;
            string rutaEscapada = "'" + rutaEfectiva.Replace("'", "'\\''") + "'";

            string comando = $$"""
                (
                cd "${__NS_CWD:-$HOME}" 2>/dev/null || cd ~;
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
                  \( -name .git -o -name node_modules -o -name .venv -o -name venv -o -name __pycache__ -o -name dist -o -name build -o -name .next -o -name bin -o -name obj -o -name .cache \) -prune \
                  -o -type d -print 2>/dev/null | sed 's|^\./||' | sort
                )
                """.ReplaceLineEndings("\n");

            Peticion peticion = gestor.Ejecutar(destino, comando, TimeSpan.FromSeconds(60));
            await EsperarAsync(peticion.Finalizacion, 20, cancellationToken).ConfigureAwait(false);

            if (peticion.Estado != EstadoPeticionEnum.Completado)
                throw new NativeSshException($"La operación no se completó (estado: {peticion.Estado}).");

            string salida = ReconstruirSalida(peticion).Trim();
            if (peticion.CodigoSalida != 0 && peticion.Estado == EstadoPeticionEnum.Completado)
                throw new NativeSshException(string.IsNullOrWhiteSpace(salida) ? $"Error al listar directorios (código {peticion.CodigoSalida})." : salida);

            if (string.IsNullOrEmpty(salida))
                return $"No se encontraron subdirectorios en '{rutaEfectiva}'.";

            string[] lineas = salida.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lineas.Length > limite)
            {
                string salidaTruncada = string.Join('\n', lineas.AsSpan(0, limite));
                return $"{salidaTruncada}\n\n[Límite alcanzado: se muestran {limite} directorios (profundidad máx: {profundidad}). Refina la ruta o profundidad para acotar.]";
            }

            return salida;
        }

        [McpServerTool(Name = "ssh_find", ReadOnly = true, Idempotent = true)]
        [Description("Busca archivos o carpetas por nombre o patrón glob (ej. '*.service', 'docker-compose*.yml', '.env*') excluyendo automáticamente carpetas ruidosas (.git, node_modules, .venv, bin, obj, etc.). Devuelve las rutas relativas encontradas.")]
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
                "all" => "",
                _ => "-type f "
            };

            string comando = $$"""
                (
                cd "${__NS_CWD:-$HOME}" 2>/dev/null || cd ~;
                if [ ! -e {{rutaEscapada}} ]; then
                  printf 'Error: la ruta de búsqueda no existe: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                cd -- {{rutaEscapada}} && \
                find -L . -maxdepth {{profundidad}} -mindepth 1 \
                  \( -name .git -o -name node_modules -o -name .venv -o -name venv -o -name __pycache__ -o -name dist -o -name build -o -name .next -o -name bin -o -name obj -o -name .cache \) -prune \
                  -o {{filtroTipo}}-name {{patronEscapado}} -print 2>/dev/null | sed 's|^\./||' | sort
                )
                """.ReplaceLineEndings("\n");

            Peticion peticion = gestor.Ejecutar(destino, comando, TimeSpan.FromSeconds(60));
            await EsperarAsync(peticion.Finalizacion, 20, cancellationToken).ConfigureAwait(false);

            if (peticion.Estado != EstadoPeticionEnum.Completado)
                throw new NativeSshException($"La operación no se completó (estado: {peticion.Estado}).");

            string salida = ReconstruirSalida(peticion).Trim();
            if (peticion.CodigoSalida != 0 && peticion.Estado == EstadoPeticionEnum.Completado)
                throw new NativeSshException(string.IsNullOrWhiteSpace(salida) ? $"Error en búsqueda por nombre (código {peticion.CodigoSalida})." : salida);

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
        [Description("Devuelve metadatos básicos de un archivo o carpeta en formato estructurado (existe, tipo, tamaño en bytes, tamaño legible, permisos, última modificación) de forma defensiva antes de leer archivos potencialmente masivos.")]
        public async Task<string> ConsultarMetadatosAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Ruta del archivo o carpeta a inspeccionar (absoluta o relativa a __NS_CWD).")] string ruta,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ruta))
                throw new NativeSshException("La ruta no puede estar vacía.");

            string rutaEscapada = "'" + ruta.Replace("'", "'\\''") + "'";

            string comando = $$"""
                (
                cd "${__NS_CWD:-$HOME}" 2>/dev/null || cd ~;
                if [ ! -e {{rutaEscapada}} ] && [ ! -L {{rutaEscapada}} ]; then
                  echo "NO_EXISTE"
                  exit 0
                fi
                stat -c '%F|%s|%a|%y' -- {{rutaEscapada}} 2>/dev/null || stat -f '%HT|%z|%p|%Sm' -- {{rutaEscapada}} 2>/dev/null
                )
                """.ReplaceLineEndings("\n");

            Peticion peticion = gestor.Ejecutar(destino, comando, TimeSpan.FromSeconds(60));
            await EsperarAsync(peticion.Finalizacion, 20, cancellationToken).ConfigureAwait(false);

            if (peticion.Estado != EstadoPeticionEnum.Completado)
                throw new NativeSshException($"La operación no se completó (estado: {peticion.Estado}).");

            string salida = ReconstruirSalida(peticion).Trim();
            if (salida == "NO_EXISTE")
                return JsonSerializer.Serialize(new ResultadoMetadatos(ruta, false, "El archivo o directorio no existe."), ContextoJsonNativeSsh.Default.ResultadoMetadatos);

            string[] partes = salida.Split('|');
            if (partes.Length < 4)
                throw new NativeSshException(string.IsNullOrWhiteSpace(salida) ? $"Error al consultar metadatos de '{ruta}'." : salida);

            string tipo = partes[0].Trim();
            _ = long.TryParse(partes[1].Trim(), CultureInfo.InvariantCulture, out long tamanoBytes);
            string tamanoHumano = FormatearTamano(tamanoBytes);
            string permisos = partes[2].Trim();
            string ultimaModificacion = partes[3].Trim();

            return JsonSerializer.Serialize(
                new ResultadoMetadatos(ruta, true, null, tipo, tamanoBytes, tamanoHumano, permisos, ultimaModificacion),
                ContextoJsonNativeSsh.Default.ResultadoMetadatos);
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
        [Description("Establece o restablece el directorio de trabajo persistente (__NS_CWD) para la sesión del destino. Comprueba previamente en el servidor remoto que la ruta exista y sea un directorio. Si se omite 'ruta' o se pasa vacío/null, desactiva la persistencia volviendo al directorio por defecto ($HOME).")]
        public async Task<string> FijarDirectorioAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Ruta remota a establecer como espacio de trabajo persistente. Si es null o vacía, restablece a $HOME (~).")] string? ruta = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ruta))
            {
                string comandoReset = "unset __NS_CWD; export __NS_CWD=\"\"; pwd";
                Peticion peticionReset = gestor.Ejecutar(destino, comandoReset, TimeSpan.FromSeconds(60));
                await EsperarAsync(peticionReset.Finalizacion, 10, cancellationToken).ConfigureAwait(false);

                if (peticionReset.Estado != EstadoPeticionEnum.Completado || peticionReset.CodigoSalida != 0)
                {
                    string errorReset = ReconstruirSalida(peticionReset).Trim();
                    throw new NativeSshException(string.IsNullOrWhiteSpace(errorReset) ? $"Error al restablecer el directorio (código {peticionReset.CodigoSalida})." : errorReset);
                }

                return "Directorio de trabajo persistente restablecido a $HOME (~).";
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

            Peticion peticionValidacion = gestor.Ejecutar(destino, comandoValidacion, TimeSpan.FromSeconds(60));
            await EsperarAsync(peticionValidacion.Finalizacion, 20, cancellationToken).ConfigureAwait(false);

            if (peticionValidacion.CodigoSalida != 0 && peticionValidacion.Estado == EstadoPeticionEnum.Completado)
            {
                string salidaError = ReconstruirSalida(peticionValidacion).Trim();
                throw new NativeSshException(string.IsNullOrWhiteSpace(salidaError) ? $"Error al validar el directorio (código {peticionValidacion.CodigoSalida})." : salidaError);
            }

            if (peticionValidacion.Estado != EstadoPeticionEnum.Completado)
                throw new NativeSshException($"La validación no se completó (estado: {peticionValidacion.Estado}).");

            string pwdReal = ReconstruirSalida(peticionValidacion).Trim();
            string pwdRealEscapado = "'" + pwdReal.Replace("'", "'\\''") + "'";
            string comandoExport = $"export __NS_CWD={pwdRealEscapado}";
            Peticion peticionExport = gestor.Ejecutar(destino, comandoExport, TimeSpan.FromSeconds(60));
            await EsperarAsync(peticionExport.Finalizacion, 10, cancellationToken).ConfigureAwait(false);

            if (peticionExport.Estado != EstadoPeticionEnum.Completado || peticionExport.CodigoSalida != 0)
            {
                string errorExport = ReconstruirSalida(peticionExport).Trim();
                throw new NativeSshException(string.IsNullOrWhiteSpace(errorExport) ? $"Error al fijar __NS_CWD (código {peticionExport.CodigoSalida})." : errorExport);
            }

            return $"Directorio de trabajo persistente fijado a: {pwdReal}";
        }

        [McpServerTool(Name = "ssh_replace_file_content", Destructive = true)]
        [Description("Edita quirúrgicamente un bloque de texto exacto dentro de un archivo remoto sin reescribir todo el archivo a mano. Busca 'targetContent' estrictamente dentro del rango de líneas [lineaInicio, lineaFin] y lo sustituye por 'replacementContent'. Realiza un intercambio atómico con .tmp.$$ preservando permisos.")]
        public async Task<string> ReemplazarContenidoArchivoAsync(
            [Description("usuario@servidor[:puerto].")] string destino,
            [Description("Ruta del archivo remoto (absoluta o relativa a __NS_CWD).")] string ruta,
            [Description("Texto exacto a reemplazar dentro del rango de líneas (incluyendo espacios e indentación).")] string targetContent,
            [Description("Nuevo texto que reemplazará a targetContent.")] string replacementContent,
            [Description("Línea inicial (1-indexed) del rango donde buscar targetContent.")] int lineaInicio,
            [Description("Línea final (1-indexed, inclusive) del rango donde buscar targetContent.")] int lineaFin,
            [Description("Si es true, permite reemplazar si targetContent aparece múltiples veces en el rango (reemplaza todas). Por defecto false (falla si hay ambigüedad).")] bool allowMultiple = false,
            [Description("Ejecutar escritura con permisos elevados usando sudo (por defecto false).")] bool sudo = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ruta))
                throw new NativeSshException("La ruta del archivo no puede estar vacía.");

            if (string.IsNullOrEmpty(targetContent))
                throw new NativeSshException("El texto a reemplazar (targetContent) no puede estar vacío.");

            if (lineaInicio < 1)
                throw new NativeSshException("La línea inicial debe ser mayor o igual a 1.");

            if (lineaFin < lineaInicio)
                throw new NativeSshException($"La línea final ({lineaFin}) no puede ser menor que la línea inicial ({lineaInicio}).");

            string rutaEscapada = "'" + ruta.Replace("'", "'\\''") + "'";
            string comandoLectura = $$"""
                (
                cd "${__NS_CWD:-$HOME}" 2>/dev/null || cd ~;
                if [ ! -e {{rutaEscapada}} ]; then
                  printf 'Error: el archivo no existe: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                if [ -d {{rutaEscapada}} ]; then
                  printf 'Error: la ruta es un directorio: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                {{(sudo ? "sudo base64" : "base64")}} < {{rutaEscapada}}
                )
                """.ReplaceLineEndings("\n");

            Peticion peticionLectura = gestor.Ejecutar(destino, comandoLectura, TimeSpan.FromSeconds(60));
            await EsperarAsync(peticionLectura.Finalizacion, 20, cancellationToken).ConfigureAwait(false);

            if (peticionLectura.CodigoSalida != 0 && peticionLectura.Estado == EstadoPeticionEnum.Completado)
            {
                string salidaError = ReconstruirSalida(peticionLectura).Trim();
                throw new NativeSshException(string.IsNullOrWhiteSpace(salidaError) ? $"Error al leer el archivo para reemplazo (código {peticionLectura.CodigoSalida})." : salidaError);
            }

            if (peticionLectura.Estado != EstadoPeticionEnum.Completado)
                throw new NativeSshException($"La lectura para reemplazo no se completó (estado: {peticionLectura.Estado}).");

            string salidaBase64 = ReconstruirSalida(peticionLectura).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal).Trim();
            byte[] bytesArchivo = Convert.FromBase64String(salidaBase64);
            string textoOriginal = Encoding.UTF8.GetString(bytesArchivo);

            string separador = textoOriginal.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string targetNormalizado = targetContent.ReplaceLineEndings(separador);
            string replacementNormalizado = (replacementContent ?? string.Empty).ReplaceLineEndings(separador);

            string[] lineas = textoOriginal.Split(separador);
            bool hasSaltoFinal = textoOriginal.EndsWith(separador, StringComparison.Ordinal);
            int totalLineas = hasSaltoFinal ? Math.Max(0, lineas.Length - 1) : lineas.Length;

            if (lineaInicio > totalLineas)
                throw new NativeSshException($"La línea inicial ({lineaInicio}) excede el total de líneas del archivo ({totalLineas}).");

            int idxInicio = lineaInicio - 1;
            int idxFin = Math.Min(lineaFin, totalLineas) - 1;

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
                throw new NativeSshException($"No se encontró 'targetContent' en el rango de líneas {lineaInicio}-{lineaFin} de '{ruta}'. Revisa la indentación y el rango exacto.");

            if (ocurrencias > 1 && !allowMultiple)
                throw new NativeSshException($"Se encontraron {ocurrencias} apariciones de 'targetContent' en el rango de líneas {lineaInicio}-{lineaFin}. Especifica 'allowMultiple=true' o acota el rango.");

            string textoRangoModificado = allowMultiple
                ? textoRango.Replace(targetNormalizado, replacementNormalizado, StringComparison.Ordinal)
                : string.Concat(textoRango.AsSpan(0, textoRango.IndexOf(targetNormalizado, StringComparison.Ordinal)), replacementNormalizado, textoRango.AsSpan(textoRango.IndexOf(targetNormalizado, StringComparison.Ordinal) + targetNormalizado.Length));

            string textoModificado = textoAntes + textoRangoModificado + textoDespues;
            byte[] bytesModificados = Encoding.UTF8.GetBytes(textoModificado);
            string payloadBase64 = Convert.ToBase64String(bytesModificados);

            string accionEscritura = sudo
                ? $"printf '%s' '{payloadBase64}' | base64 -d | sudo tee -- \"$tmp\" >/dev/null && sudo mv -f -- \"$tmp\" {rutaEscapada}"
                : $"printf '%s' '{payloadBase64}' | base64 -d > \"$tmp\" && mv -f -- \"$tmp\" {rutaEscapada}";

            string comandoEscritura = $$"""
                (
                cd "${__NS_CWD:-$HOME}" 2>/dev/null || cd ~;
                dir=$(dirname -- {{rutaEscapada}})
                tmp="${dir}/.$(basename -- {{rutaEscapada}}).tmp.$$"
                trap 'rm -f -- "$tmp" 2>/dev/null' EXIT;
                {{accionEscritura}}
                )
                """.ReplaceLineEndings("\n");

            Peticion peticionEscritura = gestor.Ejecutar(destino, comandoEscritura, TimeSpan.FromSeconds(60));
            await EsperarAsync(peticionEscritura.Finalizacion, 20, cancellationToken).ConfigureAwait(false);

            string salidaEscritura = ReconstruirSalida(peticionEscritura).Trim();
            return peticionEscritura.CodigoSalida != 0 && peticionEscritura.Estado == EstadoPeticionEnum.Completado
                ? throw new NativeSshException(string.IsNullOrWhiteSpace(salidaEscritura) ? $"Error al escribir el archivo modificado en {ruta} (código {peticionEscritura.CodigoSalida})." : salidaEscritura)
                : peticionEscritura.Estado != EstadoPeticionEnum.Completado
                    ? throw new NativeSshException($"La escritura del archivo modificado no se completó (estado: {peticionEscritura.Estado}).")
                    : $"Archivo modificado quirúrgicamente con éxito: {ruta} ({ocurrencias} reemplazo(s) realizado(s)).";
        }

        private string ReconstruirSalida(Peticion peticion)
        {
            StringBuilder sb = new();
            long desde = 0;
            while (true)
            {
                PaginaSalida pagina = PaginadorSalida.Leer(peticion.Salida, desde, opciones.MaxBytesPorRespuesta, peticion.Estado.IsFinal());
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
                peticion.Aviso);
        }

        private static int? Codigo(Peticion peticion) => peticion.IsFinalizada ? peticion.CodigoSalida : null;

        private static string Recortar(string comando)
        {
            string linea = comando.ReplaceLineEndings(" ");
            return linea.Length <= MaxCaracteresComando ? linea : string.Concat(linea.AsSpan(0, MaxCaracteresComando - 1), "…");
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
