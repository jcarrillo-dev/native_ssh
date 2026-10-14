using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Ssh;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Arranca pruebas/sshd-local.sh una vez para toda la colección y lo para al terminar.
    public sealed class SshdLocal : IDisposable
    {
        // Rastro en el log de sshd (nivel INFO, con ForceCommand) de una petición 'signal' recibida ("refusing to send
        // signal", "no pid for session", "no session N req signal") o de una conexión cortada por un mensaje dirigido a
        // un canal ya liberado ("unknown channel", "nonexistent channel").
        public static readonly string[] LineasSenalODesconexion = ["refusing to send signal", "no pid for session", "req signal", "unknown channel", "nonexistent channel"];

        private readonly Dictionary<string, string> _entorno;

        public SshdLocal()
            : this(null, null, null)
        {
        }

        // Instancia adicional con su propio puerto, directorio de estado, ForceCommand y líneas extra de sshd_config
        // (no toca la de por defecto).
        internal SshdLocal(string? puerto, string? directorioEstado, string? forceCommand, string? configuracionExtra = null)
        {
            DirectorioPruebas = BuscarDirectorioPruebas();
            DirectorioEstado = Path.Combine(DirectorioPruebas, directorioEstado ?? ".estado");
            _entorno = EntornoScript(DirectorioEstado, puerto, forceCommand, configuracionExtra);

            string salida = Ejecutar("iniciar");
            Puerto = int.Parse(salida.Trim().Split('\n')[^1], CultureInfo.InvariantCulture);
            RutaClave = Path.Combine(DirectorioEstado, "cliente_ed25519");
            RutaKnownHosts = Path.Combine(DirectorioEstado, "known_hosts");
        }

        // Lo que recibe sshd-local.sh, siempre explícito para no heredar NATIVE_SSH_DIR_ESTADO, NATIVE_SSH_FORCE_COMMAND ni
        // NATIVE_SSH_SSHD_EXTRA del entorno de quien lanza las pruebas: el directorio de estado es el mismo que lee C# (la
        // clave, known_hosts, el log) y un ForceCommand o unas líneas extra vacías son los valores por defecto del script,
        // que trata una variable vacía como no definida. El puerto solo si se pide uno; si no, el de
        // NATIVE_SSH_PUERTO_PRUEBAS, que sí es del entorno a propósito (el bloque de puertos de cada ejecución).
        internal static Dictionary<string, string> EntornoScript(string directorioEstado, string? puerto, string? forceCommand, string? configuracionExtra)
        {
            Dictionary<string, string> entorno = new()
            {
                ["NATIVE_SSH_DIR_ESTADO"] = directorioEstado,
                ["NATIVE_SSH_FORCE_COMMAND"] = forceCommand ?? string.Empty,
                ["NATIVE_SSH_SSHD_EXTRA"] = configuracionExtra ?? string.Empty
            };
            if (puerto is not null)
                entorno["NATIVE_SSH_PUERTO_PRUEBAS"] = puerto;
            return entorno;
        }

        public string DirectorioPruebas { get; }
        public string DirectorioEstado { get; }
        public int Puerto { get; }
        public string RutaClave { get; }
        public string RutaKnownHosts { get; }
        public string RutaLog => Path.Combine(DirectorioEstado, "sshd.log");
        public string PidMaestro => File.ReadAllText(Path.Combine(DirectorioEstado, "sshd.pid")).Trim();

        // Conexiones TCP autenticadas desde que arrancó el sshd.
        public int ConexionesAceptadas() => ContarLineasLog("Accepted publickey");

        // Procesos sshd de las conexiones abiertas: los sshd-session que cuelgan del maestro (el monitor y el que atiende
        // la conexión), sin los procesos de los usuarios.
        public string[] ProcesosDeConexion() =>
            BashLocal.Ejecutar($"desc() {{ for c in $(pgrep -P $1); do case \"$(ps -o comm= -p $c)\" in sshd*) echo $c; desc $c;; esac; done; }}; desc {PidMaestro}")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Todos los procesos que cuelgan ahora del maestro (los sshd de cada conexión, los shells y lo que ejecutan), con su
        // padre y su línea de órdenes. Lo que ya quedó huérfano no está: su padre es otro.
        public IReadOnlyList<(int Pid, int Padre, string Orden)> Descendientes()
        {
            List<(int, int, string)> procesos = [];
            string salida = BashLocal.Ejecutar($"desc() {{ for c in $(pgrep -P $1); do ps -o pid=,ppid=,args= -p $c; desc $c; done; }}; desc {PidMaestro}");
            foreach (string linea in salida.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] campos = linea.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
                if (campos.Length >= 2)
                    procesos.Add((int.Parse(campos[0], CultureInfo.InvariantCulture), int.Parse(campos[1], CultureInfo.InvariantCulture), campos.Length == 3 ? campos[2].Trim() : string.Empty));
            }

            return procesos;
        }

        // Manda la señal a los descendientes del maestro cuya línea de órdenes cumple la condición (y, con isConPadre, también
        // a su padre): mata por pid lo que lanzaron las pruebas en este sshd, sin buscarlo en toda la máquina.
        public void MatarDescendientes(Func<string, bool> isElegido, string senal = "TERM", bool isConPadre = false)
        {
            List<int> objetivos = [];
            foreach ((int pid, int padre, string orden) in Descendientes())
            {
                if (!isElegido(orden))
                    continue;
                if (isConPadre)
                    objetivos.Add(padre);
                objetivos.Add(pid);
            }

            if (objetivos.Count > 0)
                _ = BashLocal.Ejecutar($"kill -{senal} {string.Join(' ', objetivos)} 2>/dev/null; true");
        }

        public int ContarLineasLog(params string[] textos)
        {
            int total = 0;
            foreach (string linea in File.ReadLines(RutaLog))
                if (Array.Exists(textos, t => linea.Contains(t, StringComparison.Ordinal)))
                    total++;
            return total;
        }

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

        internal static string BuscarDirectorioPruebas()
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
            PuertosPruebas.PuertoColeccion("NATIVE_SSH_PUERTO_PRUEBAS_LENTO", PuertosPruebas.DesplazamientoLento),
            ".estado-lento",
            ForceCommand);

        public void Dispose() => Sshd.Dispose();
    }

    // sshd con un shell de login cuyo PATH no es el de los canales exec: una granja de /usr/bin sin 'timeout' ni 'rg',
    // más un 'rg' falso y una sonda. Y MaxSessions 2: el shell y un único canal exec a la vez.
    [SupportedOSPlatform("linux")]
    public sealed class SshdLocalRestringido : IDisposable
    {
        public const string LineaRgFalso = "rg_del_login:1:PATH del shell de login";

        public SshdLocalRestringido()
            : this(PuertosPruebas.PuertoColeccion("NATIVE_SSH_PUERTO_PRUEBAS_RESTRINGIDO", PuertosPruebas.DesplazamientoRestringido), ".estado-restringido", Path.Combine(Path.GetTempPath(), $"ns_login_{Guid.NewGuid():N}"))
        {
        }

        // Con su propio puerto, directorio de estado (iniciar para antes el sshd de ese directorio) y granja de binarios.
        // Si sshd no arranca, xUnit no llama a Dispose: la granja se borra aquí.
        internal SshdLocalRestringido(string puerto, string directorioEstado, string directorioLogin)
        {
            DirectorioLogin = GranjaBinarios.Crear(directorioLogin, "timeout", "rg");
            try
            {
                CrearEjecutable("rg", $"echo '{LineaRgFalso}'");
                CrearEjecutable("ns_sonda_path", "echo sonda");
                Sshd = new SshdLocal(
                    puerto,
                    directorioEstado,
                    $"/usr/bin/env PATH={DirectorioLogin} /usr/bin/bash --norc --noprofile",
                    "MaxSessions 2");
            }
            catch
            {
                Directory.Delete(DirectorioLogin, recursive: true);
                throw;
            }
        }

        public SshdLocal Sshd { get; }
        public string DirectorioLogin { get; }

        public void Dispose()
        {
            Sshd.Dispose();
            if (Directory.Exists(DirectorioLogin))
                Directory.Delete(DirectorioLogin, recursive: true);
        }

        private void CrearEjecutable(string nombre, string cuerpo)
        {
            string ruta = Path.Combine(DirectorioLogin, nombre);
            File.WriteAllText(ruta, $"#!/usr/bin/bash\n{cuerpo}\n");
            File.SetUnixFileMode(ruta, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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

    [CollectionDefinition(Nombre)]
    [SupportedOSPlatform("linux")]
    public sealed class ColeccionSshdRestringido : ICollectionFixture<SshdLocalRestringido>
    {
        public const string Nombre = "sshd local restringido";
    }
}
