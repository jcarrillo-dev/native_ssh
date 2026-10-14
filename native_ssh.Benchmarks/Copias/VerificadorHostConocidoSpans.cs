using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;
using NativeSsh.Ssh;

namespace NativeSsh.Benchmarks.Copias
{
    // Solo para medir (GeneracionesKnownHostsBenchmarks): la misma comprobación que VerificadorHostConocido, pero leyendo cada
    // known_hosts entero en un búfer de caracteres rentado y recorriendo sus líneas y campos como ReadOnlySpan<char>, sin
    // crear un string por línea ni por campo. Solo se crea lo que se guarda (el tipo y la clave de las líneas del host, en
    // ObtenerTiposClave). Mismo resultado que el producto: el texto se decodifica con un StreamReader (como File.ReadLines,
    // con la detección de BOM), las líneas se parten igual que ReadLine ('\n', '\r' o "\r\n") y los campos igual que
    // string.Split((char[]?)null, RemoveEmptyEntries) (cualquier char.IsWhiteSpace).
    public sealed class VerificadorHostConocidoSpans(string[] rutas)
    {
        public string[] Rutas { get; } = rutas;

        public ResultadoHostEnum Verificar(string servidor, int puerto, string tipoClave, ReadOnlySpan<byte> clave)
        {
            string nombre = puerto == 22 ? servidor : $"[{servidor}]:{puerto}";
            bool hasMismoTipo = false;
            bool isConocido = false;
            foreach (string ruta in Rutas)
            {
                if (!File.Exists(ruta))
                    continue;

                char[] texto = LeerTexto(ruta, out int longitud);
                try
                {
                    ReadOnlySpan<char> resto = texto.AsSpan(0, longitud);
                    while (TrySiguienteLinea(ref resto, out ReadOnlySpan<char> linea))
                    {
                        if (!TryCampos(linea, out ReadOnlySpan<char> marcador, out ReadOnlySpan<char> tipo, out ReadOnlySpan<char> base64, nombre))
                            continue;

                        ResultadoClaveEnum resultado = CompararClave(base64, clave);
                        if (resultado == ResultadoClaveEnum.Invalida)
                            continue;

                        bool isMismaClave = resultado == ResultadoClaveEnum.Igual;
                        if (!marcador.IsEmpty)
                        {
                            if (isMismaClave)
                                return ResultadoHostEnum.Revocado;
                            continue;
                        }

                        if (!tipo.SequenceEqual(tipoClave))
                            continue;

                        hasMismoTipo = true;
                        isConocido |= isMismaClave;
                    }
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(texto, clearArray: true);
                }
            }

            return isConocido ? ResultadoHostEnum.Conocido
                : hasMismoTipo ? ResultadoHostEnum.ClaveDistinta
                : ResultadoHostEnum.Desconocido;
        }

