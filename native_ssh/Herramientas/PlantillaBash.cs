using System.Globalization;
using System.Runtime.CompilerServices;

namespace NativeSsh.Herramientas
{
    // Script de bash con valores del usuario (rutas, patrones, directorios...) interpolados: solo los tramos literales
    // —el código de la plantilla, que en un fuente CRLF llevaría '\r'— se normalizan a '\n'; los huecos se copian tal cual,
    // byte a byte. Normalizar el script ya interpolado convertía un CR, FF, NEL, LS o PS de una ruta en '\n' y la
    // herramienta operaba sobre otro archivo. Un hueco con código (un fragmento de otra plantilla) debe venir ya
    // normalizado: construido también con PlantillaBash o normalizado al definirlo.
    // Uso: string script = PlantillaBash.Crear($$"""... {{valorEscapado}} ...""");
    [InterpolatedStringHandler]
    internal ref struct PlantillaBash(int longitudLiterales, int huecos)
    {
        private DefaultInterpolatedStringHandler _texto = new(longitudLiterales, huecos, CultureInfo.InvariantCulture);

        public static string Crear(ref PlantillaBash plantilla) => plantilla._texto.ToStringAndClear();

        public void AppendLiteral(string literal) => _texto.AppendLiteral(literal.ReplaceLineEndings("\n"));

        public void AppendFormatted(string? valor) => _texto.AppendFormatted(valor);

        public void AppendFormatted<T>(T valor) => _texto.AppendFormatted(valor);
    }
}
