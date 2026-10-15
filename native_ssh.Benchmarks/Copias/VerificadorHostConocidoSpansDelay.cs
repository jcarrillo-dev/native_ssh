using System.Buffers;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;
using JCarrillo.AOT.Core.Extensiones.ValueLINQ.Delay;
using JCarrillo.AOT.Core.ValueLINQ.Interfaces;
using NativeSsh.Ssh;

namespace NativeSsh.Benchmarks.Copias
{
    // Solo para medir (GeneracionesKnownHostsBenchmarks): la lectura del producto (VerificadorHostConocido: un fichero cada vez
    // en un búfer de caracteres rentado, líneas y campos como ReadOnlySpan<char>) con el recorrido en ValueLINQ Delay y
    // procesadores struct, con la disciplina de ValueLINQ: primero los Where, del más restrictivo al más permisivo, sobre la
    // línea cruda, y al final un solo Select para pasar de un struct a otro.
    // - Where<DelHost>: solo el campo de hosts (el primero, o el segundo tras @revoked) frente al nombre, sin partir el resto;
    //   de paso descarta las líneas vacías, los comentarios y otros marcadores. Deja pasar muy pocas líneas.
    // - Where<ConCampos>: tras los hosts están el tipo y la clave (al menos 3 campos, 4 con @revoked).
    // - Select<Partir, Campos>: el tipo, la clave en base64 y si es @revoked, como spans sobre el búfer.
    // El motor Delay solo admite como origen un array o un span de T, y T no puede ser ReadOnlySpan<char> (no hay span de
    // spans): las líneas de cada fichero se apuntan antes como (inicio, longitud) en una lista rentada de structs sin
    // referencias (Linea). Mismas piezas, mismas reglas y mismo orden que el producto.
    public sealed class VerificadorHostConocidoSpansDelay(string[] rutas)
    {
        public string[] Rutas { get; } = rutas;