        public HashSet<string> ObtenerTiposClave(string servidor, int puerto)
        {
            string nombre = puerto == 22 ? servidor : $"[{servidor}]:{puerto}";
            using PooledListRef<(string Tipo, byte[] Clave)> conocidas = new();
            using PooledListRef<byte[]> revocadas = new();
            foreach (string ruta in Rutas)
            {
                if (!File.Exists(ruta))
                    continue;

                char[] texto = LeerTexto(ruta, out int longitud);
                try
                {
                    ReadOnlySpan<char> resto = texto.AsSpan(0, longitud);
                    while (TrySiguienteLinea(ref resto, out ReadOnlySpan<char> linea))
                    {
                        if (!TryCampos(linea, out ReadOnlySpan<char> marcador, out ReadOnlySpan<char> tipo, out ReadOnlySpan<char> base64, nombre))
                            continue;

                        if (!TryDecodificar(base64, out byte[] claveLinea))
                            continue;

                        if (!marcador.IsEmpty)
                            revocadas.Add(claveLinea);
                        else
                            conocidas.Add((tipo.ToString(), claveLinea));
                    }
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(texto, clearArray: true);
                }
            }

            HashSet<string> tipos = new(StringComparer.Ordinal);
            foreach ((string tipo, byte[] claveValida) in conocidas.Span)
            {
                bool isRevocada = false;
                foreach (byte[] rev in revocadas.Span)
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

        private enum ResultadoClaveEnum
        {
            Invalida,
            Igual,
            Distinta
        }

        // Los campos de una línea que cuenta (como el producto): al menos 3, el primero sin '#', el marcador solo @revoked, al
        // menos 3 tras él y el host coincide. Marcador vacío = sin marcador.
        private static bool TryCampos(ReadOnlySpan<char> linea, out ReadOnlySpan<char> marcador, out ReadOnlySpan<char> tipo, out ReadOnlySpan<char> base64, string nombre)
        {
            marcador = tipo = base64 = default;
            ReadOnlySpan<char> resto = linea;
            ReadOnlySpan<char> primero = SiguienteCampo(ref resto);
            if (primero.IsEmpty || primero[0] == '#')
                return false;

            int restantes = ContarCampos(resto);
            if (restantes < 2)
                return false;

            ReadOnlySpan<char> hosts = primero;
            if (primero[0] == '@')
            {
                if (!primero.SequenceEqual("@revoked") || restantes < 3)
                    return false;
                marcador = primero;
                hosts = SiguienteCampo(ref resto);
            }

            if (!IsCoincideHost(hosts, nombre))
                return false;

            tipo = SiguienteCampo(ref resto);
            base64 = SiguienteCampo(ref resto);
            return true;
        }

        private static int ContarCampos(ReadOnlySpan<char> resto)
        {
            int total = 0;
            while (!SiguienteCampo(ref resto).IsEmpty)
                total++;
            return total;
        }

        // Campos separados por cualquier char.IsWhiteSpace, sin vacíos (string.Split((char[]?)null, RemoveEmptyEntries)); vacío
        // si no quedan.
        private static ReadOnlySpan<char> SiguienteCampo(scoped ref ReadOnlySpan<char> resto)
        {
            int inicio = 0;
            while (inicio < resto.Length && char.IsWhiteSpace(resto[inicio]))
                inicio++;
            int fin = inicio;
            while (fin < resto.Length && !char.IsWhiteSpace(resto[fin]))
                fin++;
            ReadOnlySpan<char> campo = resto[inicio..fin];
            resto = resto[fin..];
            return campo;
        }

        // Como StreamReader.ReadLine: '\n', '\r' o "\r\n"; la última línea sin salto también cuenta, un salto final no.
        private static bool TrySiguienteLinea(ref ReadOnlySpan<char> resto, out ReadOnlySpan<char> linea)
        {
            linea = default;
            if (resto.IsEmpty)
                return false;

            int salto = resto.IndexOfAny('\r', '\n');
            if (salto < 0)
            {
                linea = resto;
                resto = [];
                return true;
            }

            linea = resto[..salto];
            bool isCrLf = resto[salto] == '\r' && salto + 1 < resto.Length && resto[salto + 1] == '\n';
            resto = resto[(salto + (isCrLf ? 2 : 1))..];
            return true;
        }

        // El fichero entero en un búfer de caracteres rentado (se devuelve a cero), decodificado como File.ReadLines.
        private static char[] LeerTexto(string ruta, out int longitud)
        {
            using StreamReader lector = new(ruta, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            char[] texto = ArrayPool<char>.Shared.Rent(4096);
            longitud = 0;
            while (true)
            {
                if (longitud == texto.Length)
                {
                    char[] mayor = ArrayPool<char>.Shared.Rent(texto.Length * 2);
                    texto.AsSpan(0, longitud).CopyTo(mayor);
                    ArrayPool<char>.Shared.Return(texto, clearArray: true);
                    texto = mayor;
                }

                int leidos = lector.ReadBlock(texto.AsSpan(longitud));
                if (leidos == 0)
                    return texto;
                longitud += leidos;
            }
        }

        private static ResultadoClaveEnum CompararClave(ReadOnlySpan<char> base64, ReadOnlySpan<byte> clave)
        {
            int maximo = (base64.Length + 3) / 4 * 3;
            byte[]? alquilado = maximo > 1024 ? ArrayPool<byte>.Shared.Rent(maximo) : null;
            Span<byte> destino = alquilado is null ? stackalloc byte[1024] : alquilado;
            try
            {
                return !Convert.TryFromBase64Chars(base64, destino, out int escritos) ? ResultadoClaveEnum.Invalida
                    : clave.SequenceEqual(destino[..escritos]) ? ResultadoClaveEnum.Igual
                    : ResultadoClaveEnum.Distinta;
            }
            finally
            {
                if (alquilado is not null)
                    ArrayPool<byte>.Shared.Return(alquilado);
            }
        }

        private static bool IsCoincideHost(ReadOnlySpan<char> patrones, string nombre)
        {
            if (patrones.StartsWith("|1|"))
                return IsCoincideHasheado(patrones, nombre);

            bool isCoincide = false;
            ReadOnlySpan<char> resto = patrones;
            while (true)
            {
                int coma = resto.IndexOf(',');
                ReadOnlySpan<char> patron = coma < 0 ? resto : resto[..coma];
                bool isNegado = !patron.IsEmpty && patron[0] == '!';
                if (IsCoincideComodin(isNegado ? patron[1..] : patron, nombre))
                {
                    if (isNegado)
                        return false;
                    isCoincide = true;
                }

                if (coma < 0)
                    break;
                resto = resto[(coma + 1)..];
            }

            return isCoincide;
        }

        [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "HMAC-SHA1 es el formato de las entradas hasheadas de known_hosts (|1|sal|hash); no se elige el algoritmo.")]
        private static bool IsCoincideHasheado(ReadOnlySpan<char> entrada, string nombre)
        {
            // "|1|sal|hash": exactamente 4 partes separadas por '|' (la primera vacía), como entrada.Split('|').Length == 4.
            ReadOnlySpan<char> resto = entrada[3..];
            int barra = resto.IndexOf('|');
            if (barra < 0)
                return false;
            ReadOnlySpan<char> sal64 = resto[..barra];
            ReadOnlySpan<char> hash64 = resto[(barra + 1)..];
            if (hash64.Contains('|') || !TryDecodificar(sal64, out byte[] sal) || !TryDecodificar(hash64, out byte[] hash))
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

        private static bool TryDecodificar(ReadOnlySpan<char> base64, out byte[] datos)
        {
            byte[] destino = new byte[(base64.Length + 3) / 4 * 3];
            if (!Convert.TryFromBase64Chars(base64, destino, out int escritos))
            {
                datos = [];
                return false;
            }

            datos = escritos == destino.Length ? destino : destino[..escritos];
            return true;
        }
    }
}
