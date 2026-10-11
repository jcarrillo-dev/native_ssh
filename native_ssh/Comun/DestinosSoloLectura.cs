using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace NativeSsh.Comun
{
    // Destinos en modo solo lectura (CI6): configuración local de native_ssh (variable NATIVE_SSH_SOLO_LECTURA), que ninguna
    // herramienta puede cambiar. Para ellos se rechazan ssh_exec, ssh_input, ssh_write_file y ssh_replace_file_content antes de
    // enviar nada; las herramientas de lectura y ssh_set_cwd (que solo ejecuta órdenes fijas de native_ssh) siguen
    // funcionando. No hay clasificador de comandos: ssh_exec se rechaza entero (un clasificador daría falsa seguridad).
    //
    // Formato: entradas separadas por comas (los blancos alrededor se ignoran), cada una '[usuario@]servidor[:puerto]'
    // (IPv6 entre corchetes con puerto: '[::1]:22') o '*' (todos los destinos). Sin usuario vale para cualquier usuario y sin
    // puerto para cualquier puerto. El servidor se compara sin distinguir mayúsculas y sin el punto final de un nombre
    // completo; si el texto no coincide, se comparan también sus direcciones (DNS o /etc/hosts, con un plazo de 3 s): así
    // 'prod.ejemplo.com' cubre también su IP y 'localhost' cubre 127.0.0.1 y ::1. Si un nombre no se puede resolver, solo
    // cuenta su texto (un destino que no se resuelve tampoco se puede conectar). Lo resuelto se recuerda 5 minutos.
    public sealed class DestinosSoloLectura
    {
        public const string Variable = "NATIVE_SSH_SOLO_LECTURA";

        private static readonly TimeSpan PlazoResolucion = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan DuracionRecuerdo = TimeSpan.FromMinutes(5);
        private readonly ConcurrentDictionary<string, (long Caduca, HashSet<IPAddress> Direcciones)> _resueltos = new(StringComparer.Ordinal);

        private readonly Entrada[] _entradas;
        private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolver;

        private DestinosSoloLectura(Entrada[] entradas, Func<string, CancellationToken, Task<IPAddress[]>>? resolver)
        {
            _entradas = entradas;
            _resolver = resolver ?? Dns.GetHostAddressesAsync;
        }

        public static DestinosSoloLectura Ninguno { get; } = new([], null);

        public bool IsVacio => _entradas.Length == 0;

        private readonly record struct Entrada(string? Usuario, string Servidor, int? Puerto)
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
                    error = $"{Variable}: la entrada '{trozo}' no es válida. Usa '[usuario@]servidor[:puerto]' ('[::1]:22' para IPv6 con puerto) o '*', separadas por comas.";
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
                entrada = new Entrada(null, "*", null);
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

            if (servidor.Length == 0)
                return false;

            entrada = new Entrada(usuario, Normalizar(servidor), puerto);
            return true;
        }

        private static bool TryParsearPuerto(string texto, out int? puerto)
        {
            bool isValido = int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out int valor) && valor is > 0 and <= 65535;
            puerto = isValido ? valor : null;
            return isValido;
        }

        private static string Normalizar(string servidor) => servidor.TrimEnd('.').ToLowerInvariant();

        // Si el destino está en modo solo lectura. Primero sin red (usuario, puerto y texto del servidor); solo si alguna
        // entrada casa en usuario y puerto pero no en el texto, se comparan las direcciones de los dos nombres.
        public async Task<bool> IsSoloLecturaAsync(DestinoSsh destino, CancellationToken cancellationToken)
        {
            if (_entradas.Length == 0)
                return false;

            string servidor = Normalizar(destino.Servidor);
            List<Entrada> porResolver = [];
            foreach (Entrada entrada in _entradas)
            {
                bool isUsuarioYPuerto = (entrada.Usuario is null || entrada.Usuario == destino.Usuario) && (entrada.Puerto is null || entrada.Puerto == destino.Puerto);
                if (!isUsuarioYPuerto)
                    continue;

                if (entrada.IsTodos || entrada.Servidor == servidor || IsMismaIpLiteral(entrada.Servidor, servidor))
                    return true;

                porResolver.Add(entrada);
            }

            if (porResolver.Count == 0)
                return false;

            // Todas a la vez: un nombre que no se resuelve puede tardar todo el plazo.
            Task<HashSet<IPAddress>> delDestino = ResolverAsync(servidor, cancellationToken);
            List<Task<HashSet<IPAddress>>> deLasEntradas = porResolver.ConvertAll(e => ResolverAsync(e.Servidor, cancellationToken));
            HashSet<IPAddress> direcciones = await delDestino.ConfigureAwait(false);
            foreach (HashSet<IPAddress> deLaEntrada in await Task.WhenAll(deLasEntradas).ConfigureAwait(false))
                if (deLaEntrada.Overlaps(direcciones))
                    return true;

            return false;
        }

        private static bool IsMismaIpLiteral(string a, string b) =>
            IPAddress.TryParse(a, out IPAddress? ipA) && IPAddress.TryParse(b, out IPAddress? ipB) && Unificar(ipA).Equals(Unificar(ipB));

        private static IPAddress Unificar(IPAddress direccion) => direccion.IsIPv4MappedToIPv6 ? direccion.MapToIPv4() : direccion;

        // Direcciones de un nombre (o la propia IP), con plazo; si no se puede resolver, ninguna. Se recuerdan 5 minutos
        // (también si no se pudo resolver): cada ssh_exec a un destino que no está en la lista no espera otra vez al DNS.
        private async Task<HashSet<IPAddress>> ResolverAsync(string servidor, CancellationToken cancellationToken)
        {
            HashSet<IPAddress> direcciones = [];
            if (IPAddress.TryParse(servidor, out IPAddress? literal))
            {
                _ = direcciones.Add(Unificar(literal));
                return direcciones;
            }

            long ahora = Environment.TickCount64;
            if (_resueltos.TryGetValue(servidor, out (long Caduca, HashSet<IPAddress> Direcciones) recordado) && recordado.Caduca > ahora)
                return recordado.Direcciones;

            using CancellationTokenSource plazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            plazo.CancelAfter(PlazoResolucion);
            try
            {
                foreach (IPAddress direccion in await _resolver(servidor, plazo.Token).ConfigureAwait(false))
                    _ = direcciones.Add(Unificar(direccion));
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // Sin resolver: cuenta solo el texto.
            }

            _resueltos[servidor] = (ahora + (long)DuracionRecuerdo.TotalMilliseconds, direcciones);
            return direcciones;
        }
    }
}
