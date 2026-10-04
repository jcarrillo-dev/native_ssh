using System.Buffers;

namespace NativeSsh.Buffers
{
    public readonly record struct LecturaSalida(long Desde, int Copiados, long Fin, bool HasDatosDescartados);

    // Salida de una petición en segmentos del pool con offsets absolutos.
    // Un escritor (bomba de lectura) y varios lectores (herramientas MCP): todo pasa por el candado.
    public sealed class BufferSalida
    {
        public const int TamanoSegmento = 16 * 1024;

        private readonly Lock _candado = new();
        private readonly List<byte[]> _segmentos = [];
        private readonly int _capacidadMaxima;
        private readonly TimeProvider _reloj;
        private long _inicioAbsoluto;
        private long _finAbsoluto;
        private bool _isLiberado;

        public BufferSalida(int capacidadMaxima, TimeProvider reloj)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacidadMaxima, 1);
            _capacidadMaxima = capacidadMaxima;
            _reloj = reloj;
            UltimaEscritura = reloj.GetTimestamp();
        }

        public long Longitud
        {
            get
            {
                lock (_candado)
                    return _finAbsoluto;
            }
        }

        public long UltimaEscritura
        {
            get
            {
                lock (_candado)
                    return field;
            }

            private set;
        }

        public void Escribir(ReadOnlySpan<byte> datos)
        {
            if (datos.IsEmpty)
                return;

            lock (_candado)
            {
                if (_isLiberado)
                    return;

                while (!datos.IsEmpty)
                {
                    long usados = _finAbsoluto - _inicioAbsoluto;
                    int indice = (int)(usados / TamanoSegmento);
                    int desplazamiento = (int)(usados % TamanoSegmento);
                    if (indice == _segmentos.Count)
                        _segmentos.Add(ArrayPool<byte>.Shared.Rent(TamanoSegmento));

                    int aCopiar = Math.Min(datos.Length, TamanoSegmento - desplazamiento);
                    datos[..aCopiar].CopyTo(_segmentos[indice].AsSpan(desplazamiento, aCopiar));
                    datos = datos[aCopiar..];
                    _finAbsoluto += aCopiar;
                }

                while (_finAbsoluto - _inicioAbsoluto - TamanoSegmento >= _capacidadMaxima)
                {
                    ArrayPool<byte>.Shared.Return(_segmentos[0]);
                    _segmentos.RemoveAt(0);
                    _inicioAbsoluto += TamanoSegmento;
                }

                UltimaEscritura = _reloj.GetTimestamp();
            }
        }

        public LecturaSalida Leer(long desde, Span<byte> destino)
        {
            lock (_candado)
            {
                bool hasDescartados = desde < _inicioAbsoluto;
                long inicio = Math.Clamp(desde, _inicioAbsoluto, _finAbsoluto);
                int total = (int)Math.Min(destino.Length, _finAbsoluto - inicio);
                int copiados = 0;
                while (copiados < total)
                {
                    long relativo = inicio + copiados - _inicioAbsoluto;
                    int indice = (int)(relativo / TamanoSegmento);
                    int desplazamiento = (int)(relativo % TamanoSegmento);
                    int trozo = Math.Min(total - copiados, TamanoSegmento - desplazamiento);
                    _segmentos[indice].AsSpan(desplazamiento, trozo).CopyTo(destino.Slice(copiados, trozo));
                    copiados += trozo;
                }

                return new LecturaSalida(inicio, copiados, _finAbsoluto, hasDescartados);
            }
        }

        public void Liberar()
        {
            lock (_candado)
            {
                if (_isLiberado)
                    return;

                _isLiberado = true;
                foreach (byte[] segmento in _segmentos)
                    ArrayPool<byte>.Shared.Return(segmento);
                _segmentos.Clear();
                _inicioAbsoluto = _finAbsoluto;
            }
        }
    }
}
