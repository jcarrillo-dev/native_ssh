using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // RC1 con bash real (sshd local): ssh_reset_shell con sinPerfil=true abre el shell con una petición 'exec' de
    // 'bash --noediting --norc --noprofile' en un pseudoterminal (no es un shell de login) y todo lo que va por el shell
    // funciona igual: ssh_exec, guardarSalida, ssh_write_file, ssh_replace_file_content y ssh_set_cwd; un reset sin la opción
    // vuelve al login. Con un ForceCommand de verdad (que no ejecuta el comando pedido), el shell es el de siempre.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class SinPerfilIntegracionTests : IAsyncLifetime
    {
        // El shell de login de las pruebas exporta NS_LOGIN (un perfil): el sin perfil no lo tiene. El bash del shell es
        // siempre el de la inicialización ('exec bash --noediting --norc --noprofile'), con perfil o sin él.
        private const string LoginConPerfil = "/bin/bash -c 'export NS_LOGIN=si; exec /bin/bash -l'";
        private const string Sonda = "echo \"perfil=${NS_LOGIN:-no}\"";

        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_sinperfil_{Guid.NewGuid():N}")).FullName;
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

        private HerramientasSsh Herramientas(string configuracionExtra = "")
        {
            _sshd = new SshdPropio(configuracionExtra, LoginConPerfil);
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones() with { PrefijoGuardado = Path.Combine(_directorio, "native_ssh") };
            _gestor = new GestorSesiones(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            return new HerramientasSsh(_gestor, opciones);
        }

        private string Destino => _sshd!.Sshd.Destino.Clave;

        [Fact]
        public async Task ResetSinPerfil_ShellSinLoginConLasMismasHerramientasYVueltaAlLogin()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string archivo = Path.Combine(_directorio, "reparar.txt");
            ResultadoComando antes = await h.EjecutarAsync(Destino, Sonda, esperarSegundos: 30).ComoComando();

            // Act
            _ = h.ReiniciarShell(Destino, sinPerfil: true);
            ResultadoComando sinPerfil = await h.EjecutarAsync(Destino, Sonda + "; export NS_VARIABLE=1", esperarSegundos: 30).ComoComando();
            ResultadoComando variable = await h.EjecutarAsync(Destino, "echo \"v=$NS_VARIABLE\"", esperarSegundos: 30).ComoComando();
            ResultadoComando guardado = await h.EjecutarAsync(Destino, "echo guardado", esperarSegundos: 30, guardarSalida: true).ComoComando();
            ResultadoEscritura escrito = await h.EscribirArchivoAsync(Destino, archivo, "sleep 20\nlinea buena\n");
            ResultadoEscritura reemplazado = await h.ReemplazarContenidoArchivoAsync(Destino, archivo, "sleep 20\n", "");
            ResultadoDirectorioTrabajo cwd = await h.FijarDirectorioAsync(Destino, _directorio);
            ResultadoComando pwd = await h.EjecutarAsync(Destino, "pwd", esperarSegundos: 30).ComoComando();
            bool? isSinPerfilEnEstado = h.Estado().Sesiones.Single().IsSinPerfil;
            _ = h.ReiniciarShell(Destino);
            ResultadoComando despues = await h.EjecutarAsync(Destino, Sonda, esperarSegundos: 30).ComoComando();

            // Assert
            _ = antes.Salida.Should().Be("perfil=si\n");
            _ = sinPerfil.Salida.Should().Be("perfil=no\n");
            _ = sinPerfil.Aviso.Should().Contain("Shell SIN PERFIL");
            _ = variable.Salida.Should().Be("v=1\n");
            _ = (guardado.Estado, guardado.Salida).Should().Be((EstadoPeticionEnum.Completado, "guardado\n"));
            _ = guardado.Archivo.Should().NotBeNull();
            _ = (escrito.Bytes, reemplazado.Reemplazos).Should().Be((21, 1));
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("linea buena\n");
            _ = cwd.Directorio.Should().Be(_directorio);
            _ = pwd.Salida.Should().Be($"{_directorio}\n");
            _ = isSinPerfilEnEstado.Should().BeTrue();
            _ = despues.Salida.Should().Be("perfil=si\n");
            _ = h.Estado().Sesiones.Single().IsSinPerfil.Should().BeNull();
        }

        // Un ForceCommand que no ejecuta el comando pedido (aquí, un bash de login a secas) sustituye también la petición sin
        // perfil: el shell es el de siempre, con su login. Es lo documentado (ssh_reset_shell, sinPerfil).
        [Fact]
        public async Task ResetSinPerfil_ConForceCommandReal_ElShellEsElDeSiempre()
        {
            // Arrange
            HerramientasSsh h = Herramientas($"Match all\n    ForceCommand {LoginConPerfil}");

            // Act
            _ = h.ReiniciarShell(Destino, sinPerfil: true);
            ResultadoComando sinPerfil = await h.EjecutarAsync(Destino, Sonda, esperarSegundos: 30).ComoComando();

            // Assert
            _ = sinPerfil.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = sinPerfil.Salida.Should().Be("perfil=si\n");
        }
    }
}
