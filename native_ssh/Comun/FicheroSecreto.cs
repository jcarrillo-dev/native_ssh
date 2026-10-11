using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NativeSsh.Comun
{
    // Lo que contiene un fichero secreto, para los mensajes: Del = "del token del modo HTTP", ElSecreto = "el token",
    // UnSecreto = "un token"; Variable: la que da la ruta; Alternativa: lo que se usa fuera de Linux.
    internal readonly record struct DescripcionSecreto(string Del, string ElSecreto, string UnSecreto, string Variable, string Alternativa);

    // Lectura de un fichero con un secreto (el token del modo HTTP, la passphrase de las claves): debe ser un fichero normal
    // (no un enlace, una FIFO ni un directorio) del usuario del proceso y sin ningún permiso para el grupo ni para otros
    // (0600 o 0400): si no, otro usuario de la máquina podría leerlo o elegir su contenido. Se abre sin seguir un enlace en
    // el último componente y sin bloquearse (O_NOFOLLOW | O_NONBLOCK: una FIFO no cuelga a quien lo lee), y el tipo, el dueño
    // y los permisos se comprueban en el descriptor ya abierto (statx), no en la ruta: lo comprobado es lo que se lee, sin
    // carrera entre comprobar y abrir. Un enlace en un directorio intermedio se acepta a propósito (p. ej. /home -> /var/home):
    // aunque lo controle otro usuario, solo puede llevar a un fichero propio y 0600, cuyo contenido ese usuario no conoce.
    // Se quitan los saltos de línea finales. Ningún mensaje lleva el contenido. Solo en Linux.
    [UnsupportedOSPlatform("windows")]
    internal static class FicheroSecreto
    {
        public const int MaxBytes = 4096;

        private const int PermisosAjenos = 0x3F; // 0077: cualquier permiso del grupo o de otros.
        private const int PermisosTodos = 0xFFF; // 07777

        // usuario: el uid que debe ser dueño del fichero (el efectivo del proceso; en las pruebas, otro).
        public static bool TryLeer(string ruta, uint usuario, DescripcionSecreto descripcion, out string? valor, out string? error)
        {
            valor = null;
            if (!OperatingSystem.IsLinux())
            {
                error = $"{descripcion.Variable} solo se admite en Linux (allí se comprueban el tipo, el dueño y los permisos del fichero abierto): en este sistema usa {descripcion.Alternativa}.";
                return false;
            }

            using SafeFileHandle descriptor = InteropLinux.AbrirSinSeguirEnlace(ruta, out int errorApertura);
            if (errorApertura != 0)
            {
                error = errorApertura switch
                {
                    InteropLinux.ErrorEnlace => $"El fichero {descripcion.Del} ({ruta}) es un enlace simbólico: indica la ruta del fichero real.",
                    InteropLinux.ErrorNoExiste => $"No existe el fichero {descripcion.Del} ({ruta}, de {descripcion.Variable}).",
                    _ => $"No se pudo abrir el fichero {descripcion.Del} ({ruta}): {Marshal.GetPInvokeErrorMessage(errorApertura)}."
                };
                return false;
            }

            if (!InteropLinux.TryLeerEstado(descriptor, out InteropLinux.Estado estado, out int errorEstado))
            {
                error = $"No se pudo comprobar el fichero {descripcion.Del} ({ruta}): {(errorEstado != 0 ? Marshal.GetPInvokeErrorMessage(errorEstado) : "statx no devolvió el tipo, los permisos y el dueño")}.";
                return false;
            }

            int tipo = estado.Modo & InteropLinux.TipoMascara;
            if (tipo != InteropLinux.TipoFichero)
            {
                error = tipo == InteropLinux.TipoDirectorio
                    ? $"El fichero {descripcion.Del} ({ruta}) es un directorio: indica la ruta del fichero que contiene {descripcion.ElSecreto}."
                    : $"El fichero {descripcion.Del} ({ruta}) no es un fichero normal (es {DescribirTipo(tipo)}): indica la ruta de un fichero normal con {descripcion.ElSecreto}.";
                return false;
            }

            if (estado.Dueno != usuario)
            {
                error = $"El fichero {descripcion.Del} ({ruta}) es del usuario con uid {estado.Dueno}, no del que ejecuta native_ssh (uid {usuario}): su dueño podría cambiarlo. Usa un fichero tuyo (chmod 600).";
                return false;
            }

            if ((estado.Modo & PermisosAjenos) != 0)
            {
                error = $"El fichero {descripcion.Del} ({ruta}) tiene permisos {Convert.ToString(estado.Modo & PermisosTodos, 8).PadLeft(4, '0')}: otros usuarios de la máquina podrían leerlo o cambiarlo. Déjalo solo para su dueño (chmod 600 {ruta}).";
                return false;
            }

            if (estado.Longitud > MaxBytes)
            {
                error = $"El fichero {descripcion.Del} ({ruta}) ocupa {estado.Longitud} bytes: no parece {descripcion.UnSecreto} (máximo {MaxBytes}).";
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
                error = $"No se pudo leer el fichero {descripcion.Del} ({ruta}): {ex.Message}";
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            if (valor.Length == 0)
            {
                error = $"El fichero {descripcion.Del} ({ruta}) está vacío.";
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
