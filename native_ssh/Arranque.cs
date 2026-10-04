using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Ssh;

namespace NativeSsh
{
    // Registro común a los dos transportes: el núcleo y las herramientas no saben si hablan por stdio o HTTP.
    public static class Arranque
    {
        public static void ConfigurarLogs(ILoggingBuilder logging)
        {
            _ = logging.ClearProviders();
            _ = logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            _ = logging.SetMinimumLevel(Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("NATIVE_SSH_NIVEL_LOG"), true, out LogLevel nivel) ? nivel : LogLevel.Information);
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
            _ = servicios.AddSingleton<GestorSesiones>();

            return servicios
                .AddMcpServer(o => o.ServerInfo = new() { Name = "native_ssh", Version = "2.0.0" })
                .WithTools<HerramientasSsh>(ContextoJsonNativeSsh.OpcionesHerramientas);
        }
    }
}
