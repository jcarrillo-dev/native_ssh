using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Ssh;

namespace NativeSsh.Test.Integracion
{
    // Servidores con sudo REAL en contenedores podman rootless (pruebas/contenedor-sudo.sh): Debian estable y Alpine, con
    // un usuario con contraseña y la configuración de sudo de serie de cada distribución. Cada distribución se arranca la
    // primera vez que una prueba la pide (si todas las pruebas de la colección se omiten, no se arranca nada) y se para al
    // terminar la colección.
    [SupportedOSPlatform("linux")]
    public sealed class ContenedorSudo : IDisposable
    {
        public const string Debian = "debian";
        public const string Alpine = "alpine";
        public const string SinBash = "sinbash";

        private readonly Dictionary<string, ServidorContenedor> _servidores = [];
        private readonly Lock _candado = new();

        public ServidorContenedor Servidor(string distro)
        {
            lock (_candado)
            {
                if (!_servidores.TryGetValue(distro, out ServidorContenedor? servidor))
                {
                    servidor = new ServidorContenedor(distro);
                    _servidores[distro] = servidor;
                }

                return servidor;
            }
        }

        public void Dispose()
        {
            lock (_candado)
            {
                foreach (ServidorContenedor servidor in _servidores.Values)
                    servidor.Dispose();
                _servidores.Clear();
            }
        }
    }

    // Un contenedor arrancado: destino SSH, claves y órdenes como root dentro del contenedor.
    [SupportedOSPlatform("linux")]
    public sealed class ServidorContenedor : IDisposable
    {
        private readonly Dictionary<string, string> _entorno = [];

        internal ServidorContenedor(string distro)
        {
            Distro = distro;
            DirectorioPruebas = SshdLocal.BuscarDirectorioPruebas();
            DirectorioEstado = Path.Combine(DirectorioPruebas, $".estado-contenedor-{distro}");
            // El script deja el estado en NATIVE_SSH_DIR_ESTADO si está definida: se le pasa siempre el directorio que se
            // lee aquí, para no heredar la del entorno exterior (con ella, las tres distribuciones compartirían directorio).
            _entorno["NATIVE_SSH_DIR_ESTADO"] = DirectorioEstado;
            string? puerto = Environment.GetEnvironmentVariable($"NATIVE_SSH_PUERTO_CONTENEDOR_{distro.ToUpperInvariant()}");
            if (puerto is not null)
                _entorno["NATIVE_SSH_PUERTO_CONTENEDOR"] = puerto;

            string salida = Script(["iniciar", distro], TimeSpan.FromMinutes(10));
            Puerto = int.Parse(salida.Trim().Split('\n')[^1], CultureInfo.InvariantCulture);
            Contenedor = File.ReadAllText(Path.Combine(DirectorioEstado, "contenedor")).Trim();
            Usuario = File.ReadAllText(Path.Combine(DirectorioEstado, "usuario")).Trim();
            Contrasena = File.ReadAllText(Path.Combine(DirectorioEstado, "contrasena")).Trim();
        }

        public string Distro { get; }
        public string DirectorioPruebas { get; }
        public string DirectorioEstado { get; }
        public int Puerto { get; }
        public string Contenedor { get; }
        public string Usuario { get; }
        public string Contrasena { get; }
        public string RutaClave => Path.Combine(DirectorioEstado, "cliente_ed25519");
        public string RutaKnownHosts => Path.Combine(DirectorioEstado, "known_hosts");
        public string Destino => $"{Usuario}@127.0.0.1:{Puerto.ToString(CultureInfo.InvariantCulture)}";
        public bool IsDebian => Distro == ContenedorSudo.Debian;

        public OpcionesNativeSsh Opciones() => new()
        {
            RutasIdentidad = [RutaClave],
            EsperaTrasCtrlC = TimeSpan.FromSeconds(1),
            MaxConexionesPorVentana = 20
        };

