using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace NativeSsh.Comun
{
    // Resultado de comparar un destino con NATIVE_SSH_SOLO_LECTURA. NoResuelto: el nombre (de una entrada o del destino) que no
    // se pudo resolver; entonces IsSoloLectura es true aunque no conste que sea la misma máquina (ante la duda, se deniega).
    // Entrada: si NoResuelto es el nombre de una entrada (o el HostName de una entrada que es un alias) que no se resolvió,
    // la entrada tal como se escribió (el rechazo la nombra para que se pueda corregir); null si es el destino.
    // IsCupoLleno: no se llegó a intentar resolverlo (demasiadas resoluciones en curso), no es que el DNS fallara.
    // DireccionConectada: es de solo lectura por la dirección a la que está conectada la sesión (ExtremoConectado), aunque ni
    // el texto pedido ni lo que resuelve ahora el nombre lo sean. IsDireccionDesconocida: la sesión está conectada y no se sabe
    // a qué dirección (también se deniega). IsErrorConfiguracion: NoResuelto es una entrada que ssh -G no pudo leer (no se
    // sabe a qué máquina lleva).
    public readonly record struct ComprobacionSoloLectura(bool IsSoloLectura, string? NoResuelto, bool IsCupoLleno = false, IPAddress? DireccionConectada = null, bool IsDireccionDesconocida = false, bool IsErrorConfiguracion = false, string? Entrada = null);

    // La conexión abierta de una sesión: Direccion es el extremo remoto de su socket (IConexionSsh.DireccionRemota), null si
    // no se sabe.
    public readonly record struct ExtremoConectado(IPAddress? Direccion);

    // Destinos en modo solo lectura (CI6): configuración local de native_ssh (variable NATIVE_SSH_SOLO_LECTURA), que ninguna
    // herramienta puede cambiar. Para ellos se rechazan ssh_exec, ssh_input, ssh_write_file y ssh_replace_file_content antes de
    // enviar nada; las herramientas de lectura y ssh_set_cwd (que solo ejecuta órdenes fijas de native_ssh) siguen
    // funcionando. No hay clasificador de comandos: ssh_exec se rechaza entero (un clasificador daría falsa seguridad).
    //
    // Formato: entradas separadas por comas (los blancos alrededor se ignoran), cada una '[usuario@]servidor[:puerto]'
    // (IPv6 entre corchetes con puerto: '[::1]:22') o '*' (todos los destinos). Sin usuario vale para cualquier usuario y sin
    // puerto para cualquier puerto. El servidor es una IP o un nombre (letras, dígitos, '.', '-' y '_', con al menos una letra
    // o un dígito): lo que nunca podría ser el destino de una conexión ('a@b@c', 'host:22:2222', '/', '*.dominio', '.', '-')
    // es un error. Se compara sin distinguir mayúsculas y sin el punto final de un nombre completo; localhost, 127.0.0.0/8,
    // ::1, 0.0.0.0 y :: son la misma máquina (sin DNS). Una entrada que es un alias de la configuración de ssh (ssh -G, con
    // su usuario y su puerto si los lleva, le da otro HostName: UsarConfiguracionSsh) vale por ese HostName, con el usuario
    // y el puerto de la entrada (sin ellos, cualquiera), además de por su texto; el nombre del alias no se busca en el DNS
    // (ssh no conecta ahí). Lo más restrictivo de tres cosas (con el usuario y el puerto ya resueltos): un destino es de
    // solo lectura si coincide con una entrada
    // - el texto pedido: el alias de la configuración de ssh (DestinoSsh.Alias), solo como texto, con el texto de la entrada;
    // - el destino resuelto por ssh -G (HostName): por texto y, si no coincide, por sus direcciones (DNS o /etc/hosts, con
    //   un plazo de 3 s que se cumple aunque la resolución no se pueda cancelar): así 'prod.ejemplo.com' cubre también su IP;
    // - la dirección a la que está conectada de verdad la sesión, si ya lo está (ExtremoConectado: el extremo remoto del
    //   socket; FabricaConexionSsh conecta a una dirección ya resuelta, así que es exactamente esa): una sesión abierta
    //   cuando el nombre apuntaba a una máquina protegida sigue protegida aunque el nombre apunte ahora a otra.
    // Si un nombre que haría falta comparar no se resuelve, sea cual sea la causa (también HostNotFound: con el nsswitch de
    // systemd, 'resolve [!UNAVAIL=return]', un SERVFAIL o un REFUSED llegan como HostNotFound, igual que un NXDOMAIN, así que
    // la clase del error no dice si el nombre existe), si ssh -G no puede leer una entrada, o si la sesión está conectada y no
    // se sabe a qué dirección, el destino se trata como de solo lectura (falla cerrado): una entrada mal escrita rechaza las
    // escrituras con su usuario y puerto hasta que se corrija, y el rechazo la nombra. Solo se recuerdan, 5 minutos, las
    // resoluciones correctas de las entradas; el destino se resuelve en cada comprobación. Además de la comprobación de
    // cada herramienta (antes de conectar), la sesión vuelve a comparar la dirección de la conexión justo antes de enviar
    // una escritura al shell (ComprobarConexionAsync): si la sesión se abre o se reabre después de la comprobación y el
    // nombre resuelve entonces a una máquina protegida, la escritura tampoco se envía. Lo que queda: el nombre de una
    // entrada que pase a apuntar a otra máquina se ve, como tarde, a los 5 minutos. La zona de una IPv6 ('%1', '%lo') no
    // cuenta al comparar.
    // Por nombre hay como mucho una resolución en curso, y en total MaxResolucionesEnCurso (las demás no se lanzan y el
    // rechazo lo dice), para que un DNS colgado no acumule hilos bloqueados.
    public sealed class DestinosSoloLectura
    {
        public const string Variable = "NATIVE_SSH_SOLO_LECTURA";
        public const int MaxResolucionesEnCurso = 16;

        private static readonly TimeSpan PlazoResolucion = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan DuracionRecuerdo = TimeSpan.FromMinutes(5);
        private readonly ConcurrentDictionary<string, (long Caduca, HashSet<IPAddress> Direcciones)> _resueltos = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Task<Resolucion>> _enCurso = new(StringComparer.Ordinal);
        private readonly Lock _candadoEnCurso = new();

        private readonly Entrada[] _entradas;
        private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolver;
        private volatile Func<string?, string, int?, string?>? _hostNameDeEntrada;

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

        private readonly record struct Resolucion(HashSet<IPAddress>? Direcciones, bool IsCupoLleno);

        // Escrito: el servidor de la entrada tal como se escribió (normalizado), con el que se comparan los textos pedidos;
        // Servidor: el que se compara con el destino, el HostName si la entrada es un alias de la configuración de ssh (si
        // no, el mismo). ServidorOriginal: lo escrito sin normalizar, para ssh -G. IsIlegible: ssh -G no pudo leerla.
        private readonly record struct Entrada(string Texto, string? Usuario, string Servidor, int? Puerto, string Escrito, string ServidorOriginal, bool IsIlegible = false)
        {
            public bool IsTodos => Servidor == "*";
        }

        // Con la configuración de ssh (ResolutorConfiguracionSsh), cada comprobación lee con ssh -G el HostName de cada
        // entrada (usuario, servidor tal como se escribió y puerto, null si la entrada no los lleva): null si la
        // configuración no la cambia (no es un alias) o no hay configuración; NativeSshException si ssh -G no pudo leerla.
        // Se lee en cada comprobación, como los destinos: un cambio de la configuración vale desde la llamada siguiente.
        public void UsarConfiguracionSsh(Func<string?, string, int?, string?>? hostNameDeEntrada) => _hostNameDeEntrada = hostNameDeEntrada;

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
                    error = $"{Variable}: la entrada '{trozo}' no es válida. Usa '[usuario@]servidor[:puerto]' ('[::1]:22' para IPv6 con puerto) o '*', separadas por comas; el servidor es una IP o un nombre (letras, dígitos, '.', '-' y '_', con al menos una letra o un dígito).";
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
                entrada = new Entrada(trozo, null, "*", null, "*", "*");
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

            string normalizado = Normalizar(servidor);
            entrada = new Entrada(trozo, usuario, normalizado, puerto, normalizado, servidor);
            return true;
        }

        private static bool TryParsearPuerto(string texto, out int? puerto)
        {
            bool isValido = int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out int valor) && valor is > 0 and <= 65535;
            puerto = isValido ? valor : null;
            return isValido;
        }

        // Una IP o un nombre de máquina: con ':' solo una IPv6 ('host:22:2222' no lo es); un nombre, solo letras, dígitos,
        // '.', '-' y '_' (ni '@', ni '/', ni comodines: no hay patrones) y al menos una letra o un dígito ('.', '..', '-' o
        // '_' no son el nombre de ninguna máquina).
        private static bool IsServidorValido(string servidor)
        {
            if (servidor.Contains(':', StringComparison.Ordinal))
                return IPAddress.TryParse(servidor, out IPAddress? direccion) && direccion.AddressFamily == AddressFamily.InterNetworkV6;

            bool hasLetraODigito = false;
            foreach (char c in servidor)
            {
                bool isLetraODigito = char.IsLetterOrDigit(c);
                if (!isLetraODigito && c is not ('.' or '-' or '_'))
                    return false;

                hasLetraODigito |= isLetraODigito;
            }

            return hasLetraODigito;
        }

        private static string Normalizar(string servidor) => servidor.TrimEnd('.').ToLowerInvariant();

        // Si el destino está en modo solo lectura (true también si no se pudo comprobar: ComprobarAsync).
        public async Task<bool> IsSoloLecturaAsync(DestinoSsh destino, CancellationToken cancellationToken) =>
            (await ComprobarAsync(destino, cancellationToken).ConfigureAwait(false)).IsSoloLectura;

        public Task<ComprobacionSoloLectura> ComprobarAsync(DestinoSsh destino, CancellationToken cancellationToken) =>
            ComprobarAsync(destino, conectado: null, isResolverDestino: true, cancellationToken);

        // Para las herramientas de escritura: el texto pedido, el destino resuelto y, si la sesión ya está conectada
        // (conectado), la dirección de su conexión.
        public Task<ComprobacionSoloLectura> ComprobarAsync(DestinoSsh destino, ExtremoConectado? conectado, CancellationToken cancellationToken) =>
            ComprobarAsync(destino, conectado, isResolverDestino: true, cancellationToken);

        // Para la sesión, justo antes de enviar una escritura por una conexión ya abierta: el texto pedido y la dirección de esa
        // conexión, sin volver a resolver el nombre del destino (la conexión ya está hecha: lo que cuenta es adónde).
        public Task<ComprobacionSoloLectura> ComprobarConexionAsync(DestinoSsh destino, ExtremoConectado conectado, CancellationToken cancellationToken) =>
            ComprobarAsync(destino, conectado, isResolverDestino: false, cancellationToken);

        // Primero sin red (usuario, puerto, texto del servidor y del alias, IP literal, loopback y la dirección conectada);
        // solo si alguna entrada casa en usuario y puerto pero no así, se comparan las direcciones de los nombres, todos a la
        // vez (un nombre que no se resuelve puede tardar todo el plazo). Si alguno de los que hacía falta comparar no se
        // resolvió y ninguna entrada coincide, se devuelve solo lectura con ese nombre.
        private async Task<ComprobacionSoloLectura> ComprobarAsync(DestinoSsh destino, ExtremoConectado? conectado, bool isResolverDestino, CancellationToken cancellationToken)
        {
            string servidor = Normalizar(destino.Servidor);
            Entrada[] entradas = await EntradasConConfiguracionAsync().ConfigureAwait(false);
            List<Entrada> porResolver = Candidatas(entradas, destino, servidor, out bool isCoincideSinRed);
            if (isCoincideSinRed || IsAliasCoincidente(porResolver, destino.Alias))
                return new ComprobacionSoloLectura(true, null);

            if (porResolver.Count == 0)
                return new ComprobacionSoloLectura(false, null);

            IPAddress? conectada = conectado?.Direccion is { } direccion ? Unificar(direccion) : null;
            if (conectada is not null && IsCoincideAlguna(porResolver, conectada.ToString()))
                return new ComprobacionSoloLectura(true, null, DireccionConectada: conectada);
            if (conectado is { Direccion: null })
                return new ComprobacionSoloLectura(true, null, IsDireccionDesconocida: true);

            Task<Resolucion>? delDestino = isResolverDestino ? ResolverAsync(servidor, isDestino: true, cancellationToken) : null;
            List<Task<Resolucion>> deLasEntradas = porResolver.ConvertAll(e => e.IsIlegible ? Task.FromResult(default(Resolucion)) : ResolverAsync(e.Servidor, isDestino: false, cancellationToken));
            Resolucion destinoResuelto = delDestino is null ? default : await delDestino.ConfigureAwait(false);
            Resolucion[] resueltas = await Task.WhenAll(deLasEntradas).ConfigureAwait(false);
            HashSet<IPAddress>? direcciones = destinoResuelto.Direcciones;
            string? noResuelto = isResolverDestino && direcciones is null ? servidor : null;
            bool isCupoLleno = destinoResuelto.IsCupoLleno;
            bool isErrorConfiguracion = false;
            string? entradaNoResuelta = null;
            for (int i = 0; i < resueltas.Length; i++)
            {
                if (resueltas[i].Direcciones is not { } deLaEntrada)
                {
                    // La entrada se nombra solo si su nombre no se resolvió (no si ssh -G no la pudo leer ni si no se llegó a
                    // intentar por el cupo: esos mensajes ya dicen lo que pasa).
                    bool isFalloDns = !porResolver[i].IsIlegible && !resueltas[i].IsCupoLleno;
                    if (noResuelto is null)
                        (noResuelto, isCupoLleno, isErrorConfiguracion, entradaNoResuelta) = porResolver[i].IsIlegible ? (porResolver[i].Escrito, false, true, null) : (porResolver[i].Servidor, resueltas[i].IsCupoLleno, false, isFalloDns ? porResolver[i].Texto : null);
                }
                else if (conectada is not null && deLaEntrada.Contains(conectada))
                    return new ComprobacionSoloLectura(true, null, DireccionConectada: conectada);
                else if (direcciones is not null && deLaEntrada.Overlaps(direcciones))
                    return new ComprobacionSoloLectura(true, null);
            }

            return new ComprobacionSoloLectura(noResuelto is not null, noResuelto, isCupoLleno, IsErrorConfiguracion: isErrorConfiguracion, Entrada: entradaNoResuelta);
        }

        // Las entradas con el HostName que les da la configuración de ssh, todas a la vez (ssh -G puede tardar hasta su plazo
        // con un Match exec); sin configuración, tal cual.
        private async Task<Entrada[]> EntradasConConfiguracionAsync()
        {
            if (_hostNameDeEntrada is not { } hostNameDeEntrada)
                return _entradas;

            Task<Entrada>[] leidas = Array.ConvertAll(_entradas, e => e.IsTodos ? Task.FromResult(e) : Task.Run(() => ConConfiguracion(e, hostNameDeEntrada)));
            return await Task.WhenAll(leidas).ConfigureAwait(false);
        }

        private Entrada[] EntradasConConfiguracion() =>
            _hostNameDeEntrada is not { } hostNameDeEntrada ? _entradas : Array.ConvertAll(_entradas, e => e.IsTodos ? e : ConConfiguracion(e, hostNameDeEntrada));

        private static Entrada ConConfiguracion(Entrada entrada, Func<string?, string, int?, string?> hostNameDeEntrada)
        {
            try
            {
                string? hostName = hostNameDeEntrada(entrada.Usuario, entrada.ServidorOriginal, entrada.Puerto);
                string? real = hostName is null ? null : Normalizar(hostName);
                return real is null || real.Length == 0 || real == entrada.Escrito ? entrada : entrada with { Servidor = real };
            }
            catch (NativeSshException)
            {
                return entrada with { IsIlegible = true };
            }
        }

        // Para ssh_status, sin esperar al DNS: true o false si ya se sabe (texto del destino o de los alias, IP, loopback,
        // la dirección conectada o direcciones ya resueltas); null si depende de algo que aún no se sabe (un nombre sin
        // resolver, o una sesión conectada sin dirección conocida). alias: los textos pedidos que cuentan (null: el alias
        // del propio destino).
        public bool? IsSoloLecturaSinRed(DestinoSsh destino, ExtremoConectado? conectado = null, IReadOnlyCollection<string>? alias = null)
        {
            string servidor = Normalizar(destino.Servidor);
            List<Entrada> porResolver = Candidatas(EntradasConConfiguracion(), destino, servidor, out bool isCoincideSinRed);
            if (isCoincideSinRed)
                return true;

            foreach (string texto in alias ?? (destino.Alias is { } propio ? [propio] : []))
                if (IsAliasCoincidente(porResolver, texto))
                    return true;

            if (porResolver.Count == 0)
                return false;

            IPAddress? conectada = conectado?.Direccion is { } direccion ? Unificar(direccion) : null;
            if (conectada is not null && IsCoincideAlguna(porResolver, conectada.ToString()))
                return true;

            bool isDestinoConocido = TryDireccionesSinRed(servidor, isDestino: true, out HashSet<IPAddress>? direcciones);
            bool isDesconocido = !isDestinoConocido || conectado is { Direccion: null };
            foreach (Entrada entrada in porResolver)
            {
                if (entrada.IsIlegible || !TryDireccionesSinRed(entrada.Servidor, isDestino: false, out HashSet<IPAddress>? deLaEntrada))
                    isDesconocido = true;
                else if ((isDestinoConocido && deLaEntrada!.Overlaps(direcciones!)) || (conectada is not null && deLaEntrada!.Contains(conectada)))
                    return true;
            }

            return isDesconocido ? null : false;
        }

        // Las entradas que casan en usuario y puerto pero cuyo servidor solo se puede comparar por sus direcciones;
        // isCoincideSinRed si alguna ya coincide sin red.
        private static List<Entrada> Candidatas(Entrada[] entradas, DestinoSsh destino, string servidor, out bool isCoincideSinRed)
        {
            isCoincideSinRed = false;
            List<Entrada> porResolver = [];
            bool isDestinoLoopback = IsLoopback(servidor);
            foreach (Entrada entrada in entradas)
            {
                bool isUsuarioYPuerto = (entrada.Usuario is null || entrada.Usuario == destino.Usuario) && (entrada.Puerto is null || entrada.Puerto == destino.Puerto);
                if (!isUsuarioYPuerto)
                    continue;

                if (entrada.IsTodos || IsCoincideSinRed(entrada, servidor, isDestinoLoopback))
                {
                    isCoincideSinRed = true;
                    return [];
                }

                porResolver.Add(entrada);
            }

            return porResolver;
        }

        // El texto pedido (un alias), solo como texto y con el texto de la entrada: nunca se resuelve.
        private static bool IsAliasCoincidente(List<Entrada> candidatas, string? alias)
        {
            if (alias is null)
                return false;

            string normalizado = Normalizar(alias);
            foreach (Entrada entrada in candidatas)
                if (entrada.Escrito == normalizado)
                    return true;
            return false;
        }

        private static bool IsCoincideAlguna(List<Entrada> candidatas, string servidor)
        {
            bool isServidorLoopback = IsLoopback(servidor);
            foreach (Entrada entrada in candidatas)
                if (IsCoincideSinRed(entrada, servidor, isServidorLoopback))
                    return true;
            return false;
        }

        // Por texto (el HostName de la entrada o lo que se escribió), como la misma IP literal o las dos de loopback (la misma
        // máquina).
        private static bool IsCoincideSinRed(Entrada entrada, string servidor, bool isServidorLoopback) =>
            entrada.Servidor == servidor || entrada.Escrito == servidor || IsMismaIpLiteral(entrada.Servidor, servidor) || (isServidorLoopback && IsLoopback(entrada.Servidor));

        // El mensaje de rechazo de una herramienta de escritura, o null si la comprobación la permite. isEnSesion: lo rechaza la
        // sesión justo antes de enviar la escritura al shell (ComprobarConexionAsync), cuando ya se pudo enviar algo antes
        // (abrir el shell, la lectura de ssh_replace_file_content): el mensaje dice que no se envió la escritura, no «nada».
        public static string? MensajeRechazo(ComprobacionSoloLectura comprobacion, string clave, string herramienta, bool isEnSesion = false) => comprobacion switch
        {
            { NoResuelto: { } noResuelto } => MensajeNoComprobado(clave, herramienta, noResuelto, comprobacion.IsCupoLleno, comprobacion.IsErrorConfiguracion, isEnSesion, comprobacion.Entrada),
            { IsDireccionDesconocida: true } => MensajeNoComprobado(clave, herramienta, null, isEnSesion: isEnSesion),
            { IsSoloLectura: true } => MensajeSoloLectura(clave, herramienta, comprobacion.DireccionConectada, isEnSesion),
            _ => null
        };

        // Lo que no se hizo: antes de conectar o enviar nada (la comprobación de cada herramienta) o, en la sesión, la escritura.
        private static string QueNoSeHizo(string herramienta, bool isEnSesion) =>
            !isEnSesion ? $"{herramienta} no se ha ejecutado y no se ha enviado nada al servidor"
            : herramienta == "ssh_exec" ? "el comando de ssh_exec no se ha enviado al servidor (la sesión lo volvió a comprobar justo antes de enviarlo)"
            : $"la escritura de {herramienta} no se ha enviado al servidor (la sesión lo volvió a comprobar justo antes de enviarla)";

        // noResuelto: el nombre que no se resolvió (null: la sesión está conectada y no se sabe a qué dirección). isCupoLleno:
        // el nombre no se llegó a resolver (demasiadas resoluciones a la vez), no es que el DNS fallara. isErrorConfiguracion:
        // noResuelto es una entrada que ssh -G no pudo leer. entrada: noResuelto es el nombre (o el HostName) de esa entrada de
        // NATIVE_SSH_SOLO_LECTURA, que no se resolvió: el mensaje la nombra y dice qué hacer (corregirla o comprobar el DNS).
        public static string MensajeNoComprobado(string clave, string herramienta, string? noResuelto, bool isCupoLleno = false, bool isErrorConfiguracion = false, bool isEnSesion = false, string? entrada = null)
        {
            string causa = noResuelto is null ? "no se sabe a qué dirección está conectada su sesión"
                : isErrorConfiguracion ? $"no se pudo leer la entrada {noResuelto} con la configuración de ssh (ssh -G)"
                : isCupoLleno ? $"no se llegó a resolver {noResuelto}: demasiadas resoluciones a la vez"
                : entrada is not null ? $"no se resolvió {noResuelto}, de la entrada '{entrada}' de {Variable}"
                : $"no se resolvió {noResuelto}";
            string cuando = noResuelto is null ? "la comprobación se repite en cada llamada."
                : isErrorConfiguracion ? "la comprobación se repite en cada llamada y pasará cuando ssh -G pueda leer esa entrada (quien configura native_ssh debe revisar su configuración de ssh)."
                : isCupoLleno ? "vuelve a intentarlo en unos segundos, cuando terminen las resoluciones en curso; la comprobación se repite en cada llamada."
                : entrada is not null ? $"la comprobación se repite en cada llamada y pasará cuando ese nombre se resuelva. Díselo al usuario: mientras no se resuelva, esa entrada rechaza las escrituras a todos los destinos con su usuario y puerto; si está mal escrita o el nombre ya no existe, quien configura native_ssh debe corregirla o quitarla de {Variable}, y si no, comprobar el DNS (o /etc/hosts) de la máquina donde corre native_ssh."
                : "la comprobación se repite en cada llamada y pasará cuando el nombre se resuelva (DNS o /etc/hosts).";
            return $"Rechazado: {clave}: no se pudo comprobar si es de solo lectura ({causa}); se rechaza por seguridad (configuración local de native_ssh, variable {Variable}), así que {QueNoSeHizo(herramienta, isEnSesion)}. ssh_read_file, ssh_search, ssh_find, ssh_list_dir, ssh_stat y ssh_set_cwd siguen disponibles. No busques otra vía para modificar el servidor: {cuando}";
        }

        // direccionConectada: es de solo lectura por la dirección a la que está conectada la sesión, no por el nombre.
        public static string MensajeSoloLectura(string clave, string herramienta, IPAddress? direccionConectada = null, bool isEnSesion = false)
        {
            string motivo = direccionConectada is null ? $"{clave} está en modo solo lectura" : $"la sesión de {clave} está conectada a {direccionConectada}, que está en modo solo lectura";
            return $"Rechazado: {motivo} (configuración local de native_ssh, variable {Variable}), así que {QueNoSeHizo(herramienta, isEnSesion)}. Para ese destino están desactivadas ssh_exec, ssh_input, ssh_write_file y ssh_replace_file_content: usa ssh_read_file, ssh_search, ssh_find, ssh_list_dir, ssh_stat y ssh_set_cwd. No reintentes ni busques otra vía para modificar el servidor: este modo solo lo puede quitar quien configura native_ssh.";
        }

        private static bool IsMismaIpLiteral(string a, string b) =>
            IPAddress.TryParse(a, out IPAddress? ipA) && IPAddress.TryParse(b, out IPAddress? ipB) && Unificar(ipA).Equals(Unificar(ipB));

        // localhost o una dirección de esta máquina: 127.0.0.0/8, ::1, 0.0.0.0 y :: (conectar a ellas es conectar aquí).
        private static bool IsLoopback(string servidor) =>
            servidor == "localhost" || (IPAddress.TryParse(servidor, out IPAddress? direccion) && IsDireccionLoopback(Unificar(direccion)));

        private static bool IsDireccionLoopback(IPAddress direccion) =>
            IPAddress.IsLoopback(direccion) || direccion.Equals(IPAddress.Any) || direccion.Equals(IPAddress.IPv6Any);

        // Sin zona (IPv6 '%1', '%lo': IPAddress.Equals e IsLoopback comparan también el ScopeId, y la zona no cambia de qué
        // dirección se trata; ScopeId solo existe en IPv6), IPv4 mapeada a IPv6 como IPv4 y cualquier dirección de loopback como 127.0.0.1, para comparar
        // conjuntos. La zona se quita antes de mirar el loopback.
        private static IPAddress Unificar(IPAddress direccion)
        {
            IPAddress sinZona = direccion.AddressFamily == AddressFamily.InterNetworkV6 && direccion.ScopeId != 0 ? new IPAddress(direccion.GetAddressBytes()) : direccion;
            IPAddress unificada = sinZona.IsIPv4MappedToIPv6 ? sinZona.MapToIPv4() : sinZona;
            return IsDireccionLoopback(unificada) ? IPAddress.Loopback : unificada;
        }

        // Sin red: la propia IP, localhost o, para una entrada, lo ya resuelto y aún recordado. Lo del destino nunca se toma
        // de lo recordado (isDestino): se resuelve en cada comprobación, lo más cerca posible de la conexión.
        private bool TryDireccionesSinRed(string servidor, bool isDestino, out HashSet<IPAddress>? direcciones)
        {
            direcciones = null;
            if (IPAddress.TryParse(servidor, out IPAddress? literal))
                direcciones = [Unificar(literal)];
            else if (servidor == "localhost")
                direcciones = [IPAddress.Loopback];
            else if (!isDestino && _resueltos.TryGetValue(servidor, out (long Caduca, HashSet<IPAddress> Direcciones) recordado) && recordado.Caduca > Environment.TickCount64)
                direcciones = recordado.Direcciones;
            return direcciones is not null;
        }

        // Direcciones de un nombre (o la propia IP), o sin ellas si no se pudo resolver: fallo, plazo de 3 s vencido (se deja
        // de esperar aunque la resolución siga) o demasiadas resoluciones en curso (IsCupoLleno). Si ya hay una en curso para ese nombre, se
        // espera esa misma (también si la pidió otra llamada: está en curso, así que es igual de reciente).
        private async Task<Resolucion> ResolverAsync(string servidor, bool isDestino, CancellationToken cancellationToken)
        {
            if (TryDireccionesSinRed(servidor, isDestino, out HashSet<IPAddress>? conocidas))
                return new Resolucion(conocidas, IsCupoLleno: false);

            Task<Resolucion>? resolucion;
            lock (_candadoEnCurso)
            {
                if (!_enCurso.TryGetValue(servidor, out resolucion))
                {
                    if (_enCurso.Count >= MaxResolucionesEnCurso)
                        return new Resolucion(null, IsCupoLleno: true);

                    // En el grupo de hilos (getaddrinfo puede bloquear el hilo que lo llama) y fuera de este candado: su
                    // 'finally' lo necesita para quitarse de _enCurso, así que no puede terminar antes de anotarla.
                    resolucion = Task.Run(() => ResolverEnCursoAsync(servidor, isRecordar: !isDestino));
                    _enCurso[servidor] = resolucion;
                }
            }

            try
            {
                return await resolucion.WaitAsync(PlazoResolucion, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new Resolucion(null, IsCupoLleno: false);
            }
        }

        // Una resolución compartida: con su propio plazo (por si el resolver lo respeta), sin el token de quien la pidió
        // (la esperan varios); si acaba bien y la pidió una entrada (isRecordar), se recuerda (las del destino no). Cualquier
        // fallo (también HostNotFound, que no distingue un nombre inexistente de un DNS que falla) es no resuelto y no se
        // recuerda. Al terminar deja de estar en curso.
        private async Task<Resolucion> ResolverEnCursoAsync(string servidor, bool isRecordar)
        {
            try
            {
                using CancellationTokenSource plazo = new(PlazoResolucion);
                HashSet<IPAddress> direcciones = [];
                foreach (IPAddress direccion in await _resolver(servidor, plazo.Token).ConfigureAwait(false))
                    _ = direcciones.Add(Unificar(direccion));
                if (direcciones.Count == 0)
                    return default;

                if (isRecordar)
                    _resueltos[servidor] = (Environment.TickCount64 + (long)DuracionRecuerdo.TotalMilliseconds, direcciones);
                return new Resolucion(direcciones, IsCupoLleno: false);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException)
            {
                return default;
            }
            finally
            {
                lock (_candadoEnCurso)
                    _ = _enCurso.Remove(servidor);
            }
        }
    }
}
