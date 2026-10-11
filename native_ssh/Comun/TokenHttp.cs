using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
    // los saltos de línea finales; un token que empieza o termina con espacios se rechaza (los extremos de una cabecera HTTP
    // no se conservan: nunca coincidiría). Los clientes lo envían en cada petición: 'Authorization: Bearer <token>'.
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

        private const int PermisosAjenos = 0x3F; // 0077: cualquier permiso del grupo o de otros.
        private const int PermisosTodos = 0xFFF; // 07777

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

        // Ver la cabecera: todo se comprueba en el descriptor abierto sin seguir el enlace final ni bloquearse.
        [UnsupportedOSPlatform("windows")]
        private static bool TryLeerFichero(string ruta, uint usuario, out string? valor, out string? error)
        {
            valor = null;
            if (!OperatingSystem.IsLinux())
            {
                error = $"{VariableFichero} solo se admite en Linux (allí se comprueban el tipo, el dueño y los permisos del fichero abierto): en este sistema usa {VariableToken}.";
                return false;
            }

            using SafeFileHandle descriptor = InteropLinux.AbrirSinSeguirEnlace(ruta, out int errorApertura);
            if (errorApertura != 0)
            {
                error = errorApertura switch
                {
                    InteropLinux.ErrorEnlace => $"El fichero del token del modo HTTP ({ruta}) es un enlace simbólico: indica la ruta del fichero real.",
                    InteropLinux.ErrorNoExiste => $"No existe el fichero del token del modo HTTP ({ruta}, de {VariableFichero}).",
                    _ => $"No se pudo abrir el fichero del token del modo HTTP ({ruta}): {Marshal.GetPInvokeErrorMessage(errorApertura)}."
                };
                return false;
            }

            if (!InteropLinux.TryLeerEstado(descriptor, out InteropLinux.Estado estado, out int errorEstado))
            {
                error = $"No se pudo comprobar el fichero del token del modo HTTP ({ruta}): {(errorEstado != 0 ? Marshal.GetPInvokeErrorMessage(errorEstado) : "statx no devolvió el tipo, los permisos y el dueño")}.";
                return false;
            }

            int tipo = estado.Modo & InteropLinux.TipoMascara;
            if (tipo != InteropLinux.TipoFichero)
            {
                error = tipo == InteropLinux.TipoDirectorio
                    ? $"El fichero del token del modo HTTP ({ruta}) es un directorio: indica la ruta del fichero que contiene el token."
                    : $"El fichero del token del modo HTTP ({ruta}) no es un fichero normal (es {DescribirTipo(tipo)}): indica la ruta de un fichero normal con el token.";
                return false;
            }

            if (estado.Dueno != usuario)
            {
                error = $"El fichero del token del modo HTTP ({ruta}) es del usuario con uid {estado.Dueno}, no del que ejecuta native_ssh (uid {usuario}): su dueño podría cambiarlo. Usa un fichero tuyo (chmod 600).";
                return false;
            }

            if ((estado.Modo & PermisosAjenos) != 0)
            {
                error = $"El fichero del token del modo HTTP ({ruta}) tiene permisos {Convert.ToString(estado.Modo & PermisosTodos, 8).PadLeft(4, '0')}: otros usuarios de la máquina podrían leerlo o cambiarlo. Déjalo solo para su dueño (chmod 600 {ruta}).";
                return false;
            }

            if (estado.Longitud > MaxBytesFichero)
            {
                error = $"El fichero del token del modo HTTP ({ruta}) ocupa {estado.Longitud} bytes: no parece un token (máximo {MaxBytesFichero}).";
                return false;
            }

            byte[] bytes = new byte[estado.Longitud];
            try
            {
                int leidos = 0;
                for (int n = 1; leidos < bytes.Length && n > 0; leidos += n)
                    n = RandomAccess.Read(descriptor, bytes.AsSpan(leidos), leidos);
                valor = Encoding.UTF8.GetString(bytes, 0, leidos).TrimEnd('\n', '\r');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"No se pudo leer el fichero del token del modo HTTP ({ruta}): {ex.Message}";
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            if (valor.Length == 0)
            {
                error = $"El fichero del token del modo HTTP ({ruta}) está vacío.";
                return false;
            }

            error = null;
            return true;
        }

        [SupportedOSPlatform("linux")]
        private static string DescribirTipo(int tipo) => tipo switch
        {
            InteropLinux.TipoFifo => "una FIFO",
            InteropLinux.TipoSocket => "un socket",
            InteropLinux.TipoEnlace => "un enlace simbólico",
            _ => "un dispositivo"
        };
    }
}
