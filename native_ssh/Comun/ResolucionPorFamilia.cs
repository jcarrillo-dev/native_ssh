using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace NativeSsh.Comun
{
    // Las direcciones de un nombre por familia: Ipv4 o Ipv6 vacío si el nombre no tiene direcciones de esa familia (NODATA:
    // un host solo IPv4 o solo IPv6); null si esa familia falló (el DNS dice que el nombre no existe, falla, rechaza la
    // consulta o no responde) o no terminó dentro del plazo.
    public readonly record struct DireccionesPorFamilia(IPAddress[]? Ipv4, IPAddress[]? Ipv6)
    {
        public bool HasFallo => Ipv4 is null || Ipv6 is null;
    }

    // Fuera de Linux: Windows distingue «sin direcciones de esa familia» (WSANO_DATA); macOS y los demás Unix no.
    public enum SistemaNoLinuxEnum
    {
        Windows,
        Otro
    }

    // Resolución de las entradas de NATIVE_SSH_SOLO_LECTURA por familia (A y AAAA por separado, a la vez) en todos los
    // sistemas, nunca las dos juntas: con AF_UNSPEC, si el DNS falla solo en una familia, getaddrinfo devuelve la otra como
    // si fuera todo y la dirección de la familia que falló no se compararía; preguntando cada familia, el fallo se ve.
    // «Sin direcciones de esa familia» (un host solo IPv4 o solo IPv6) se distingue en Linux con getaddrinfo propio
    // (EAI_NODATA) y en Windows con SocketError.NoData (WSANO_DATA, que .NET pasa tal cual); en macOS y los demás Unix .NET lo
    // junta con «no existe», así que ahí cualquier fallo de una familia es un fallo (falla cerrado).
    public static class ResolucionPorFamilia
    {
        public static Task<DireccionesPorFamilia> ResolverAsync(string nombre, CancellationToken plazo) =>
            OperatingSystem.IsLinux()
                ? ResolverAsync(nombre, ResolverFamiliaLinux, plazo)
                : ResolverAsync(nombre, OperatingSystem.IsWindows() ? SistemaNoLinuxEnum.Windows : SistemaNoLinuxEnum.Otro, Dns.GetHostAddresses, plazo);

        [SupportedOSPlatform("linux")]
        private static IPAddress[]? ResolverFamiliaLinux(string nombre, AddressFamily familia) => InteropLinux.ResolverFamilia(nombre, familia, out _);

        // Fuera de Linux, por familia con el DNS de .NET ('dns': Dns.GetHostAddresses(nombre, familia)).
        internal static Task<DireccionesPorFamilia> ResolverAsync(string nombre, SistemaNoLinuxEnum sistema, Func<string, AddressFamily, IPAddress[]> dns, CancellationToken plazo)
        {
            bool isNoDataSinDirecciones = sistema == SistemaNoLinuxEnum.Windows;
            return ResolverAsync(nombre, (n, familia) => ResolverFamiliaPorDns(n, familia, isNoDataSinDirecciones, dns), plazo);
        }

        // Una familia con el DNS de .NET: solo las direcciones de esa familia; vacío si el sistema dice que el nombre no tiene
        // direcciones de esa familia (SocketError.NoData) y eso se distingue (isNoDataSinDirecciones: Windows). Cualquier otra
        // excepción sale y hace fallar esa familia.
        internal static IPAddress[] ResolverFamiliaPorDns(string nombre, AddressFamily familia, bool isNoDataSinDirecciones, Func<string, AddressFamily, IPAddress[]> dns)
        {
            try
            {
                return Array.FindAll(dns(nombre, familia), d => d.AddressFamily == familia);
            }
            catch (SocketException ex) when (isNoDataSinDirecciones && ex.SocketErrorCode == SocketError.NoData)
            {
                return [];
            }
        }

        // El fichero hosts del sistema: en Windows, %SystemRoot%\System32\drivers\etc\hosts; en los demás, /etc/hosts.
        public static string RutaHostsDelSistema =>
            OperatingSystem.IsWindows() ? RutaHostsWindows(Environment.GetFolderPath(Environment.SpecialFolder.System)) : "/etc/hosts";

        // carpetaSistema: %SystemRoot%\System32 (vacía si no se sabe: la de una instalación normal).
        internal static string RutaHostsWindows(string carpetaSistema) =>
            (carpetaSistema.Length > 0 ? carpetaSistema.TrimEnd('\\') : @"C:\Windows\System32") + @"\drivers\etc\hosts";

        // Las dos familias a la vez, cada una en un hilo del grupo (getaddrinfo bloquea y no se puede cancelar); cuando vence
        // 'plazo' se deja de esperar y la familia que no terminó cuenta como fallida (su hilo acaba cuando acabe getaddrinfo).
        // resolverFamilia: las direcciones de una familia, vacío si no tiene, null si falla.
        public static async Task<DireccionesPorFamilia> ResolverAsync(string nombre, Func<string, AddressFamily, IPAddress[]?> resolverFamilia, CancellationToken plazo)
        {
            Task<IPAddress[]?> ipv4 = Task.Run(() => resolverFamilia(nombre, AddressFamily.InterNetwork), CancellationToken.None);
            Task<IPAddress[]?> ipv6 = Task.Run(() => resolverFamilia(nombre, AddressFamily.InterNetworkV6), CancellationToken.None);
            await ((Task)Task.WhenAll(ipv4, ipv6)).WaitAsync(plazo).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return new DireccionesPorFamilia(Resultado(ipv4), Resultado(ipv6));
        }

        private static IPAddress[]? Resultado(Task<IPAddress[]?> familia) => familia.IsCompletedSuccessfully ? familia.Result : null;

        // Una lista de direcciones de las dos familias, repartida (la familia sin ninguna, vacía). Solo para un resolver de
        // las dos familias juntas (el de las pruebas): no distingue una familia que falló de una sin direcciones.
        public static DireccionesPorFamilia PorFamilia(IPAddress[] direcciones) =>
            new(Array.FindAll(direcciones, d => d.AddressFamily == AddressFamily.InterNetwork), Array.FindAll(direcciones, d => d.AddressFamily == AddressFamily.InterNetworkV6));

        // Las direcciones de 'nombre' (ya normalizado: minúsculas, sin el punto final) en un fichero con el formato de
        // /etc/hosts ('IP nombre [alias...]', '#' empieza un comentario); vacío si no está o el fichero no se puede leer.
        public static List<IPAddress> DireccionesEnHosts(string ruta, string nombre)
        {
            List<IPAddress> direcciones = [];
            try
            {
                foreach (string linea in File.ReadLines(ruta))
                {
                    int comentario = linea.IndexOf('#', StringComparison.Ordinal);
                    string[] campos = (comentario < 0 ? linea : linea[..comentario]).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (campos.Length < 2 || !IPAddress.TryParse(campos[0], out IPAddress? direccion))
                        continue;

                    for (int i = 1; i < campos.Length; i++)
                        if (campos[i].TrimEnd('.').Equals(nombre, StringComparison.OrdinalIgnoreCase))
                        {
                            direcciones.Add(direccion);
                            break;
                        }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }

            return direcciones;
        }
    }
}
