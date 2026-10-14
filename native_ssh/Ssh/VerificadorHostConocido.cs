using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;

namespace NativeSsh.Ssh
{
    public enum ResultadoHostEnum
    {
        Conocido,
        Desconocido,
        ClaveDistinta,
        Revocado
    }

    // Comprueba la clave del servidor contra known_hosts (OpenSSH): patrones planos, comodines,
    // negaciones, '[host]:puerto', entradas hasheadas '|1|sal|hash' y el marcador @revoked.
    public sealed class VerificadorHostConocido(string[] rutas)
    {
        public string[] Rutas { get; } = rutas;

        // Las líneas van en una lista rentada que se devuelve al salir (un 'using' no se puede pasar por ref a LeerLineas).
        public ResultadoHostEnum Verificar(string servidor, int puerto, string tipoClave, ReadOnlySpan<byte> clave)
        {
            PooledListRef<string> lineas = new();
            try
            {
                LeerLineas(ref lineas);
                return Verificar(lineas.Span, servidor, puerto, tipoClave, clave);
            }
            finally
            {
                lineas.Dispose();
            }
        }

        public HashSet<string> ObtenerTiposClave(string servidor, int puerto)
        {
            PooledListRef<string> lineas = new();
            try
            {
                LeerLineas(ref lineas);
                return ObtenerTiposClave(lineas.Span, servidor, puerto);
            }
            finally
            {
                lineas.Dispose();
            }
        }

        // Todas las líneas de los known_hosts que existen, en orden (antes se leían a demanda con un iterador; el resultado de
        // la comprobación es el mismo, porque la de @revoked solo adelanta la respuesta).
        private void LeerLineas(scoped ref PooledListRef<string> lineas)
        {
            foreach (string ruta in Rutas)
                if (File.Exists(ruta))
                    foreach (string linea in File.ReadLines(ruta))
                        lineas.Add(linea);
        }

        public static HashSet<string> ObtenerTiposClave(ReadOnlySpan<string> lineas, string servidor, int puerto)
        {
            string nombre = puerto == 22 ? servidor : $"[{servidor}]:{puerto}";
            // Las claves de cada línea son arrays propios (TryDecodificar): las listas rentadas solo guardan referencias, que se
            // limpian al devolverlas.
            using PooledListRef<(string Tipo, byte[] Clave)> conocidas = new();
            using PooledListRef<byte[]> revocadas = new();

            foreach (string linea in lineas)
            {
                string[] campos = linea.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (campos.Length < 3 || campos[0].StartsWith('#'))
                    continue;

                int indice = 0;
                string? marcador = campos[0].StartsWith('@') ? campos[indice++] : null;
                if (marcador is not null and not "@revoked")
                    continue;

                if (campos.Length < indice + 3 || !IsCoincideHost(campos[indice], nombre))
                    continue;

                if (!TryDecodificar(campos[indice + 2], out byte[] claveLinea))
                    continue;

                if (marcador == "@revoked")
                    revocadas.Add(claveLinea);
                else
                    conocidas.Add((campos[indice + 1], claveLinea));
            }

            HashSet<string> tipos = new(StringComparer.Ordinal);
            foreach ((string tipo, byte[] claveValida) in conocidas)
            {
                bool isRevocada = false;
                foreach (byte[] rev in revocadas)
                    if (claveValida.AsSpan().SequenceEqual(rev))
                    {
                        isRevocada = true;
                        break;
                    }

                if (!isRevocada)
                    _ = tipos.Add(tipo);
            }

            return tipos;
        }

