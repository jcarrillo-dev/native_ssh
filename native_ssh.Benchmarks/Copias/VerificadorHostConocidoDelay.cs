using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;
using JCarrillo.AOT.Core.Extensiones.ValueLINQ.Delay;
using JCarrillo.AOT.Core.ValueLINQ.Interfaces;
using NativeSsh.Ssh;

namespace NativeSsh.Benchmarks.Copias
{
    // Solo para medir (GeneracionesKnownHostsBenchmarks): la misma comprobación que VerificadorHostConocido con el motor Delay
    // de ValueLINQ (perezoso, sobre la pila, con procesadores struct). Las rutas existentes salen de una canalización Delay
    // (Where<Existe>); de cada una se leen SUS líneas a una lista rentada (la misma para todas, vaciada entre ficheros: nunca
    // coexisten las líneas de dos ficheros) y sobre ellas va Select<Partir, Campos>().Where<DelHost, string>(nombre). El
    // motor Delay solo admite como origen un array o un span, así que las líneas de cada fichero se reúnen antes.
    public sealed class VerificadorHostConocidoDelay(string[] rutas)
    {
        public string[] Rutas { get; } = rutas;

        public ResultadoHostEnum Verificar(string servidor, int puerto, string tipoClave, ReadOnlySpan<byte> clave)
        {
            string nombre = puerto == 22 ? servidor : $"[{servidor}]:{puerto}";
            bool hasMismoTipo = false;
            bool isConocido = false;
            // Un 'using' no se puede pasar por ref a LeerLineas: se devuelve en el finally.
            PooledListRef<string> lineas = new();
            try
            {
                foreach (ref readonly string ruta in Rutas.ToValueDelayQuery().Where(default(Existe)))
                {
                    LeerLineas(ruta, ref lineas);
                    foreach (ref readonly Campos campos in ((ReadOnlySpan<string>)lineas.Span).ToValueDelayQuery()
                        .Select<Partir, Campos>()
                        .Where<DelHost, string>(nombre))
                    {
                        if (!TryDecodificar(campos.Base64, out byte[] claveLinea))
                            continue;

                        bool isMismaClave = clave.SequenceEqual(claveLinea);
                        if (campos.IsRevocada)
                        {
                            if (isMismaClave)
                                return ResultadoHostEnum.Revocado;
                            continue;
                        }

                        if (!string.Equals(campos.Tipo, tipoClave, StringComparison.Ordinal))
                            continue;

                        hasMismoTipo = true;
                        isConocido |= isMismaClave;
                    }
                }
            }
            finally
            {
                lineas.Dispose();
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
            // Un 'using' no se puede pasar por ref a LeerLineas: se devuelve en el finally.
            PooledListRef<string> lineas = new();
            try
            {
                foreach (ref readonly string ruta in Rutas.ToValueDelayQuery().Where(default(Existe)))
                {
                    LeerLineas(ruta, ref lineas);
                    foreach (ref readonly Campos campos in ((ReadOnlySpan<string>)lineas.Span).ToValueDelayQuery()
                        .Select<Partir, Campos>()
                        .Where<DelHost, string>(nombre))
                    {
                        if (!TryDecodificar(campos.Base64, out byte[] claveLinea))
                            continue;

                        if (campos.IsRevocada)
                            revocadas.Add(claveLinea);
                        else
                            conocidas.Add((campos.Tipo, claveLinea));
                    }
                }
            }
            finally
            {
                lineas.Dispose();
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

        // Las líneas de un fichero, en la lista rentada vaciada (sin volver a rentar).
        private static void LeerLineas(string ruta, scoped ref PooledListRef<string> lineas)
        {
            lineas.Clear();
            foreach (string linea in File.ReadLines(ruta))
                lineas.Add(linea);
        }

        private struct Existe : IWhereDelegado<string>
        {
            public readonly bool Ejecutar(string objetoLista) => File.Exists(objetoLista);
        }

        // Los campos de una línea: Hosts vacío si la línea no cuenta (menos de 3 campos, comentario, otro marcador).
        private readonly record struct Campos(string Hosts, string Tipo, string Base64, bool IsRevocada);

        private struct Partir : ISelectDelegado<string, Campos>
        {
            public readonly Campos Ejecutar(string objetoLista)
            {
                string[] campos = objetoLista.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (campos.Length < 3 || campos[0].StartsWith('#'))
                    return default;

                int indice = 0;
                string? marcador = campos[0].StartsWith('@') ? campos[indice++] : null;
                bool isInvalida = (marcador is not null and not "@revoked") || campos.Length < indice + 3;
                return isInvalida ? default : new Campos(campos[indice], campos[indice + 1], campos[indice + 2], marcador is not null);
            }
        }

        private struct DelHost : IWhereDelegado<Campos, string>
        {
            public readonly bool Ejecutar(Campos objetoLista, string otro) => objetoLista.Hosts is not null && IsCoincideHost(objetoLista.Hosts, otro);
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
