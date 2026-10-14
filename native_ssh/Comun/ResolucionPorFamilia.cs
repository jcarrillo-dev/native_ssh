using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;

namespace NativeSsh.Comun
{
    // Las direcciones de un nombre por familia: Ipv4 o Ipv6 vacío si el nombre no tiene direcciones de esa familia (NODATA:
    // un host solo IPv4 o solo IPv6); null si esa familia falló (el DNS dice que el nombre no existe, falla, rechaza la
    // consulta o no responde) o no terminó dentro del plazo. Incidencia: lo que impidió resolverla aparte del DNS (un
    // proceso de resolución que no se pudo terminar o el tope de ellos: ResolutorEnProceso, en Linux), para el rechazo.
    public readonly record struct DireccionesPorFamilia(IPAddress[]? Ipv4, IPAddress[]? Ipv6, string? Incidencia = null)
    {
        public bool HasFallo => Ipv4 is null || Ipv6 is null;
    }

    // Lo que dio la resolución de un nombre (una familia o, para el destino, las dos juntas): las direcciones (vacío: sin
    // direcciones de esa familia; null: falló o no terminó en su plazo) y la incidencia, como en DireccionesPorFamilia.
    public readonly record struct ResultadoResolucion(IPAddress[]? Direcciones, string? Incidencia = null);

    // Resolución de las entradas de NATIVE_SSH_SOLO_LECTURA por familia (A y AAAA por separado, a la vez) en todos los
    // sistemas, nunca las dos juntas: con AF_UNSPEC, si el DNS falla solo en una familia, getaddrinfo devuelve la otra como
    // si fuera todo y la dirección de la familia que falló no se compararía; preguntando cada familia, el fallo se ve. Lo
    // de cada sistema (cómo se pide una familia, si se distingue «sin direcciones de esa familia», dónde está el fichero
    // hosts) va en ResolucionPorFamilia.Linux.cs, .Windows.cs y .Unix.cs, y solo se compila el del sistema para el que se
    // compila (NativeSshSistema en Directory.Build.props); esta parte común no mira el sistema.
    public static partial class ResolucionPorFamilia
    {
        // Lo que se espera, tras vencer el plazo de una resolución que se hace en otro proceso (Linux: ResolutorEnProceso),
        // a que ese proceso muera tras el SIGKILL y se recoja; quien espera la resolución cuenta con ello.
        public static readonly TimeSpan EsperaRecogida = TimeSpan.FromSeconds(1);

        // Las dos familias de una entrada con el resolver del sistema, dentro del plazo (al vencer, la que no terminó falla).
        public static Task<DireccionesPorFamilia> ResolverAsync(string nombre, CancellationToken plazo) => ResolverDelSistemaAsync(nombre, plazo);

        // El destino, con las dos familias juntas (como lo resuelve la conexión), con el resolver del sistema y dentro del
        // plazo: sus direcciones, o null si falla o no termina a tiempo.
        public static Task<ResultadoResolucion> ResolverDestinoAsync(string nombre, CancellationToken plazo) => ResolverDestinoDelSistemaAsync(nombre, plazo);

        // Las resoluciones del sistema que no se pudieron terminar y siguen vivas (ssh_status), una por texto; vacía si no hay
        // o el sistema no las tiene (solo Linux resuelve en otro proceso).
        public static string[] ResolucionesSinTerminar()
        {
            PooledListRef<string> sinTerminar = new();
            try
            {
                AnadirSinTerminarDelSistema(ref sinTerminar);
                return sinTerminar.Span.ToArray();
            }
            finally
            {
                sinTerminar.Dispose();
            }
        }

        // El modo interno del binario (Linux: el proceso hijo que resuelve un nombre, ResolutorEnProceso): si 'args' lo pide y
        // el sistema lo tiene, lo ejecuta y da su código de salida. En los demás sistemas no existe (el argumento se rechaza
        // como cualquier otro desconocido).
        public static bool TryEjecutarModoInterno(string[] args, out int codigo)
        {
            codigo = 0;
            bool isHecho = false;
            EjecutarModoInternoDelSistema(args, ref codigo, ref isHecho);
            return isHecho;
        }

        private static partial Task<DireccionesPorFamilia> ResolverDelSistemaAsync(string nombre, CancellationToken plazo);

        // El destino con el DNS asíncrono de .NET, que deja de esperar al vencer el plazo (el de Windows y los demás Unix).
        internal static async Task<ResultadoResolucion> ResolverDestinoConNetAsync(string nombre, CancellationToken plazo) =>
            new(await Dns.GetHostAddressesAsync(nombre, plazo).ConfigureAwait(false));

        private static partial Task<ResultadoResolucion> ResolverDestinoDelSistemaAsync(string nombre, CancellationToken plazo);

        static partial void AnadirSinTerminarDelSistema(scoped ref PooledListRef<string> sinTerminar);

        static partial void EjecutarModoInternoDelSistema(string[] args, ref int codigo, ref bool isHecho);

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
        // resolverFamilia: las direcciones de una familia, vacío si no tiene, null si falla. Cada llamada lanza las suyas.
        public static Task<DireccionesPorFamilia> ResolverAsync(string nombre, Func<string, AddressFamily, IPAddress[]?> resolverFamilia, CancellationToken plazo) =>
            ResolverAsync(nombre, resolverFamilia, new FamiliasEnCurso(), plazo);

