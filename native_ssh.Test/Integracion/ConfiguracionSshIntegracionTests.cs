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
    // CI4 de punta a punta contra el sshd local: un alias de un fichero de configuración propio (nunca el ~/.ssh/config real)
    // lleva al puerto, al usuario y a la clave de su entrada; known_hosts se consulta con el HostName y el Port reales.
    [Collection(ColeccionSshd.Nombre)]
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    public sealed class ConfiguracionSshIntegracionTests(SshdLocal sshd) : IDisposable
    {
        private static readonly TimeSpan Limite = TimeSpan.FromSeconds(30);
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_cfg_int_{Guid.NewGuid():N}")).FullName;

        public void Dispose() => Directory.Delete(_directorio, recursive: true);

        private GestorSesiones Gestor(string configuracion)
        {
            string ruta = Path.Combine(_directorio, "config");
            File.WriteAllText(ruta, configuracion);
            OpcionesNativeSsh opciones = sshd.Opciones() with { RutasIdentidad = [], ConfiguracionSsh = ruta, UsuarioPorDefecto = "usuario-que-no-existe" };
            return new GestorSesiones(sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance, new ResolutorConfiguracionSsh(opciones, NullLogger<ResolutorConfiguracionSsh>.Instance));
        }

        private static string Texto(Peticion peticion)
        {
            byte[] destino = new byte[(int)peticion.Salida.Longitud];
            int copiados = peticion.Salida.Leer(0, destino).Copiados;
            return Encoding.UTF8.GetString(destino, 0, copiados);
        }

        [Fact]
        public async Task Alias_ConectaConElPuertoElUsuarioYLaClaveDeSuEntrada()
        {
            // Arrange
            await using GestorSesiones gestor = Gestor($"Host alias-prueba\n  HostName 127.0.0.1\n  Port {sshd.Puerto}\n  User {Environment.UserName}\n  IdentityFile {sshd.RutaClave}\n");

            // Act
            Peticion peticion = gestor.Ejecutar("alias-prueba", "echo hola-alias", TimeSpan.FromSeconds(15));
            await peticion.Finalizacion.WaitAsync(Limite);

            // Assert
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Completado, peticion.Aviso);
            _ = Texto(peticion).Trim().Should().Be("hola-alias");
            _ = gestor.ListarSesiones().Should().ContainSingle().Which.Destino.Clave.Should().Be($"{Environment.UserName}@127.0.0.1:{sshd.Puerto}");
        }

        // Sin el IdentityFile (y sin claves de native_ssh) no hay credenciales: la clave del alias venía de la configuración.
        [Fact]
        public async Task AliasSinIdentityFile_SinCredenciales()
        {
            // Arrange
            await using GestorSesiones gestor = Gestor($"Host alias-prueba\n  HostName 127.0.0.1\n  Port {sshd.Puerto}\n  User {Environment.UserName}\n");

            // Act
            Peticion peticion = gestor.Ejecutar("alias-prueba", "echo hola-alias", TimeSpan.FromSeconds(15));
            await peticion.Finalizacion.WaitAsync(Limite);

            // Assert
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = peticion.Aviso.Should().Contain("No hay credenciales");
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
            $"Host cuenta-a\n  HostName 127.0.0.1\n  Port {sshd.Puerto}\n  User {Environment.UserName}\n  IdentityFile {sshd.RutaClave}\n" +
            $"Host cuenta-x\n  HostName 127.0.0.1\n  Port {sshd.Puerto}\n  User {Environment.UserName}\n  IdentityFile {claveX}\n";

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
            string configuracion = $"Host cuenta-a\n  HostName 127.0.0.1\n  Port {sshd.Puerto}\n  User {Environment.UserName}\n  IdentityFile {{0}}\n";
            await using GestorSesiones gestor = Gestor(string.Format(System.Globalization.CultureInfo.InvariantCulture, configuracion, sshd.RutaClave));
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
