using System.Buffers;
using System.Text;

namespace NativeSsh.Protocolo
{
    public static class ConversorTexto
    {
        private static readonly SearchValues<byte> Especiales = SearchValues.Create("\u001B\r\u0007"u8);

        // Bytes de continuación UTF-8 (10xxxxxx) al principio: pertenecen a un carácter cortado por la página anterior.
        public static int ContarContinuacionesIniciales(ReadOnlySpan<byte> datos)
        {
            int cantidad = 0;
            while (cantidad < datos.Length && cantidad < 3 && (datos[cantidad] & 0xC0) == 0x80)
                cantidad++;
            return cantidad;
        }

        // Longitud máxima que no termina en mitad de una secuencia UTF-8.
        public static int AjustarFinUtf8(ReadOnlySpan<byte> datos)
        {
            int limite = Math.Max(0, datos.Length - 4);
            for (int i = datos.Length - 1; i >= limite; i--)
            {
                byte actual = datos[i];
                if ((actual & 0xC0) == 0x80)
                    continue;

                int necesarios = actual < 0x80 ? 1
                    : (actual & 0xE0) == 0xC0 ? 2
                    : (actual & 0xF0) == 0xE0 ? 3
                    : (actual & 0xF8) == 0xF0 ? 4
                    : 1;
                return datos.Length - i >= necesarios ? datos.Length : i;
            }

            return datos.Length;
        }

        // Quita secuencias CSI/OSC/charset, BEL y el CR de los CRLF. Un CR suelto se conserva.
        public static int LimpiarAnsi(Span<byte> datos)
        {
            int escritura = 0;
            int lectura = 0;
            while (lectura < datos.Length)
            {
                int tramo = datos[lectura..].IndexOfAny(Especiales);
                if (tramo < 0)
                    tramo = datos.Length - lectura;

                if (tramo > 0)
                {
                    datos.Slice(lectura, tramo).CopyTo(datos[escritura..]);
                    escritura += tramo;
                    lectura += tramo;
                    continue;
                }

                byte actual = datos[lectura];
                if (actual == 0x1B)
                    lectura = SaltarEscape(datos, lectura);
                else if (actual == (byte)'\r' && (lectura + 1 >= datos.Length || datos[lectura + 1] != (byte)'\n'))
                    datos[escritura++] = datos[lectura++];
                else
                    lectura++;
            }

            return escritura;
        }

        public static string ATexto(ReadOnlySpan<byte> datos) => Encoding.UTF8.GetString(datos);

        private static int SaltarEscape(ReadOnlySpan<byte> datos, int inicio)
        {
            int i = inicio + 1;
            if (i >= datos.Length)
                return i;

            byte tipo = datos[i];
            if (tipo == (byte)'[')
            {
                for (i++; i < datos.Length; i++)
                    if (datos[i] is >= 0x40 and <= 0x7E)
                        return i + 1;

                return i;
            }

            if (tipo == (byte)']')
            {
                for (i++; i < datos.Length; i++)
                    if (datos[i] == 0x07)
                        return i + 1;
                    else if (datos[i] == 0x1B && i + 1 < datos.Length && datos[i + 1] == (byte)'\\')
                        return i + 2;

                return i;
            }

            return tipo is (byte)'(' or (byte)')' or (byte)'*' or (byte)'+' ? Math.Min(i + 2, datos.Length) : i + 1;
        }
    }
}
