using System.ComponentModel;
using ModelContextProtocol.Server;
using NativeSsh.Comun;
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
            List<EstadoSesion> sesiones = [.. gestor.ListarSesiones()
                .Where(s => clave is null || s.Destino.Clave == clave)
                .Select(s => new EstadoSesion(s.Destino.Clave, s.IsConectada, s.IsShellActivo, s.ComandosEjecutados, s.ConexionesAbiertas, s.ComandosEnCola, s.PeticionActual?.IdTexto))];
            List<ResumenPeticion> peticiones = [.. gestor.ListarPeticiones()
                .Where(p => clave is null || p.Destino.Clave == clave)
                .Take(MaxPeticionesEnEstado)
                .Select(p => new ResumenPeticion(p.IdTexto, p.Destino.Clave, p.Estado, Codigo(p), Recortar(p.Comando), Math.Round(p.TiempoEjecucion.TotalSeconds, 1)))];
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
