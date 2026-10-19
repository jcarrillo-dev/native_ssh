using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Ssh;

namespace NativeSsh
{
    // Registro común a los dos transportes: el núcleo y las herramientas no saben si hablan por stdio o HTTP. Lo de cada
    // sistema va en Arranque.Windows.cs (solo se compila el del sistema para el que se compila: NativeSshSistema en
    // Directory.Build.props).
    public static partial class Arranque
    {
        // Categorías del SDK de MCP (ModelContextProtocol.*, también las de su transporte HTTP): en Trace escriben cada mensaje
        // JSON-RPC completo («transport received message. Message: '…'», «sending message. Message: '…'»), con los
        // argumentos de las herramientas (el texto de ssh_input, p. ej. una contraseña de sudo, y el contenido de
        // ssh_write_file) y sus respuestas (salidas y archivos leídos). Nunca bajan de Debug, aunque NATIVE_SSH_NIVEL_LOG
        // pida Trace: se pierde esa traza del SDK. En la versión 2.2.0 solo los mensajes de Trace llevan el contenido.
        internal const string CategoriaSdkMcp = "ModelContextProtocol";

        // Lo que necesita la salida de errores del sistema antes de que nada escriba en ella (los mensajes de arranque y los
        // logs). Va lo primero de Program.cs: el log de consola toma la salida de errores al crearse.
        public static void PrepararSalidaDeErrores() => PrepararSalidaDeErroresDelSistema();

        // Lo propio del sistema para el que se compila (sin fichero de ese sistema, nada).
        static partial void PrepararSalidaDeErroresDelSistema();

        public static void ConfigurarLogs(ILoggingBuilder logging)
        {
            LogLevel nivel = Enum.TryParse(Environment.GetEnvironmentVariable("NATIVE_SSH_NIVEL_LOG"), true, out LogLevel pedido) ? pedido : LogLevel.Information;
            _ = logging.ClearProviders();
            _ = logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            _ = logging.SetMinimumLevel(nivel);
            _ = logging.AddFilter(CategoriaSdkMcp, nivel < LogLevel.Debug ? LogLevel.Debug : nivel);
        }

        public static IMcpServerBuilder RegistrarServicios(IServiceCollection servicios)
        {
            OpcionesNativeSsh opciones = OpcionesNativeSsh.DesdeEntorno();
            _ = servicios.AddSingleton(opciones);
            _ = servicios.AddSingleton(TimeProvider.System);
            _ = servicios.AddSingleton(new VerificadorHostConocido([Path.Combine(opciones.DirectorioSsh, "known_hosts"), Path.Combine(opciones.DirectorioSsh, "known_hosts2")]));
            _ = servicios.AddSingleton<LimitadorConexiones>();
            _ = servicios.AddSingleton<RegistroPeticiones>();
            _ = servicios.AddSingleton<FabricaConexionSsh>();
            _ = servicios.AddSingleton<IFabricaConexion>(sp => sp.GetRequiredService<FabricaConexionSsh>());
            _ = servicios.AddSingleton<IResolutorDestino, ResolutorConfiguracionSsh>();
            _ = servicios.AddSingleton<GestorSesiones>();

            return servicios
                .AddMcpServer(o =>
                {
                    o.ServerInfo = new() { Name = IdentidadServidor.Nombre, Version = IdentidadServidor.Version };
                    o.ServerInstructions = InstruccionesServidor.Texto;
                })
                .WithTools<HerramientasSsh>(ContextoJsonNativeSsh.OpcionesHerramientas)
                .WithRequestFilters(filtros => filtros.AddCallToolFilter(ValidadorArgumentos.Filtro));
        }
    }
}
