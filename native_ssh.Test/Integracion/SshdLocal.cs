using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Ssh;

namespace NativeSsh.Test.Integracion
{
    // Arranca pruebas/sshd-local.sh una vez para toda la colección y lo para al terminar.
    public sealed class SshdLocal : IDisposable
    {
        private readonly Dictionary<string, string> _entorno = [];

        public SshdLocal()
            : this(null, null, null)
        {
        }

        // Instancia adicional con su propio puerto, directorio de estado y ForceCommand (no toca la de por defecto).
        internal SshdLocal(string? puerto, string? directorioEstado, string? forceCommand)
        {
            DirectorioPruebas = BuscarDirectorioPruebas();
            DirectorioEstado = Path.Combine(DirectorioPruebas, directorioEstado ?? ".estado");
            if (puerto is not null)
                _entorno["NATIVE_SSH_PUERTO_PRUEBAS"] = puerto;
            if (directorioEstado is not null)
                _entorno["NATIVE_SSH_DIR_ESTADO"] = DirectorioEstado;
            if (forceCommand is not null)
                _entorno["NATIVE_SSH_FORCE_COMMAND"] = forceCommand;

            string salida = Ejecutar("iniciar");
            Puerto = int.Parse(salida.Trim().Split('\n')[^1], CultureInfo.InvariantCulture);
            RutaClave = Path.Combine(DirectorioEstado, "cliente_ed25519");
            RutaKnownHosts = Path.Combine(DirectorioEstado, "known_hosts");
        }

        public string DirectorioPruebas { get; }
        public string DirectorioEstado { get; }
        public int Puerto { get; }
        public string RutaClave { get; }
        public string RutaKnownHosts { get; }

        public DestinoSsh Destino
        {
            get
            {
                _ = DestinoSsh.TryParse($"{Environment.UserName}@127.0.0.1:{Puerto}", "x", out DestinoSsh destino);
                return destino;
            }
        }

        public OpcionesNativeSsh Opciones(int maxConexiones = 4) => new()
        {
            RutasIdentidad = [RutaClave],
            EsperaTrasCtrlC = TimeSpan.FromSeconds(1),
            MaxConexionesPorVentana = maxConexiones
        };

        public FabricaConexionSsh Fabrica(OpcionesNativeSsh opciones, string? rutaKnownHosts = null) =>
            new(opciones, new VerificadorHostConocido([rutaKnownHosts ?? RutaKnownHosts]), NullLogger<FabricaConexionSsh>.Instance);

        public SesionShell Sesion(OpcionesNativeSsh? opciones = null)
        {
            opciones ??= Opciones();
            return new SesionShell(Destino, Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), opciones, NullLogger.Instance);
        }

        public void Dispose() => Ejecutar("parar");

        private string Ejecutar(string accion)
        {
            ProcessStartInfo inicio = new("bash", [Path.Combine(DirectorioPruebas, "sshd-local.sh"), accion])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach ((string clave, string valor) in _entorno)
                inicio.Environment[clave] = valor;
            using Process proceso = Process.Start(inicio)!;
            string salida = proceso.StandardOutput.ReadToEnd();
            string error = proceso.StandardError.ReadToEnd();
            proceso.WaitForExit();
            return proceso.ExitCode != 0
                ? throw new InvalidOperationException($"sshd-local.sh {accion} falló ({proceso.ExitCode}): {error}")
                : salida;
        }

        private static string BuscarDirectorioPruebas()
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string candidato = Path.Combine(dir.FullName, "pruebas", "sshd-local.sh");
                if (File.Exists(candidato))
                    return Path.GetDirectoryName(candidato)!;
            }

            throw new InvalidOperationException("No se encontró pruebas/sshd-local.sh.");
        }
    }

    // sshd cuyo login tarda más que el reenvío de la inicialización (2 s), como un bash -l con un perfil lento.
    public sealed class SshdLocalLoginLento : IDisposable
    {
        public const string ForceCommand = "/bin/bash -c 'sleep 3; exec /bin/bash -l'";

        public SshdLocal Sshd { get; } = new(
            Environment.GetEnvironmentVariable("NATIVE_SSH_PUERTO_PRUEBAS_LENTO") ?? "25022",
            ".estado-lento",
            ForceCommand);

        public void Dispose() => Sshd.Dispose();
    }

    [CollectionDefinition(Nombre)]
    public sealed class ColeccionSshd : ICollectionFixture<SshdLocal>
    {
        public const string Nombre = "sshd local";
    }

    [CollectionDefinition(Nombre)]
    public sealed class ColeccionSshdLoginLento : ICollectionFixture<SshdLocalLoginLento>
    {
        public const string Nombre = "sshd local con login lento";
    }
}
