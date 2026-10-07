using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NativeSsh.Comun
{
    // Token obligatorio de los modos HTTP (--http y --http-unsecure): sin él, cualquier proceso de la máquina (o de la red,
    // con --http-unsecure) que alcance el puerto podría ejecutar comandos en los servidores. Sale de NATIVE_SSH_TOKEN_HTTP o
    // del fichero de NATIVE_SSH_TOKEN_HTTP_FICHERO, que no puede ser un enlace ni tener ningún permiso para el grupo ni para
    // otros (0600 o 0400): con permisos más abiertos, otro usuario de la máquina podría leerlo. Del fichero se quitan los
    // saltos de línea finales. Los clientes lo envían en cada petición: 'Authorization: Bearer <token>'.
    // Se guarda y se compara su SHA-256 en tiempo constante (CryptographicOperations.FixedTimeEquals): ni la duración de la
    // comparación ni la longitud del token presentado dicen cuánto se parece al bueno. Ningún mensaje lleva el token.
    // El modo stdio no lo lee.
    public sealed class TokenHttp
    {
        public const string VariableToken = "NATIVE_SSH_TOKEN_HTTP";
        public const string VariableFichero = "NATIVE_SSH_TOKEN_HTTP_FICHERO";
        public const int MinCaracteres = 16;
        private const int MaxBytesFichero = 4096;
        private const string Esquema = "Bearer";

        private const UnixFileMode PermisosAjenos = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        private static readonly string ComoConfigurarlo = $"define {VariableToken} con el token (al menos {MinCaracteres} caracteres) o {VariableFichero} con la ruta de un fichero que lo contenga y que solo pueda leer su dueño (chmod 600). Los clientes lo envían en cada petición con la cabecera 'Authorization: {Esquema} <token>'.";

        private readonly byte[] _hash;

        private TokenHttp(byte[] hash) => _hash = hash;

        // entorno: lectura de las variables (Environment.GetEnvironmentVariable; en las pruebas, un diccionario).
        [UnsupportedOSPlatform("windows")]
        public static bool TryCargar(Func<string, string?> entorno, out TokenHttp? token, out string? error)
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

            if (hasFichero && !TryLeerFichero(fichero!, out valor, out error))
                return false;

            if (valor!.Length < MinCaracteres)
            {
                error = $"El token del modo HTTP ({(hasFichero ? $"fichero {fichero}" : VariableToken)}) es demasiado corto: necesita al menos {MinCaracteres} caracteres.";
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

        // Los permisos se miran en el descriptor ya abierto (no en la ruta): lo comprobado es lo que se lee. Antes, el enlace
        // se rechaza sin seguirlo.
        [UnsupportedOSPlatform("windows")]
        private static bool TryLeerFichero(string ruta, out string? valor, out string? error)
        {
            valor = null;
            FileInfo info = new(ruta);
            if (info.LinkTarget is not null)
            {
                error = $"El fichero del token del modo HTTP ({ruta}) es un enlace simbólico: indica la ruta del fichero real.";
                return false;
            }

            if (!info.Exists)
            {
                error = $"No existe el fichero del token del modo HTTP ({ruta}, de {VariableFichero}).";
                return false;
            }

            try
            {
                using SafeFileHandle descriptor = File.OpenHandle(ruta);
                UnixFileMode modo = File.GetUnixFileMode(descriptor);
                if ((modo & PermisosAjenos) != 0)
                {
                    error = $"El fichero del token del modo HTTP ({ruta}) tiene permisos {Convert.ToString((int)modo, 8).PadLeft(4, '0')}: otros usuarios de la máquina podrían leerlo o cambiarlo. Déjalo solo para su dueño (chmod 600 {ruta}).";
                    return false;
                }

                long longitud = RandomAccess.GetLength(descriptor);
                if (longitud > MaxBytesFichero)
                {
                    error = $"El fichero del token del modo HTTP ({ruta}) ocupa {longitud} bytes: no parece un token (máximo {MaxBytesFichero}).";
                    return false;
                }

                byte[] bytes = new byte[longitud];
                int leidos = RandomAccess.Read(descriptor, bytes, 0);
                valor = Encoding.UTF8.GetString(bytes, 0, leidos).TrimEnd('\n', '\r');
                CryptographicOperations.ZeroMemory(bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"No se pudo leer el fichero del token del modo HTTP ({ruta}): {ex.Message}";
                return false;
            }

            if (valor.Length == 0)
            {
                error = $"El fichero del token del modo HTTP ({ruta}) está vacío.";
                return false;
            }

            error = null;
            return true;
        }
    }
}
