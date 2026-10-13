using System.Net;
using System.Net.Sockets;

namespace NativeSsh.Comun
{
    // Lo de macOS y los demás Unix de la resolución por familia (solo se compila para ellos: ver NativeSshSistema en
    // Directory.Build.props): cada familia con el DNS de .NET (Dns.GetHostAddresses(nombre, familia)). .NET junta «sin
    // direcciones de esa familia» (EAI_NODATA) con «el nombre no existe», así que no se distingue: cualquier error sale y
    // hace fallar esa familia (falla cerrado; un host del DNS solo IPv4 o solo IPv6 bloquea salvo que esté en /etc/hosts).
    public static partial class ResolucionPorFamilia
    {
        private static partial IPAddress[]? ResolverFamiliaDelSistema(string nombre, AddressFamily familia) => ResolverFamiliaUnix(nombre, familia, Dns.GetHostAddresses);

        public static partial string RutaHostsDelSistema => "/etc/hosts";

        // dns: el resolver de una familia (las pruebas dan el suyo).
        internal static IPAddress[] ResolverFamiliaUnix(string nombre, AddressFamily familia, Func<string, AddressFamily, IPAddress[]> dns) =>
            DeLaFamilia(dns(nombre, familia), familia);
    }
}
