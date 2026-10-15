using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;
using JCarrillo.AOT.Core.Extensiones.ValueLINQ.Delay;
using JCarrillo.AOT.Core.ValueLINQ.Interfaces;

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
    // Cada fichero se lee entero en un búfer de caracteres rentado, uno cada vez; sus líneas se apuntan como (inicio, longitud)
    // y se recorren con ValueLINQ Delay y procesadores struct: primero los Where, del más restrictivo al más permisivo, sobre
    // la línea cruda (el campo de hosts; en Verificar, el tipo; que estén el tipo y la clave) y al final un solo Select al
    // struct de campos (spans sobre el búfer). Solo se crea lo que sale (los tipos de ObtenerTiposClave) y las claves que hay
    // que guardar; las claves y las entradas hasheadas se comparan en la pila. El búfer vuelve al pool sin limpiar
    // (known_hosts solo tiene claves públicas y nombres de host); se lee solo lo escrito, hasta la longitud propia, nunca
    // hasta el Length del array rentado. Mismo resultado que leyendo con File.ReadLines y partiendo con string.Split: el texto
    // se decodifica con un StreamReader (UTF-8 con detección de BOM), las líneas se cortan como ReadLine ('\n', '\r' o
    // "\r\n"; U+2028 no corta) y los campos con cualquier char.IsWhiteSpace, sin vacíos.
    public sealed class VerificadorHostConocido(string[] rutas)
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
            // Un 'using' no se puede pasar por ref: se devuelve en el finally.
            PooledListRef<Linea> lineas = new();
            try
            {
                foreach (string ruta in Rutas)
                {
                    if (!File.Exists(ruta))
                        continue;

                    char[] texto = LeerTexto(ruta, out int longitud);
                    try
                    {
                        ApuntarLineas(texto.AsSpan(0, longitud), ref lineas);
                        if (IsRevocadaAcumulando(texto, lineas.Span, nombre, tipoClave, clave, ref hasMismoTipo, ref isConocido))
                            return ResultadoHostEnum.Revocado;
                    }
                    finally
                    {
                        ArrayPool<char>.Shared.Return(texto);
                    }
                }
            }
            finally
            {
                lineas.Dispose();
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
            PooledListRef<Linea> lineas = new();
            try
            {
                foreach (string ruta in Rutas)
                {
                    if (!File.Exists(ruta))
                        continue;

                    char[] texto = LeerTexto(ruta, out int longitud);
                    try
                    {
                        ApuntarLineas(texto.AsSpan(0, longitud), ref lineas);
                        AcumularClaves(texto, lineas.Span, nombre, ref conocidas, ref revocadas);
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
                lineas.Dispose();
                revocadas.Dispose();
                conocidas.Dispose();
            }
        }

        // Sobre líneas ya leídas: cada una es una línea entera (no se vuelve a cortar por saltos), juntas en un búfer rentado.
        public static HashSet<string> ObtenerTiposClave(ReadOnlySpan<string> lineas, string servidor, int puerto)
        {
            string nombre = NombreHost(servidor, puerto);
            PooledListRef<(string Tipo, byte[] Clave)> conocidas = new();
            PooledListRef<byte[]> revocadas = new();
            PooledListRef<Linea> apuntadas = new();
            char[] texto = Juntar(lineas, ref apuntadas);
            try
            {
                AcumularClaves(texto, apuntadas.Span, nombre, ref conocidas, ref revocadas);
                return TiposNoRevocados(conocidas.Span, revocadas.Span);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(texto);
                apuntadas.Dispose();
                revocadas.Dispose();
                conocidas.Dispose();
            }
        }

        public static ResultadoHostEnum Verificar(ReadOnlySpan<string> lineas, string servidor, int puerto, string tipoClave, ReadOnlySpan<byte> clave)
        {
            string nombre = NombreHost(servidor, puerto);
            bool hasMismoTipo = false;
            bool isConocido = false;
            PooledListRef<Linea> apuntadas = new();
            char[] texto = Juntar(lineas, ref apuntadas);
            try
            {
                return IsRevocadaAcumulando(texto, apuntadas.Span, nombre, tipoClave, clave, ref hasMismoTipo, ref isConocido)
                    ? ResultadoHostEnum.Revocado
                    : Resultado(hasMismoTipo, isConocido);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(texto);
                apuntadas.Dispose();
            }
        }

        private static string NombreHost(string servidor, int puerto) => puerto == 22 ? servidor : $"[{servidor}]:{puerto}";

        private static ResultadoHostEnum Resultado(bool hasMismoTipo, bool isConocido) =>
            isConocido ? ResultadoHostEnum.Conocido
                : hasMismoTipo ? ResultadoHostEnum.ClaveDistinta
                : ResultadoHostEnum.Desconocido;

        // Las líneas de un fichero: true en cuanto una revoca la clave; si no, acumula si hay una entrada del mismo tipo y si
        // alguna es la clave (el resultado de varios ficheros es el de sus líneas seguidas). Los Where dejan las del host, del
        // tipo pedido (o @revoked, que revoca la clave sea cual sea su tipo) y con tipo y clave; solo esas se decodifican.
        private static bool IsRevocadaAcumulando(char[] texto, ReadOnlySpan<Linea> lineas, string nombre, string tipoClave, ReadOnlySpan<byte> clave, ref bool hasMismoTipo, ref bool isConocido)
        {
            DelHost delHost = new(texto);
            DelTipo delTipo = new(texto);
            Partir partir = new(texto);
            foreach (ref readonly Campos campos in lineas.ToValueDelayQuery()
                .Where(nombre, ref delHost)
                .Where(tipoClave, ref delTipo)
                .Where(new ConTipoYClave(texto))
                .Select<Partir, Campos>(ref partir))
            {
                ResultadoClaveEnum resultado = CompararClave(campos.Base64, clave);
                if (resultado == ResultadoClaveEnum.Invalida)
                    continue;

                bool isMismaClave = resultado == ResultadoClaveEnum.Igual;
                if (campos.IsRevocada)
                {
                    if (isMismaClave)
                        return true;
                    continue;
                }

                hasMismoTipo = true;
                isConocido |= isMismaClave;
            }

            return false;
        }

        // Las líneas del host con clave válida: a las revocadas o, con su tipo, a las conocidas.
        private static void AcumularClaves(char[] texto, ReadOnlySpan<Linea> lineas, string nombre, scoped ref PooledListRef<(string Tipo, byte[] Clave)> conocidas, scoped ref PooledListRef<byte[]> revocadas)
        {
            DelHost delHost = new(texto);
            Partir partir = new(texto);
            foreach (ref readonly Campos campos in lineas.ToValueDelayQuery()
                .Where(nombre, ref delHost)
                .Where(new ConTipoYClave(texto))
                .Select<Partir, Campos>(ref partir))
            {
                if (!TryDecodificar(campos.Base64, out byte[] claveLinea))
                    continue;

                if (campos.IsRevocada)
                    revocadas.Add(claveLinea);
                else
                    conocidas.Add((campos.Tipo.ToString(), claveLinea));
            }
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

        // Las líneas del texto como (inicio, longitud), con la misma partición que ReadLine, en la lista vaciada. El motor Delay
        // solo admite como origen un array o un span de T, y T no puede ser ReadOnlySpan<char> (no hay span de spans).
        private static void ApuntarLineas(ReadOnlySpan<char> texto, scoped ref PooledListRef<Linea> lineas)
        {
            lineas.Clear();
            ReadOnlySpan<char> resto = texto;
            int inicio = 0;
            while (TrySiguienteLinea(ref resto, out ReadOnlySpan<char> linea))
            {
                lineas.Add(new Linea(inicio, linea.Length));
                inicio = texto.Length - resto.Length;
            }
        }

        // Las líneas ya leídas, seguidas en un búfer rentado (se devuelve al llamador), cada una apuntada entera.
        private static char[] Juntar(ReadOnlySpan<string> lineas, scoped ref PooledListRef<Linea> apuntadas)
        {
            int total = 0;
            foreach (string linea in lineas)
                total += linea.Length;
            char[] texto = ArrayPool<char>.Shared.Rent(total);
            int inicio = 0;
            foreach (string linea in lineas)
            {
                linea.CopyTo(texto.AsSpan(inicio, linea.Length));
                apuntadas.Add(new Linea(inicio, linea.Length));
                inicio += linea.Length;
            }

            return texto;
        }

        private readonly record struct Linea(int Inicio, int Longitud);

        // Los campos que se usan de una línea que ya pasó los filtros, como spans sobre el búfer.
        private readonly ref struct Campos(ReadOnlySpan<char> tipo, ReadOnlySpan<char> base64, bool isRevocada)
        {
            public ReadOnlySpan<char> Tipo { get; } = tipo;
            public ReadOnlySpan<char> Base64 { get; } = base64;
            public bool IsRevocada { get; } = isRevocada;
        }

        // El más restrictivo: el campo de hosts (el primero si no empieza por '#'; el segundo tras @revoked, el único marcador
        // que cuenta) coincide con el nombre. No mira el resto de la línea.
        private readonly struct DelHost(char[] texto) : IWhereDelegado<Linea, string>
        {
            private readonly char[] _texto = texto;

            public bool Ejecutar(Linea objetoLista, string otro)
            {
                ReadOnlySpan<char> resto = _texto.AsSpan(objetoLista.Inicio, objetoLista.Longitud);
                ReadOnlySpan<char> hosts = SiguienteCampo(ref resto);
                if (hosts.IsEmpty || hosts[0] == '#')
                    return false;

                if (hosts[0] == '@')
                {
                    if (!hosts.SequenceEqual("@revoked"))
                        return false;
                    hosts = SiguienteCampo(ref resto);
                }

                return IsCoincideHost(hosts, otro);
            }
        }

        // Tras DelHost: una entrada del tipo pedido, o @revoked (revoca la clave sea cual sea el tipo escrito).
        private readonly struct DelTipo(char[] texto) : IWhereDelegado<Linea, string>
        {
            private readonly char[] _texto = texto;

            public bool Ejecutar(Linea objetoLista, string otro)
            {
                ReadOnlySpan<char> resto = _texto.AsSpan(objetoLista.Inicio, objetoLista.Longitud);
                ReadOnlySpan<char> primero = SiguienteCampo(ref resto);
                return (!primero.IsEmpty && primero[0] == '@') || SiguienteCampo(ref resto).SequenceEqual(otro);
            }
        }

        // El más permisivo: tras el campo de hosts (y el marcador) están el tipo y la clave; es lo mismo que exigir al menos 3
        // campos, o 4 con @revoked.
        private readonly struct ConTipoYClave(char[] texto) : IWhereDelegado<Linea>
        {
            private readonly char[] _texto = texto;

            public bool Ejecutar(Linea objetoLista)
            {
                ReadOnlySpan<char> resto = _texto.AsSpan(objetoLista.Inicio, objetoLista.Longitud);
                ReadOnlySpan<char> primero = SiguienteCampo(ref resto);
                if (!primero.IsEmpty && primero[0] == '@')
                    _ = SiguienteCampo(ref resto);
                _ = SiguienteCampo(ref resto);
                return !SiguienteCampo(ref resto).IsEmpty;
            }
        }

        // El único Select: de la línea que pasó los filtros a sus campos.
        private readonly struct Partir(char[] texto) : ISelectDelegado<Linea, Campos>
        {
            private readonly char[] _texto = texto;

            public Campos Ejecutar(Linea objetoLista)
            {
                ReadOnlySpan<char> resto = _texto.AsSpan(objetoLista.Inicio, objetoLista.Longitud);
                ReadOnlySpan<char> primero = SiguienteCampo(ref resto);
                bool isRevocada = primero[0] == '@';
                if (isRevocada)
                    _ = SiguienteCampo(ref resto);
                ReadOnlySpan<char> tipo = SiguienteCampo(ref resto);
                return new Campos(tipo, SiguienteCampo(ref resto), isRevocada);
            }
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

        // Tipo de clave leído del propio blob (string SSH inicial). SSH.NET informa del algoritmo de firma
        // (p. ej. rsa-sha2-512), que no coincide con el tipo guardado en known_hosts (ssh-rsa).
        public static string? LeerTipo(ReadOnlySpan<byte> blob)
        {
            if (blob.Length < 4)
                return null;

            int longitud = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(blob);
            return longitud is > 0 and <= 64 && blob.Length >= 4 + longitud ? Encoding.ASCII.GetString(blob.Slice(4, longitud)) : null;
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
