using System.Net;
using System.Net.Sockets;

namespace NativeSsh.Comun
{
    // Lo de Windows de la resolución por familia (solo se compila para Windows: ver NativeSshSistema en
    // Directory.Build.props): cada familia con el DNS de .NET (Dns.GetHostAddresses(nombre, familia)). «Sin direcciones de
    // esa familia» es SocketError.NoData (WSANO_DATA, 11004, que .NET pasa tal cual: leído en su código, por medir en
    // Windows; si no llegara así, un host solo IPv4 o solo IPv6 fallaría cerrado). Cualquier otro error sale y hace fallar
    // esa familia. El fichero hosts está en %SystemRoot%\System32\drivers\etc.
    public static partial class ResolucionPorFamilia
    {
        private static partial IPAddress[]? ResolverFamiliaDelSistema(string nombre, AddressFamily familia) => ResolverFamiliaWindows(nombre, familia, Dns.GetHostAddresses);

        public static partial string RutaHostsDelSistema => RutaHostsWindows(Environment.GetFolderPath(Environment.SpecialFolder.System));

        // dns: el resolver de una familia (las pruebas dan el suyo).
        internal static IPAddress[] ResolverFamiliaWindows(string nombre, AddressFamily familia, Func<string, AddressFamily, IPAddress[]> dns)
        {
            try
            {
                return DeLaFamilia(dns(nombre, familia), familia);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.NoData)
            {
                return [];
            }
        }

        // carpetaSistema: %SystemRoot%\System32 (vacía si no se sabe: la de una instalación normal).
        internal static string RutaHostsWindows(string carpetaSistema) =>
            (carpetaSistema.Length > 0 ? carpetaSistema.TrimEnd('\\') : @"C:\Windows\System32") + @"\drivers\etc\hosts";
    }
}
