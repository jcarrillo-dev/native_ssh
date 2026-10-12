using FluentAssertions;
using NativeSsh.Herramientas;
using NativeSsh.Protocolo;
using NativeSsh.Sesion;

namespace NativeSsh.Test.Herramientas
{
    // Reglas de ssh_input sin shell: qué es «más de una línea» para secreto=true y cómo se dice el recuento de lo descartado.
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

        // El recuento llega con tope (MarcadorFin.MaxLineasDescartadas): solo el tope se dice «o más».
        [Theory]
        [InlineData(1, "1 línea")]
        [InlineData(2, "2 líneas")]
        [InlineData(MarcadorFin.MaxLineasDescartadas - 1, "999998 líneas")]
        [InlineData(MarcadorFin.MaxLineasDescartadas, "999999 o más líneas")]
        public void DescribirLineas_SoloElTopeDiceOMas(int lineas, string esperado) =>
            _ = SesionShell.DescribirLineas(lineas).Should().Be(esperado);

        [Fact]
        public void DescribirTecleadoDescartado_SinLineasNoAvisaYConLineasDiceQuienNoLasLeyo()
        {
            // Act
            string? ninguna = SesionShell.DescribirTecleadoDescartado(0, "abc");
            string? dos = SesionShell.DescribirTecleadoDescartado(2, "abc");
            string? sinId = SesionShell.DescribirTecleadoDescartado(1, null);

            // Assert
            _ = ninguna.Should().BeNull();
            _ = dos.Should().StartWith("El comando anterior de este shell (abc) no leyó 2 líneas de entrada tecleada con ssh_input").And.Contain("ninguna se ejecutó como orden");
            _ = sinId.Should().StartWith("El comando anterior de este shell no leyó 1 línea");
        }
    }
}
