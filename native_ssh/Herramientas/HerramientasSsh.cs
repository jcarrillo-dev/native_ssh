using System.ComponentModel;
using System.Text;
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
        [Description("Ejecuta un comando bash en un shell persistente del destino (una conexión SSH por destino; los comandos se encolan en orden). Las variables exportadas se conservan entre comandos; la carpeta vuelve a ~ después de cada uno. Si el comando no termina en 'esperarSegundos', devuelve estado Ejecutando: usa ssh_output con el id para seguir, ssh_input si espera una respuesta, o ssh_cancel.")]
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
            [Description("Número máximo de coincidencias a devolver (por defecto 50, máx 200).")] int maxResultados = 50,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(patron))
                throw new NativeSshException("El patrón de búsqueda no puede estar vacío.");

            int limite = Math.Clamp(maxResultados, 1, 200);
            string rutaEfectiva = string.IsNullOrWhiteSpace(ruta) ? "." : ruta;
            string rutaEscapada = "'" + rutaEfectiva.Replace("'", "'\\''") + "'";
            string patronEscapado = "'" + patron.Replace("'", "'\\''") + "'";

            string comando = $$"""
                (
                cd "${__NS_CWD:-$HOME}" 2>/dev/null || cd ~;
                if [ ! -e {{rutaEscapada}} ]; then
                  printf 'Error: la ruta de búsqueda no existe: %s\n' {{rutaEscapada}} >&2
                  exit 1
                fi
                grep -rnIs \
                  --exclude-dir={.git,node_modules,.venv,venv,__pycache__,dist,build,.next,.cache,bin,obj} \
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
