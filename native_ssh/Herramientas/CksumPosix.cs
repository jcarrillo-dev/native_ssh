namespace NativeSsh.Herramientas
{
    // Suma de 'cksum' (POSIX): CRC-32 de polinomio 0x04C11DB7 sin reflejar sobre los datos seguidos de su longitud en
    // bytes (little-endian, sin ceros a la izquierda), complementado. 'cksum < archivo' imprime "<suma> <bytes>". Respaldo de
    // ssh_replace_file_content para comprobar en el servidor que el archivo no cambió desde la lectura si no hay sha256sum.
    internal static class CksumPosix
    {
        private const uint Polinomio = 0x04C1_1DB7;
        private static readonly uint[] Tabla = CrearTabla();

        public static uint Calcular(ReadOnlySpan<byte> datos)
        {
            uint crc = 0;
            foreach (byte dato in datos)
                crc = (crc << 8) ^ Tabla[(crc >> 24) ^ dato];

            for (long longitud = datos.Length; longitud != 0; longitud >>= 8)
                crc = (crc << 8) ^ Tabla[(crc >> 24) ^ (byte)longitud];

            return ~crc;
        }

        private static uint[] CrearTabla()
        {
            uint[] tabla = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint valor = i << 24;
                for (int bit = 0; bit < 8; bit++)
                    valor = (valor & 0x8000_0000) != 0 ? (valor << 1) ^ Polinomio : valor << 1;
                tabla[i] = valor;
            }

            return tabla;
        }
    }
}
