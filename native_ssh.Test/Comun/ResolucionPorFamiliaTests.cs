using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using NativeSsh.Comun;

namespace NativeSsh.Test.Comun
{
    // Resolución de las entradas de NATIVE_SSH_SOLO_LECTURA por familia (A y AAAA por separado).
    public sealed class ResolucionPorFamiliaTests
    {
        // Las dos familias a la vez: vacío (sin direcciones de esa familia) y null (fallo) pasan tal cual; una excepción es
        // un fallo de esa familia.
        [Fact]
        public async Task CadaFamiliaPorSuLado()
        {
            // Arrange
            static IPAddress[]? SoloIpv4(string nombre, AddressFamily familia) => familia == AddressFamily.InterNetwork ? [IPAddress.Parse("10.0.0.5")] : [];
            static IPAddress[]? FallaIpv6(string nombre, AddressFamily familia) => familia == AddressFamily.InterNetwork ? [IPAddress.Parse("10.0.0.5")] : null;
            static IPAddress[]? LanzaIpv4(string nombre, AddressFamily familia) => familia == AddressFamily.InterNetwork ? throw new InvalidOperationException("prueba") : [IPAddress.Parse("fd00::5")];

            // Act
            DireccionesPorFamilia soloIpv4 = await ResolucionPorFamilia.ResolverAsync("prod.ejemplo", SoloIpv4, CancellationToken.None);
            DireccionesPorFamilia fallaIpv6 = await ResolucionPorFamilia.ResolverAsync("prod.ejemplo", FallaIpv6, CancellationToken.None);
            DireccionesPorFamilia lanzaIpv4 = await ResolucionPorFamilia.ResolverAsync("prod.ejemplo", LanzaIpv4, CancellationToken.None);

            // Assert
            _ = soloIpv4.Ipv4.Should().Equal(IPAddress.Parse("10.0.0.5"));
            _ = soloIpv4.Ipv6.Should().BeEmpty();
            _ = soloIpv4.HasFallo.Should().BeFalse();
            _ = fallaIpv6.Ipv6.Should().BeNull();
            _ = fallaIpv6.HasFallo.Should().BeTrue();
            _ = lanzaIpv4.Ipv4.Should().BeNull();
            _ = lanzaIpv4.Ipv6.Should().Equal(IPAddress.Parse("fd00::5"));
        }

        // El plazo cubre las dos consultas: la familia que no termina (getaddrinfo no se puede cancelar) cuenta como fallida
        // al vencer y la otra se devuelve; no se espera a la colgada.
        [Fact]
        public async Task FamiliaColgada_FallaAlVencerElPlazoYLaOtraSeDevuelve()
        {
            // Arrange
            using ManualResetEventSlim soltar = new(false);
            IPAddress[]? Ipv6Colgada(string nombre, AddressFamily familia)
            {
                if (familia == AddressFamily.InterNetworkV6)
                    _ = soltar.Wait(TimeSpan.FromSeconds(30));
                return [familia == AddressFamily.InterNetwork ? IPAddress.Parse("10.0.0.5") : IPAddress.Parse("fd00::5")];
            }

            using CancellationTokenSource plazo = new(TimeSpan.FromMilliseconds(300));
            Stopwatch reloj = Stopwatch.StartNew();

            // Act
            DireccionesPorFamilia resultado = await ResolucionPorFamilia.ResolverAsync("prod.ejemplo", Ipv6Colgada, plazo.Token);
            TimeSpan tardado = reloj.Elapsed;
            soltar.Set();

            // Assert
            _ = resultado.Ipv4.Should().Equal(IPAddress.Parse("10.0.0.5"));
            _ = resultado.Ipv6.Should().BeNull();
            _ = tardado.Should().BeLessThan(TimeSpan.FromSeconds(5));
        }

        // getaddrinfo de verdad, por familia: localhost tiene 127.0.0.1 (de /etc/hosts o del módulo de NSS de la máquina).
        [Fact]
        public async Task Getaddrinfo_LocalhostPorFamilia()
        {
            // Arrange
            using CancellationTokenSource plazo = new(TimeSpan.FromSeconds(10));

            // Act
            DireccionesPorFamilia resultado = await ResolucionPorFamilia.ResolverAsync("localhost", plazo.Token);

            // Assert
            _ = resultado.Ipv4.Should().NotBeNull().And.Contain(IPAddress.Loopback);
            _ = resultado.Ipv4.Should().OnlyContain(d => d.AddressFamily == AddressFamily.InterNetwork);
            _ = (resultado.Ipv6 ?? []).Should().OnlyContain(d => d.AddressFamily == AddressFamily.InterNetworkV6);
        }

        [Fact]
        public async Task DireccionesEnHosts_FormatoDeEtcHosts()
        {
            // Arrange
            string ruta = Path.GetTempFileName();
            await File.WriteAllTextAsync(ruta, "127.0.0.1 localhost\n10.0.0.5\tprod  PROD.ejemplo. # máquina protegida\n# 10.0.0.6 prod.ejemplo\nfd00::5 prod.ejemplo\nno-es-ip prod.ejemplo\n10.0.0.7 prod.ejemplo.com\n10.0.0.9 otro # prod.ejemplo\n");

            try
            {
                // Act
                List<IPAddress> direcciones = ResolucionPorFamilia.DireccionesEnHosts(ruta, "prod.ejemplo");
                List<IPAddress> ninguna = ResolucionPorFamilia.DireccionesEnHosts(ruta, "otro.ejemplo");
                List<IPAddress> sinFichero = ResolucionPorFamilia.DireccionesEnHosts(ruta + ".no-existe", "prod.ejemplo");

                // Assert
                _ = direcciones.Should().Equal(IPAddress.Parse("10.0.0.5"), IPAddress.Parse("fd00::5"));
                _ = ninguna.Should().BeEmpty();
                _ = sinFichero.Should().BeEmpty();
            }
            finally
            {
                File.Delete(ruta);
            }
        }
    }
}
