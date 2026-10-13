using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using NativeSsh.Comun;

namespace NativeSsh.Test.Comun
{
    // Lo de macOS y los demás Unix (ResolucionPorFamilia.Unix.cs; solo se compila para ellos). Con -p:NativeSshSistema=unix
    // se compilan y se pueden ejecutar también en Linux (DNS falso).
    public sealed class ResolucionPorFamiliaUnixTests
    {
        // DNS falso: A da 10.0.0.5 y AAAA lanza 'ipv6'.
        private static IPAddress[] Dns(AddressFamily familia, SocketError ipv6) =>
            familia == AddressFamily.InterNetworkV6 ? throw new SocketException((int)ipv6) : [IPAddress.Parse("10.0.0.5"), IPAddress.Parse("fd00::9")];

        // .NET junta «sin direcciones de esa familia» con «no existe»: cualquier error de una familia es un fallo, también
        // NoData (falla cerrado). Solo cuenta la familia pedida. Nunca se piden las dos juntas.
        [Theory]
        [InlineData(SocketError.NoData)]
        [InlineData(SocketError.HostNotFound)]
        [InlineData(SocketError.TryAgain)]
        public async Task CualquierErrorDeUnaFamilia_EsUnFallo(SocketError errorIpv6)
        {
            // Arrange
            List<AddressFamily> pedidas = [];
            IPAddress[]? Familia(string nombre, AddressFamily familia)
            {
                lock (pedidas)
                    pedidas.Add(familia);
                return ResolucionPorFamilia.ResolverFamiliaUnix(nombre, familia, (_, f) => Dns(f, errorIpv6));
            }

            // Act
            DireccionesPorFamilia resultado = await ResolucionPorFamilia.ResolverAsync("prod.ejemplo", Familia, CancellationToken.None);

            // Assert
            _ = resultado.Ipv4.Should().Equal(IPAddress.Parse("10.0.0.5"));
            _ = resultado.Ipv6.Should().BeNull();
            _ = pedidas.Should().BeEquivalentTo([AddressFamily.InterNetwork, AddressFamily.InterNetworkV6]);
        }

        [Fact]
        public void RutaHostsDelSistema_EsEtcHosts() =>
            _ = ResolucionPorFamilia.RutaHostsDelSistema.Should().Be("/etc/hosts");
    }
}
