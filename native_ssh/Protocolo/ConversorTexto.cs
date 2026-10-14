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

        // Quita secuencias CSI/OSC/charset, BEL y el CR de los CRLF. Un CR suelto se conserva, salvo con isComoTerminal (RC3,
        // lo que ve el LLM de ssh_exec): entonces se trata como en un terminal, y lo que se escribe tras él en la misma línea
        // sustituye a lo anterior (las barras de progreso de apt, dpkg o curl daban líneas de miles de caracteres). Un CR suelto
        // al final de los datos o seguido del salto de línea no borra nada (se quita): si lo que lo sigue llega en otro trozo,
        // no se puede deshacer lo ya entregado. No es un terminal completo: un texto más corto que el anterior no deja a la
        // vista el resto de este.
        public static int LimpiarAnsi(Span<byte> datos, bool isComoTerminal = false)
        {
            int escritura = 0;
            int lectura = 0;
            int inicioLinea = 0;
            // Con isComoTerminal, un CR suelto deja el cursor al principio de la línea: lo siguiente que se escriba en ella la
            // sustituye; si lo siguiente es el salto de línea (p. ej. '\r\r\n'), la línea queda como estaba.
            bool isAlPrincipio = false;
            while (lectura < datos.Length)
            {
                int tramo = datos[lectura..].IndexOfAny(Especiales);
                if (tramo < 0)
                    tramo = datos.Length - lectura;

                if (tramo > 0)
                {
                    if (isAlPrincipio && datos[lectura] != (byte)'\n')
                        escritura = inicioLinea;
                    isAlPrincipio = false;
                    datos.Slice(lectura, tramo).CopyTo(datos[escritura..]);
                    int salto = datos.Slice(escritura, tramo).LastIndexOf((byte)'\n');
                    if (salto >= 0)
                        inicioLinea = escritura + salto + 1;
                    escritura += tramo;
                    lectura += tramo;
                    continue;
                }

                byte actual = datos[lectura];
                bool isRetornoSuelto = actual == (byte)'\r' && (lectura + 1 >= datos.Length || datos[lectura + 1] != (byte)'\n');
                if (actual == 0x1B)
                    lectura = SaltarEscape(datos, lectura);
                else if (isRetornoSuelto && isComoTerminal)
                {
                    isAlPrincipio = true;
                    lectura++;
                }
                else if (isRetornoSuelto)
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
