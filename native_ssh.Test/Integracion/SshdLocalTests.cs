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
        private static int PuertoLibre() => PuertosPruebas.Libre();

        private static void BorrarEstado(string directorioEstado)
        {
            string ruta = Path.Combine(SshdLocal.BuscarDirectorioPruebas(), directorioEstado);
            if (Directory.Exists(ruta))
                Directory.Delete(ruta, recursive: true);
        }

        [Fact]
        public void PuertoLibre_EsDelRangoManualDelBloqueDeLasPruebasYNoEsEfimero()
        {
            // Arrange: NATIVE_SSH_PUERTO_PRUEBAS (X022) fija el bloque de mil puertos de esta ejecución; los sshd manuales
            // usan X400–X999.
            string? variable = Environment.GetEnvironmentVariable("NATIVE_SSH_PUERTO_PRUEBAS");
            int bloque = (variable is null ? 22022 : int.Parse(variable, CultureInfo.InvariantCulture)) / 1000 * 1000;
            string[] efimero = File.ReadAllText("/proc/sys/net/ipv4/ip_local_port_range").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // Act
            int puerto = PuertoLibre();

            // Assert
            _ = puerto.Should().BeInRange(bloque + 400, bloque + 999);
            _ = puerto.Should().NotBeInRange(int.Parse(efimero[0], CultureInfo.InvariantCulture), int.Parse(efimero[1], CultureInfo.InvariantCulture));
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
            int libre = PuertoLibre();
            using TcpListener ocupante = new(IPAddress.Loopback, libre);
            ocupante.Start();
            string puerto = libre.ToString(CultureInfo.InvariantCulture);
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
