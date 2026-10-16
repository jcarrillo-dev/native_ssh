using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // ssh_set_cwd contra un sshd real: lo que dice el error sobre el directorio de trabajo es lo que queda de verdad en el
    // shell, en las herramientas de lectura y en C# (casos Q01 y Q12 de la revisión del A-decies; A-undecies, AU1 y AU2).
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El sshd y el gestor se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class HerramientasSetCwdIntegracionTests : IAsyncLifetime
    {
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_setcwd_int_{Guid.NewGuid():N}")).FullName;
        private SshdPropio? _sshd;
        private GestorSesiones? _gestor;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (_gestor is not null)
                await _gestor.DisposeAsync();
            _sshd?.Dispose();
            Directory.Delete(_directorio, recursive: true);
        }

        private (HerramientasSsh Herramientas, string Destino) Crear()
        {
            _sshd = new SshdPropio(string.Empty);
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones();
            _gestor = new GestorSesiones(
                _sshd.Sshd.Fabrica(opciones),
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            return (new HerramientasSsh(_gestor, opciones), _sshd.Sshd.Destino.Clave);
        }

        private string? DirectorioDeLaSesion(string destino) =>
            _gestor!.ListarSesiones().FirstOrDefault(s => s.Destino.Clave == destino)?.DirectorioTrabajo;

        private static async Task<string> PwdDelShellAsync(HerramientasSsh herramientas, string destino)
        {
            ResultadoComando resultado = await herramientas.EjecutarAsync(destino, "printf '%s' \"$PWD\" | base64 -w0", 30, 30).ComoComando();
            _ = resultado.Estado.Should().Be(EstadoPeticionEnum.Completado, resultado.Aviso);
            return Encoding.UTF8.GetString(Convert.FromBase64String(resultado.Salida.Trim()));
        }

        // La validación de set_cwd(Y) espera en la cola detrás de un comando y ssh_disconnect la cancela: la sesión ya no
        // existe y la siguiente herramienta abre una conexión nueva en $HOME. El error lo dice y no afirma que el
        // directorio siga siendo X (caso Q01).
        [Fact]
        public async Task DisconnectDuranteLaValidacion_DiceQueLaSesionSeCerroYLaProximaEmpiezaEnHome()
        {
            // Arrange
            (HerramientasSsh herramientas, string destino) = Crear();
            string x = Directory.CreateDirectory(Path.Combine(_directorio, "x")).FullName;
            string y = Directory.CreateDirectory(Path.Combine(_directorio, "y")).FullName;
            _ = await herramientas.FijarDirectorioAsync(destino, x);
            _ = await herramientas.EjecutarAsync(destino, "sleep 30", 60, 0).ComoComando();
            Task<ResultadoDirectorioTrabajo> fijar = herramientas.FijarDirectorioAsync(destino, y);
            for (int i = 0; i < 200 && !_gestor!.ListarPeticiones().Any(p => p.Estado == EstadoPeticionEnum.Encolado && p.Comando.Contains("pwd -P", StringComparison.Ordinal)); i++)
                await Task.Delay(50);

            // Act
            _ = await herramientas.DesconectarAsync(destino);
            Exception? error = await Record.ExceptionAsync(() => fijar);
            string pwd = await PwdDelShellAsync(herramientas, destino);

            // Assert
            _ = error.Should().BeOfType<NativeSshException>().Which.Message.Should()
                .EndWith($"La sesión con {destino} se cerró mientras tanto (ssh_disconnect) y con ella se olvidó el directorio de trabajo: la próxima herramienta abrirá una conexión nueva en $HOME (~).")
                .And.NotContain("sigue siendo").And.NotContain("ssh_reset_shell");
            _ = pwd.Should().Be(Environment.GetEnvironmentVariable("HOME"));
            _ = DirectorioDeLaSesion(destino).Should().BeNull();
        }

        // Volver a $HOME con __NS_CWD de solo lectura: 'unset' falla, así que el shell sigue en X. ssh_set_cwd no responde
        // «restablecido»: dice que sigue siendo X, y C# y el shell coinciden (caso Q12).
        [Fact]
        public async Task VolverAHomeConLaVariableDeSoloLectura_DiceQueSigueEnElAnteriorYCsYShellCoinciden()
        {
            // Arrange
            (HerramientasSsh herramientas, string destino) = Crear();
            string x = Directory.CreateDirectory(Path.Combine(_directorio, "x12")).FullName;
            _ = await herramientas.FijarDirectorioAsync(destino, x);
            ResultadoComando soloLectura = await herramientas.EjecutarAsync(destino, "readonly __NS_CWD", 30, 30).ComoComando();
            _ = soloLectura.Estado.Should().Be(EstadoPeticionEnum.Completado, soloLectura.Aviso);

            // Act
            Exception? error = await Record.ExceptionAsync(() => herramientas.FijarDirectorioAsync(destino, ""));
            string pwd = await PwdDelShellAsync(herramientas, destino);

            // Assert
            _ = error.Should().BeOfType<NativeSshException>().Which.Message.Should()
                .EndWith($"El directorio de trabajo sigue siendo {x}.").And.NotContain("restablecido");
            _ = pwd.Should().Be(x);
            _ = DirectorioDeLaSesion(destino).Should().Be(x);
        }
    }
}
