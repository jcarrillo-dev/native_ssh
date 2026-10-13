using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Ssh;

namespace NativeSsh.Test.Integracion
{
    // CI4 de punta a punta contra el sshd local: un alias de un fichero de configuración propio (nunca el ~/.ssh/config real)
    // lleva al puerto, al usuario y a la clave de su entrada; known_hosts se consulta con el HostName y el Port reales.
    [Collection(ColeccionSshd.Nombre)]
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [Trait("Categoria", "SoloLinux")]
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
    }
}
