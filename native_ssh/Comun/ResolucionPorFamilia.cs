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

    // Resolución de las entradas de NATIVE_SSH_SOLO_LECTURA por familia (A y AAAA por separado, a la vez). Con AF_UNSPEC,
    // si el DNS falla solo en una familia, getaddrinfo devuelve la otra como si fuera todo y la dirección de la familia que
    // falló no se compararía; preguntando cada familia, el fallo se ve. Solo en Linux se distingue «sin direcciones de esa
    // familia» (getaddrinfo propio: EAI_NODATA); en otro sistema se usa Dns.GetHostAddressesAsync y cualquier fallo es de
    // las dos familias.
    public static class ResolucionPorFamilia
    {
        public static Task<DireccionesPorFamilia> ResolverAsync(string nombre, CancellationToken plazo) =>
            OperatingSystem.IsLinux()
                ? ResolverAsync(nombre, ResolverFamiliaLinux, plazo)
                : ResolverSinFamiliasAsync(nombre, plazo);

        [SupportedOSPlatform("linux")]
        private static IPAddress[]? ResolverFamiliaLinux(string nombre, AddressFamily familia) => InteropLinux.ResolverFamilia(nombre, familia, out _);

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

        private static async Task<DireccionesPorFamilia> ResolverSinFamiliasAsync(string nombre, CancellationToken plazo)
        {
            try
            {
                return PorFamilia(await Dns.GetHostAddressesAsync(nombre, plazo).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException)
            {
                return default;
            }
        }

        // Una lista de direcciones de las dos familias, repartida (la familia sin ninguna, vacía).
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
