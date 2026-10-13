using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using NativeSsh.Comun;

namespace NativeSsh.Test.Comun
{
    // getaddrinfo propio de una familia (InteropLinux.ResolverFamilia): qué hace con cada código, cómo lee la lista de struct
    // addrinfo y que la libera. Con getaddrinfo y freeaddrinfo falsos (una lista construida en memoria nativa) y con los de
    // libc para direcciones literales, que no pasan por el DNS ni por NSS.
    [SupportedOSPlatform("linux")]
    public sealed class InteropLinuxGetAddrInfoTests
    {
        private const int EaiNoName = -2;
        private const int EaiAgain = -3;
        private const int EaiFail = -4;
        private const int EaiAddrFamily = -9;
        private const int EaiSystem = -11;

        // La lista de struct addrinfo en memoria nativa: un nodo por dirección, con su sockaddr; en los bytes que no son la
        // dirección (puerto, flowinfo, scope id) va basura, para que leer en otro desplazamiento se note.
        private sealed class ListaFalsa : IDisposable
        {
            private readonly List<IntPtr> _bloques = [];

            public IntPtr Cabeza { get; }

            public ListaFalsa(params IPAddress[] direcciones)
            {
                IntPtr siguiente = IntPtr.Zero;
                for (int i = direcciones.Length - 1; i >= 0; i--)
                    siguiente = Nodo(direcciones[i], siguiente);
                Cabeza = siguiente;
            }

            private IntPtr Nodo(IPAddress direccion, IntPtr siguiente)
            {
                bool isIpv6 = direccion.AddressFamily == AddressFamily.InterNetworkV6;
                int longitud = isIpv6 ? 28 : 16;
                IntPtr sockaddr = Reservar(longitud);
                for (int i = 0; i < longitud; i++)
                    Marshal.WriteByte(sockaddr, i, 0xAB);
                Marshal.WriteInt16(sockaddr, 0, (short)(isIpv6 ? InteropLinux.FamiliaIpv6 : InteropLinux.FamiliaIpv4));
                byte[] bytes = direccion.GetAddressBytes();
                Marshal.Copy(bytes, 0, sockaddr + (isIpv6 ? 8 : 4), bytes.Length);

                InteropLinux.InfoDireccion info = new()
                {
                    Familia = isIpv6 ? InteropLinux.FamiliaIpv6 : InteropLinux.FamiliaIpv4,
                    TipoSocket = 1,
                    LongitudDireccion = (uint)longitud,
                    Direccion = sockaddr,
                    Siguiente = siguiente
                };
                IntPtr nodo = Reservar(Marshal.SizeOf<InteropLinux.InfoDireccion>());
                Marshal.StructureToPtr(info, nodo, fDeleteOld: false);
                return nodo;
            }

            private IntPtr Reservar(int bytes)
            {
                IntPtr bloque = Marshal.AllocHGlobal(bytes);
                _bloques.Add(bloque);
                return bloque;
            }

            public void Dispose()
            {
                foreach (IntPtr bloque in _bloques)
                    Marshal.FreeHGlobal(bloque);
            }
        }

        // EAI_NODATA: el nombre no tiene direcciones de esa familia (un host solo IPv4 o solo IPv6), que es legítimo: vacío.
        // Cualquier otro código es un fallo de la familia (null), también que el nombre no exista. Sin lista no hay nada que
        // liberar.
        [Theory]
        [InlineData(InteropLinux.SinDireccionesDeLaFamilia, false)]
        [InlineData(EaiNoName, true)]
        [InlineData(EaiAgain, true)]
        [InlineData(EaiFail, true)]
        [InlineData(EaiAddrFamily, true)]
        [InlineData(EaiSystem, true)]
        public void CodigoDeError_NodataEsVacioYLoDemasFallo(int codigoGetAddrInfo, bool isFallo)
        {
            // Arrange
            int liberadas = 0;
            int Obtener(string nombre, int familia, out IntPtr lista)
            {
                lista = IntPtr.Zero;
                return codigoGetAddrInfo;
            }

            // Act
            IPAddress[]? resultado = InteropLinux.ResolverFamilia("prod.ejemplo", AddressFamily.InterNetworkV6, Obtener, _ => liberadas++, out int codigo);

            // Assert
            _ = codigo.Should().Be(codigoGetAddrInfo);
            _ = (resultado is null).Should().Be(isFallo);
            _ = (resultado ?? []).Should().BeEmpty();
            _ = liberadas.Should().Be(0);
        }

