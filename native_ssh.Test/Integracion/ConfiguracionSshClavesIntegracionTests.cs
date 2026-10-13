using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Ssh;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Dos alias de un fichero de configuración propio al mismo usuario@127.0.0.1:puerto con IdentityFile distintos: cada uno
    // se autentica con su clave (sesiones distintas). Estas pruebas fallan la autenticación a propósito: con un sshd propio
    // por prueba, las penalizaciones por origen de OpenSSH (PerSourcePenalties: authfail) no alcanzan al sshd compartido.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class ConfiguracionSshClavesIntegracionTests : IDisposable
    {
        private static readonly TimeSpan Limite = TimeSpan.FromSeconds(30);
        private readonly SshdPropio _sshd = new(string.Empty);
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_cfg_claves_{Guid.NewGuid():N}")).FullName;

        public void Dispose()
        {
            _sshd.Dispose();
            Directory.Delete(_directorio, recursive: true);
        }

        private GestorSesiones Gestor(string configuracion)
        {
            string ruta = Path.Combine(_directorio, "config");
            File.WriteAllText(ruta, configuracion);
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones() with { RutasIdentidad = [], ConfiguracionSsh = ruta, UsuarioPorDefecto = "usuario-que-no-existe" };
            return new GestorSesiones(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance, new ResolutorConfiguracionSsh(opciones, NullLogger<ResolutorConfiguracionSsh>.Instance));
        }

        private static string Texto(Peticion peticion)
        {
            byte[] destino = new byte[(int)peticion.Salida.Longitud];
            int copiados = peticion.Salida.Leer(0, destino).Copiados;
            return Encoding.UTF8.GetString(destino, 0, copiados);
        }

        // Clave nueva que el sshd NO tiene en authorized_keys.
        private string ClaveNoAutorizada()
        {
            string ruta = Path.Combine(_directorio, "no_autorizada");
            if (!File.Exists(ruta))
                _ = BashLocal.Ejecutar($"ssh-keygen -q -t ed25519 -N '' -f '{ruta}' -C no-autorizada");
            return ruta;
        }

        // Dos alias al mismo usuario@127.0.0.1:puerto: cuenta-a con la clave autorizada y cuenta-x con otra.
        private string DosAlias(string claveX) =>
            $"Host cuenta-a\n  HostName 127.0.0.1\n  Port {_sshd.Sshd.Puerto}\n  User {Environment.UserName}\n  IdentityFile {_sshd.Sshd.RutaClave}\n" +
            $"Host cuenta-x\n  HostName 127.0.0.1\n  Port {_sshd.Sshd.Puerto}\n  User {Environment.UserName}\n  IdentityFile {claveX}\n";

        private static async Task<Peticion> Ejecutar(GestorSesiones gestor, string destino, string comando)
        {
            Peticion peticion = gestor.Ejecutar(destino, comando, TimeSpan.FromSeconds(15));
            await peticion.Finalizacion.WaitAsync(Limite);
            return peticion;
        }

        // Un alias cuya clave no está autorizada no ejecuta nada con la sesión que abrió otro alias al mismo destino.
        [Fact]
        public async Task AliasConClaveNoAutorizada_NoUsaLaSesionDeOtroAlias()
        {
            // Arrange
            await using GestorSesiones gestor = Gestor(DosAlias(ClaveNoAutorizada()));
            Peticion buena = await Ejecutar(gestor, "cuenta-a", "echo hola-a");
            _ = buena.Estado.Should().Be(EstadoPeticionEnum.Completado, buena.Aviso);

            // Act
            Peticion mala = await Ejecutar(gestor, "cuenta-x", "echo ejecutado-como-x");

            // Assert
            _ = mala.Estado.Should().Be(EstadoPeticionEnum.Error, $"la clave de cuenta-x no está autorizada, pero salió: {Texto(mala)}");
            _ = mala.Aviso.Should().Contain("Autenticación rechazada");
        }

        // El alias bueno conecta con su clave aunque antes fallara otro alias al mismo destino con una clave mala.
        [Fact]
        public async Task AliasBuenoTrasUnoMalo_ConectaConSuClave()
        {
            // Arrange
            await using GestorSesiones gestor = Gestor(DosAlias(ClaveNoAutorizada()));
            Peticion mala = await Ejecutar(gestor, "cuenta-x", "echo x");
            _ = mala.Estado.Should().Be(EstadoPeticionEnum.Error);
            await Task.Delay(TimeSpan.FromSeconds(3)); // espera del limitador tras un fallo

            // Act
            Peticion buena = await Ejecutar(gestor, "cuenta-a", "echo hola-a");

            // Assert
            _ = buena.Estado.Should().Be(EstadoPeticionEnum.Completado, buena.Aviso);
            _ = Texto(buena).Trim().Should().Be("hola-a");
        }

        // Cambiar el IdentityFile de un alias con la sesión viva vale desde la llamada siguiente: una clave no autorizada ya
        // no sigue entrando con la sesión de la clave anterior.
        [Fact]
        public async Task CambioDeIdentityFileConSesionViva_LaLlamadaSiguienteUsaLaClaveNueva()
        {
            // Arrange
            string configuracion = $"Host cuenta-a\n  HostName 127.0.0.1\n  Port {_sshd.Sshd.Puerto}\n  User {Environment.UserName}\n  IdentityFile {{0}}\n";
            await using GestorSesiones gestor = Gestor(string.Format(System.Globalization.CultureInfo.InvariantCulture, configuracion, _sshd.Sshd.RutaClave));
            Peticion antes = await Ejecutar(gestor, "cuenta-a", "echo antes");
            _ = antes.Estado.Should().Be(EstadoPeticionEnum.Completado, antes.Aviso);
            File.WriteAllText(Path.Combine(_directorio, "config"), string.Format(System.Globalization.CultureInfo.InvariantCulture, configuracion, ClaveNoAutorizada()));

            // Act
            Peticion despues = await Ejecutar(gestor, "cuenta-a", "echo sigue");

            // Assert
            _ = despues.Estado.Should().Be(EstadoPeticionEnum.Error, $"la clave nueva no está autorizada, pero salió: {Texto(despues)}");
        }
    }
}
