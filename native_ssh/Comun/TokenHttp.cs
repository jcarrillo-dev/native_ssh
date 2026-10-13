using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace NativeSsh.Comun
{
    // Token obligatorio de los modos HTTP (--http y --http-unsecure): sin él, cualquier proceso de la máquina (o de la red,
    // con --http-unsecure) que alcance el puerto podría ejecutar comandos en los servidores. Sale de NATIVE_SSH_TOKEN_HTTP o
    // del fichero de NATIVE_SSH_TOKEN_HTTP_FICHERO (solo en Linux), que debe ser un fichero normal (no un enlace, una FIFO ni
    // un directorio) del usuario del proceso y sin ningún permiso para el grupo ni para otros (0600 o 0400): si no, otro
    // usuario de la máquina podría leerlo o elegir su contenido. Se abre sin seguir un enlace en el último componente y sin
    // bloquearse (O_NOFOLLOW | O_NONBLOCK: una FIFO no cuelga el arranque), y el tipo, el dueño y los permisos se comprueban
    // en el descriptor ya abierto (statx), no en la ruta: lo comprobado es lo que se lee, sin carrera entre comprobar y
    // abrir. Un enlace en un directorio intermedio se acepta a propósito (p. ej. /home -> /var/home): aunque lo controle
    // otro usuario, solo puede llevar a un fichero propio y 0600, cuyo contenido ese usuario no conoce. Del fichero se quitan
    // los saltos de línea finales; un token con más de una línea, o que empieza o termina con espacios, se rechaza (una
    // cabecera HTTP no lleva saltos de línea y no conserva sus extremos: nunca coincidiría). Los clientes lo envían en cada petición: 'Authorization: Bearer <token>'.
    // Se guarda y se compara su SHA-256 en tiempo constante (CryptographicOperations.FixedTimeEquals): ni la duración de la
    // comparación ni la longitud del token presentado dicen cuánto se parece al bueno. Ningún mensaje lleva el token.
    // El modo stdio no lo lee.
    public sealed class TokenHttp
    {
        public const string VariableToken = "NATIVE_SSH_TOKEN_HTTP";
        public const string VariableFichero = "NATIVE_SSH_TOKEN_HTTP_FICHERO";
        public const int MinCaracteres = 16;
        private const string Esquema = "Bearer";

        private static readonly DescripcionSecreto Descripcion = new("del token del modo HTTP", "el token", "un token", VariableFichero, VariableToken);

        private static readonly string ComoConfigurarlo = $"define {VariableToken} con el token (al menos {MinCaracteres} caracteres) o {VariableFichero} con la ruta de un fichero que lo contenga y que solo pueda leer su dueño (chmod 600). Los clientes lo envían en cada petición con la cabecera 'Authorization: {Esquema} <token>'.";

        private readonly byte[] _hash;

        private TokenHttp(byte[] hash) => _hash = hash;

        // entorno: lectura de las variables (Environment.GetEnvironmentVariable; en las pruebas, un diccionario).
        [UnsupportedOSPlatform("windows")]
        public static bool TryCargar(Func<string, string?> entorno, out TokenHttp? token, out string? error) =>
            TryCargar(entorno, OperatingSystem.IsLinux() ? InteropLinux.UsuarioEfectivo() : 0, out token, out error);

        // usuario: el uid que debe ser dueño del fichero (el efectivo del proceso; en las pruebas, otro).
        [UnsupportedOSPlatform("windows")]
        internal static bool TryCargar(Func<string, string?> entorno, uint usuario, out TokenHttp? token, out string? error)
        {
            token = null;
            string? valor = entorno(VariableToken) is { Length: > 0 } v ? v : null;
            string? fichero = entorno(VariableFichero) is { Length: > 0 } f ? f : null;
            bool hasValor = valor is not null;
            bool hasFichero = fichero is not null;
            if (hasValor && hasFichero)
            {
                error = $"Define solo una de {VariableToken} o {VariableFichero} para el token del modo HTTP.";
                return false;
            }

            if (!hasValor && !hasFichero)
            {
                error = $"El modo HTTP necesita un token y no hay ninguno configurado: {ComoConfigurarlo} Sin token no se arranca: cualquier proceso que alcance el puerto podría ejecutar comandos en tus servidores.";
                return false;
            }

            if (hasFichero && !TryLeerFichero(fichero!, usuario, out valor, out error))
                return false;

            string origen = hasFichero ? $"fichero {fichero}" : VariableToken;
            if (valor.AsSpan().IndexOfAny('\n', '\r') >= 0)
            {
                error = $"El token del modo HTTP ({origen}) tiene más de una línea: una cabecera HTTP no puede llevar saltos de línea, así que nunca coincidiría. Deja solo el token, en una línea.";
                return false;
            }

            if (char.IsWhiteSpace(valor![0]) || char.IsWhiteSpace(valor[^1]))
            {
                error = $"El token del modo HTTP ({origen}) empieza o termina con espacios: los extremos de una cabecera HTTP no se conservan, así que nunca coincidiría. Quítalos.";
                return false;
            }

            if (valor.Length < MinCaracteres)
            {
                error = $"El token del modo HTTP ({origen}) es demasiado corto: necesita al menos {MinCaracteres} caracteres.";
                return false;
            }

            token = new TokenHttp(SHA256.HashData(Encoding.UTF8.GetBytes(valor)));
            error = null;
            return true;
        }

        // Cabecera Authorization de una petición: válida solo con el esquema Bearer (sin distinguir mayúsculas, RFC 7235) y
        // exactamente el token.
        public bool IsValido(string? cabecera)
        {
            if (string.IsNullOrEmpty(cabecera))
                return false;

            int espacio = cabecera.IndexOf(' ', StringComparison.Ordinal);
            if (espacio < 0 || !cabecera.AsSpan(0, espacio).Equals(Esquema, StringComparison.OrdinalIgnoreCase))
                return false;

            byte[] presentado = SHA256.HashData(Encoding.UTF8.GetBytes(cabecera[(espacio + 1)..]));
            return CryptographicOperations.FixedTimeEquals(presentado, _hash);
        }

        // Ver la cabecera y FicheroSecreto: todo se comprueba en el descriptor abierto sin seguir el enlace final ni bloquearse.
        [UnsupportedOSPlatform("windows")]
        private static bool TryLeerFichero(string ruta, uint usuario, out string? valor, out string? error) =>
            FicheroSecreto.TryLeer(ruta, usuario, Descripcion, out valor, out error);
    }
}
