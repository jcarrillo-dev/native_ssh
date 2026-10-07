using System.Buffers;
using System.Security.Cryptography;

namespace NativeSsh.Buffers
{
    public readonly record struct LecturaSalida(long Desde, int Copiados, long Fin, bool HasDatosDescartados);

    // Salida de una petición en segmentos del pool con offsets absolutos.
    // Un escritor (bomba de lectura) y varios lectores (herramientas MCP): todo pasa por el candado.
    //
    // Secretos (ssh_input con secreto=true, RegistrarSecreto): toda aparición literal de sus bytes en lo que se escribe
    // después (el eco del terminal, un programa que lo imprime) se sustituye por '*' byte a byte dentro de Escribir, antes de
    // que ningún lector la vea; con la misma longitud, las posiciones (desdeByte, siguienteByte) no cambian. Lo que al final
    // de lo escrito podría ser el principio de un secreto (el eco llega carácter a carácter) se retiene: Longitud y Leer no
    // lo cuentan hasta que la escritura siguiente lo aclara. Al cerrar la salida (la petición terminó, Cerrar) lo retenido
    // ya no se completará y se enmascara (lado seguro: si el final de la salida coincide por casualidad con el principio de
    // un secreto, sale con '*'). Solo se reconocen los bytes UTF-8 tal cual: un eco transformado (^X de ECHOCTL, secuencias
    // intercaladas por un readline, un programa que lo imprime alterado) no.
    public sealed class BufferSalida
    {
        public const int TamanoSegmento = 16 * 1024;
        private const byte Mascara = (byte)'*';

        private readonly Lock _candado = new();
        private readonly List<byte[]> _segmentos = [];
        private readonly int _capacidadMaxima;
        private readonly TimeProvider _reloj;
        private long _inicioAbsoluto;
        private long _finAbsoluto;
        private bool _isLiberado;
        private List<byte[]>? _secretos;
        private int _maxBytesSecreto;
        private int _retenidos;
        private bool _isCerrado;

        public BufferSalida(int capacidadMaxima, TimeProvider reloj)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacidadMaxima, 1);
            _capacidadMaxima = capacidadMaxima;
            _reloj = reloj;
            UltimaEscritura = reloj.GetTimestamp();
        }

        // Lo que se puede leer: sin lo retenido por si es el principio de un secreto.
        public long Longitud
        {
            get
            {
                lock (_candado)
                    return FinVisible;
            }
        }

        private long FinVisible => _finAbsoluto - _retenidos;

        // Desde ahora, toda aparición de 'secreto' en lo que se escriba se enmascara (ver la cabecera).
        public void RegistrarSecreto(ReadOnlySpan<byte> secreto)
        {
            if (secreto.IsEmpty)
                return;

            lock (_candado)
            {
                if (_isLiberado)
                    return;

                (_secretos ??= []).Add(secreto.ToArray());
                _maxBytesSecreto = Math.Max(_maxBytesSecreto, secreto.Length);
            }
        }

        // La petición terminó: lo retenido ya no se completará y se enmascara; lo que llegue después se enmascara sin retener.
        public void Cerrar()
        {
            lock (_candado)
            {
                if (_isCerrado)
                    return;

                _isCerrado = true;
                if (_retenidos > 0 && !_isLiberado)
                    Rellenar(_finAbsoluto - _retenidos, _retenidos);
                _retenidos = 0;
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

                long inicioNuevos = _finAbsoluto;
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

                if (_secretos is not null)
                    EnmascararSecretos(inicioNuevos);

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
                long fin = FinVisible;
                bool hasDescartados = desde < _inicioAbsoluto;
                long inicio = Math.Clamp(desde, _inicioAbsoluto, fin);
                int total = (int)Math.Min(destino.Length, fin - inicio);
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

                return new LecturaSalida(inicio, copiados, fin, hasDescartados);
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
                    ArrayPool<byte>.Shared.Return(segmento, clearArray: _secretos is not null);
                _segmentos.Clear();
                _inicioAbsoluto = _finAbsoluto;
                _retenidos = 0;
                if (_secretos is not null)
                    foreach (byte[] secreto in _secretos)
                        CryptographicOperations.ZeroMemory(secreto);
                _secretos = null;
            }
        }

        // Busca los secretos en lo recién escrito más los (máximo - 1) bytes anteriores (un secreto partido entre dos
        // escrituras), enmascara cada aparición (también solapadas) y calcula lo que hay que retener al final. Bajo _candado,
        // antes de descartar segmentos por el tope.
        private void EnmascararSecretos(long inicioNuevos)
        {
            long inicio = Math.Max(_inicioAbsoluto, inicioNuevos - (_maxBytesSecreto - 1));
            int longitud = (int)(_finAbsoluto - inicio);
            byte[] ventana = ArrayPool<byte>.Shared.Rent(longitud);
            try
            {
                Span<byte> datos = ventana.AsSpan(0, longitud);
                Copiar(inicio, datos);
                foreach (byte[] secreto in _secretos!)
                    for (int desde = 0; desde <= datos.Length - secreto.Length;)
                    {
                        int posicion = datos[desde..].IndexOf(secreto);
                        if (posicion < 0)
                            break;

                        desde += posicion;
                        Rellenar(inicio + desde, secreto.Length);
                        desde++;
                    }

                // Tras enmascarar, lo que queda al final y empieza como algún secreto: se retiene (o, cerrada la salida,
                // se enmascara ya). Se vuelve a copiar la cola: lo enmascarado ya no cuenta como principio de nada.
                Copiar(inicio, datos);
                int retenidos = LongitudPrefijoDeSecretoAlFinal(datos);
                if (_isCerrado)
                {
                    if (retenidos > 0)
                        Rellenar(_finAbsoluto - retenidos, retenidos);
                    _retenidos = 0;
                }
                else
                {
                    _retenidos = retenidos;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(ventana, clearArray: true);
            }
        }

        // Mayor sufijo de 'datos' que es un principio propio (más corto que él) de algún secreto.
        private int LongitudPrefijoDeSecretoAlFinal(ReadOnlySpan<byte> datos)
        {
            int maximo = 0;
            foreach (byte[] secreto in _secretos!)
                for (int k = Math.Min(secreto.Length - 1, datos.Length); k > maximo; k--)
                    if (datos[^k..].SequenceEqual(secreto.AsSpan(0, k)))
                    {
                        maximo = k;
                        break;
                    }

            return maximo;
        }

        private void Copiar(long inicio, Span<byte> destino)
        {
            int copiados = 0;
            while (copiados < destino.Length)
            {
                long relativo = inicio + copiados - _inicioAbsoluto;
                int indice = (int)(relativo / TamanoSegmento);
                int desplazamiento = (int)(relativo % TamanoSegmento);
                int trozo = Math.Min(destino.Length - copiados, TamanoSegmento - desplazamiento);
                _segmentos[indice].AsSpan(desplazamiento, trozo).CopyTo(destino.Slice(copiados, trozo));
                copiados += trozo;
            }
        }

        private void Rellenar(long inicio, int longitud)
        {
            int rellenos = 0;
            while (rellenos < longitud)
            {
                long relativo = inicio + rellenos - _inicioAbsoluto;
                int indice = (int)(relativo / TamanoSegmento);
                int desplazamiento = (int)(relativo % TamanoSegmento);
                int trozo = Math.Min(longitud - rellenos, TamanoSegmento - desplazamiento);
                _segmentos[indice].AsSpan(desplazamiento, trozo).Fill(Mascara);
                rellenos += trozo;
            }
        }
    }
}
