using System.Buffers;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;
using JCarrillo.AOT.Core.Extensiones.ValueLINQ.Delay;
using JCarrillo.AOT.Core.ValueLINQ.Interfaces;
using NativeSsh.Ssh;

namespace NativeSsh.Benchmarks.Copias
{
    // Solo para medir (GeneracionesKnownHostsBenchmarks): el lector por spans (VerificadorHostConocidoSpans: un fichero cada
    // vez en un búfer de caracteres rentado, sin un string por línea) con el recorrido y el filtrado en ValueLINQ Delay y
    // procesadores struct. El motor Delay solo admite como origen un array o un span de T, y T no puede ser ReadOnlySpan<char>
    // (no hay span de spans; el enumerador de origen no admite ref struct): las líneas de cada fichero se apuntan antes como
    // (inicio, longitud) en una lista rentada de structs sin referencias (Linea). Select<Partir, Campos> parte cada línea
    // en sus campos (Campos es un ref struct con ReadOnlySpan<char> sobre el búfer) y Where<DelHost, string> se queda con las
    // del host. Mismos campos, mismas reglas y mismo orden que el producto.
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
                foreach (ref readonly string ruta in Rutas.ToValueDelayQuery().Where(default(Existe)))
                {
                    char[] texto = VerificadorHostConocidoSpans.LeerTexto(ruta, out int longitud);
                    try
                    {
                        ApuntarLineas(texto.AsSpan(0, longitud), ref lineas);
                        Partir partir = new(texto);
                        DelHost delHost = default;
                        foreach (ref readonly Campos campos in ((ReadOnlySpan<Linea>)lineas.Span).ToValueDelayQuery()
                            .Select<Partir, Campos>(ref partir)
                            .Where(nombre, ref delHost))
                        {
                            VerificadorHostConocidoSpans.ResultadoClaveEnum resultado = VerificadorHostConocidoSpans.CompararClave(campos.Base64, clave);
                            if (resultado == VerificadorHostConocidoSpans.ResultadoClaveEnum.Invalida)
                                continue;

                            bool isMismaClave = resultado == VerificadorHostConocidoSpans.ResultadoClaveEnum.Igual;
                            if (campos.IsRevocada)
                            {
                                if (isMismaClave)
                                    return ResultadoHostEnum.Revocado;
                                continue;
                            }

                            if (!campos.Tipo.SequenceEqual(tipoClave))
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
                foreach (ref readonly string ruta in Rutas.ToValueDelayQuery().Where(default(Existe)))
                {
                    char[] texto = VerificadorHostConocidoSpans.LeerTexto(ruta, out int longitud);
                    try
                    {
                        ApuntarLineas(texto.AsSpan(0, longitud), ref lineas);
                        Partir partir = new(texto);
                        DelHost delHost = default;
                        foreach (ref readonly Campos campos in ((ReadOnlySpan<Linea>)lineas.Span).ToValueDelayQuery()
                            .Select<Partir, Campos>(ref partir)
                            .Where(nombre, ref delHost))
                        {
                            if (!VerificadorHostConocidoSpans.TryDecodificar(campos.Base64, out byte[] claveLinea))
                                continue;

                            if (campos.IsRevocada)
                                revocadas.Add(claveLinea);
                            else
                                conocidas.Add((campos.Tipo.ToString(), claveLinea));
                        }
                    }
                    finally
                    {
                        ArrayPool<char>.Shared.Return(texto, clearArray: true);
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
            while (VerificadorHostConocidoSpans.TrySiguienteLinea(ref resto, out ReadOnlySpan<char> linea))
            {
                lineas.Add(new Linea(inicio, linea.Length));
                inicio = texto.Length - resto.Length;
            }
        }

        private readonly record struct Linea(int Inicio, int Longitud);

        // Los campos de una línea sobre el búfer; IsValida falso si la línea no cuenta (menos de 3 campos, comentario, otro
        // marcador).
        private readonly ref struct Campos(bool isValida, ReadOnlySpan<char> hosts, ReadOnlySpan<char> tipo, ReadOnlySpan<char> base64, bool isRevocada)
        {
            public bool IsValida { get; } = isValida;
            public ReadOnlySpan<char> Hosts { get; } = hosts;
            public ReadOnlySpan<char> Tipo { get; } = tipo;
            public ReadOnlySpan<char> Base64 { get; } = base64;
            public bool IsRevocada { get; } = isRevocada;
        }

        private readonly struct Partir(char[] texto) : ISelectDelegado<Linea, Campos>
        {
            private readonly char[] _texto = texto;

            public Campos Ejecutar(Linea objetoLista)
            {
                ReadOnlySpan<char> resto = _texto.AsSpan(objetoLista.Inicio, objetoLista.Longitud);
                ReadOnlySpan<char> primero = VerificadorHostConocidoSpans.SiguienteCampo(ref resto);
                if (primero.IsEmpty || primero[0] == '#')
                    return default;

                int restantes = VerificadorHostConocidoSpans.ContarCampos(resto);
                if (restantes < 2)
                    return default;

                bool isRevocada = primero[0] == '@';
                if (isRevocada && (!primero.SequenceEqual("@revoked") || restantes < 3))
                    return default;

                ReadOnlySpan<char> hosts = isRevocada ? VerificadorHostConocidoSpans.SiguienteCampo(ref resto) : primero;
                ReadOnlySpan<char> tipo = VerificadorHostConocidoSpans.SiguienteCampo(ref resto);
                ReadOnlySpan<char> base64 = VerificadorHostConocidoSpans.SiguienteCampo(ref resto);
                return new Campos(true, hosts, tipo, base64, isRevocada);
            }
        }

        private struct DelHost : IWhereDelegado<Campos, string>
        {
            public readonly bool Ejecutar(Campos objetoLista, string otro) => objetoLista.IsValida && VerificadorHostConocidoSpans.IsCoincideHost(objetoLista.Hosts, otro);
        }

        private struct Existe : IWhereDelegado<string>
        {
            public readonly bool Ejecutar(string objetoLista) => File.Exists(objetoLista);
        }
    }
}
