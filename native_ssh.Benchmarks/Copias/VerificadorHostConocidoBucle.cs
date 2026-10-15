using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;
using NativeSsh.Ssh;

namespace NativeSsh.Benchmarks.Copias
{
    // Copia de VerificadorHostConocido tal como estaba en 4809fd5 (el fichero en un búfer rentado y sus líneas y campos como
    // spans, recorridos con un bucle a mano), antes de pasar el recorrido a ValueLINQ Delay: solo para medir en la misma tanda
    // el antes y el después (GeneracionesKnownHostsBenchmarks) y para comparar resultados. Sin LeerTipo, que no cambia.
    public sealed class VerificadorHostConocidoBucle(string[] rutas)
    {
        // Caracteres del búfer inicial; si el fichero no cabe, se dobla.
        private const int CapacidadInicial = 4096;

        // Bytes de una clave decodificada que van en la pila; una mayor se decodifica en un array rentado.
        private const int MaximoClaveEnPila = 1024;

        // Entradas hasheadas calculadas en la pila: sal y hash en base64 de hasta 64 caracteres (48 bytes; OpenSSH usa 20) y
        // nombres de hasta 256 caracteres.
        private const int MaximoBase64Hasheado = 64;
        private const int MaximoBytesHasheado = MaximoBase64Hasheado / 4 * 3;
        private const int MaximoNombreEnPila = 256;

        public string[] Rutas { get; } = rutas;

        // Fichero a fichero: una clave revocada responde sin abrir los siguientes, así que un fichero posterior que no se puede
        // leer no convierte la revocación en una excepción.
        public ResultadoHostEnum Verificar(string servidor, int puerto, string tipoClave, ReadOnlySpan<byte> clave)
        {
            string nombre = NombreHost(servidor, puerto);
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
                        if (IsRevocadaAcumulando(linea, nombre, tipoClave, clave, ref hasMismoTipo, ref isConocido))
                            return ResultadoHostEnum.Revocado;
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(texto);
                }
            }

