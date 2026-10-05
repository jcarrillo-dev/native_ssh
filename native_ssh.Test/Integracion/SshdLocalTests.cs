using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using FluentAssertions;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // La infraestructura de sshd de las pruebas, con instancias propias en puertos libres (no toca las de las colecciones).
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    public sealed class SshdLocalTests
    {
        private static int PuertoLibre()
        {
            using TcpListener sonda = new(IPAddress.Loopback, 0);
            sonda.Start();
            return ((IPEndPoint)sonda.LocalEndpoint).Port;
        }

        private static void BorrarEstado(string directorioEstado)
        {
            string ruta = Path.Combine(SshdLocal.BuscarDirectorioPruebas(), directorioEstado);
            if (Directory.Exists(ruta))
                Directory.Delete(ruta, recursive: true);
        }

        [Fact]
        public void Parar_ConElMaestroQueNoAtiendeTerm_EsperaASuFinYDejaElPuertoLibre()
        {
            // Arrange: detenido con SIGSTOP, el sshd no atiende el TERM de 'parar' (como uno que tarda en salir).
            int puerto = PuertoLibre();
            string estado = $".estado-parada-{Guid.NewGuid():N}";
            SshdLocal sshd = new(puerto.ToString(CultureInfo.InvariantCulture), estado, null);
            string pid = sshd.PidMaestro;

            try
            {
                _ = BashLocal.Ejecutar($"kill -STOP {pid}");

                // Act
                sshd.Dispose();
                string estadoProceso = BashLocal.Ejecutar($"ps -o stat= -p {pid} || true");
                using TcpListener nuevo = new(IPAddress.Loopback, puerto);
                Action escuchar = nuevo.Start;

                // Assert: ni vivo ni detenido (como mucho, pendiente de que su padre lo recoja), y el puerto libre.
                _ = estadoProceso.Should().Match(e => e.Length == 0 || e.StartsWith('Z'));
                _ = escuchar.Should().NotThrow();
            }
            finally
            {
                _ = BashLocal.Ejecutar($"kill -KILL {pid} 2>/dev/null || true");
                BorrarEstado(estado);
            }
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
