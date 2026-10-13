using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using NativeSsh.Comun;

namespace NativeSsh.Test.Comun
{
    // Lo de Windows (ResolucionPorFamilia.Windows.cs; solo se compila para Windows). Pensadas para ejecutarse en Windows; con
    // -p:NativeSshSistema=windows se compilan y, salvo RutaHostsDelSistema_EnLaCarpetaDelSistema, se pueden ejecutar también
    // en Linux (DNS falso).
    public sealed class ResolucionPorFamiliaWindowsTests
    {
        // DNS falso: A da 10.0.0.5 y AAAA lanza 'ipv6' (null: da fd00::5); apunta las familias pedidas.
        private static Func<string, AddressFamily, IPAddress[]> DnsFalso(SocketError? ipv6, List<AddressFamily>? pedidas = null) => (nombre, familia) =>
        {
            lock (pedidas ?? [])
                pedidas?.Add(familia);
            bool isIpv6 = familia == AddressFamily.InterNetworkV6;
            return isIpv6 && ipv6 is { } error ? throw new SocketException((int)error)
                : isIpv6 ? [IPAddress.Parse("fd00::5")]
                : [IPAddress.Parse("10.0.0.5")];
        };

        private static Task<DireccionesPorFamilia> ResolverAsync(Func<string, AddressFamily, IPAddress[]> dns) =>
            ResolucionPorFamilia.ResolverAsync("prod.ejemplo", (nombre, familia) => ResolucionPorFamilia.ResolverFamiliaWindows(nombre, familia, dns), CancellationToken.None);

        // B1 fuera de Linux (prefusión-quater): cada familia por separado (nunca Unspecified); un fallo de la AAAA es un fallo
        // de esa familia, no «sin direcciones».
        [Theory]
        [InlineData(SocketError.TryAgain)]
        [InlineData(SocketError.HostNotFound)]
        [InlineData(SocketError.NoRecovery)]
        public async Task UnaFamiliaQueFalla_EsUnFallo(SocketError errorIpv6)
        {
            // Arrange
            List<AddressFamily> pedidas = [];

            // Act
            DireccionesPorFamilia resultado = await ResolverAsync(DnsFalso(errorIpv6, pedidas));

            // Assert
            _ = resultado.Ipv4.Should().Equal(IPAddress.Parse("10.0.0.5"));
            _ = resultado.Ipv6.Should().BeNull();
            _ = pedidas.Should().BeEquivalentTo([AddressFamily.InterNetwork, AddressFamily.InterNetworkV6]);
        }

        // WSANO_DATA (SocketError.NoData): el nombre no tiene direcciones de esa familia, que es legítimo.
        [Fact]
        public async Task NoData_EsSinDirecciones()
        {
            // Act
            DireccionesPorFamilia sinIpv6 = await ResolverAsync(DnsFalso(SocketError.NoData));
            DireccionesPorFamilia lasDos = await ResolverAsync(DnsFalso(null));

            // Assert
            _ = sinIpv6.Ipv4.Should().Equal(IPAddress.Parse("10.0.0.5"));
            _ = sinIpv6.Ipv6.Should().NotBeNull().And.BeEmpty();
            _ = lasDos.Ipv6.Should().Equal(IPAddress.Parse("fd00::5"));
        }

        // Solo cuenta la familia pedida, y cualquier otra excepción sale (hace fallar la tarea de esa familia).
        [Fact]
        public void UnaFamilia_SoloEsaFamiliaYOtroErrorSale()
        {
            // Arrange
            static IPAddress[] Mezcla(string nombre, AddressFamily familia) => [IPAddress.Parse("10.0.0.5"), IPAddress.Parse("fd00::5")];
            static IPAddress[] Argumento(string nombre, AddressFamily familia) => throw new ArgumentException("prueba");

            // Act
            IPAddress[] ipv4 = ResolucionPorFamilia.ResolverFamiliaWindows("prod.ejemplo", AddressFamily.InterNetwork, Mezcla);
            IPAddress[] ipv6 = ResolucionPorFamilia.ResolverFamiliaWindows("prod.ejemplo", AddressFamily.InterNetworkV6, Mezcla);
            Action argumento = () => ResolucionPorFamilia.ResolverFamiliaWindows("prod.ejemplo", AddressFamily.InterNetwork, Argumento);

            // Assert
            _ = ipv4.Should().Equal(IPAddress.Parse("10.0.0.5"));
            _ = ipv6.Should().Equal(IPAddress.Parse("fd00::5"));
            _ = argumento.Should().Throw<ArgumentException>();
        }

        [Theory]
        [InlineData(@"C:\Windows\system32", @"C:\Windows\system32\drivers\etc\hosts")]
        [InlineData(@"D:\WINDOWS\System32\", @"D:\WINDOWS\System32\drivers\etc\hosts")]
        [InlineData("", @"C:\Windows\System32\drivers\etc\hosts")]
        public void RutaHostsWindows_EnLaCarpetaDelSistema(string carpetaSistema, string esperada) =>
            _ = ResolucionPorFamilia.RutaHostsWindows(carpetaSistema).Should().Be(esperada);

        // En Windows de verdad: el fichero hosts del sistema existe donde se dice.
        [Fact]
        public void RutaHostsDelSistema_EnLaCarpetaDelSistema()
        {
            // Act
            string ruta = ResolucionPorFamilia.RutaHostsDelSistema;

            // Assert
            _ = ruta.Should().EndWithEquivalentOf(@"\System32\drivers\etc\hosts");
            _ = File.Exists(ruta).Should().BeTrue();
        }

        // En Windows de verdad, con el DNS del sistema: una dirección literal de una familia, y localhost.
        [Fact]
        public async Task ConElSistema_LiteralYLocalhost()
        {
            // Act
            DireccionesPorFamilia literal = await ResolucionPorFamilia.ResolverAsync("127.0.0.1", CancellationToken.None);
            DireccionesPorFamilia localhost = await ResolucionPorFamilia.ResolverAsync("localhost", CancellationToken.None);

            // Assert
            _ = literal.Ipv4.Should().Equal(IPAddress.Loopback);
            _ = localhost.Ipv4.Should().NotBeNull().And.Contain(IPAddress.Loopback);
        }
    }
}