        // Con éxito: cada dirección de la lista una vez (IPv4 en el byte 4 de sockaddr_in, IPv6 en el byte 8 de sockaddr_in6),
        // la familia pedida a getaddrinfo es la de la consulta, y la lista se libera una vez, la misma que se obtuvo.
        [Theory]
        [InlineData(AddressFamily.InterNetwork, InteropLinux.FamiliaIpv4, new[] { "10.0.0.5", "10.0.0.6", "10.0.0.5" }, new[] { "10.0.0.5", "10.0.0.6" })]
        [InlineData(AddressFamily.InterNetworkV6, InteropLinux.FamiliaIpv6, new[] { "fd00::5", "2001:db8::1:2:3", "fd00::5" }, new[] { "fd00::5", "2001:db8::1:2:3" })]
        public void ConExito_LeeLaListaYLaLibera(AddressFamily familia, int familiaPedida, string[] lista, string[] esperadas)
        {
            // Arrange
            using ListaFalsa falsa = new(Array.ConvertAll(lista, IPAddress.Parse));
            List<int> familias = [];
            List<IntPtr> liberadas = [];
            int Obtener(string nombre, int f, out IntPtr cabeza)
            {
                familias.Add(f);
                cabeza = falsa.Cabeza;
                return 0;
            }

            // Act
            IPAddress[]? resultado = InteropLinux.ResolverFamilia("prod.ejemplo", familia, Obtener, liberadas.Add, out int codigo);

            // Assert
            _ = codigo.Should().Be(0);
            _ = resultado.Should().Equal(Array.ConvertAll(esperadas, IPAddress.Parse));
            _ = familias.Should().Equal(familiaPedida);
            _ = liberadas.Should().Equal(falsa.Cabeza);
        }

        // Con libc: una dirección literal se convierte sin DNS ni NSS. La IPv6 sale entera (lee sockaddr_in6 donde toca) y una
        // literal de la otra familia es un fallo (EAI_ADDRFAMILY), no «sin direcciones».
        [Theory]
        [InlineData("::1", AddressFamily.InterNetworkV6, "::1")]
        [InlineData("2001:db8::1:2:3", AddressFamily.InterNetworkV6, "2001:db8::1:2:3")]
        [InlineData("127.0.0.1", AddressFamily.InterNetwork, "127.0.0.1")]
        [InlineData("10.0.0.5", AddressFamily.InterNetwork, "10.0.0.5")]
        public void ConLibc_LiteralDeSuFamilia(string nombre, AddressFamily familia, string esperada)
        {
            // Act
            IPAddress[]? resultado = InteropLinux.ResolverFamilia(nombre, familia, out int codigo);

            // Assert
            _ = codigo.Should().Be(0);
            _ = resultado.Should().Equal(IPAddress.Parse(esperada));
        }

        [Theory]
        [InlineData("127.0.0.1", AddressFamily.InterNetworkV6)]
        [InlineData("::1", AddressFamily.InterNetwork)]
        public void ConLibc_LiteralDeLaOtraFamilia_EsFallo(string nombre, AddressFamily familia)
        {
            // Act
            IPAddress[]? resultado = InteropLinux.ResolverFamilia(nombre, familia, out int codigo);

            // Assert
            _ = resultado.Should().BeNull();
            _ = codigo.Should().Be(EaiAddrFamily);
        }
    }
}
