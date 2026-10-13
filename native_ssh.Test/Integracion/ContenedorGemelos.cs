using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Ssh;

namespace NativeSsh.Test.Integracion
{
    // Dos contenedores Debian gemelos (pruebas/contenedor-sudo.sh gemelos): misma imagen y mismo arranque, en una red propia
    // de podman; desde A se entra en B con 'ssh -tt … prueba@gemelo-b'. El bash de una sesión tiene en los dos el mismo $$ y
    // $PPID, así que cada prueba los arranca de nuevo (la secuencia de pids depende de lo que haya pasado en cada uno) y los
    // para al terminar. Puertos: NATIVE_SSH_PUERTO_GEMELO_A y NATIVE_SSH_PUERTO_GEMELO_B (los lee el script).
    [SupportedOSPlatform("linux")]
    public sealed class ServidorGemelos : IDisposable
    {
        private ServidorGemelos(int puertoA)
        {
            DirectorioPruebas = SshdLocal.BuscarDirectorioPruebas();
            DirectorioEstado = RutaEstado(DirectorioPruebas);
            PuertoA = puertoA;
            Usuario = File.ReadAllText(Path.Combine(DirectorioEstado, "usuario")).Trim();
        }

        public string DirectorioPruebas { get; }
        public string DirectorioEstado { get; }
        public int PuertoA { get; }
        public string Usuario { get; }
        public string DestinoA => $"{Usuario}@127.0.0.1:{PuertoA.ToString(CultureInfo.InvariantCulture)}";

        // Orden con la que el usuario de prueba de A entra en B (la clave del cliente está montada en A).
        public string SshAlGemelo => $"ssh -tt -i /etc/ns/cliente_ed25519 -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR {Usuario}@gemelo-b";

        // El script deja el estado en NATIVE_SSH_DIR_ESTADO si está definida: se le pasa siempre el directorio que se lee
        // aquí, para no heredar la del entorno exterior.
        private static string RutaEstado(string directorioPruebas) => Path.Combine(directorioPruebas, ".estado-contenedor-gemelos");

        private static Dictionary<string, string> EntornoScript(string directorioPruebas) => new() { ["NATIVE_SSH_DIR_ESTADO"] = RutaEstado(directorioPruebas) };

        public static ServidorGemelos Iniciar()
        {
            string directorioPruebas = SshdLocal.BuscarDirectorioPruebas();
            string script = Path.Combine(directorioPruebas, "contenedor-sudo.sh");
            (int codigo, string salida, string errores) = ServidorContenedor.Ejecutar("bash", [script, "gemelos", "iniciar"], TimeSpan.FromMinutes(10), EntornoScript(directorioPruebas));
            return codigo != 0
                ? throw new InvalidOperationException($"contenedor-sudo.sh gemelos iniciar falló ({codigo}): {errores}")
                : new ServidorGemelos(int.Parse(salida.Trim().Split('\n')[^1].Split(' ')[0], CultureInfo.InvariantCulture));
        }

        public OpcionesNativeSsh Opciones() => new()
        {
            RutasIdentidad = [Path.Combine(DirectorioEstado, "cliente_ed25519")],
            EsperaTrasCtrlC = TimeSpan.FromSeconds(3),
            MaxConexionesPorVentana = 20
        };

        public GestorSesiones Gestor(OpcionesNativeSsh opciones)
        {
            FabricaConexionSsh fabrica = new(opciones, new VerificadorHostConocido([Path.Combine(DirectorioEstado, "known_hosts")]), NullLogger<FabricaConexionSsh>.Instance);
            return new GestorSesiones(fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
        }

        public void Dispose() => _ = ServidorContenedor.Ejecutar("bash", [Path.Combine(DirectorioPruebas, "contenedor-sudo.sh"), "gemelos", "parar"], TimeSpan.FromMinutes(1), EntornoScript(DirectorioPruebas));
    }
}
