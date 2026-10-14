using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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

        // isSinDirecciones: lo que dice el DNS de Windows (DnsQuery_W) de una familia que Dns.GetHostAddresses no encontró
        // (por defecto, que no es «sin direcciones»); apunta las familias preguntadas.
        private static Task<DireccionesPorFamilia> ResolverAsync(Func<string, AddressFamily, IPAddress[]> dns, bool isSinDirecciones = false, List<AddressFamily>? preguntadas = null) =>
            ResolucionPorFamilia.ResolverAsync("prod.ejemplo", (nombre, familia) => ResolucionPorFamilia.ResolverFamiliaWindows(nombre, familia, dns, (_, f) =>
            {
                lock (preguntadas ?? [])
                    preguntadas?.Add(f);
                return isSinDirecciones;
            }), CancellationToken.None);

        // B1 fuera de Linux (prefusión-quater): cada familia por separado (nunca Unspecified); un fallo de la AAAA es un fallo
        // de esa familia, no «sin direcciones» (con HostNotFound, si el DNS de Windows no dice que lo sea).
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

        // WSANO_DATA (SocketError.NoData) o una respuesta sin ninguna dirección de la familia pedida: como con HostNotFound,
        // solo es «sin direcciones» si el DNS de Windows lo confirma (la consulta estándar y la solo TCP sin caché); si no,
        // esa familia falla (falla cerrado). Con direcciones de la familia, no se le pregunta.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task NoDataOSinDireccionesDeLaFamilia_SinDireccionesSoloSiLoConfirmaElDnsDeWindows(bool isSinDirecciones)
        {
            // Arrange
            static IPAddress[] SoloIpv4(string nombre, AddressFamily familia) => [IPAddress.Parse("10.0.0.5")];
            static IPAddress[] Ninguna(string nombre, AddressFamily familia) => familia == AddressFamily.InterNetwork ? [IPAddress.Parse("10.0.0.5")] : [];
            List<AddressFamily> preguntadas = [];

            // Act
            DireccionesPorFamilia noData = await ResolverAsync(DnsFalso(SocketError.NoData), isSinDirecciones, preguntadas);
            DireccionesPorFamilia otraFamilia = await ResolverAsync(SoloIpv4, isSinDirecciones, preguntadas);
            DireccionesPorFamilia vacia = await ResolverAsync(Ninguna, isSinDirecciones, preguntadas);
            DireccionesPorFamilia lasDos = await ResolverAsync(DnsFalso(null), isSinDirecciones, preguntadas);

            // Assert
            foreach (DireccionesPorFamilia resultado in new[] { noData, otraFamilia, vacia })
            {
                _ = resultado.Ipv4.Should().Equal(IPAddress.Parse("10.0.0.5"));
                _ = (resultado.Ipv6 is []).Should().Be(isSinDirecciones);
                _ = (resultado.Ipv6 is null).Should().Be(!isSinDirecciones);
            }

            _ = lasDos.Ipv6.Should().Equal(IPAddress.Parse("fd00::5"));
            _ = preguntadas.Should().Equal(AddressFamily.InterNetworkV6, AddressFamily.InterNetworkV6, AddressFamily.InterNetworkV6);
        }

        // Solo cuenta la familia pedida, y cualquier otra excepción sale (hace fallar la tarea de esa familia).
        [Fact]
        public void UnaFamilia_SoloEsaFamiliaYOtroErrorSale()
        {
            // Arrange
            static IPAddress[] Mezcla(string nombre, AddressFamily familia) => [IPAddress.Parse("10.0.0.5"), IPAddress.Parse("fd00::5")];
            static IPAddress[] Argumento(string nombre, AddressFamily familia) => throw new ArgumentException("prueba");

            // Act
            IPAddress[]? ipv4 = ResolucionPorFamilia.ResolverFamiliaWindows("prod.ejemplo", AddressFamily.InterNetwork, Mezcla, (_, _) => true);
            IPAddress[]? ipv6 = ResolucionPorFamilia.ResolverFamiliaWindows("prod.ejemplo", AddressFamily.InterNetworkV6, Mezcla, (_, _) => true);
            Action argumento = () => ResolucionPorFamilia.ResolverFamiliaWindows("prod.ejemplo", AddressFamily.InterNetwork, Argumento, (_, _) => true);

            // Assert
            _ = ipv4.Should().Equal(IPAddress.Parse("10.0.0.5"));
            _ = ipv6.Should().Equal(IPAddress.Parse("fd00::5"));
            _ = argumento.Should().Throw<ArgumentException>();
        }

        // W1: en Windows, NODATA llega de Dns.GetHostAddresses como HostNotFound, igual que un nombre que no existe: solo
        // entonces se pregunta al DNS de Windows (por esa familia), y si dice «sin direcciones», lo es; con otro error, no se
        // le pregunta.
        [Theory]
        [InlineData(SocketError.HostNotFound, true, true)]
        [InlineData(SocketError.HostNotFound, false, true)]
        [InlineData(SocketError.TryAgain, true, false)]
        [InlineData(SocketError.NoRecovery, true, false)]
        public async Task HostNotFound_SinDireccionesSoloSiLoDiceElDnsDeWindows(SocketError errorIpv6, bool isSinDirecciones, bool isPreguntada)
        {
            // Arrange
            List<AddressFamily> preguntadas = [];

            // Act
            DireccionesPorFamilia resultado = await ResolverAsync(DnsFalso(errorIpv6), isSinDirecciones, preguntadas);

            // Assert
            _ = resultado.Ipv4.Should().Equal(IPAddress.Parse("10.0.0.5"));
            bool isVacia = isSinDirecciones && isPreguntada;
            _ = (resultado.Ipv6 is []).Should().Be(isVacia);
            _ = (resultado.Ipv6 is null).Should().Be(!isVacia);
            _ = preguntadas.Should().Equal(isPreguntada ? [AddressFamily.InterNetworkV6] : []);
        }

        // Una lista de DNS_RECORD falsa en memoria no gestionada (cada nodo con su tipo), y su liberación.
        private sealed class ListaFalsa : IDisposable
        {
            private readonly List<IntPtr> _nodos = [];

            public IntPtr Inicio { get; }

            public int Liberaciones { get; private set; }

            public ListaFalsa(params ushort[] tipos)
            {
                IntPtr siguiente = IntPtr.Zero;
                for (int i = tipos.Length - 1; i >= 0; i--)
                {
                    IntPtr nodo = Marshal.AllocHGlobal(Marshal.SizeOf<DnsWindows.RegistroDns>());
                    Marshal.StructureToPtr(new DnsWindows.RegistroDns { Siguiente = siguiente, Nombre = IntPtr.Zero, Tipo = tipos[i] }, nodo, false);
                    _nodos.Add(nodo);
                    siguiente = nodo;
                }

                Inicio = siguiente;
            }

            public void Liberar(IntPtr lista)
            {
                _ = lista.Should().Be(Inicio);
                Liberaciones++;
            }

            public void Dispose()
            {
                foreach (IntPtr nodo in _nodos)
                    Marshal.FreeHGlobal(nodo);
            }
        }

        private const ushort TipoCname = 5;

        // Lo que DnsQuery_W dice de una familia (las dos consultas dan lo mismo): 9501 (DNS_INFO_NO_RECORDS) o una respuesta
        // correcta sin registros de ese tipo (un CNAME a un nombre de la otra familia) es «sin direcciones»; 9003 (no existe),
        // 9002 (fallo del servidor), una respuesta correcta sin lista o con registros de ese tipo, no. Pregunta el tipo de la
        // familia, primero con la consulta estándar y, solo si esa dice «sin direcciones», otra vez solo por TCP y sin caché;
        // libera cada lista una vez siempre que la hay.
        [Theory]
        [InlineData(9501, new ushort[0], AddressFamily.InterNetworkV6, true)]
        [InlineData(9501, new ushort[] { 6 }, AddressFamily.InterNetwork, true)]
        [InlineData(0, new ushort[] { TipoCname }, AddressFamily.InterNetwork, true)]
        [InlineData(0, new ushort[] { TipoCname, 28 }, AddressFamily.InterNetwork, true)]
        [InlineData(0, new ushort[] { TipoCname, 1 }, AddressFamily.InterNetwork, false)]
        [InlineData(0, new ushort[] { 28 }, AddressFamily.InterNetworkV6, false)]
        [InlineData(0, new ushort[0], AddressFamily.InterNetwork, false)]
        [InlineData(9003, new ushort[0], AddressFamily.InterNetworkV6, false)]
        [InlineData(9003, new ushort[] { 6 }, AddressFamily.InterNetworkV6, false)]
        [InlineData(9002, new ushort[0], AddressFamily.InterNetwork, false)]
        public void DnsDeWindows_SinDireccionesSoloConNoDataOSinRegistrosDelTipo(int codigo, ushort[] tipos, AddressFamily familia, bool isEsperado)
        {
            // Arrange
            using ListaFalsa lista = new(tipos);
            List<(ushort Tipo, uint Opciones)> pedidos = [];
            int Consultar(string nombre, ushort tipo, uint opciones, out IntPtr resultado)
            {
                pedidos.Add((tipo, opciones));
                resultado = lista.Inicio;
                return codigo;
            }

            // Act
            bool isSinDirecciones = DnsWindows.IsSinDirecciones("prod.ejemplo", familia, Consultar, lista.Liberar);

            // Assert
            _ = isSinDirecciones.Should().Be(isEsperado);
            ushort tipoEsperado = familia == AddressFamily.InterNetworkV6 ? DnsWindows.TipoAaaa : DnsWindows.TipoA;
            _ = pedidos.Should().Equal(isEsperado
                ? [(tipoEsperado, DnsWindows.ConsultaEstandar), (tipoEsperado, DnsWindows.ConsultaSoloTcpSinCache)]
                : [(tipoEsperado, DnsWindows.ConsultaEstandar)]);
            _ = lista.Liberaciones.Should().Be(tipos.Length > 0 ? pedidos.Count : 0);
        }

        // WB1: la consulta estándar dice «sin direcciones» (9501), pero eso también lo da una respuesta truncada cuyo
        // reintento por TCP falla: solo cuenta si la confirmación solo por TCP y sin caché (DNS_QUERY_USE_TCP_ONLY |
        // DNS_QUERY_BYPASS_CACHE) también lo dice (9501, o una respuesta sin registros de ese tipo). Lo medido en Windows 11
        // con el TCP/53 cerrado (10057), que cierra sin responder (9503) o que no responde (1460), y cualquier otro código o
        // registros de ese tipo, falla cerrado.
        [Theory]
        [InlineData(9501, new ushort[0], true)]
        [InlineData(0, new ushort[] { TipoCname }, true)]
        [InlineData(10057, new ushort[0], false)]
        [InlineData(9503, new ushort[0], false)]
        [InlineData(1460, new ushort[0], false)]
        [InlineData(9002, new ushort[0], false)]
        [InlineData(9003, new ushort[0], false)]
        [InlineData(0, new ushort[] { 28 }, false)]
        [InlineData(0, new ushort[0], false)]
        public void DnsDeWindows_SinDireccionesSoloSiLaConsultaPorTcpSinCacheLoConfirma(int codigoTcp, ushort[] tiposTcp, bool isEsperado)
        {
            // Arrange
            using ListaFalsa listaTcp = new(tiposTcp);
            List<uint> opcionesPedidas = [];
            int Consultar(string nombre, ushort tipo, uint opciones, out IntPtr resultado)
            {
                opcionesPedidas.Add(opciones);
                bool isTcp = opciones == DnsWindows.ConsultaSoloTcpSinCache;
                resultado = isTcp ? listaTcp.Inicio : IntPtr.Zero;
                return isTcp ? codigoTcp : DnsWindows.SinRegistros;
            }

            // Act
            bool isSinDirecciones = DnsWindows.IsSinDirecciones("prod.ejemplo", AddressFamily.InterNetworkV6, Consultar, listaTcp.Liberar);

            // Assert
            _ = isSinDirecciones.Should().Be(isEsperado);
            _ = opcionesPedidas.Should().Equal(DnsWindows.ConsultaEstandar, DnsWindows.ConsultaSoloTcpSinCache);
            _ = listaTcp.Liberaciones.Should().Be(tiposTcp.Length > 0 ? 1 : 0);
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

        // En Windows de verdad, con el DNS del sistema (necesita salida al DNS): ipv4only.arpa (RFC 7050) solo tiene A, así
        // que su AAAA es NODATA y cuenta como «sin direcciones IPv6», no como un fallo; un nombre que no existe (.invalid,
        // RFC 6761) falla en las dos familias.
        [Fact]
        public async Task ConElSistema_NombreSoloIpv4EsSinIpv6YUnoQueNoExisteFalla()
        {
            // Act
            DireccionesPorFamilia soloIpv4 = await ResolucionPorFamilia.ResolverAsync("ipv4only.arpa", CancellationToken.None);
            DireccionesPorFamilia noExiste = await ResolucionPorFamilia.ResolverAsync("no-existe.native-ssh.invalid", CancellationToken.None);

            // Assert
            _ = soloIpv4.Ipv4.Should().NotBeNullOrEmpty();
            _ = soloIpv4.Ipv6.Should().NotBeNull().And.BeEmpty();
            _ = noExiste.Ipv4.Should().BeNull();
            _ = noExiste.Ipv6.Should().BeNull();
        }
    }
}
