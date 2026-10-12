using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Herramientas
{
    // Reglas de ssh_input sin shell: qué es «más de una línea» para secreto=true.
    public class EntradaTecleadaTests
    {
        // Fin de línea del terminal en modo canónico: '\n', '\r' (ICRNL) y Ctrl+D (VEOF). Con enter=false se admite uno solo al
        // final; con enter=true, ninguno (el salto lo añade la herramienta). NUL no termina línea.
        [Theory]
        [InlineData("clave", true, false)]
        [InlineData("clave", false, false)]
        [InlineData("cla\nve", true, true)]
        [InlineData("cla\rve", false, true)]
        [InlineData("cla\u0004ve", true, true)]
        [InlineData("\u0004\u0004clave", true, true)]
        [InlineData("\u0004clave", false, true)]
        [InlineData("clave\n", true, true)]
        [InlineData("clave\u0004", true, true)]
        [InlineData("clave\n", false, false)]
        [InlineData("clave\r", false, false)]
        [InlineData("clave\u0004", false, false)]
        [InlineData("clave\r\n", false, true)]
        [InlineData("clave\u0004\n", false, true)]
        [InlineData("cla\0ve", true, false)]
        [InlineData("", true, false)]
        [InlineData("\n", false, false)]
        public void IsVariasLineas_FinesDeLineaDelTerminal(string texto, bool isEnter, bool isVarias) =>
            _ = HerramientasSsh.IsVariasLineas(texto, isEnter).Should().Be(isVarias);
    }
}
