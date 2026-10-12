using System.Linq;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Ssh;
using NativeSsh.Test.Sesion.Falsos;

namespace NativeSsh.Test.Ssh
{
    // Sesiones con la configuración de ssh: una sesión por destino resuelto Y claves (las de IdentityFile, en orden), como
    // en ssh, donde cada alias se autentica con las suyas. Un alias no usa la sesión (ni las claves) de otro alias al mismo
    // usuario@servidor:puerto; el limitador sigue por servidor real. Configuración propia (ssh -F) y conexiones falsas.
    [SupportedOSPlatform("linux")]
    public sealed class SesionesConConfiguracionSshTests : IDisposable
    {
        private static readonly TimeSpan Limite = TimeSpan.FromSeconds(10);
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_cfg_ses_{Guid.NewGuid():N}")).FullName;
        private readonly FabricaFalsa _fabrica = new(comando => new RespuestaFalsa($"eco:{comando}\n"));
        private readonly string _configuracion;
        private readonly GestorSesiones _gestor;

        public SesionesConConfiguracionSshTests()
        {
            _configuracion = Path.Combine(_directorio, "config");
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana", ConfiguracionSsh = _configuracion };
            _gestor = new GestorSesiones(_fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance, new ResolutorConfiguracionSsh(opciones, NullLogger<ResolutorConfiguracionSsh>.Instance));
            Herramientas = new HerramientasSsh(_gestor, opciones);
        }

        private HerramientasSsh Herramientas { get; }

        public void Dispose()
        {
            _gestor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Directory.Delete(_directorio, recursive: true);
        }

        // Dos alias al mismo deploy@127.0.0.1:2222 con IdentityFile distintos, y un tercero sin IdentityFile.
        private void EscribirConfiguracion(string claveA = "/k/a", string servidorA = "127.0.0.1") =>
            File.WriteAllText(_configuracion,
                $"Host cuenta-a\n  HostName {servidorA}\n  User deploy\n  Port 2222\n  IdentityFile {claveA}\n" +
                "Host cuenta-b\n  HostName 127.0.0.1\n  User deploy\n  Port 2222\n  IdentityFile /k/b\n" +
                "Host web\n  HostName 127.0.0.1\n  User deploy\n  Port 2222\n");

        private async Task Ejecutar(params string[] destinos)
        {
            foreach (string destino in destinos)
            {
                Peticion peticion = _gestor.Ejecutar(destino, "a", TimeSpan.FromSeconds(10));
                await peticion.Finalizacion.WaitAsync(Limite);
                _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Completado, peticion.Aviso);
            }
        }

        // Un alias y su usuario@HostName:Port comparten sesión si resuelven a las mismas claves.
        [Fact]
        public async Task AliasYDestinoRealConLasMismasClaves_CompartenSesion()
        {
            // Arrange
            EscribirConfiguracion();

            // Act
            await Ejecutar("web", "deploy@127.0.0.1:2222");

            // Assert
            _ = _gestor.ListarSesiones().Should().ContainSingle().Which.Destino.Clave.Should().Be("deploy@127.0.0.1:2222");
            _ = _fabrica.Conexiones.Should().Be(1);
        }

        [Fact]
        public async Task AliasConOtroIdentityFile_TieneSuPropiaSesionConSusClaves()
        {
            // Arrange
            EscribirConfiguracion();

            // Act
            await Ejecutar("cuenta-a", "cuenta-b", "web", "cuenta-a");

            // Assert
            _ = _fabrica.Conexiones.Should().Be(3);
            _ = _gestor.ListarSesiones().Select(s => s.Destino.Identidades is null ? "por defecto" : string.Join(",", s.Destino.Identidades))
                .Should().BeEquivalentTo(["/k/a", "/k/b", "por defecto"]);
            _ = _gestor.ListarSesiones().Should().OnlyContain(s => s.Destino.Clave == "deploy@127.0.0.1:2222");
        }