        public ResultadoHostEnum Verificar(string servidor, int puerto, string tipoClave, ReadOnlySpan<byte> clave)
        {
            string nombre = puerto == 22 ? servidor : $"[{servidor}]:{puerto}";
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

                    char[] texto = VerificadorHostConocido.LeerTexto(ruta, out int longitud);
                    try
                    {
                        ApuntarLineas(texto.AsSpan(0, longitud), ref lineas);
                        DelHost delHost = new(texto);
                        Partir partir = new(texto);
                        foreach (ref readonly Campos campos in ((ReadOnlySpan<Linea>)lineas.Span).ToValueDelayQuery()
                            .Where(nombre, ref delHost)
                            .Where(new ConCampos(texto))
                            .Select<Partir, Campos>(ref partir))
                        {
                            if (!campos.IsRevocada && !campos.Tipo.SequenceEqual(tipoClave))
                                continue;

                            VerificadorHostConocido.ResultadoClaveEnum resultado = VerificadorHostConocido.CompararClave(campos.Base64, clave);
                            if (resultado == VerificadorHostConocido.ResultadoClaveEnum.Invalida)
                                continue;

                            bool isMismaClave = resultado == VerificadorHostConocido.ResultadoClaveEnum.Igual;
                            if (campos.IsRevocada)
                            {
                                if (isMismaClave)
                                    return ResultadoHostEnum.Revocado;
                                continue;
                            }

                            hasMismoTipo = true;
                            isConocido |= isMismaClave;
                        }
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

            return isConocido ? ResultadoHostEnum.Conocido
                : hasMismoTipo ? ResultadoHostEnum.ClaveDistinta
                : ResultadoHostEnum.Desconocido;
        }

        public HashSet<string> ObtenerTiposClave(string servidor, int puerto)
        {
            string nombre = puerto == 22 ? servidor : $"[{servidor}]:{puerto}";
            using PooledListRef<(string Tipo, byte[] Clave)> conocidas = new();
            using PooledListRef<byte[]> revocadas = new();
            // Un 'using' no se puede pasar por ref: se devuelve en el finally.
            PooledListRef<Linea> lineas = new();
            try
            {
                foreach (string ruta in Rutas)
                {
                    if (!File.Exists(ruta))
                        continue;

                    char[] texto = VerificadorHostConocido.LeerTexto(ruta, out int longitud);
                    try
                    {
                        ApuntarLineas(texto.AsSpan(0, longitud), ref lineas);
                        DelHost delHost = new(texto);
                        Partir partir = new(texto);
                        foreach (ref readonly Campos campos in ((ReadOnlySpan<Linea>)lineas.Span).ToValueDelayQuery()
                            .Where(nombre, ref delHost)
                            .Where(new ConCampos(texto))
                            .Select<Partir, Campos>(ref partir))
                        {
                            if (!VerificadorHostConocido.TryDecodificar(campos.Base64, out byte[] claveLinea))
                                continue;

                            if (campos.IsRevocada)
                                revocadas.Add(claveLinea);
                            else
                                conocidas.Add((campos.Tipo.ToString(), claveLinea));
                        }
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

        // Las líneas del texto como (inicio, longitud), con la misma partición que ReadLine, en la lista vaciada.
        private static void ApuntarLineas(ReadOnlySpan<char> texto, scoped ref PooledListRef<Linea> lineas)
        {
            lineas.Clear();
            ReadOnlySpan<char> resto = texto;
            int inicio = 0;
            while (VerificadorHostConocido.TrySiguienteLinea(ref resto, out ReadOnlySpan<char> linea))
            {
                lineas.Add(new Linea(inicio, linea.Length));
                inicio = texto.Length - resto.Length;
            }
        }

        private readonly record struct Linea(int Inicio, int Longitud);

        // Los campos que se usan de una línea que ya pasó los filtros, sobre el búfer.
        private readonly ref struct Campos(ReadOnlySpan<char> tipo, ReadOnlySpan<char> base64, bool isRevocada)
        {
            public ReadOnlySpan<char> Tipo { get; } = tipo;
            public ReadOnlySpan<char> Base64 { get; } = base64;
            public bool IsRevocada { get; } = isRevocada;
        }

        // El campo de hosts (sin comentario ni otro marcador que @revoked) coincide con el nombre.
        private readonly struct DelHost(char[] texto) : IWhereDelegado<Linea, string>
        {
            private readonly char[] _texto = texto;

            public bool Ejecutar(Linea objetoLista, string otro)
            {
                ReadOnlySpan<char> resto = _texto.AsSpan(objetoLista.Inicio, objetoLista.Longitud);
                ReadOnlySpan<char> hosts = VerificadorHostConocido.SiguienteCampo(ref resto);
                if (hosts.IsEmpty || hosts[0] == '#')
                    return false;

                if (hosts[0] == '@')
                {
                    if (!hosts.SequenceEqual("@revoked"))
                        return false;
                    hosts = VerificadorHostConocido.SiguienteCampo(ref resto);
                }

                return VerificadorHostConocido.IsCoincideHost(hosts, otro);
            }
        }

        // Tras el campo de hosts (y el marcador, si lo hay) están el tipo y la clave: al menos 3 campos, o 4 con marcador.
        private readonly struct ConCampos(char[] texto) : IWhereDelegado<Linea>
        {
            private readonly char[] _texto = texto;

            public bool Ejecutar(Linea objetoLista)
            {
                ReadOnlySpan<char> resto = _texto.AsSpan(objetoLista.Inicio, objetoLista.Longitud);
                ReadOnlySpan<char> primero = VerificadorHostConocido.SiguienteCampo(ref resto);
                if (!primero.IsEmpty && primero[0] == '@')
                    _ = VerificadorHostConocido.SiguienteCampo(ref resto);
                _ = VerificadorHostConocido.SiguienteCampo(ref resto);
                return !VerificadorHostConocido.SiguienteCampo(ref resto).IsEmpty;
            }
        }

        private readonly struct Partir(char[] texto) : ISelectDelegado<Linea, Campos>
        {
            private readonly char[] _texto = texto;

            public Campos Ejecutar(Linea objetoLista)
            {
                ReadOnlySpan<char> resto = _texto.AsSpan(objetoLista.Inicio, objetoLista.Longitud);
                ReadOnlySpan<char> primero = VerificadorHostConocido.SiguienteCampo(ref resto);
                bool isRevocada = primero[0] == '@';
                if (isRevocada)
                    _ = VerificadorHostConocido.SiguienteCampo(ref resto);
                ReadOnlySpan<char> tipo = VerificadorHostConocido.SiguienteCampo(ref resto);
                return new Campos(tipo, VerificadorHostConocido.SiguienteCampo(ref resto), isRevocada);
            }
        }
    }
}
