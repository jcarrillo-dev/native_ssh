using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using NativeSsh.Ssh;

namespace NativeSsh.Benchmarks.Copias
{
    // Copia de VerificadorHostConocido tal como estaba en 7ffba5c (antes de pasar las líneas de known_hosts a una lista rentada):
    // solo para medir en la misma tanda el antes y el después (GeneracionesKnownHostsBenchmarks). Sin LeerTipo, que no se mide.
    public sealed class VerificadorHostConocidoAntes(IReadOnlyList<string> rutas)
    {
        public IReadOnlyList<string> Rutas { get; } = rutas;

        public ResultadoHostEnum Verificar(string servidor, int puerto, string tipoClave, ReadOnlySpan<byte> clave) =>
            Verificar(LeerLineas(), servidor, puerto, tipoClave, clave);

        public IReadOnlySet<string> ObtenerTiposClave(string servidor, int puerto) =>
            ObtenerTiposClave(LeerLineas(), servidor, puerto);

        private IEnumerable<string> LeerLineas()
        {
            foreach (string ruta in Rutas)
                if (File.Exists(ruta))
                    foreach (string linea in File.ReadLines(ruta))
                        yield return linea;
        }

        public static IReadOnlySet<string> ObtenerTiposClave(IEnumerable<string> lineas, string servidor, int puerto)
        {
            string nombre = puerto == 22 ? servidor : $"[{servidor}]:{puerto}";
            List<(string Tipo, byte[] Clave)> conocidas = [];
            List<byte[]> revocadas = [];

            foreach (string linea in lineas)
            {
                string[] campos = linea.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (campos.Length < 3 || campos[0].StartsWith('#'))
                    continue;

                int indice = 0;
                string? marcador = campos[0].StartsWith('@') ? campos[indice++] : null;
                if (marcador is not null and not "@revoked")
                    continue;

                if (campos.Length < indice + 3 || !CoincideHost(campos[indice], nombre))
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
                {
                    if (claveValida.AsSpan().SequenceEqual(rev))
                    {
                        isRevocada = true;
                        break;
                    }
                }

                if (!isRevocada)
                    _ = tipos.Add(tipo);
            }

            return tipos;
        }

        public static ResultadoHostEnum Verificar(IEnumerable<string> lineas, string servidor, int puerto, string tipoClave, ReadOnlySpan<byte> clave)
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

                if (campos.Length < indice + 3 || !CoincideHost(campos[indice], nombre))
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

        private static bool CoincideHost(string patrones, string nombre)
        {
            if (patrones.StartsWith("|1|", StringComparison.Ordinal))
                return CoincideHasheado(patrones, nombre);

            bool isCoincide = false;
            foreach (string patron in patrones.Split(','))
            {
                bool isNegado = patron.StartsWith('!');
                if (!CoincideComodin(isNegado ? patron[1..] : patron, nombre))
                    continue;
                if (isNegado)
                    return false;
                isCoincide = true;
            }

            return isCoincide;
        }

        [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "HMAC-SHA1 es el formato de las entradas hasheadas de known_hosts (|1|sal|hash); no se elige el algoritmo.")]
        private static bool CoincideHasheado(string entrada, string nombre)
        {
            string[] partes = entrada.Split('|');
            if (partes.Length != 4 || !TryDecodificar(partes[2], out byte[] sal) || !TryDecodificar(partes[3], out byte[] hash))
                return false;

            byte[] calculado = HMACSHA1.HashData(sal, Encoding.UTF8.GetBytes(nombre.ToLowerInvariant()));
            return CryptographicOperations.FixedTimeEquals(calculado, hash);
        }

        // '*' = cualquier secuencia, '?' = un carácter. Sin distinguir mayúsculas, como OpenSSH.
        private static bool CoincideComodin(ReadOnlySpan<char> patron, ReadOnlySpan<char> texto)
        {
            int p = 0, t = 0, estrella = -1, marca = 0;
            while (t < texto.Length)
            {
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
                {
                    return false;
                }
            }

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
