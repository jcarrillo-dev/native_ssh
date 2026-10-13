using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NativeSsh.Comun
{
    // Lo de Windows de la resolución por familia (solo se compila para Windows: ver NativeSshSistema en
    // Directory.Build.props): cada familia con el DNS de .NET (Dns.GetHostAddresses(nombre, familia)). En Windows, un nombre
    // que existe y no tiene direcciones de esa familia (NODATA) llega igual que uno que no existe (SocketError.HostNotFound,
    // 11001; medido en Windows 11 con ipv4only.arpa, que solo tiene A), así que, con HostNotFound, esa familia se pregunta a
    // la API de DNS de Windows (DnsWindows: DnsQuery_W, la del servicio Cliente DNS, con su caché y el fichero hosts, y
    // después, para confirmarlo, solo por TCP y sin caché), y solo si dice que el nombre existe y no tiene direcciones de
    // esa familia cuenta como «sin direcciones»; si no, esa familia falla (falla cerrado). SocketError.NoData (WSANO_DATA)
    // también es «sin direcciones». Cualquier otro error sale y hace fallar esa familia. El fichero hosts está en
    // %SystemRoot%\System32\drivers\etc.
    public static partial class ResolucionPorFamilia
    {
        private static partial IPAddress[]? ResolverFamiliaDelSistema(string nombre, AddressFamily familia) =>
            ResolverFamiliaWindows(nombre, familia, Dns.GetHostAddresses, DnsWindows.IsSinDirecciones);

        public static partial string RutaHostsDelSistema => RutaHostsWindows(Environment.GetFolderPath(Environment.SpecialFolder.System));

        // dns: el resolver de una familia; isSinDirecciones: si el DNS de Windows dice que el nombre existe y no tiene
        // direcciones de esa familia (las pruebas dan los suyos).
        internal static IPAddress[] ResolverFamiliaWindows(string nombre, AddressFamily familia, Func<string, AddressFamily, IPAddress[]> dns, Func<string, AddressFamily, bool> isSinDirecciones)
        {
            try
            {
                return DeLaFamilia(dns(nombre, familia), familia);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.NoData)
            {
                return [];
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound)
            {
                if (isSinDirecciones(nombre, familia))
                    return [];
                throw;
            }
        }

        // carpetaSistema: %SystemRoot%\System32 (vacía si no se sabe: la de una instalación normal).
        internal static string RutaHostsWindows(string carpetaSistema) =>
            (carpetaSistema.Length > 0 ? carpetaSistema.TrimEnd('\\') : @"C:\Windows\System32") + @"\drivers\etc\hosts";
    }

    // DnsQuery_W de dnsapi.dll para una familia (LibraryImport, compatible con AOT): primero con las opciones por defecto
    // (DNS_QUERY_STANDARD: la caché del Cliente DNS, el fichero hosts y los servidores, como el resto del sistema) y, si
    // esa dice «sin direcciones», otra vez solo por TCP y sin la caché, para confirmarlo.
    internal static partial class DnsWindows
    {
        public const int Correcto = 0; // ERROR_SUCCESS
        public const int SinRegistros = 9501; // DNS_INFO_NO_RECORDS: el nombre existe y no tiene registros de ese tipo
        public const ushort TipoA = 1; // DNS_TYPE_A
        public const ushort TipoAaaa = 28; // DNS_TYPE_AAAA
        public const uint ConsultaEstandar = 0; // DNS_QUERY_STANDARD
        public const uint ConsultaSoloTcpSinCache = 0x2 | 0x8; // DNS_QUERY_USE_TCP_ONLY | DNS_QUERY_BYPASS_CACHE
        private const int ListaDeRegistros = 1; // DnsFreeRecordList

        // El principio de DNS_RECORD (windns.h), lo único que se lee: el siguiente de la lista, el nombre y el tipo.
        [StructLayout(LayoutKind.Sequential)]
        internal struct RegistroDns
        {
            public IntPtr Siguiente;
            public IntPtr Nombre;
            public ushort Tipo;
        }

        // DnsQuery_W de un tipo con unas opciones: su código y la lista de registros (IntPtr.Zero si no hay).
        internal delegate int Consultar(string nombre, ushort tipo, uint opciones, out IntPtr lista);

        public static bool IsSinDirecciones(string nombre, AddressFamily familia) => IsSinDirecciones(nombre, familia, ConsultarConDnsApi, LiberarConDnsApi);

        // Si el DNS de Windows dice que 'nombre' existe y no tiene direcciones de esa familia (A para InterNetwork, AAAA para
        // InterNetworkV6), primero con la consulta estándar y después, para confirmarlo, solo por TCP y sin la caché: las dos
        // tienen que decirlo. La estándar no basta: Windows pregunta por UDP sin EDNS, y si la respuesta no cabe en 512 bytes
        // el servidor la manda truncada (TC=1, sin registros) y Windows la repite por TCP; si el TCP falla (puerto cerrado,
        // cierre sin respuesta o sin respuesta), DnsQuery_W da DNS_INFO_NO_RECORDS con la lista vacía, igual que un nombre
        // sin registros de ese tipo (medido en Windows 11), y la caché lo recuerda. Por TCP y sin caché, en cambio, se
        // distingue (medido: NODATA de verdad 9501; TCP cerrado 10057, cierre sin respuesta 9503, sin respuesta 1460). Si el
        // TCP/53 está bloqueado, un nombre de una sola familia falla cerrado (no se puede confirmar). Los nombres del fichero
        // hosts no dependen de esto: los lee DestinosSoloLectura con su propio lector. Con DnsQuery_W y DnsFree dados (las
        // pruebas dan los suyos): cada lista se libera siempre que se obtuvo.
        internal static bool IsSinDirecciones(string nombre, AddressFamily familia, Consultar consultar, Action<IntPtr> liberar) =>
            IsSinRegistros(nombre, familia, ConsultaEstandar, consultar, liberar) && IsSinRegistros(nombre, familia, ConsultaSoloTcpSinCache, consultar, liberar);

        // Si una consulta con 'opciones' dice que el nombre existe y no tiene registros de ese tipo: DNS_INFO_NO_RECORDS
        // (9501; medido con ipv4only.arpa y con un nombre del fichero hosts que solo tiene la otra familia), o una respuesta
        // correcta sin ningún registro de ese tipo (medido con ipv6.google.com, un CNAME a un nombre solo IPv6: la respuesta
        // de A trae solo el CNAME). Cualquier otro código (9003, el nombre no existe; 9002, el servidor falló; sin
        // servidor...), una respuesta correcta sin lista o una con algún registro de ese tipo (que Dns.GetHostAddresses no
        // dio: no cuadra) es false.
        private static unsafe bool IsSinRegistros(string nombre, AddressFamily familia, uint opciones, Consultar consultar, Action<IntPtr> liberar)
        {
            ushort tipo = familia == AddressFamily.InterNetworkV6 ? TipoAaaa : TipoA;
            int codigo = consultar(nombre, tipo, opciones, out IntPtr lista);
            try
            {
                if (codigo == SinRegistros)
                    return true;
                if (codigo != Correcto || lista == IntPtr.Zero)
                    return false;

                for (RegistroDns* registro = (RegistroDns*)lista; registro != null; registro = (RegistroDns*)registro->Siguiente)
                    if (registro->Tipo == tipo)
                        return false;
                return true;
            }
            finally
            {
                if (lista != IntPtr.Zero)
                    liberar(lista);
            }
        }

        private static int ConsultarConDnsApi(string nombre, ushort tipo, uint opciones, out IntPtr lista) =>
            DnsQuery(nombre, tipo, opciones, IntPtr.Zero, out lista, IntPtr.Zero);

        private static void LiberarConDnsApi(IntPtr lista) => DnsFree(lista, ListaDeRegistros);

        [LibraryImport("dnsapi.dll", EntryPoint = "DnsQuery_W", StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int DnsQuery(string nombre, ushort tipo, uint opciones, IntPtr extra, out IntPtr resultados, IntPtr reservado);

        [LibraryImport("dnsapi.dll", EntryPoint = "DnsFree")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial void DnsFree(IntPtr datos, int tipoLiberacion);
    }
}