            return Resultado(hasMismoTipo, isConocido);
        }

        // Necesita todos los ficheros (un @revoked de uno anula la clave de otro): uno que no se puede leer lanza.
        public HashSet<string> ObtenerTiposClave(string servidor, int puerto)
        {
            string nombre = NombreHost(servidor, puerto);
            // Las claves de cada línea son arrays propios (TryDecodificar): las listas rentadas solo guardan referencias, que se
            // limpian al devolverlas. Un 'using' no se puede pasar por ref: se devuelven en el finally.
            PooledListRef<(string Tipo, byte[] Clave)> conocidas = new();
            PooledListRef<byte[]> revocadas = new();
            try
            {
                foreach (string ruta in Rutas)
                {
                    if (!File.Exists(ruta))
                        continue;

                    char[] texto = LeerTexto(ruta, out int longitud);
                    try
                    {
                        ReadOnlySpan<char> resto = texto.AsSpan(0, longitud);
                        while (TrySiguienteLinea(ref resto, out ReadOnlySpan<char> linea))
                            AcumularClave(linea, nombre, ref conocidas, ref revocadas);
                    }
                    finally
                    {
                        ArrayPool<char>.Shared.Return(texto);
                    }
                }

                return TiposNoRevocados(conocidas.Span, revocadas.Span);
            }
            finally
            {
                revocadas.Dispose();
                conocidas.Dispose();
            }
        }

        public static HashSet<string> ObtenerTiposClave(ReadOnlySpan<string> lineas, string servidor, int puerto)
        {
            string nombre = NombreHost(servidor, puerto);
            PooledListRef<(string Tipo, byte[] Clave)> conocidas = new();
            PooledListRef<byte[]> revocadas = new();
            try
            {
                foreach (string linea in lineas)
                    AcumularClave(linea, nombre, ref conocidas, ref revocadas);
                return TiposNoRevocados(conocidas.Span, revocadas.Span);
            }
            finally
            {
                revocadas.Dispose();
                conocidas.Dispose();
            }
        }

        public static ResultadoHostEnum Verificar(ReadOnlySpan<string> lineas, string servidor, int puerto, string tipoClave, ReadOnlySpan<byte> clave)
        {
            string nombre = NombreHost(servidor, puerto);
            bool hasMismoTipo = false;
            bool isConocido = false;
            foreach (string linea in lineas)
                if (IsRevocadaAcumulando(linea, nombre, tipoClave, clave, ref hasMismoTipo, ref isConocido))
                    return ResultadoHostEnum.Revocado;

            return Resultado(hasMismoTipo, isConocido);
        }

        private static string NombreHost(string servidor, int puerto) => puerto == 22 ? servidor : $"[{servidor}]:{puerto}";

        private static ResultadoHostEnum Resultado(bool hasMismoTipo, bool isConocido) =>
            isConocido ? ResultadoHostEnum.Conocido
                : hasMismoTipo ? ResultadoHostEnum.ClaveDistinta
                : ResultadoHostEnum.Desconocido;

        // Una línea: true si revoca la clave; si no, acumula si es una entrada del mismo tipo y si es la clave (el resultado
        // de varias líneas o ficheros es el de todas seguidas).
        private static bool IsRevocadaAcumulando(ReadOnlySpan<char> linea, string nombre, string tipoClave, ReadOnlySpan<byte> clave, ref bool hasMismoTipo, ref bool isConocido)
        {
            if (!TryCampos(linea, nombre, out bool isRevocada, out ReadOnlySpan<char> tipo, out ReadOnlySpan<char> base64))
                return false;

            // Una entrada de otro tipo no cuenta, sea válida o no su clave (@revoked sí: revoca la clave sea cual sea el tipo
            // escrito): no se decodifica.
            if (!isRevocada && !tipo.SequenceEqual(tipoClave))
                return false;

            ResultadoClaveEnum resultado = CompararClave(base64, clave);
            if (resultado == ResultadoClaveEnum.Invalida)
                return false;

            bool isMismaClave = resultado == ResultadoClaveEnum.Igual;
            if (isRevocada)
                return isMismaClave;

            hasMismoTipo = true;
            isConocido |= isMismaClave;
            return false;
        }

        // Una línea del host con clave válida: a las revocadas o, con su tipo, a las conocidas.
        private static void AcumularClave(scoped ReadOnlySpan<char> linea, string nombre, scoped ref PooledListRef<(string Tipo, byte[] Clave)> conocidas, scoped ref PooledListRef<byte[]> revocadas)
        {
            if (!TryCampos(linea, nombre, out bool isRevocada, out ReadOnlySpan<char> tipo, out ReadOnlySpan<char> base64)
                || !TryDecodificar(base64, out byte[] claveLinea))
                return;

            if (isRevocada)
                revocadas.Add(claveLinea);
            else
                conocidas.Add((tipo.ToString(), claveLinea));
        }

        private static HashSet<string> TiposNoRevocados(ReadOnlySpan<(string Tipo, byte[] Clave)> conocidas, ReadOnlySpan<byte[]> revocadas)
        {
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

        // Los campos de una línea que cuenta, del filtro más restrictivo al más permisivo y sin mirar más de lo necesario: el
        // campo de hosts (el primero, si no empieza por '#'; o el segundo tras @revoked, el único marcador que cuenta)
        // coincide con el nombre, y tras él están el tipo y la clave. El resto (el comentario) no se mira. Es lo mismo que
        // exigir antes al menos 3 campos (4 con @revoked): las condiciones son puras y la línea cuenta si se cumplen todas.
        private static bool TryCampos(ReadOnlySpan<char> linea, string nombre, out bool isRevocada, out ReadOnlySpan<char> tipo, out ReadOnlySpan<char> base64)
        {
            isRevocada = false;
            tipo = base64 = default;
            ReadOnlySpan<char> resto = linea;
            ReadOnlySpan<char> hosts = SiguienteCampo(ref resto);
            if (hosts.IsEmpty || hosts[0] == '#')
                return false;

            if (hosts[0] == '@')
            {
                if (!hosts.SequenceEqual("@revoked"))
                    return false;
                isRevocada = true;
                hosts = SiguienteCampo(ref resto);
            }

            if (!IsCoincideHost(hosts, nombre))
                return false;

            tipo = SiguienteCampo(ref resto);
            base64 = SiguienteCampo(ref resto);
            return !base64.IsEmpty;
        }

        // Campos separados por cualquier char.IsWhiteSpace, sin vacíos (como string.Split((char[]?)null,
        // RemoveEmptyEntries)); vacío si no quedan.
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

        // El fichero entero en un búfer de caracteres rentado, decodificado como File.ReadLines (UTF-8 y BOM). La capacidad
        // es la pedida, no el Length del array (Rent puede dar uno mayor). Si la lectura lanza, el búfer vuelve antes.
        private static char[] LeerTexto(string ruta, out int longitud)
        {
            int capacidad = CapacidadInicial;
            char[] texto = ArrayPool<char>.Shared.Rent(capacidad);
            longitud = 0;
            try
            {
                using StreamReader lector = new(ruta, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                int leidos;
                while ((leidos = lector.ReadBlock(texto.AsSpan(longitud, capacidad - longitud))) > 0)
                {
                    longitud += leidos;
                    if (longitud < capacidad)
                        continue;

                    capacidad *= 2;
                    char[] mayor = ArrayPool<char>.Shared.Rent(capacidad);
                    texto.AsSpan(0, longitud).CopyTo(mayor);
                    ArrayPool<char>.Shared.Return(texto);
                    texto = mayor;
                }

                return texto;
            }
            catch
            {
                ArrayPool<char>.Shared.Return(texto);
                throw;
            }
        }

        private enum ResultadoClaveEnum
        {
            Invalida,
            Igual,
            Distinta
        }

        // La clave de la línea comparada con la del servidor sin crear un array por línea: en la pila o, si es grande, en un
        // array rentado del que solo se usa lo pedido.
        private static ResultadoClaveEnum CompararClave(ReadOnlySpan<char> base64, ReadOnlySpan<byte> clave)
        {
            int maximo = (base64.Length + 3) / 4 * 3;
            byte[]? alquilado = maximo > MaximoClaveEnPila ? ArrayPool<byte>.Shared.Rent(maximo) : null;
            Span<byte> destino = alquilado is null ? stackalloc byte[MaximoClaveEnPila] : alquilado.AsSpan(0, maximo);
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
                    return isCoincide;
                resto = resto[(coma + 1)..];
            }
        }

        private static bool IsCoincideHasheado(ReadOnlySpan<char> entrada, string nombre)
        {
            // "|1|sal|hash": exactamente 4 partes separadas por '|' (la primera vacía), como entrada.Split('|').Length == 4.
            ReadOnlySpan<char> resto = entrada[3..];
            int barra = resto.IndexOf('|');
            if (barra < 0)
                return false;
            ReadOnlySpan<char> sal64 = resto[..barra];
            ReadOnlySpan<char> hash64 = resto[(barra + 1)..];
            if (hash64.Contains('|'))
                return false;

            // Lo de OpenSSH (sal y hash de 20 bytes) con un nombre corto se calcula en la pila, sin crear nada por línea; lo que
            // no cabe, creando los arrays, con el mismo resultado.
            bool isEnPila = sal64.Length <= MaximoBase64Hasheado && hash64.Length <= MaximoBase64Hasheado && nombre.Length <= MaximoNombreEnPila;
            return isEnPila ? IsCoincideHasheadoEnPila(sal64, hash64, nombre) : IsCoincideHasheadoCreando(sal64, hash64, nombre);
        }

        [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "HMAC-SHA1 es el formato de las entradas hasheadas de known_hosts (|1|sal|hash); no se elige el algoritmo.")]
        private static bool IsCoincideHasheadoEnPila(ReadOnlySpan<char> sal64, ReadOnlySpan<char> hash64, string nombre)
        {
            Span<byte> sal = stackalloc byte[MaximoBytesHasheado];
            Span<byte> hash = stackalloc byte[MaximoBytesHasheado];
            if (!Convert.TryFromBase64Chars(sal64, sal, out int bytesSal) || !Convert.TryFromBase64Chars(hash64, hash, out int bytesHash))
                return false;

            Span<char> minusculas = stackalloc char[MaximoNombreEnPila];
            int caracteres = nombre.AsSpan().ToLowerInvariant(minusculas);
            Span<byte> utf8 = stackalloc byte[MaximoNombreEnPila * 3];
            int bytesNombre = Encoding.UTF8.GetBytes(minusculas[..caracteres], utf8);
            Span<byte> calculado = stackalloc byte[HMACSHA1.HashSizeInBytes];
            _ = HMACSHA1.HashData(sal[..bytesSal], utf8[..bytesNombre], calculado);
            return CryptographicOperations.FixedTimeEquals(calculado, hash[..bytesHash]);
        }

        [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "HMAC-SHA1 es el formato de las entradas hasheadas de known_hosts (|1|sal|hash); no se elige el algoritmo.")]
        private static bool IsCoincideHasheadoCreando(ReadOnlySpan<char> sal64, ReadOnlySpan<char> hash64, string nombre)
        {
            if (!TryDecodificar(sal64, out byte[] sal) || !TryDecodificar(hash64, out byte[] hash))
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

        // La clave decodificada en un array propio del tamaño exacto (se guarda): se decodifica antes en la pila o en un array
        // rentado del que solo se usa lo pedido.
        private static bool TryDecodificar(ReadOnlySpan<char> base64, out byte[] datos)
        {
            int maximo = (base64.Length + 3) / 4 * 3;
            byte[]? alquilado = maximo > MaximoClaveEnPila ? ArrayPool<byte>.Shared.Rent(maximo) : null;
            Span<byte> destino = alquilado is null ? stackalloc byte[MaximoClaveEnPila] : alquilado.AsSpan(0, maximo);
            try
            {
                bool isValida = Convert.TryFromBase64Chars(base64, destino, out int escritos);
                datos = isValida ? destino[..escritos].ToArray() : [];
                return isValida;
            }
            finally
            {
                if (alquilado is not null)
                    ArrayPool<byte>.Shared.Return(alquilado);
            }
        }
    }
}
