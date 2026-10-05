using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using FluentAssertions;

namespace NativeSsh.Test.Integracion
{
    // La infraestructura de sshd de las pruebas, con instancias propias en puertos libres (no toca las de las colecciones).
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    public sealed class SshdLocalTests
    {
        private static void BorrarEstado(string directorioEstado)
        {
            string ruta = Path.Combine(SshdLocal.BuscarDirectorioPruebas(), directorioEstado);
            if (Directory.Exists(ruta))
                Directory.Delete(ruta, recursive: true);
        }

        [Fact]
        public void Restringido_SiSshdNoArranca_NoDejaSuGranjaDeBinarios()
        {
            // Arrange: el puerto ya está ocupado, así que sshd no puede escuchar en él.
            using TcpListener ocupante = new(IPAddress.Loopback, 0);
            ocupante.Start();
            string puerto = ((IPEndPoint)ocupante.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
            string granja = Path.Combine(Path.GetTempPath(), $"ns_login_{Guid.NewGuid():N}");
            string estado = $".estado-fallo-{Guid.NewGuid():N}";

            try
            {
                // Act
                Action arrancar = () => _ = new SshdLocalRestringido(puerto, estado, granja);

                // Assert
                _ = arrancar.Should().Throw<InvalidOperationException>();
                _ = Directory.Exists(granja).Should().BeFalse();
            }
            finally
            {
                if (Directory.Exists(granja))
                    Directory.Delete(granja, recursive: true);
                BorrarEstado(estado);
            }
        }
    }
}
