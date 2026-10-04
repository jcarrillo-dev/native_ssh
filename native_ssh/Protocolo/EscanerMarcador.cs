using NativeSsh.Buffers;

namespace NativeSsh.Protocolo
{
    public enum ResultadoMarcadorEnum
    {
        Completa,
        Incompleta,
        Invalida
    }

    // Busca el marcador de fin en un flujo troceado. Cada byte se examina una vez más como mucho:
    // solo se retienen los bytes finales que todavía pueden ser el principio de un marcador.
    // No es seguro entre hilos: SesionShell lo usa siempre bajo su candado de proceso.
    public sealed class EscanerMarcador
    {
        private readonly byte[] _ventana;
        private readonly byte[] _idEsperado = new byte[MarcadorFin.LongitudId];
        private readonly byte[] _idIgnorado = new byte[MarcadorFin.LongitudId];
        private readonly int _tamanoMaximoBloque;
        private int _pendientes;
        private bool _hasIdIgnorado;
        private bool _isSaltarSaltoLinea;

        public EscanerMarcador(int tamanoMaximoBloque)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(tamanoMaximoBloque, 1);
            _tamanoMaximoBloque = tamanoMaximoBloque;
            _ventana = new byte[tamanoMaximoBloque + MarcadorFin.MaxLongitud];
        }

        public bool IsActivo { get; private set; }

        // idIgnorado: marcador que se descarta sin emitirlo (el del comando original durante una interrupción).
        public void Reiniciar(Guid id, Guid? idIgnorado = null)
        {
            _ = id.TryFormat(_idEsperado, out _, "N");
            _hasIdIgnorado = idIgnorado.HasValue;
            if (idIgnorado is { } ignorado)
                _ = ignorado.TryFormat(_idIgnorado, out _, "N");
            IsActivo = true;
        }

        public void Desactivar()
        {
            IsActivo = false;
            _pendientes = 0;
            _isSaltarSaltoLinea = false;
        }

        public bool Procesar(ReadOnlySpan<byte> bloque, BufferSalida? salida, out int codigo)
        {
            codigo = 0;
            if (bloque.Length > _tamanoMaximoBloque)
                throw new ArgumentOutOfRangeException(nameof(bloque), "El bloque supera el tamaño máximo del escáner.");

            if (_isSaltarSaltoLinea && !bloque.IsEmpty)
            {
                _isSaltarSaltoLinea = false;
                if (bloque[0] == (byte)'\n')
                    bloque = bloque[1..];
            }

            if (!IsActivo || bloque.IsEmpty)
                return false;

            bloque.CopyTo(_ventana.AsSpan(_pendientes));
            Span<byte> ventana = _ventana.AsSpan(0, _pendientes + bloque.Length);
            _pendientes = 0;
            int emitido = 0;
            int busqueda = 0;
            while (true)
            {
                int relativo = ventana[busqueda..].IndexOf(MarcadorFin.Cabecera);
                if (relativo < 0)
                    break;

                int inicio = busqueda + relativo;
                ResultadoMarcadorEnum resultado = Analizar(ventana[inicio..], out int codigoLeido, out int longitud);
                if (resultado == ResultadoMarcadorEnum.Incompleta)
                {
                    Emitir(salida, ventana[emitido..inicio]);
                    Retener(ventana[inicio..]);
                    return false;
                }

                if (resultado == ResultadoMarcadorEnum.Invalida)
                {
                    busqueda = inicio + 1;
                    continue;
                }

                Span<byte> id = ventana.Slice(inicio + MarcadorFin.LongitudCabecera, MarcadorFin.LongitudId);
                if (id.SequenceEqual(_idEsperado))
                {
                    Emitir(salida, ventana[emitido..inicio]);
                    _isSaltarSaltoLinea = inicio + longitud == ventana.Length;
                    IsActivo = false;
                    codigo = codigoLeido;
                    return true;
                }

                if (_hasIdIgnorado && id.SequenceEqual(_idIgnorado))
                {
                    Emitir(salida, ventana[emitido..inicio]);
                    emitido = inicio + longitud;
                    if (emitido == ventana.Length)
                        _isSaltarSaltoLinea = true;
                    else if (ventana[emitido] == (byte)'\n')
                        emitido++;
                    busqueda = emitido;
                    continue;
                }

                busqueda = inicio + 1;
            }

            int retenidos = LongitudPrefijoFinal(ventana[emitido..]);
            Emitir(salida, ventana[emitido..^retenidos]);
            Retener(ventana[^retenidos..]);
            return false;
        }

        public static ResultadoMarcadorEnum Analizar(ReadOnlySpan<byte> datos, out int codigo, out int longitud)
        {
            codigo = 0;
            longitud = 0;
            int i = MarcadorFin.LongitudCabecera;
            int finId = i + MarcadorFin.LongitudId;
            for (; i < finId; i++)
            {
                if (i >= datos.Length)
                    return ResultadoMarcadorEnum.Incompleta;
                if (!char.IsAsciiHexDigitLower((char)datos[i]))
                    return ResultadoMarcadorEnum.Invalida;
            }

            if (i >= datos.Length)
                return ResultadoMarcadorEnum.Incompleta;
            if (datos[i++] != (byte)'_')
                return ResultadoMarcadorEnum.Invalida;

            int digitos = 0;
            while (true)
            {
                if (i >= datos.Length)
                    return ResultadoMarcadorEnum.Incompleta;

                byte actual = datos[i];
                if (!char.IsAsciiDigit((char)actual))
                    break;
                if (++digitos > MarcadorFin.MaxDigitosCodigo)
                    return ResultadoMarcadorEnum.Invalida;

                codigo = (codigo * 10) + (actual - '0');
                i++;
            }

            if (digitos == 0 || datos[i] != (byte)'_')
                return ResultadoMarcadorEnum.Invalida;
            if (i + 1 >= datos.Length)
                return ResultadoMarcadorEnum.Incompleta;
            if (datos[i + 1] != (byte)'_')
                return ResultadoMarcadorEnum.Invalida;

            longitud = i + 2;
            return ResultadoMarcadorEnum.Completa;
        }

        // Sufijo más largo (hasta 5 bytes) que coincide con el principio de la cabecera.
        private static int LongitudPrefijoFinal(ReadOnlySpan<byte> datos)
        {
            ReadOnlySpan<byte> cabecera = MarcadorFin.Cabecera;
            for (int k = Math.Min(cabecera.Length - 1, datos.Length); k > 0; k--)
            {
                if (datos[^k..].SequenceEqual(cabecera[..k]))
                    return k;
            }

            return 0;
        }

        private static void Emitir(BufferSalida? salida, ReadOnlySpan<byte> datos)
        {
            if (!datos.IsEmpty)
                salida?.Escribir(datos);
        }

        private void Retener(ReadOnlySpan<byte> datos)
        {
            datos.CopyTo(_ventana);
            _pendientes = datos.Length;
        }
    }
}
