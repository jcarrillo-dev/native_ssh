using System.Net;
using System.Runtime.Versioning;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Ssh;

namespace NativeSsh.Test.Integracion
{
    // La dirección remota de una conexión real (IConexionSsh.DireccionRemota, que usa el modo solo lectura): la fábrica
    // resuelve el nombre, conecta a una dirección ya resuelta y prueba la siguiente si una no acepta la conexión, como hacía
    // SSH.NET con el nombre. El sshd local solo escucha en 127.0.0.1: 'localhost' resuelve antes a ::1 (si el sistema lo da
    // primero), que rechaza la conexión. Su log dice desde dónde se conectó (oráculo del extremo real).
    [Collection(ColeccionSshd.Nombre)]
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    public sealed class DireccionRemotaIntegracionTests(SshdLocal sshd) : IDisposable
    {
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_dir_rem_{Guid.NewGuid():N}")).FullName;

        public void Dispose() => Directory.Delete(_directorio, recursive: true);

        [Fact]
        public async Task ConexionAUnaIp_DaEsaDireccion()
        {
            // Arrange
            using FabricaConexionSsh fabrica = sshd.Fabrica(sshd.Opciones());

            // Act
            using IConexionSsh conexion = await fabrica.ConectarAsync(sshd.Destino, CancellationToken.None);

            // Assert
            _ = conexion.DireccionRemota.Should().Be(IPAddress.Loopback);
        }

        [Fact]
        public async Task ConexionAUnNombre_ConectaALaDireccionQueAceptaYLaDa()
        {
            // Arrange: known_hosts con la clave del sshd también para [localhost]:puerto.
            string knownHosts = Path.Combine(_directorio, "known_hosts");
            string clave = File.ReadAllLines(sshd.RutaKnownHosts)[0].Split(' ', 2)[1];
            File.WriteAllText(knownHosts, $"[localhost]:{sshd.Puerto} {clave}\n");
            using FabricaConexionSsh fabrica = sshd.Fabrica(sshd.Opciones(), knownHosts);
            _ = DestinoSsh.TryParse($"{Environment.UserName}@localhost:{sshd.Puerto}", "x", out DestinoSsh destino).Should().BeTrue();
            int aceptadasAntes = sshd.ContarLineasLog("Accepted publickey");
            int desdeLoopbackAntes = sshd.ContarLineasLog("from 127.0.0.1 port");
            IPAddress[] direcciones = await Dns.GetHostAddressesAsync("localhost");

            // Act
            using IConexionSsh conexion = await fabrica.ConectarAsync(destino, CancellationToken.None);

            // Assert
            _ = direcciones.Should().Contain(IPAddress.Loopback);
            _ = conexion.IsConectada.Should().BeTrue();
            _ = conexion.DireccionRemota.Should().Be(IPAddress.Loopback);
            _ = sshd.ContarLineasLog("Accepted publickey").Should().Be(aceptadasAntes + 1);
            _ = sshd.ContarLineasLog("from 127.0.0.1 port").Should().BeGreaterThan(desdeLoopbackAntes, "el sshd ve la conexión desde 127.0.0.1");
        }
    }
}
