using System.Buffers;
using System.Security.Cryptography;

namespace NativeSsh.Buffers
{
    // Buffer temporal del pool para usar con 'using' (Dispose por patrón, sin interfaz ni boxing). Vuelve al pool con el
    // tramo usado a cero: lleva el comando, lo tecleado o una página de la salida.
    public ref struct BufferAlquilado
    {
        private byte[]? _array;
        private readonly int _longitud;

        public BufferAlquilado(int longitud)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(longitud);
            _longitud = longitud;
            _array = longitud == 0 ? null : ArrayPool<byte>.Shared.Rent(longitud);
        }

        public readonly Span<byte> Span => _array is null ? [] : _array.AsSpan(0, _longitud);

        public void Dispose()
        {
            byte[]? array = _array;
            if (array is null)
                return;

            _array = null;
            // Solo el tramo que se pudo escribir (Span): Rent puede dar un array mayor.
            CryptographicOperations.ZeroMemory(array.AsSpan(0, _longitud));
            ArrayPool<byte>.Shared.Return(array);
        }
    }
}