        public static ResultadoHostEnum Verificar(ReadOnlySpan<string> lineas, string servidor, int puerto, string tipoClave, ReadOnlySpan<byte> clave)
        {
            string nombre = puerto == 22 ? servidor : $"[{servidor}]:{puerto}";
            bool hasMismoTipo = false;
            bool isConocido = false;
            foreach (string linea in lineas)
            {
                string[] campos = linea.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (campos.Length < 3 || campos[0].StartsWith('#'))
                    continue;

                int indice = 0;
                string? marcador = campos[0].StartsWith('@') ? campos[indice++] : null;
                if (marcador is not null and not "@revoked")
                    continue;

                if (campos.Length < indice + 3 || !IsCoincideHost(campos[indice], nombre))
                    continue;

                if (!TryDecodificar(campos[indice + 2], out byte[] claveLinea))
                    continue;

                bool isMismaClave = clave.SequenceEqual(claveLinea);
                if (marcador == "@revoked")
                {
                    if (isMismaClave)
                        return ResultadoHostEnum.Revocado;
                    continue;
                }

                if (!string.Equals(campos[indice + 1], tipoClave, StringComparison.Ordinal))
                    continue;

                hasMismoTipo = true;
                isConocido |= isMismaClave;
            }

            return isConocido ? ResultadoHostEnum.Conocido
                : hasMismoTipo ? ResultadoHostEnum.ClaveDistinta
                : ResultadoHostEnum.Desconocido;
        }

        // Tipo de clave leído del propio blob (string SSH inicial). SSH.NET informa del algoritmo de firma
        // (p. ej. rsa-sha2-512), que no coincide con el tipo guardado en known_hosts (ssh-rsa).
        public static string? LeerTipo(ReadOnlySpan<byte> blob)
        {
            if (blob.Length < 4)
                return null;

            int longitud = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(blob);
            return longitud is > 0 and <= 64 && blob.Length >= 4 + longitud ? Encoding.ASCII.GetString(blob.Slice(4, longitud)) : null;
        }

        private static bool IsCoincideHost(string patrones, string nombre)
        {
            if (patrones.StartsWith("|1|", StringComparison.Ordinal))
                return IsCoincideHasheado(patrones, nombre);

            bool isCoincide = false;
            foreach (string patron in patrones.Split(','))
            {
                bool isNegado = patron.StartsWith('!');
                if (!IsCoincideComodin(isNegado ? patron[1..] : patron, nombre))
                    continue;
                if (isNegado)
                    return false;
                isCoincide = true;
            }

            return isCoincide;
        }

        [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "HMAC-SHA1 es el formato de las entradas hasheadas de known_hosts (|1|sal|hash); no se elige el algoritmo.")]
        private static bool IsCoincideHasheado(string entrada, string nombre)
        {
            string[] partes = entrada.Split('|');
            if (partes.Length != 4 || !TryDecodificar(partes[2], out byte[] sal) || !TryDecodificar(partes[3], out byte[] hash))
                return false;

            byte[] calculado = HMACSHA1.HashData(sal, Encoding.UTF8.GetBytes(nombre.ToLowerInvariant()));
            return CryptographicOperations.FixedTimeEquals(calculado, hash);
        }

        // '*' = cualquier secuencia, '?' = un carácter. Sin distinguir mayúsculas, como OpenSSH.
        private static bool IsCoincideComodin(ReadOnlySpan<char> patron, ReadOnlySpan<char> texto)
        {
            int p = 0, t = 0, estrella = -1, marca = 0;
            while (t < texto.Length)
                if (p < patron.Length && (patron[p] == '?' || char.ToLowerInvariant(patron[p]) == char.ToLowerInvariant(texto[t])))
                {
                    p++;
                    t++;
                }
                else if (p < patron.Length && patron[p] == '*')
                {
                    estrella = p++;
                    marca = t;
                }
                else if (estrella >= 0)
                {
                    p = estrella + 1;
                    t = ++marca;
                }
                else
                    return false;

            while (p < patron.Length && patron[p] == '*')
                p++;
            return p == patron.Length;
        }

        private static bool TryDecodificar(string base64, out byte[] datos)
        {
            try
            {
                datos = Convert.FromBase64String(base64);
                return true;
            }
            catch (FormatException)
            {
                datos = [];
                return false;
            }
        }
    }
}
