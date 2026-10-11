using System.Globalization;

namespace NativeSsh.Comun
{
    /// Destino SSH normalizado. Se parsea una vez por petición (ruta fría).
    public readonly record struct DestinoSsh
    {
        public const int PuertoPorDefecto = 22;

        private DestinoSsh(string usuario, string servidor, int puerto)
        {
            Usuario = usuario;
            Servidor = servidor;
            Puerto = puerto;
            Clave = $"{usuario}@{servidor}:{puerto}";
        }

        // Claves de los IdentityFile de la configuración de ssh (ResolutorConfiguracionSsh), ya expandidas; null: las de
        // NATIVE_SSH_IDENTIDADES (o las por defecto). No forma parte de la clave de la sesión.
        public IReadOnlyList<string>? Identidades { get; private init; }

        public string Usuario { get; }
        public string Servidor { get; }
        public int Puerto { get; }
        public string Clave { get; }
        public string ClaveServidor => $"{Servidor.ToLowerInvariant()}:{Puerto}";

        // Destino ya resuelto con la configuración de ssh (ssh -G).
        public static DestinoSsh Resuelto(string usuario, string servidor, int puerto, IReadOnlyList<string>? identidades) =>
            new(usuario, servidor, puerto) { Identidades = identidades };

        public static bool TryParse(ReadOnlySpan<char> entrada, string usuarioPorDefecto, out DestinoSsh destino) =>
            TryParse(entrada, usuarioPorDefecto, out destino, out _, out _);

        // isUsuarioExplicito / isPuertoExplicito: la entrada los lleva (si no, son los por defecto); con la configuración de
        // ssh, lo explícito manda sobre ella, como en ssh.
        public static bool TryParse(ReadOnlySpan<char> entrada, string usuarioPorDefecto, out DestinoSsh destino, out bool isUsuarioExplicito, out bool isPuertoExplicito)
        {
            destino = default;
            isUsuarioExplicito = false;
            isPuertoExplicito = false;
            ReadOnlySpan<char> resto = entrada.Trim();
            if (resto.IsEmpty || resto.ContainsAny(' ', '\t'))
                return false;

            ReadOnlySpan<char> usuario = usuarioPorDefecto;
            int indiceArroba = resto.IndexOf('@');
            if (indiceArroba >= 0)
            {
                usuario = resto[..indiceArroba];
                resto = resto[(indiceArroba + 1)..];
                isUsuarioExplicito = true;
            }

            if (usuario.IsEmpty || resto.IsEmpty)
                return false;

            int puerto = PuertoPorDefecto;
            ReadOnlySpan<char> servidor;
            if (resto[0] == '[')
            {
                int cierre = resto.IndexOf(']');
                if (cierre <= 1)
                    return false;

                servidor = resto[1..cierre];
                ReadOnlySpan<char> trasCorchete = resto[(cierre + 1)..];
                if (!trasCorchete.IsEmpty && (trasCorchete[0] != ':' || !TryParsePuerto(trasCorchete[1..], out puerto)))
                    return false;

                isPuertoExplicito = !trasCorchete.IsEmpty;
            }
            else
            {
                int dosPuntos = resto.IndexOf(':');
                bool isIpv6SinCorchetes = dosPuntos >= 0 && resto.LastIndexOf(':') != dosPuntos;
                if (dosPuntos < 0 || isIpv6SinCorchetes)
                    servidor = resto;
                else
                {
                    servidor = resto[..dosPuntos];
                    if (!TryParsePuerto(resto[(dosPuntos + 1)..], out puerto))
                        return false;

                    isPuertoExplicito = true;
                }
            }

            if (servidor.IsEmpty)
                return false;

            destino = new DestinoSsh(usuario.ToString(), servidor.ToString(), puerto);
            return true;
        }

        private static bool TryParsePuerto(ReadOnlySpan<char> texto, out int puerto) =>
            int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out puerto) && puerto is > 0 and <= 65535;
    }
}
