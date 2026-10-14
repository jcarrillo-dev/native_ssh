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
    // un secreto, sale con '*'). La búsqueda y lo retenido se deciden sobre los bytes originales, no sobre los ya
    // enmascarados: desde el primer secreto se guardan aparte los últimos bytes tal como llegaron (_colaOriginal: al menos
    // MinBytesColaOriginal y al menos el secreto más largo - 1), para que una aparición solapada con otra ya tapada y
    // repartida entre dos escrituras se encuentre entera, también la de un secreto registrado después (su principio pudo
    // quedar tapado por otro más corto). La búsqueda solo mira los últimos (máximo - 1) de esos bytes. El resultado es el
    // mismo que enmascarar el flujo completo de una vez. Lo ya legible no se vuelve a retener (un secreto registrado a mitad
    // de la salida no hace retroceder Longitud). Limitación: un secreto de más de MinBytesColaOriginal + 1 bytes registrado
    // después de que otro se enmascarara puede no encontrarse si su principio llegó antes y quedó tapado (la cola se amplía
    // con lo que queda en el búfer, que puede estar ya enmascarado). Solo se reconocen los bytes UTF-8 tal cual: un eco
    // transformado (^X de ECHOCTL, secuencias intercaladas por un readline, un programa que lo imprime alterado) no.
    public sealed class BufferSalida : IDestinoSalida
    {
        public const int TamanoSegmento = 16 * 1024;

        // Bytes originales que se guardan como mínimo desde el primer secreto (ver la cabecera): más que una línea del
        // terminal en modo canónico (4095), lo más largo que se suele teclear.
        public const int MinBytesColaOriginal = 4096;
        private const byte Mascara = (byte)'*';

        private readonly Lock _candado = new();
        private readonly List<byte[]> _segmentos = [];
        private readonly TimeProvider _reloj;
        private long _inicioAbsoluto;
        private long _finAbsoluto;
        private bool _isLiberado;
        private List<byte[]>? _secretos;
        private volatile bool _hasSecretos;
        private int _maxBytesSecreto;
        private int _retenidos;
        private bool _isCerrado;
        private byte[]? _colaOriginal;
        private int _longitudColaOriginal;

        public BufferSalida(int capacidadMaxima, TimeProvider reloj)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacidadMaxima, 1);
            CapacidadMaxima = capacidadMaxima;
            _reloj = reloj;
            UltimaEscritura = reloj.GetTimestamp();
        }

        // Lo que se conserva como mucho: lo anterior se descarta (Longitud sigue contando desde el principio).
        public int CapacidadMaxima { get; }

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

        // true si alguna vez se registró un secreto (no vuelve a false al liberar): lo que no pasa por este búfer y puede
        // contenerlo (la vista previa de guardarSalida, leída del archivo) no se muestra.
        public bool HasSecretos => _hasSecretos;

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
                _hasSecretos = true;
                int capacidad = Math.Max(secreto.Length - 1, MinBytesColaOriginal);
                if (_colaOriginal is null || capacidad > _colaOriginal.Length)
                    AmpliarColaOriginal(capacidad);
                _maxBytesSecreto = Math.Max(_maxBytesSecreto, secreto.Length);
            }
        }

        // La cola original pasa a guardar 'capacidad' bytes: los que ya tenía (originales) y, delante, los anteriores que
        // queden en el búfer (antes del primer secreto, originales; si no, pueden estar ya enmascarados).
        private void AmpliarColaOriginal(int capacidad)
        {
            byte[] nueva = new byte[capacidad];
            int disponibles = (int)Math.Min(capacidad, _finAbsoluto - _inicioAbsoluto);
            Copiar(_finAbsoluto - disponibles, nueva.AsSpan(0, disponibles));
            if (_colaOriginal is not null)
            {
                int conocidos = Math.Min(_longitudColaOriginal, disponibles);
                _colaOriginal.AsSpan(_longitudColaOriginal - conocidos, conocidos).CopyTo(nueva.AsSpan(disponibles - conocidos));
                CryptographicOperations.ZeroMemory(_colaOriginal);
            }

            _colaOriginal = nueva;
            _longitudColaOriginal = disponibles;
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
                    RellenarDesde(_finAbsoluto - _retenidos, _retenidos);
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
                long finVisibleAnterior = FinVisible;
                ReadOnlySpan<byte> nuevos = datos;
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
                    EnmascararSecretos(nuevos, inicioNuevos, finVisibleAnterior);

                while (_finAbsoluto - _inicioAbsoluto - TamanoSegmento >= CapacidadMaxima)
                {
                    // Como al liberar: a cero (lleva la salida remota y, con secretos, pudo tener uno antes de enmascararlo).
                    ArrayPool<byte>.Shared.Return(_segmentos[0], clearArray: true);
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
                // Lo retenido (principio de un secreto más largo que el tope) puede empezar antes de lo que se conserva.
                long fin = Math.Max(FinVisible, _inicioAbsoluto);
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
                    ArrayPool<byte>.Shared.Return(segmento, clearArray: true);
                _segmentos.Clear();
                _inicioAbsoluto = _finAbsoluto;
                _retenidos = 0;
                if (_secretos is not null)
                    foreach (byte[] secreto in _secretos)
                        CryptographicOperations.ZeroMemory(secreto);
                _secretos = null;
                if (_colaOriginal is not null)
                    CryptographicOperations.ZeroMemory(_colaOriginal);
                _colaOriginal = null;
                _longitudColaOriginal = 0;
            }
        }

        // Busca los secretos en lo recién escrito más los (máximo - 1) bytes anteriores tal como llegaron (de _colaOriginal:
        // un secreto partido entre dos escrituras, aunque se solape con otra aparición ya enmascarada), enmascara cada
        // aparición (también solapadas) y calcula lo que hay que retener al final, también sobre los originales; después
        // añade lo nuevo a la cola. Bajo _candado, antes de descartar segmentos por el tope.
        private void EnmascararSecretos(ReadOnlySpan<byte> nuevos, long inicioNuevos, long finVisibleAnterior)
        {
            int previos = Math.Min(_longitudColaOriginal, _maxBytesSecreto - 1);
            int longitud = previos + nuevos.Length;
            long inicio = inicioNuevos - previos;
            byte[] ventana = ArrayPool<byte>.Shared.Rent(longitud);
            try
            {
                Span<byte> datos = ventana.AsSpan(0, longitud);
                _colaOriginal.AsSpan(_longitudColaOriginal - previos, previos).CopyTo(datos);
                nuevos.CopyTo(datos[previos..]);
                foreach (byte[] secreto in _secretos!)
                    for (int desde = 0; desde <= datos.Length - secreto.Length;)
                    {
                        int posicion = datos[desde..].IndexOf(secreto);
                        if (posicion < 0)
                            break;

                        desde += posicion;
                        RellenarDesde(inicio + desde, secreto.Length);
                        desde++;
                    }

                // Lo que queda al final y empieza como algún secreto: se retiene (o, cerrada la salida, se enmascara ya). Nunca
                // lo que ya se podía leer antes de esta escritura.
                int retenidos = (int)Math.Min(LongitudPrefijoDeSecretoAlFinal(datos), _finAbsoluto - finVisibleAnterior);
                if (_isCerrado)
                {
                    if (retenidos > 0)
                        RellenarDesde(_finAbsoluto - retenidos, retenidos);
                    _retenidos = 0;
                }
                else
                    _retenidos = retenidos;

                AnadirAColaOriginal(nuevos);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(ventana, clearArray: true);
            }
        }

        // La cola original se queda con sus últimos bytes y los nuevos, hasta su capacidad (CopyTo admite el solapamiento).
        private void AnadirAColaOriginal(ReadOnlySpan<byte> nuevos)
        {
            byte[] cola = _colaOriginal!;
            int conservados = Math.Min(cola.Length, _longitudColaOriginal + nuevos.Length);
            if (nuevos.Length >= conservados)
                nuevos[^conservados..].CopyTo(cola);
            else
            {
                int deLaCola = conservados - nuevos.Length;
                cola.AsSpan(_longitudColaOriginal - deLaCola, deLaCola).CopyTo(cola);
                nuevos.CopyTo(cola.AsSpan(deLaCola));
            }

            _longitudColaOriginal = conservados;
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

        // Enmascara [inicio, inicio + longitud) en lo que sigue en el búfer: lo anterior a _inicioAbsoluto ya se descartó por el
        // tope (una aparición que empezaba allí).
        private void RellenarDesde(long inicio, int longitud)
        {
            long recorte = Math.Max(0, _inicioAbsoluto - inicio);
            if (recorte < longitud)
                Rellenar(inicio + recorte, (int)(longitud - recorte));
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