        // Cambiar el IdentityFile de un alias con la sesión viva: la llamada siguiente usa una sesión nueva con las claves
        // nuevas; la anterior sigue abierta hasta ssh_disconnect.
        [Fact]
        public async Task CambioDeIdentityFileConSesionViva_LaLlamadaSiguienteUsaUnaSesionConLasClavesNuevas()
        {
            // Arrange
            EscribirConfiguracion();
            await Ejecutar("cuenta-a");
            EscribirConfiguracion(claveA: "/k/nueva");

            // Act
            Peticion peticion = _gestor.Ejecutar("cuenta-a", "b", TimeSpan.FromSeconds(10));
            await peticion.Finalizacion.WaitAsync(Limite);

            // Assert
            _ = peticion.Destino.Identidades.Should().Equal("/k/nueva");
            _ = _fabrica.Conexiones.Should().Be(2);
            _ = _gestor.ListarSesiones().Should().HaveCount(2);
        }

        // ssh_disconnect con un alias cierra solo la sesión de ese alias.
        [Fact]
        public async Task DesconectarUnAlias_CierraSoloLaSuya()
        {
            // Arrange
            EscribirConfiguracion();
            await Ejecutar("cuenta-a", "cuenta-b");

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync("cuenta-a");

            // Assert
            _ = resultado.IsHecho.Should().BeTrue();
            _ = resultado.Mensaje.Should().Be("Conexión cerrada.");
            _ = _gestor.ListarSesiones().Should().ContainSingle().Which.Destino.Identidades.Should().Equal("/k/b");
        }

        // ssh_disconnect con usuario@servidor:puerto cierra todas las sesiones de ese destino, sean de las claves que sean, y
        // dice cuántas.
        [Fact]
        public async Task DesconectarElDestinoReal_CierraTodasLasDeEseDestinoYDiceCuantas()
        {
            // Arrange
            EscribirConfiguracion();
            await Ejecutar("cuenta-a", "cuenta-b", "web");

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync("deploy@127.0.0.1:2222");

            // Assert
            _ = resultado.IsHecho.Should().BeTrue();
            _ = resultado.Mensaje.Should().StartWith("Se cerraron 3 conexiones con deploy@127.0.0.1:2222").And.Contain("/k/a").And.Contain("/k/b");
            _ = _gestor.ListarSesiones().Should().BeEmpty();
        }

        // Un alias cuya configuración cambió (otro HostName) con la sesión viva: ssh_disconnect con el alias cierra la sesión
        // que se abrió con él, en vez de responder que no había conexión.
        [Fact]
        public async Task DesconectarUnAliasCuyaConfiguracionCambio_CierraLaSesionQueAbrio()
        {
            // Arrange
            EscribirConfiguracion();
            await Ejecutar("cuenta-a");
            EscribirConfiguracion(servidorA: "localhost");

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync("cuenta-a");

            // Assert
            _ = resultado.IsHecho.Should().BeTrue();
            _ = _gestor.ListarSesiones().Should().BeEmpty();
        }

        // ssh_status distingue las sesiones al mismo destino por sus claves y su alias, y filtra por alias.
        [Fact]
        public async Task Estado_DistingueLasSesionesAlMismoDestinoYFiltraPorAlias()
        {
            // Arrange
            EscribirConfiguracion();
            await Ejecutar("cuenta-a", "cuenta-b");

            // Act
            ResultadoEstado todas = Herramientas.Estado();
            ResultadoEstado deB = Herramientas.Estado("cuenta-b");

            // Assert
            _ = todas.Sesiones.Should().HaveCount(2);
            _ = todas.Sesiones.Select(s => (s.Destino, s.Alias?.Single(), s.IdentityFile?.Single()))
                .Should().BeEquivalentTo([("deploy@127.0.0.1:2222", "cuenta-a", "/k/a"), ("deploy@127.0.0.1:2222", "cuenta-b", "/k/b")]);
            _ = deB.Sesiones.Should().ContainSingle().Which.IdentityFile.Should().Equal("/k/b");
            _ = deB.Peticiones.Should().ContainSingle();
        }
    }
}
