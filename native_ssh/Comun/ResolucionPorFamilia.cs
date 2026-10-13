using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace NativeSsh.Comun
{
    // Las direcciones de un nombre por familia: Ipv4 o Ipv6 vacío si el nombre no tiene direcciones de esa familia (NODATA:
    // un host solo IPv4 o solo IPv6); null si esa familia falló (el DNS dice que el nombre no existe, falla, rechaza la
    // consulta o no responde) o no terminó dentro del plazo.
    public readonly record struct DireccionesPorFamilia(IPAddress[]? Ipv4, IPAddress[]? Ipv6)
    {
        public bool HasFallo => Ipv4 is null || Ipv6 is null;
    }

    // Resolución de las entradas de NATIVE_SSH_SOLO_LECTURA por familia (A y AAAA por separado, a la vez) en todos los
    // sistemas, nunca las dos juntas: con AF_UNSPEC, si el DNS falla solo en una familia, getaddrinfo devuelve la otra como
    // si fuera todo y la dirección de la familia que falló no se compararía; preguntando cada familia, el fallo se ve. Lo
    // de cada sistema (cómo se pide una familia, si se distingue «sin direcciones de esa familia», dónde está el fichero
    // hosts) va en ResolucionPorFamilia.Linux.cs, .Windows.cs y .Unix.cs, y solo se compila el del sistema para el que se
    // compila (NativeSshSistema en Directory.Build.props); esta parte común no mira el sistema.
    public static partial class ResolucionPorFamilia
    {
        public static Task<DireccionesPorFamilia> ResolverAsync(string nombre, CancellationToken plazo) =>
            ResolverAsync(nombre, ResolverFamiliaDelSistema, plazo);

        // Las direcciones de 'nombre' de una sola familia en este sistema: vacío si no tiene direcciones de esa familia y el
        // sistema lo distingue; null o una excepción si esa familia falla. Bloquea el hilo.
        private static partial IPAddress[]? ResolverFamiliaDelSistema(string nombre, AddressFamily familia);

        // El fichero con el formato de /etc/hosts del sistema.
        public static partial string RutaHostsDelSistema { get; }

        // Solo las direcciones de la familia pedida (por si el sistema devuelve también la otra).
        internal static IPAddress[] DeLaFamilia(IPAddress[] direcciones, AddressFamily familia) =>
            Array.FindAll(direcciones, d => d.AddressFamily == familia);

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
