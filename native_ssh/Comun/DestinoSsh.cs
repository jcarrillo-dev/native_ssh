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

        public string Usuario { get; }
        public string Servidor { get; }
        public int Puerto { get; }
        public string Clave { get; }
        public string ClaveServidor => $"{Servidor.ToLowerInvariant()}:{Puerto}";

        public static bool TryParse(ReadOnlySpan<char> entrada, string usuarioPorDefecto, out DestinoSsh destino)
        {
            destino = default;
            ReadOnlySpan<char> resto = entrada.Trim();
            if (resto.IsEmpty || resto.ContainsAny(' ', '\t'))
                return false;

            ReadOnlySpan<char> usuario = usuarioPorDefecto;
            int indiceArroba = resto.IndexOf('@');
            if (indiceArroba >= 0)
            {
                usuario = resto[..indiceArroba];
                resto = resto[(indiceArroba + 1)..];
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