        public GestorSesiones Gestor(OpcionesNativeSsh? opciones = null)
        {
            opciones ??= Opciones();
            FabricaConexionSsh fabrica = new(opciones, new VerificadorHostConocido([RutaKnownHosts]), NullLogger<FabricaConexionSsh>.Instance);
            return new GestorSesiones(fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
        }

        public HerramientasSsh Herramientas(GestorSesiones gestor, TimeSpan? timeout = null) => new(gestor, Opciones()) { TimeoutForzado = timeout };

        // Ejecuta 'script' con sh como root (real) dentro del contenedor. "$1"... = argumentos. Devuelve stdout tal cual.
        public string Root(string script, params string[] argumentos)
        {
            (int codigo, string salida, string errores) = RootConCodigo(script, argumentos);
            return codigo != 0
                ? throw new InvalidOperationException($"El script como root en {Contenedor} falló ({codigo}): {errores}\n{script}")
                : salida;
        }

        public (int Codigo, string Salida, string Errores) RootConCodigo(string script, params string[] argumentos) =>
            Ejecutar("podman", ["exec", "--user", "0", Contenedor, "sh", "-c", script, "ns", .. argumentos], TimeSpan.FromMinutes(2));

        // Como el usuario de prueba, fuera de native_ssh (para preparar archivos suyos o retener un candado).
        public string ComoUsuario(string script, params string[] argumentos)
        {
            (int codigo, string salida, string errores) = Ejecutar("podman", ["exec", "--user", Usuario, "--workdir", $"/home/{Usuario}", Contenedor, "sh", "-c", script, "ns", .. argumentos], TimeSpan.FromMinutes(2));
            return codigo != 0
                ? throw new InvalidOperationException($"El script como {Usuario} en {Contenedor} falló ({codigo}): {errores}\n{script}")
                : salida;
        }

        // Lanza 'script' como root en segundo plano dentro del contenedor (p. ej. un flock que retiene un candado); el
        // proceso local de podman exec vive mientras dure el script.
        public Process RootEnSegundoPlano(string script, params string[] argumentos)
        {
            ProcessStartInfo inicio = new("podman", ["exec", "--user", "0", Contenedor, "sh", "-c", script, "ns", .. argumentos])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            return Process.Start(inicio)!;
        }

        public void Dispose() => _ = Script(["parar", Distro], TimeSpan.FromMinutes(1));

        private string Script(string[] argumentos, TimeSpan limite)
        {
            (int codigo, string salida, string errores) = Ejecutar("bash", [Path.Combine(DirectorioPruebas, "contenedor-sudo.sh"), .. argumentos], limite, _entorno);
            return codigo != 0
                ? throw new InvalidOperationException($"contenedor-sudo.sh {string.Join(' ', argumentos)} falló ({codigo}): {errores}")
                : salida;
        }

        internal static (int Codigo, string Salida, string Errores) Ejecutar(string programa, string[] argumentos, TimeSpan limite, Dictionary<string, string>? entorno = null)
        {
            ProcessStartInfo inicio = new(programa, argumentos)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (entorno is not null)
                foreach ((string clave, string valor) in entorno)
                    inicio.Environment[clave] = valor;

            using Process proceso = Process.Start(inicio)!;
            Task<string> errores = proceso.StandardError.ReadToEndAsync();
            Task<string> salida = proceso.StandardOutput.ReadToEndAsync();
            if (!proceso.WaitForExit(limite))
            {
                proceso.Kill(entireProcessTree: true);
                throw new TimeoutException($"{programa} {string.Join(' ', argumentos)} no terminó en {limite.TotalSeconds:0} s.");
            }

            proceso.WaitForExit();
            return (proceso.ExitCode, salida.Result, errores.Result);
        }
    }

    [CollectionDefinition(Nombre)]
    [SupportedOSPlatform("linux")]
    public sealed class ColeccionContenedores : ICollectionFixture<ContenedorSudo>
    {
        public const string Nombre = "contenedores con sudo real";
    }
}
