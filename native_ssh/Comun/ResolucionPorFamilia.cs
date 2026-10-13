using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

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

        // Los blancos que separan los campos de una línea sencilla del fichero hosts.
        private static readonly SearchValues<byte> Blancos = SearchValues.Create(" \t"u8);
        private static readonly SearchValues<byte> ImprimiblesYTabulador = SearchValues.Create(CrearImprimiblesYTabulador());
        private static readonly SearchValues<char> CaracteresIpv6 = SearchValues.Create("0123456789abcdefABCDEF:.");

        // Lo más largo que puede medir una dirección escrita (IPv6 con una IPv4 al final: 45).
        private const int LongitudMaximaDireccion = 64;

        // Las direcciones de 'nombre' (ya normalizado: minúsculas, sin el punto final) en un fichero con el formato de
        // /etc/hosts ('IP nombre [alias...]'); vacío si no está, si el fichero no se puede leer o si no se puede estar seguro
        // de que la máquina use para el nombre solo esas direcciones. Lo que da es la tolerancia de estar en /etc/hosts, así
        // que solo cuentan las líneas sencillas e inequívocas (más estricto que glibc, que usa algunas que no lo son): el
        // fichero se lee como bytes, sin quitar una marca de orden de bytes ni decodificarlo; si empieza por una (UTF-8,
        // UTF-16 o UTF-32) o tiene un NUL, ninguna línea cuenta. Una línea es sencilla si acaba en '\n' (puede llevar '\r'
        // justo antes), es solo ASCII imprimible (más tabuladores), empieza por la dirección sin sangría, como la acepta
        // inet_pton (TryDireccionEstricta), y separa los campos con espacios o tabuladores; '#' empieza un comentario. El
        // nombre se compara entero (sin quitarle el punto final) y sin distinguir mayúsculas solo en ASCII. Si una línea que
        // no es sencilla contiene el nombre antes del comentario (aunque sea dentro de otro, como 'prod.ejemplo.com'), no
        // cuenta ninguna: la máquina podría usar también su dirección y native_ssh no la compararía.
        public static List<IPAddress> DireccionesEnHosts(string ruta, string nombre)
        {
            byte[] contenido;
            try
            {
                contenido = File.ReadAllBytes(ruta);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }

            ReadOnlySpan<byte> resto = contenido;
            if (!Ascii.IsValid(nombre) || nombre.Length == 0 || IsConMarcaDeOrden(resto) || resto.Contains((byte)0))
                return [];

            List<IPAddress> direcciones = [];
            while (!resto.IsEmpty)
            {
                int finLinea = resto.IndexOf((byte)'\n');
                bool isAcabada = finLinea >= 0;
                ReadOnlySpan<byte> linea = isAcabada ? resto[..finLinea] : resto;
                resto = isAcabada ? resto[(finLinea + 1)..] : [];
                if (isAcabada && linea.EndsWith("\r"u8))
                    linea = linea[..^1];

                int comentario = linea.IndexOf((byte)'#');
                ReadOnlySpan<byte> util = comentario < 0 ? linea : linea[..comentario];
                if (!IsContieneSinMayusculasAscii(util, nombre))
                    continue;

                if (!isAcabada || !IsLineaSencilla(linea) || !TryLeerLinea(util, nombre, out IPAddress? direccion))
                    return [];

                if (direccion is not null)
                    direcciones.Add(direccion);
            }

            return direcciones;
        }

        // UTF-8 (EF BB BF), UTF-16 (FE FF, FF FE; también UTF-32 LE, FF FE 00 00) o UTF-32 BE (00 00 FE FF).
        private static bool IsConMarcaDeOrden(ReadOnlySpan<byte> contenido) =>
            contenido is [0xEF, 0xBB, 0xBF, ..] or [0xFE, 0xFF, ..] or [0xFF, 0xFE, ..] or [0x00, 0x00, 0xFE, 0xFF, ..];

        // Solo ASCII imprimible y tabuladores (sin '\r' en medio, '\v', '\f' ni otros de control), sin sangría.
        private static bool IsLineaSencilla(ReadOnlySpan<byte> linea) =>
            !linea.IsEmpty && linea[0] is not (byte)' ' and not (byte)'\t' && !linea.ContainsAnyExcept(ImprimiblesYTabulador);

        private static byte[] CrearImprimiblesYTabulador()
        {
            byte[] bytes = new byte['~' - ' ' + 2];
            for (int i = 0; i < bytes.Length - 1; i++)
                bytes[i] = (byte)(' ' + i);
            bytes[^1] = (byte)'\t';
            return bytes;
        }

        // La dirección de una línea sencilla (sin el comentario) si 'nombre' es uno de sus nombres; null si no lo es. false si
        // el primer campo no es una dirección.
        private static bool TryLeerLinea(ReadOnlySpan<byte> util, string nombre, out IPAddress? direccion)
        {
            direccion = null;
            if (!TrySiguienteCampo(ref util, out ReadOnlySpan<byte> campo) || campo.Length > LongitudMaximaDireccion)
                return false;

            Span<char> texto = stackalloc char[campo.Length];
            _ = Ascii.ToUtf16(campo, texto, out _);
            if (!TryDireccionEstricta(texto, out IPAddress? leida))
                return false;

            while (TrySiguienteCampo(ref util, out campo))
                if (Ascii.EqualsIgnoreCase(campo, nombre))
                {
                    direccion = leida;
                    break;
                }

            return true;
        }

        // El siguiente campo de la línea (sin blancos) y lo que queda después; false si no queda ninguno.
        private static bool TrySiguienteCampo(ref ReadOnlySpan<byte> linea, out ReadOnlySpan<byte> campo)
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

        // Si 'nombre' (ASCII) aparece en cualquier sitio de 'texto', sin distinguir mayúsculas solo en las letras ASCII.
        private static bool IsContieneSinMayusculasAscii(ReadOnlySpan<byte> texto, string nombre)
        {
            for (int i = 0; i + nombre.Length <= texto.Length; i++)
                if (Ascii.EqualsIgnoreCase(texto.Slice(i, nombre.Length), nombre))
                    return true;
            return false;
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
