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

        // El DNS de un sistema que no es Linux, falso: A da 10.0.0.5 y AAAA lanza 'ipv6' (null: da fd00::5). Pedir las dos
        // familias juntas (Unspecified) da lo que daría AF_UNSPEC con la AAAA fallida: solo la A, como si fuera todo.
        private static Func<string, AddressFamily, IPAddress[]> DnsFalso(SocketError? ipv6, List<AddressFamily>? pedidas = null) => (nombre, familia) =>
        {
            lock (pedidas ?? [])
                pedidas?.Add(familia);
            bool isIpv6 = familia == AddressFamily.InterNetworkV6;
            return isIpv6 && ipv6 is { } error ? throw new SocketException((int)error)
                : isIpv6 ? [IPAddress.Parse("fd00::5")]
                : [IPAddress.Parse("10.0.0.5")];
        };

        // B1 fuera de Linux (prefusión-quater): Windows, macOS y los demás Unix también resuelven por familia (nunca las dos
        // juntas), así que un fallo de la AAAA (o de la A) es un fallo de esa familia y no «sin direcciones».
        [Theory]
        [InlineData(SistemaNoLinuxEnum.Windows, SocketError.TryAgain)]
        [InlineData(SistemaNoLinuxEnum.Windows, SocketError.HostNotFound)]
        [InlineData(SistemaNoLinuxEnum.Windows, SocketError.NoRecovery)]
        [InlineData(SistemaNoLinuxEnum.Otro, SocketError.TryAgain)]
        [InlineData(SistemaNoLinuxEnum.Otro, SocketError.HostNotFound)]
        public async Task FueraDeLinux_PorFamilia_UnaFamiliaQueFallaEsUnFallo(SistemaNoLinuxEnum sistema, SocketError errorIpv6)
        {
            // Arrange
            List<AddressFamily> pedidas = [];

            // Act
            DireccionesPorFamilia resultado = await ResolucionPorFamilia.ResolverAsync("prod.ejemplo", sistema, DnsFalso(errorIpv6, pedidas), CancellationToken.None);

            // Assert
            _ = resultado.Ipv4.Should().Equal(IPAddress.Parse("10.0.0.5"));
            _ = resultado.Ipv6.Should().BeNull();
            _ = resultado.HasFallo.Should().BeTrue();
            _ = pedidas.Should().BeEquivalentTo([AddressFamily.InterNetwork, AddressFamily.InterNetworkV6]);
        }

        // «Sin direcciones de esa familia» (WSANO_DATA, que .NET da como SocketError.NoData) solo vale en Windows; en macOS y
        // los demás Unix .NET lo junta con «no existe», así que falla cerrado. Sin error, cada familia con lo suyo.
        [Theory]
        [InlineData(SistemaNoLinuxEnum.Windows, false)]
        [InlineData(SistemaNoLinuxEnum.Otro, true)]
        public async Task FueraDeLinux_NoDataSoloValeEnWindows(SistemaNoLinuxEnum sistema, bool isFallo)
        {
            // Act
            DireccionesPorFamilia sinIpv6 = await ResolucionPorFamilia.ResolverAsync("prod.ejemplo", sistema, DnsFalso(SocketError.NoData), CancellationToken.None);
            DireccionesPorFamilia lasDos = await ResolucionPorFamilia.ResolverAsync("prod.ejemplo", sistema, DnsFalso(null), CancellationToken.None);

            // Assert
            _ = sinIpv6.Ipv4.Should().Equal(IPAddress.Parse("10.0.0.5"));
            _ = sinIpv6.HasFallo.Should().Be(isFallo);
            _ = (sinIpv6.Ipv6 is null).Should().Be(isFallo);
            _ = (sinIpv6.Ipv6 ?? []).Should().BeEmpty();
            _ = lasDos.Ipv4.Should().Equal(IPAddress.Parse("10.0.0.5"));
            _ = lasDos.Ipv6.Should().Equal(IPAddress.Parse("fd00::5"));
        }

        // De lo que dé el sistema para una familia solo cuenta esa familia (por si devuelve también la otra), y cualquier
        // otra excepción es un fallo de la familia.
        [Theory]
        [InlineData(SistemaNoLinuxEnum.Windows)]
        [InlineData(SistemaNoLinuxEnum.Otro)]
        public void FueraDeLinux_UnaFamilia_SoloEsaFamiliaYCualquierOtroErrorEsFallo(SistemaNoLinuxEnum sistema)
        {
            // Arrange
            bool isWindows = sistema == SistemaNoLinuxEnum.Windows;
            static IPAddress[] Mezcla(string nombre, AddressFamily familia) => [IPAddress.Parse("10.0.0.5"), IPAddress.Parse("fd00::5")];
            static IPAddress[] Argumento(string nombre, AddressFamily familia) => throw new ArgumentException("prueba");

            // Act
            IPAddress[]? ipv4 = ResolucionPorFamilia.ResolverFamiliaPorDns("prod.ejemplo", AddressFamily.InterNetwork, isWindows, Mezcla);
            IPAddress[]? ipv6 = ResolucionPorFamilia.ResolverFamiliaPorDns("prod.ejemplo", AddressFamily.InterNetworkV6, isWindows, Mezcla);
            Action argumento = () => ResolucionPorFamilia.ResolverFamiliaPorDns("prod.ejemplo", AddressFamily.InterNetwork, isWindows, Argumento);

            // Assert
            _ = ipv4.Should().Equal(IPAddress.Parse("10.0.0.5"));
            _ = ipv6.Should().Equal(IPAddress.Parse("fd00::5"));
            _ = argumento.Should().Throw<ArgumentException>("una excepción hace fallar la tarea de esa familia, que cuenta como fallida (CadaFamiliaPorSuLado)");
        }

        // El fichero hosts de Windows está en %SystemRoot%\System32\drivers\etc (también si no se sabe la carpeta del sistema:
        // la de una instalación normal); en Linux, /etc/hosts.
        [Theory]
        [InlineData(@"C:\Windows\system32", @"C:\Windows\system32\drivers\etc\hosts")]
        [InlineData(@"D:\WINDOWS\System32\", @"D:\WINDOWS\System32\drivers\etc\hosts")]
        [InlineData("", @"C:\Windows\System32\drivers\etc\hosts")]
        public void RutaHostsWindows_EnLaCarpetaDelSistema(string carpetaSistema, string esperada) =>
            _ = ResolucionPorFamilia.RutaHostsWindows(carpetaSistema).Should().Be(esperada);

        [Fact]
        public void RutaHostsDelSistema_EnLinuxEsEtcHosts() =>
            _ = ResolucionPorFamilia.RutaHostsDelSistema.Should().Be(OperatingSystem.IsWindows() ? ResolucionPorFamilia.RutaHostsWindows(Environment.GetFolderPath(Environment.SpecialFolder.System)) : "/etc/hosts");

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
