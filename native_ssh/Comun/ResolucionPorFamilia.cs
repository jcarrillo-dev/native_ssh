using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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

        // Los blancos que separan los campos de una línea de hosts en glibc (isspace de C).
        private static readonly SearchValues<char> Blancos = SearchValues.Create(" \t\v\f\r\n");
        private static readonly SearchValues<char> CaracteresIpv6 = SearchValues.Create("0123456789abcdefABCDEF:.");

        // Las direcciones de 'nombre' (ya normalizado: minúsculas, sin el punto final) en un fichero con el formato de
        // /etc/hosts ('IP nombre [alias...]'); vacío si no está o el fichero no se puede leer. Se lee como glibc (las líneas
        // que glibc no usaría para el nombre no cuentan, porque lo que da es la tolerancia de estar en /etc/hosts): las
        // líneas acaban solo en '\n' y pueden ser de cualquier longitud; '#' o un NUL acaban lo que se lee de la línea; los
        // campos se separan con cualquier blanco de C (también \v, \f y \r); la dirección, solo como la acepta inet_pton
        // (EsDireccionEstricta); el nombre, entero (sin quitarle el punto final) y sin distinguir mayúsculas solo en ASCII.
        public static List<IPAddress> DireccionesEnHosts(string ruta, string nombre)
        {
            List<IPAddress> direcciones = [];
            string contenido;
            try
            {
                contenido = File.ReadAllText(ruta);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }

            ReadOnlySpan<char> resto = contenido;
            while (!resto.IsEmpty)
            {
                int finLinea = resto.IndexOf('\n');
                ReadOnlySpan<char> linea = finLinea < 0 ? resto : resto[..finLinea];
                resto = finLinea < 0 ? [] : resto[(finLinea + 1)..];
                int finUtil = linea.IndexOfAny('#', '\0');
                if (finUtil >= 0)
                    linea = linea[..finUtil];

                if (!TrySiguienteCampo(ref linea, out ReadOnlySpan<char> campo) || !TryDireccionEstricta(campo, out IPAddress? direccion))
                    continue;

                while (TrySiguienteCampo(ref linea, out campo))
                    if (IsIgualSinMayusculasAscii(campo, nombre))
                    {
                        direcciones.Add(direccion);
                        break;
                    }
            }

            return direcciones;
        }

        // El siguiente campo de la línea (sin blancos) y lo que queda después; false si no queda ninguno.
        private static bool TrySiguienteCampo(ref ReadOnlySpan<char> linea, out ReadOnlySpan<char> campo)
        {
            int inicio = linea.IndexOfAnyExcept(Blancos);
            if (inicio < 0)
            {
                campo = [];
                linea = [];
                return false;
            }

            linea = linea[inicio..];
            int fin = linea.IndexOfAny(Blancos);
            campo = fin < 0 ? linea : linea[..fin];
            linea = fin < 0 ? [] : linea[fin..];
            return true;
        }

        // Como strcasecmp en la configuración regional C: solo las letras ASCII se igualan sin distinguir mayúsculas.
        private static bool IsIgualSinMayusculasAscii(ReadOnlySpan<char> campo, string nombre)
        {
            if (campo.Length != nombre.Length)
                return false;

            for (int i = 0; i < campo.Length; i++)
                if (campo[i] != nombre[i] && !(char.IsAsciiLetter(campo[i]) && (campo[i] | 0x20) == (nombre[i] | 0x20)))
                    return false;
            return true;
        }

        // Una dirección como la acepta inet_pton de glibc: IPv4 con exactamente cuatro números decimales de 0 a 255 sin ceros
        // a la izquierda (no '10.1', ni hexadecimal, ni '010'); IPv6 solo con dígitos hexadecimales, ':' y, al final, una IPv4
        // así (no zona '%', ni corchetes, ni prefijo '/').
        internal static bool TryDireccionEstricta(ReadOnlySpan<char> texto, [NotNullWhen(true)] out IPAddress? direccion)
        {
            direccion = null;
            bool isIpv6 = texto.Contains(':');
            if (!isIpv6)
                return IsIpv4Estricta(texto) && IPAddress.TryParse(texto, out direccion);

            int ultimosDosPuntos = texto.LastIndexOf(':');
            bool isIpv4Final = texto[ultimosDosPuntos..].Contains('.');
            bool isValida = !texto.ContainsAnyExcept(CaracteresIpv6) && (!isIpv4Final || IsIpv4Estricta(texto[(ultimosDosPuntos + 1)..]));
            return isValida && IPAddress.TryParse(texto, out direccion) && direccion.AddressFamily == AddressFamily.InterNetworkV6;
        }

        private static bool IsIpv4Estricta(ReadOnlySpan<char> texto)
        {
            int numeros = 0;
            foreach (Range parte in texto.Split('.'))
            {
                ReadOnlySpan<char> numero = texto[parte];
                bool isNumero = numero.Length is > 0 and <= 3 && !numero.ContainsAnyExceptInRange('0', '9') && (numero.Length == 1 || numero[0] != '0');
                if (!isNumero || int.Parse(numero, CultureInfo.InvariantCulture) > 255)
                    return false;
                numeros++;
            }

            return numeros == 4;
        }
    }
}
