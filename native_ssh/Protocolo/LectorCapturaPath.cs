using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace NativeSsh.Protocolo
{
    // Lee la salida de la línea de captura del PATH (ConstructorComando.EscribirCapturaPath): el base64 entre
    // "__NSP_<id>_\n" y "\n__NSQ_<id>_\n", con ambos marcadores exactamente una vez y en orden. Lo que haya fuera de
    // ellos (salida ajena en el terminal) se ignora. El PATH debe ser UTF-8 estricto, sin caracteres de control y con
    // todas sus entradas absolutas: una entrada vacía o relativa haría buscar programas en el directorio de trabajo.
    public static class LectorCapturaPath
    {
        private const int LongitudId = MarcadorFin.LongitudId;

        private static readonly UTF8Encoding Utf8Estricto = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        private static readonly SearchValues<byte> AlfabetoBase64 = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/="u8);

        public static bool TryLeer(ReadOnlySpan<byte> salida, Guid id, [NotNullWhen(true)] out string? path, [NotNullWhen(false)] out string? motivo)
        {
            path = null;
            Span<byte> inicio = stackalloc byte[6 + LongitudId + 2];
            Span<byte> fin = stackalloc byte[7 + LongitudId + 2];
            Componer("__NSP_"u8, id, inicio);
            Componer("\n__NSQ_"u8, id, fin);

            int posicionInicio = salida.IndexOf(inicio);
            int posicionFin = salida.IndexOf(fin);
            bool isUnaVezYEnOrden = posicionInicio >= 0 && posicionFin >= posicionInicio + inicio.Length
                && salida.LastIndexOf(inicio) == posicionInicio && salida.LastIndexOf(fin) == posicionFin;
            if (!isUnaVezYEnOrden)
            {
                motivo = "la salida no contiene los marcadores de la captura exactamente una vez y en orden";
                return false;
            }

            ReadOnlySpan<byte> base64 = salida[(posicionInicio + inicio.Length)..posicionFin];
            if (base64.Length % 4 != 0 || base64.ContainsAnyExcept(AlfabetoBase64))
            {
                motivo = "lo que hay entre los marcadores no es base64";
                return false;
            }

            string texto;
            try
            {
                texto = Utf8Estricto.GetString(Convert.FromBase64String(Encoding.ASCII.GetString(base64)));
            }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
            {
                motivo = ex is FormatException ? "lo que hay entre los marcadores no es base64" : "el PATH no es UTF-8 válido";
                return false;
            }

            motivo = Validar(texto);
            if (motivo is not null)
                return false;

            path = texto;
            return true;
        }

        private static string? Validar(string texto)
        {
            if (texto.Length == 0)
                return "PATH vacío";

            foreach (char caracter in texto)
                if (char.IsControl(caracter))
                    return "el PATH contiene caracteres de control";

            foreach (string entrada in texto.Split(':'))
            {
                if (entrada.Length == 0)
                    return "el PATH tiene una entrada vacía (equivale al directorio de trabajo)";
                if (entrada[0] != '/')
                    return $"el PATH tiene una entrada relativa ('{entrada}')";
            }

            return null;
        }

        private static void Componer(ReadOnlySpan<byte> cabecera, Guid id, Span<byte> destino)
        {
            cabecera.CopyTo(destino);
            _ = id.TryFormat(destino[cabecera.Length..], out int escritos, "N");
            "_\n"u8.CopyTo(destino[(cabecera.Length + escritos)..]);
        }
    }
}
