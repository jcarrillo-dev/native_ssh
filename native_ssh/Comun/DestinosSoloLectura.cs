using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace NativeSsh.Comun
{
    // Resultado de comparar un destino con NATIVE_SSH_SOLO_LECTURA. NoResuelto: el nombre (de una entrada o del destino) que no
    // se pudo resolver; entonces IsSoloLectura es true aunque no conste que sea la misma máquina (ante la duda, se deniega).
    public readonly record struct ComprobacionSoloLectura(bool IsSoloLectura, string? NoResuelto);

    // Destinos en modo solo lectura (CI6): configuración local de native_ssh (variable NATIVE_SSH_SOLO_LECTURA), que ninguna
    // herramienta puede cambiar. Para ellos se rechazan ssh_exec, ssh_input, ssh_write_file y ssh_replace_file_content antes de
    // enviar nada; las herramientas de lectura y ssh_set_cwd (que solo ejecuta órdenes fijas de native_ssh) siguen
    // funcionando. No hay clasificador de comandos: ssh_exec se rechaza entero (un clasificador daría falsa seguridad).
    //
    // Formato: entradas separadas por comas (los blancos alrededor se ignoran), cada una '[usuario@]servidor[:puerto]'
    // (IPv6 entre corchetes con puerto: '[::1]:22') o '*' (todos los destinos). Sin usuario vale para cualquier usuario y sin
    // puerto para cualquier puerto. El servidor es una IP o un nombre (letras, dígitos, '.', '-' y '_'): lo que nunca podría
    // ser el destino de una conexión ('a@b@c', 'host:22:2222', '/', '*.dominio') es un error. Se compara sin distinguir
    // mayúsculas y sin el punto final de un nombre completo; localhost, 127.0.0.0/8, ::1, 0.0.0.0 y :: son la misma máquina
    // (sin DNS). Si el texto no coincide, se comparan las direcciones de los dos (DNS o /etc/hosts, con un plazo de 3 s que se
    // cumple aunque la resolución no se pueda cancelar): así 'prod.ejemplo.com' cubre también su IP. Si un nombre que haría
    // falta comparar no se resuelve (fallo o plazo), el destino se trata como de solo lectura (falla cerrado). Solo se
    // recuerdan las resoluciones correctas, 5 minutos; por nombre hay como mucho una resolución en curso, y en total
    // MaxResolucionesEnCurso (las demás se rechazan sin lanzarse), para que un DNS colgado no acumule hilos bloqueados.
    public sealed class DestinosSoloLectura
    {
        public const string Variable = "NATIVE_SSH_SOLO_LECTURA";
        public const int MaxResolucionesEnCurso = 16;

        private static readonly TimeSpan PlazoResolucion = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan DuracionRecuerdo = TimeSpan.FromMinutes(5);
        private readonly ConcurrentDictionary<string, (long Caduca, HashSet<IPAddress> Direcciones)> _resueltos = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Task<HashSet<IPAddress>?>> _enCurso = new(StringComparer.Ordinal);
        private readonly Lock _candadoEnCurso = new();

        private readonly Entrada[] _entradas;
        private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolver;

        private DestinosSoloLectura(Entrada[] entradas, Func<string, CancellationToken, Task<IPAddress[]>>? resolver)
        {
            _entradas = entradas;
            _resolver = resolver ?? Dns.GetHostAddressesAsync;
            Entradas = Array.ConvertAll(entradas, e => e.Texto);
        }

        public static DestinosSoloLectura Ninguno { get; } = new([], null);

        public bool IsVacio => _entradas.Length == 0;

        // Las entradas tal como se configuraron (ssh_status las muestra).
        public IReadOnlyList<string> Entradas { get; }

        private readonly record struct Entrada(string Texto, string? Usuario, string Servidor, int? Puerto)
        {
            public bool IsTodos => Servidor == "*";
        }

        // Sin la variable (o vacía), ningún destino. Una entrada que no se entiende es un error: native_ssh no arranca, para no
        // dejar como escribible un destino que se quería proteger.
        public static bool TryParsear(string? texto, out DestinosSoloLectura destinos, out string? error, Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
        {
            destinos = Ninguno;
            error = null;
            if (string.IsNullOrWhiteSpace(texto))
                return true;

            List<Entrada> entradas = [];
            foreach (string trozo in texto.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParsearEntrada(trozo, out Entrada entrada))
                {
                    error = $"{Variable}: la entrada '{trozo}' no es válida. Usa '[usuario@]servidor[:puerto]' ('[::1]:22' para IPv6 con puerto) o '*', separadas por comas; el servidor es una IP o un nombre (letras, dígitos, '.', '-' y '_').";
                    return false;
                }

                entradas.Add(entrada);
            }

            destinos = new DestinosSoloLectura([.. entradas], resolver);
            return true;
        }

        private static bool TryParsearEntrada(string trozo, out Entrada entrada)
        {
            entrada = default;
            if (trozo == "*")
            {
                entrada = new Entrada(trozo, null, "*", null);
                return true;
            }

            if (trozo.AsSpan().ContainsAny(' ', '\t'))
                return false;

            string? usuario = null;
            string resto = trozo;
            int arroba = trozo.IndexOf('@', StringComparison.Ordinal);
            if (arroba >= 0)
            {
                usuario = trozo[..arroba];
                resto = trozo[(arroba + 1)..];
                if (usuario.Length == 0)
                    return false;
            }

            int? puerto = null;
            string servidor;
            if (resto.StartsWith('['))
            {
                int cierre = resto.IndexOf(']', StringComparison.Ordinal);
                if (cierre <= 1)
                    return false;

                servidor = resto[1..cierre];
                string tras = resto[(cierre + 1)..];
                if (tras.Length > 0 && (tras[0] != ':' || !TryParsearPuerto(tras[1..], out puerto)))
                    return false;
            }
            else
            {
                int dosPuntos = resto.IndexOf(':', StringComparison.Ordinal);
                bool isIpv6SinCorchetes = dosPuntos >= 0 && resto.LastIndexOf(':') != dosPuntos;
                servidor = dosPuntos < 0 || isIpv6SinCorchetes ? resto : resto[..dosPuntos];
                if (dosPuntos >= 0 && !isIpv6SinCorchetes && !TryParsearPuerto(resto[(dosPuntos + 1)..], out puerto))
                    return false;
            }

            if (!IsServidorValido(servidor))
                return false;

            entrada = new Entrada(trozo, usuario, Normalizar(servidor), puerto);
            return true;
        }

        private static bool TryParsearPuerto(string texto, out int? puerto)
        {
            bool isValido = int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out int valor) && valor is > 0 and <= 65535;
            puerto = isValido ? valor : null;
            return isValido;
        }

        // Una IP o un nombre de máquina: con ':' solo una IPv6 ('host:22:2222' no lo es); un nombre, solo letras, dígitos,
        // '.', '-' y '_' (ni '@', ni '/', ni comodines: no hay patrones).
        private static bool IsServidorValido(string servidor)
        {
            if (servidor.Length == 0)
                return false;

            if (servidor.Contains(':', StringComparison.Ordinal))
                return IPAddress.TryParse(servidor, out IPAddress? direccion) && direccion.AddressFamily == AddressFamily.InterNetworkV6;

            foreach (char c in servidor)
                if (!char.IsLetterOrDigit(c) && c is not ('.' or '-' or '_'))
                    return false;

            return true;
        }

        private static string Normalizar(string servidor) => servidor.TrimEnd('.').ToLowerInvariant();

        // Si el destino está en modo solo lectura (true también si no se pudo comprobar: ComprobarAsync).
        public async Task<bool> IsSoloLecturaAsync(DestinoSsh destino, CancellationToken cancellationToken) =>
            (await ComprobarAsync(destino, cancellationToken).ConfigureAwait(false)).IsSoloLectura;

        // Primero sin red (usuario, puerto, texto del servidor, IP literal y loopback); solo si alguna entrada casa en usuario y
        // puerto pero no así, se comparan las direcciones de los nombres, todos a la vez (un nombre que no se resuelve puede
        // tardar todo el plazo). Si alguno de los que hacía falta comparar no se resolvió y ninguna entrada coincide, se
        // devuelve solo lectura con ese nombre.
        public async Task<ComprobacionSoloLectura> ComprobarAsync(DestinoSsh destino, CancellationToken cancellationToken)
        {
            string servidor = Normalizar(destino.Servidor);
            List<Entrada> porResolver = Candidatas(destino, servidor, out bool isCoincideSinRed);
            if (isCoincideSinRed)
                return new ComprobacionSoloLectura(true, null);

            if (porResolver.Count == 0)
                return new ComprobacionSoloLectura(false, null);

            Task<HashSet<IPAddress>?> delDestino = ResolverAsync(servidor, cancellationToken);
            List<Task<HashSet<IPAddress>?>> deLasEntradas = porResolver.ConvertAll(e => ResolverAsync(e.Servidor, cancellationToken));
            HashSet<IPAddress>? direcciones = await delDestino.ConfigureAwait(false);
            HashSet<IPAddress>?[] resueltas = await Task.WhenAll(deLasEntradas).ConfigureAwait(false);
            string? noResuelto = direcciones is null ? servidor : null;
            for (int i = 0; i < resueltas.Length; i++)
            {
                if (resueltas[i] is not { } deLaEntrada)
                    noResuelto ??= porResolver[i].Servidor;
                else if (direcciones is not null && deLaEntrada.Overlaps(direcciones))
                    return new ComprobacionSoloLectura(true, null);
            }

            return new ComprobacionSoloLectura(noResuelto is not null, noResuelto);
        }

        // Para ssh_status, sin esperar al DNS: true o false si ya se sabe (texto, IP, loopback o direcciones ya resueltas);
        // null si depende de un nombre que aún no se ha resuelto.
        public bool? IsSoloLecturaSinRed(DestinoSsh destino)
        {
            string servidor = Normalizar(destino.Servidor);
            List<Entrada> porResolver = Candidatas(destino, servidor, out bool isCoincideSinRed);
            if (isCoincideSinRed)
                return true;

            if (porResolver.Count == 0)
                return false;

            bool isDestinoConocido = TryDireccionesSinRed(servidor, out HashSet<IPAddress>? direcciones);
            bool isDesconocido = !isDestinoConocido;
            foreach (Entrada entrada in porResolver)
            {
                if (!TryDireccionesSinRed(entrada.Servidor, out HashSet<IPAddress>? deLaEntrada))
                    isDesconocido = true;
                else if (isDestinoConocido && deLaEntrada!.Overlaps(direcciones!))
                    return true;
            }

            return isDesconocido ? null : false;
        }

        // Las entradas que casan en usuario y puerto pero cuyo servidor solo se puede comparar por sus direcciones;
        // isCoincideSinRed si alguna ya coincide sin red.
        private List<Entrada> Candidatas(DestinoSsh destino, string servidor, out bool isCoincideSinRed)
        {
            isCoincideSinRed = false;
            List<Entrada> porResolver = [];
            bool isDestinoLoopback = IsLoopback(servidor);
            foreach (Entrada entrada in _entradas)
            {
                bool isUsuarioYPuerto = (entrada.Usuario is null || entrada.Usuario == destino.Usuario) && (entrada.Puerto is null || entrada.Puerto == destino.Puerto);
                if (!isUsuarioYPuerto)
                    continue;

                if (entrada.IsTodos || entrada.Servidor == servidor || IsMismaIpLiteral(entrada.Servidor, servidor) || (isDestinoLoopback && IsLoopback(entrada.Servidor)))
                {
                    isCoincideSinRed = true;
                    return [];
                }

                porResolver.Add(entrada);
            }

            return porResolver;
        }

        private static bool IsMismaIpLiteral(string a, string b) =>
            IPAddress.TryParse(a, out IPAddress? ipA) && IPAddress.TryParse(b, out IPAddress? ipB) && Unificar(ipA).Equals(Unificar(ipB));

        // localhost o una dirección de esta máquina: 127.0.0.0/8, ::1, 0.0.0.0 y :: (conectar a ellas es conectar aquí).
        private static bool IsLoopback(string servidor) =>
            servidor == "localhost" || (IPAddress.TryParse(servidor, out IPAddress? direccion) && IsDireccionLoopback(Unificar(direccion)));

        private static bool IsDireccionLoopback(IPAddress direccion) =>
            IPAddress.IsLoopback(direccion) || direccion.Equals(IPAddress.Any) || direccion.Equals(IPAddress.IPv6Any);

        // IPv4 mapeada a IPv6 como IPv4 y cualquier dirección de loopback como 127.0.0.1, para comparar conjuntos.
        private static IPAddress Unificar(IPAddress direccion)
        {
            IPAddress unificada = direccion.IsIPv4MappedToIPv6 ? direccion.MapToIPv4() : direccion;
            return IsDireccionLoopback(unificada) ? IPAddress.Loopback : unificada;
        }

        // Sin red: la propia IP, localhost o lo ya resuelto y aún recordado.
        private bool TryDireccionesSinRed(string servidor, out HashSet<IPAddress>? direcciones)
        {
            direcciones = null;
            if (IPAddress.TryParse(servidor, out IPAddress? literal))
                direcciones = [Unificar(literal)];
            else if (servidor == "localhost")
                direcciones = [IPAddress.Loopback];
            else if (_resueltos.TryGetValue(servidor, out (long Caduca, HashSet<IPAddress> Direcciones) recordado) && recordado.Caduca > Environment.TickCount64)
                direcciones = recordado.Direcciones;
            return direcciones is not null;
        }

        // Direcciones de un nombre (o la propia IP), o null si no se pudo resolver: fallo, plazo de 3 s vencido (se deja de
        // esperar aunque la resolución siga) o demasiadas resoluciones en curso. Si ya hay una en curso para ese nombre, se
        // espera esa misma.
        private async Task<HashSet<IPAddress>?> ResolverAsync(string servidor, CancellationToken cancellationToken)
        {
            if (TryDireccionesSinRed(servidor, out HashSet<IPAddress>? conocidas))
                return conocidas;

            Task<HashSet<IPAddress>?>? resolucion;
            lock (_candadoEnCurso)
            {
                if (!_enCurso.TryGetValue(servidor, out resolucion))
                {
                    if (_enCurso.Count >= MaxResolucionesEnCurso)
                        return null;

                    // En el grupo de hilos (getaddrinfo puede bloquear el hilo que lo llama) y fuera de este candado: su
                    // 'finally' lo necesita para quitarse de _enCurso, así que no puede terminar antes de anotarla.
                    resolucion = Task.Run(() => ResolverEnCursoAsync(servidor));
                    _enCurso[servidor] = resolucion;
                }
            }

            try
            {
                return await resolucion.WaitAsync(PlazoResolucion, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        // Una resolución compartida: con su propio plazo (por si el resolver lo respeta), sin el token de quien la pidió
        // (la esperan varios); si acaba bien, se recuerda. Al terminar deja de estar en curso.
        private async Task<HashSet<IPAddress>?> ResolverEnCursoAsync(string servidor)
        {
            try
            {
                using CancellationTokenSource plazo = new(PlazoResolucion);
                HashSet<IPAddress> direcciones = [];
                foreach (IPAddress direccion in await _resolver(servidor, plazo.Token).ConfigureAwait(false))
                    _ = direcciones.Add(Unificar(direccion));
                if (direcciones.Count == 0)
                    return null;

                _resueltos[servidor] = (Environment.TickCount64 + (long)DuracionRecuerdo.TotalMilliseconds, direcciones);
                return direcciones;
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException)
            {
                return null;
            }
            finally
            {
                lock (_candadoEnCurso)
                    _ = _enCurso.Remove(servidor);
            }
        }
    }
}
