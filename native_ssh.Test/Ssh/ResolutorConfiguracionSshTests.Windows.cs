using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Ssh;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Ssh
{
    // Lo de Windows de 'ssh -G' (solo se compila para Windows), con el ssh.exe del sistema (OpenSSH para Windows) y un
    // fichero de configuración propio de la prueba (ssh -F), nunca el del usuario.
    public sealed class ResolutorConfiguracionSshWindowsTests : IDisposable
    {
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_cfgw_{Guid.NewGuid():N}")).FullName;
        private readonly LoggerPrueba _log = new();

        public void Dispose()
        {
            _log.Dispose();
            Directory.Delete(_directorio, recursive: true);
        }

        // ssh.exe se quedaba colgado hasta el plazo al escribir más de una vez en su salida de errores en una tubería (con la
        // entrada en una tubería escribe que no pedirá un terminal): con la configuración leída a tiempo, no se cuelga.
        [Fact]
        public void SshDelSistema_ConLaEntradaYLosErroresEnTuberias_LeeLaConfiguracionSinColgarse()
        {
            // Arrange
            string configuracion = Path.Combine(_directorio, "config");
            File.WriteAllText(configuracion, "Host web\n  HostName 127.0.0.1\n  User deploy\n  Port 2222\n");
            ResolutorConfiguracionSsh resolutor = new(new OpcionesNativeSsh
            {
                UsuarioPorDefecto = Environment.UserName,
                ConfiguracionSsh = configuracion,
                BinarioSsh = "ssh",
                TimeoutConfiguracionSsh = TimeSpan.FromSeconds(5)
            }, new LoggerDe<ResolutorConfiguracionSsh>(_log));
            _ = DestinoSsh.TryParse("web", Environment.UserName, out DestinoSsh destino, out bool isUsuario, out bool isPuerto).Should().BeTrue();
            Stopwatch reloj = Stopwatch.StartNew();

            // Act
            DestinoSsh resuelto = resolutor.Resolver(destino, isUsuario, isPuerto);

            // Assert
            _ = resuelto.Clave.Should().Be("deploy@127.0.0.1:2222");
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        }

        // Un mensaje de ssh (aquí, el de una opción que no existe) tampoco lo cuelga: el error llega a tiempo y con su motivo.
        [Fact]
        public void SshDelSistema_ConfiguracionConErrores_ErrorConElMotivoSinColgarse()
        {
            // Arrange
            string configuracion = Path.Combine(_directorio, "config");
            File.WriteAllText(configuracion, "Host web\n  NoExisteEstaOpcion si\n");
            ResolutorConfiguracionSsh resolutor = new(new OpcionesNativeSsh
            {
                UsuarioPorDefecto = Environment.UserName,
                ConfiguracionSsh = configuracion,
                BinarioSsh = "ssh",
                TimeoutConfiguracionSsh = TimeSpan.FromSeconds(5)
            }, new LoggerDe<ResolutorConfiguracionSsh>(_log));
            _ = DestinoSsh.TryParse("web", Environment.UserName, out DestinoSsh destino, out bool isUsuario, out bool isPuerto).Should().BeTrue();
            Stopwatch reloj = Stopwatch.StartNew();

            // Act
            Action resolver = () => resolutor.Resolver(destino, isUsuario, isPuerto);

            // Assert
            _ = resolver.Should().Throw<NativeSshException>().Which.Message.Should().Contain("(código 255)").And.Contain("noexisteestaopcion");
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        }

        private ResolutorConfiguracionSsh Resolutor(string configuracion) =>
            new(new OpcionesNativeSsh
            {
                UsuarioPorDefecto = Environment.UserName,
                ConfiguracionSsh = configuracion,
                BinarioSsh = "ssh",
                TimeoutConfiguracionSsh = TimeSpan.FromSeconds(5)
            }, new LoggerDe<ResolutorConfiguracionSsh>(_log));

        // Muchos mensajes de ssh (500 opciones que no existen: cada una, una línea en el fichero de -E) tampoco lo cuelgan.
        [Fact]
        public void SshDelSistema_MuchosMensajes_ErrorATiempoSinColgarse()
        {
            // Arrange
            string configuracion = Path.Combine(_directorio, "config");
            File.WriteAllText(configuracion, "Host web\n" + string.Concat(Enumerable.Range(0, 500).Select(i => $"  NoExiste{i} si\n")));
            _ = DestinoSsh.TryParse("web", Environment.UserName, out DestinoSsh destino, out bool isUsuario, out bool isPuerto).Should().BeTrue();
            ResolutorConfiguracionSsh resolutor = Resolutor(configuracion);
            Stopwatch reloj = Stopwatch.StartNew();

            // Act
            Action resolver = () => resolutor.Resolver(destino, isUsuario, isPuerto);

            // Assert
            _ = resolver.Should().Throw<NativeSshException>().Which.Message.Should().Contain("(código 255)").And.Contain("noexiste0");
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        }

        // El error lleva lo que escribió un Match exec (en la salida de errores de ssh) delante del mensaje de ssh (en el
        // fichero de -E), en el orden en que ocurrió: ssh lanza el Match exec al leer su línea y termina con su error después.
        [Fact]
        public void SshDelSistema_MatchExecQueEscribeYOpcionQueNoExiste_ElErrorEnElOrdenEnQueOcurrio()
        {
            // Arrange
            string configuracion = Path.Combine(_directorio, "config");
            File.WriteAllText(configuracion, "Match exec \"cmd /c echo mensaje-del-match-exec 1>&2\"\n  Port 9\nHost web\n  NoExisteEstaOpcion si\n");
            _ = DestinoSsh.TryParse("web", Environment.UserName, out DestinoSsh destino, out bool isUsuario, out bool isPuerto).Should().BeTrue();
            ResolutorConfiguracionSsh resolutor = Resolutor(configuracion);

            // Act
            Action resolver = () => resolutor.Resolver(destino, isUsuario, isPuerto);

            // Assert
            string mensaje = resolver.Should().Throw<NativeSshException>().Which.Message;
            int delMatchExec = mensaje.IndexOf("mensaje-del-match-exec", StringComparison.Ordinal);
            int deSsh = mensaje.IndexOf("noexisteestaopcion", StringComparison.Ordinal);
            _ = delMatchExec.Should().BeGreaterThan(0, mensaje);
            _ = deSsh.Should().BeGreaterThan(delMatchExec, mensaje);
        }

        // El ssh de Git for Windows (MSYS) lee '/c/…' como 'C:\…' y conecta con un IdentityFile así; native_ssh usa la misma
        // clave. Solo la forma de unidad ('/' + una letra + '/' o fin) y solo si el ssh que leyó la configuración es de MSYS:
        // el de OpenSSH para Windows la lee como una ruta de la unidad actual, y native_ssh también.
        [Theory]
        [InlineData(true, "/c/Users/ana/.ssh/id_ed25519", @"C:\Users\ana\.ssh\id_ed25519")]
        [InlineData(true, "/D/claves/id", @"D:\claves\id")]
        [InlineData(true, "/c", @"C:\")]
        [InlineData(true, "/c/", @"C:\")]
        [InlineData(true, "C:/Users/ana/.ssh/id", "C:/Users/ana/.ssh/id")]
        [InlineData(true, @"C:\Users\ana\.ssh\id", @"C:\Users\ana\.ssh\id")]
        [InlineData(true, "/etc/ssh/id", "/etc/ssh/id")]
        [InlineData(true, "/cc/id", "/cc/id")]
        [InlineData(true, "/1/id", "/1/id")]
        [InlineData(false, "/c/Users/ana/.ssh/id_ed25519", "/c/Users/ana/.ssh/id_ed25519")]
        [InlineData(false, "/k/a", "/k/a")]
        public void TryExpandir_RutaDeUnidadDeMsys_ComoRutaDeWindowsSoloConElSshDeMsys(bool isSshDeMsys, string identidad, string esperada)
        {
            // Act
            bool isExpandida = ResolutorConfiguracionSsh.TryExpandir(identidad, @"C:\Users\ana", "web", "web", "ana", 22, isSshDeMsys, out string? ruta, out string? motivo);

            // Assert
            _ = isExpandida.Should().BeTrue(motivo);
            _ = ruta.Should().Be(esperada);
        }

        // El ssh de MSYS se reconoce por su runtime (msys-2.0.dll) en la misma carpeta que ssh.exe.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void IsProgramaDeMsys_PorElRuntimeEnSuCarpeta(bool hasRuntime)
        {
            // Arrange
            string programa = Path.Combine(_directorio, "ssh.exe");
            File.WriteAllText(programa, "");
            if (hasRuntime)
                File.WriteAllText(Path.Combine(_directorio, "msys-2.0.dll"), "");

            // Act
            bool isDeMsys = ResolutorConfiguracionSsh.IsProgramaDeMsys(programa);

            // Assert
            _ = isDeMsys.Should().Be(hasRuntime);
        }
    }

    // Lo de Windows de 'ssh -G' que no puede ir en paralelo con otras pruebas (colección sin paralelismo): cambia variables
    // de entorno del proceso o cuenta los ficheros de -E de la carpeta temporal, que otras pruebas crean y borran.
    [Collection(ColeccionEntornoProceso.Nombre)]
    public sealed class ResolutorConfiguracionSshWindowsEntornoTests : IDisposable
    {
        private static readonly string[] Variables = ["NATIVE_SSH_PASSWORD", "NATIVE_SSH_PASSPHRASE", "NATIVE_SSH_TOKEN_HTTP"];
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_cfgw_env_{Guid.NewGuid():N}")).FullName;
        private readonly Dictionary<string, string?> _anteriores = Variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        private readonly string? _shellAnterior = Environment.GetEnvironmentVariable("SHELL");
        private readonly LoggerPrueba _log = new();

        // El ssh de Git for Windows (MSYS) ejecuta el Match exec con $SHELL; en una sesión del sshd de Windows vale
        // c:\windows\system32\cmd.exe, que no entiende '-c' y devuelve al instante (el Match no espera y el plazo no se
        // agota). Sin SHELL, usa su sh, como en una consola normal. El ssh de OpenSSH para Windows no la mira.
        public ResolutorConfiguracionSshWindowsEntornoTests() => Environment.SetEnvironmentVariable("SHELL", null);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("SHELL", _shellAnterior);
            foreach ((string variable, string? valor) in _anteriores)
                Environment.SetEnvironmentVariable(variable, valor);
            _log.Dispose();
            Directory.Delete(_directorio, recursive: true);
        }

        private ResolutorConfiguracionSsh Resolutor(string configuracion, TimeSpan plazo) =>
            new(new OpcionesNativeSsh
            {
                UsuarioPorDefecto = Environment.UserName,
                ConfiguracionSsh = configuracion,
                BinarioSsh = "ssh",
                TimeoutConfiguracionSsh = plazo
            }, new LoggerDe<ResolutorConfiguracionSsh>(_log));

        // Lo de MatchExec_NoVeLosSecretosDeNativeSsh (Linux, con env) con cmd: lo que lanza un Match exec no ve los secretos
        // de native_ssh en su entorno.
        [Fact]
        public void MatchExecConCmd_NoVeLosSecretosDeNativeSsh()
        {
            // Arrange
            // El volcado va en un .cmd que el Match exec lanza por su ruta: con el ssh de MSYS, el '/c' de 'cmd /c' en la línea
            // del Match exec se convertiría en la ruta 'C:/'.
            string volcado = Path.Combine(_directorio, "entorno");
            string volcar = Path.Combine(_directorio, "volcar.cmd");
            File.WriteAllText(volcar, $"@set > \"{volcado}\"\r\n");
            string configuracion = Path.Combine(_directorio, "config");
            File.WriteAllText(configuracion, $"Match exec \"{volcar.Replace('\\', '/')}\"\n  Port 9\n");
            foreach (string variable in Variables)
                Environment.SetEnvironmentVariable(variable, "Canario-secreto-entorno");
            _ = DestinoSsh.TryParse("web", "ana", out DestinoSsh destino);

            // Act
            DestinoSsh resuelto = Resolutor(configuracion, TimeSpan.FromSeconds(5)).Resolver(destino, false, false);

            // Assert
            _ = resuelto.Puerto.Should().Be(9);
            string entorno = File.ReadAllText(volcado);
            _ = entorno.Should().ContainEquivalentOf("PATH=").And.NotContain("Canario-secreto-entorno");
        }

        // Con el plazo agotado (un Match exec lento), el error llega a tiempo y el fichero de -E no queda en la carpeta
        // temporal.
        [Fact]
        public void PlazoAgotado_ErrorATiempoYNoQuedaElFicheroDeRegistro()
        {
            // Arrange
            string configuracion = Path.Combine(_directorio, "config");
            File.WriteAllText(configuracion, "Match exec \"ping -n 30 127.0.0.1\"\n  Port 9\nHost web\n  HostName 127.0.0.1\n");
            _ = DestinoSsh.TryParse("web", Environment.UserName, out DestinoSsh destino, out bool isUsuario, out bool isPuerto).Should().BeTrue();
            ResolutorConfiguracionSsh resolutor = Resolutor(configuracion, TimeSpan.FromSeconds(2));
            string[] registrosAntes = Directory.GetFiles(Path.GetTempPath(), "native_ssh_g_*");
            Stopwatch reloj = Stopwatch.StartNew();

            // Act
            Action resolver = () => resolutor.Resolver(destino, isUsuario, isPuerto);

            // Assert
            _ = resolver.Should().Throw<NativeSshException>().Which.Message.Should().Contain("no terminó");
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
            _ = Directory.GetFiles(Path.GetTempPath(), "native_ssh_g_*").Except(registrosAntes).Should().BeEmpty();
        }

        // CreateProcess busca un programa sin ruta en el directorio de native_ssh y en el directorio actual antes que en PATH:
        // un ssh.exe dejado en la carpeta desde la que se lanza native_ssh (la del proyecto que abre el cliente MCP) se
        // ejecutaría. El programa se busca solo en PATH: aquí, un ejecutable con ese nombre solo en el directorio actual (una
        // copia de whoami.exe, que falla con las opciones de ssh) no se ejecuta, y el destino se usa tal cual, como sin ssh.
        // Cambia el directorio actual del proceso: en esta colección, sin paralelismo.
        [Fact]
        public void ProgramaSoloEnElDirectorioActual_NoSeEjecuta()
        {
            // Arrange
            string nombre = $"ns_ssh_plantado_{Guid.NewGuid():N}";
            File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), Path.Combine(_directorio, nombre + ".exe"));
            string configuracion = Path.Combine(_directorio, "config");
            File.WriteAllText(configuracion, "Host web\n  HostName 127.0.0.1\n  Port 2222\n");
            ResolutorConfiguracionSsh resolutor = new(new OpcionesNativeSsh
            {
                UsuarioPorDefecto = "ana",
                ConfiguracionSsh = configuracion,
                BinarioSsh = nombre,
                TimeoutConfiguracionSsh = TimeSpan.FromSeconds(5)
            }, new LoggerDe<ResolutorConfiguracionSsh>(_log));
            _ = DestinoSsh.TryParse("web", "ana", out DestinoSsh destino, out bool isUsuario, out bool isPuerto).Should().BeTrue();
            string anterior = Environment.CurrentDirectory;
            Environment.CurrentDirectory = _directorio;
            DestinoSsh resuelto;
            try
            {
                // Act
                resuelto = resolutor.Resolver(destino, isUsuario, isPuerto);
            }
            finally
            {
                Environment.CurrentDirectory = anterior;
            }

            // Assert
            _ = resuelto.Clave.Should().Be("ana@web:22");
            _ = _log.Entradas.Should().Contain(e => e.Mensaje.Contains($"No se encontró el programa '{nombre}'", StringComparison.Ordinal));
        }
        // Si el job object no se puede asignar (aquí, a un proceso que ya terminó), queda un aviso en el registro (EventId 68)
        // con el error de Windows, y no hay grupo: Matar mata solo el árbol de procesos.
        [Fact]
        public void JobNoAsignado_AvisaEnElRegistroConElErrorDeWindows()
        {
            // Arrange
            using Process terminado = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
            terminado.WaitForExit();

            // Act
            using System.Runtime.InteropServices.SafeHandle? grupo = ResolutorConfiguracionSsh.Agrupar(terminado, "web", new LoggerDe<ResolutorConfiguracionSsh>(_log));

            // Assert
            _ = grupo.Should().BeNull();
            _ = _log.Entradas.Should().ContainSingle(e => e.Evento == 68).Which.Mensaje.Should().StartWith("No se pudo meter 'ssh -G' de 'web' en un job object propio (error ").And.NotContain("error 0 ");
        }

        // Un Match exec cuyo padre termina antes que él (en el ssh de Git for Windows, el sh de MSYS que lo lanza sale en
        // cuanto lo arranca; aquí, un cmd intermedio lanzado con 'start /b' que sale al instante) no cuelga de ssh en el
        // árbol de procesos: al agotarse el plazo también se mata, y deja de escribir en su marca. En esta colección porque
        // la clase quita SHELL del entorno del proceso.
        [Fact]
        public void PlazoAgotado_MataLoQueLanzoElMatchExecAunqueSuPadreHayaTerminado()
        {
            // Arrange
            string marca = Path.Combine(_directorio, "marca");
            string bucle = Path.Combine(_directorio, "bucle.cmd");
            string intermedio = Path.Combine(_directorio, "intermedio.cmd");
            string lanzar = Path.Combine(_directorio, "lanzar.cmd");
            File.WriteAllText(bucle, $"@for /l %%i in (1,1,60) do @(echo x>>\"{marca}\" & ping -n 2 127.0.0.1 >nul)\r\n");
            File.WriteAllText(intermedio, $"@start \"\" /b cmd /c \"{bucle}\"\r\n");
            File.WriteAllText(lanzar, $"@start \"\" /b cmd /c \"{intermedio}\"\r\n@ping -n 30 127.0.0.1 >nul\r\n");
            string configuracion = Path.Combine(_directorio, "config");
            File.WriteAllText(configuracion, $"Match exec \"{lanzar.Replace('\\', '/')}\"\n  Port 9\nHost web\n  HostName 127.0.0.1\n");
            _ = DestinoSsh.TryParse("web", Environment.UserName, out DestinoSsh destino, out bool isUsuario, out bool isPuerto).Should().BeTrue();
            ResolutorConfiguracionSsh resolutor = new(new OpcionesNativeSsh
            {
                UsuarioPorDefecto = Environment.UserName,
                ConfiguracionSsh = configuracion,
                BinarioSsh = "ssh",
                TimeoutConfiguracionSsh = TimeSpan.FromSeconds(3)
            }, new LoggerDe<ResolutorConfiguracionSsh>(_log));

            // Act
            Action resolver = () => resolutor.Resolver(destino, isUsuario, isPuerto);

            // Assert
            _ = resolver.Should().Throw<NativeSshException>().Which.Message.Should().Contain("no terminó");
            _ = File.Exists(marca).Should().BeTrue("el Match exec llegó a lanzar el bucle antes del plazo");
            Thread.Sleep(500);
            long tamanoTrasMatar = new FileInfo(marca).Length;
            Thread.Sleep(3000);
            _ = new FileInfo(marca).Length.Should().Be(tamanoTrasMatar, "lo que lanzó el Match exec sigue vivo y escribiendo");
        }

        // Lo mismo con el ssh de MSYS (Git for Windows) primero en PATH, el caso para el que entra ssh en un job object: su sh
        // ejecuta el Match exec y, con 'exec', el programa (otro sh de MSYS, con el bucle que escribe en la marca) sustituye
        // al sh, cuyo proceso de Windows termina; el bucle queda sin padre en el árbol de procesos y ssh lo sigue esperando.
        [FactSshDeMsys]
        public void PlazoAgotado_ConElSshDeMsys_MataElProgramaDelMatchExecQueSeQuedoSinPadre()
        {
            // Arrange
            string marca = Path.Combine(_directorio, "marca");
            string bucle = Path.Combine(_directorio, "bucle.sh");
            File.WriteAllText(bucle, $"while :; do echo x >> '{RutaMsys(marca)}'; sleep 0.3; done\n");
            string configuracion = Path.Combine(_directorio, "config");
            File.WriteAllText(configuracion, $"Match exec \"exec sh '{RutaMsys(bucle)}'\"\n  Port 9\nHost web\n  HostName 127.0.0.1\n");
            _ = DestinoSsh.TryParse("web", Environment.UserName, out DestinoSsh destino, out bool isUsuario, out bool isPuerto).Should().BeTrue();
            ResolutorConfiguracionSsh resolutor = Resolutor(configuracion, TimeSpan.FromSeconds(3));

            // Act
            Action resolver = () => resolutor.Resolver(destino, isUsuario, isPuerto);

            // Assert
            _ = resolver.Should().Throw<NativeSshException>().Which.Message.Should().Contain("no terminó");
            _ = File.Exists(marca).Should().BeTrue("el Match exec llegó a lanzar el bucle antes del plazo");
            Thread.Sleep(500);
            long tamanoTrasMatar = new FileInfo(marca).Length;
            Thread.Sleep(3000);
            _ = new FileInfo(marca).Length.Should().Be(tamanoTrasMatar, "lo que lanzó el Match exec sigue vivo y escribiendo");
        }

        // 'C:\a\b' como la escribe MSYS: '/c/a/b'.
        private static string RutaMsys(string ruta) => "/" + char.ToLowerInvariant(ruta[0]) + ruta[2..].Replace('\\', '/');
    }

    // Solo si el ssh que se resuelve (el primero de PATH) es de MSYS, como el de Git for Windows.
    public sealed class FactSshDeMsysAttribute : FactAttribute
    {
        private static readonly Lazy<string?> Motivo = new(() =>
            new ResolutorConfiguracionSsh(new OpcionesNativeSsh { UsuarioPorDefecto = "ana" }, Microsoft.Extensions.Logging.Abstractions.NullLogger<ResolutorConfiguracionSsh>.Instance).IsSshDeMsys()
                ? null
                : "El ssh del PATH no es de MSYS (Git for Windows): pon C:\\Program Files\\Git\\usr\\bin delante en PATH para esta prueba.");

        public override string? Skip
        {
            get => base.Skip ?? Motivo.Value;
            set => base.Skip = value;
        }
    }
}