        // Igual, pero una familia del nombre que sigue en curso en 'enCurso' (de una llamada anterior cuyo plazo ya venció) se
        // espera en lugar de lanzarla otra vez (la del sistema en Windows y los demás Unix comparte las suyas:
        // EnCursoDelSistema; en Linux cada resolución va en un proceso que se mata al vencer: ResolutorEnProceso).
        internal static async Task<DireccionesPorFamilia> ResolverAsync(string nombre, Func<string, AddressFamily, IPAddress[]?> resolverFamilia, FamiliasEnCurso enCurso, CancellationToken plazo)
        {
            Task<IPAddress[]?> ipv4 = enCurso.Lanzar(nombre, AddressFamily.InterNetwork, resolverFamilia);
            Task<IPAddress[]?> ipv6 = enCurso.Lanzar(nombre, AddressFamily.InterNetworkV6, resolverFamilia);
            await ((Task)Task.WhenAll(ipv4, ipv6)).WaitAsync(plazo).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return new DireccionesPorFamilia(Resultado(ipv4), Resultado(ipv6));
        }

        private static IPAddress[]? Resultado(Task<IPAddress[]?> familia) => familia.IsCompletedSuccessfully ? familia.Result : null;

        // Las dos familias a la vez con un resolver asíncrono que cumple él mismo el plazo (lanzar: el de Linux, que resuelve
        // cada una en un proceso que mata al vencer): se esperan las dos, con su incidencia si la hubo (una excepción es un
        // fallo de esa familia).
        internal static async Task<DireccionesPorFamilia> ResolverAsync(string nombre, Func<string, AddressFamily, CancellationToken, Task<ResultadoResolucion>> lanzar, CancellationToken plazo)
        {
            Task<ResultadoResolucion> ipv4 = lanzar(nombre, AddressFamily.InterNetwork, plazo);
            Task<ResultadoResolucion> ipv6 = lanzar(nombre, AddressFamily.InterNetworkV6, plazo);
            await ((Task)Task.WhenAll(ipv4, ipv6)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            ResultadoResolucion de4 = ipv4.IsCompletedSuccessfully ? ipv4.Result : default;
            ResultadoResolucion de6 = ipv6.IsCompletedSuccessfully ? ipv6.Result : default;
            string? incidencia = de4.Incidencia is null || de6.Incidencia is null || de4.Incidencia == de6.Incidencia ? de4.Incidencia ?? de6.Incidencia : $"{de4.Incidencia}; {de6.Incidencia}";
            return new DireccionesPorFamilia(de4.Direcciones, de6.Direcciones, incidencia);
        }

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
        public static IPAddress[] DireccionesEnHosts(string ruta, string nombre)
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

            using PooledListRef<IPAddress> direcciones = new();
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

            return direcciones.Span.ToArray();
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
            bool isUltimoCampo = fin < 0;
            campo = isUltimoCampo ? linea : linea[..fin];
            linea = isUltimoCampo ? [] : linea[fin..];
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

    // Las resoluciones de una familia lanzadas y aún sin terminar, por nombre y familia, donde se resuelve en hilos (Windows
    // y los demás Unix; Linux usa procesos: ResolutorEnProceso). DnsQuery_W bloquea el hilo y no se puede cancelar: al
    // vencer el plazo se deja de esperar, pero el hilo sigue bloqueado hasta que el resolver se rinda. Si cada comprobación
    // lanzara la familia otra vez, los hilos bloqueados crecerían con cada reintento (medido sin esto en Windows: unos 135
    // con 20 entradas y 30 llamadas). Mientras una siga sin terminar, la misma familia del mismo nombre espera esa en lugar
    // de lanzar otra: como mucho un hilo bloqueado por nombre y familia, aunque se reintente. Al terminar deja de estar en
    // curso y la siguiente se lanza (en Windows, medido con el TCP/53 colgado: la primera llamada sin rechazo llega 8,6 s
    // después de que el DNS responda). Con un resolver que no se rindiera nunca (getaddrinfo de glibc con un TCP/53 que no
    // cierra) el nombre no volvería a resolverse: por eso Linux no lo usa.
    internal sealed class FamiliasEnCurso
    {
        private readonly Dictionary<(string Nombre, AddressFamily Familia), Task<IPAddress[]?>> _enCurso = [];
        private readonly Lock _candado = new();

        // Las que siguen sin terminar (para las pruebas).
        public int Cantidad
        {
            get
            {
                lock (_candado)
                    return _enCurso.Count;
            }
        }

        // La resolución de esa familia del nombre: la que sigue en curso, o una nueva en un hilo del grupo.
        public Task<IPAddress[]?> Lanzar(string nombre, AddressFamily familia, Func<string, AddressFamily, IPAddress[]?> resolverFamilia)
        {
            (string, AddressFamily) clave = (nombre, familia);
            Task<IPAddress[]?> resolucion;
            lock (_candado)
            {
                if (_enCurso.TryGetValue(clave, out Task<IPAddress[]?>? enCurso))
                    return enCurso;

                resolucion = Task.Run(() => resolverFamilia(nombre, familia), CancellationToken.None);
                _enCurso[clave] = resolucion;
            }

            _ = resolucion.ContinueWith(terminada => Quitar(clave, terminada), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return resolucion;
        }

        private void Quitar((string, AddressFamily) clave, Task<IPAddress[]?> terminada)
        {
            lock (_candado)
                if (_enCurso.TryGetValue(clave, out Task<IPAddress[]?>? enCurso) && ReferenceEquals(enCurso, terminada))
                    _ = _enCurso.Remove(clave);
        }
    }
}
