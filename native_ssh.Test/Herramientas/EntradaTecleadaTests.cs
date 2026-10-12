using FluentAssertions;
using NativeSsh.Herramientas;
using NativeSsh.Protocolo;
using NativeSsh.Sesion;

namespace NativeSsh.Test.Herramientas
{
    // Reglas de ssh_input sin shell: qué admite (con y sin secreto=true) y cómo se dice el recuento de lo descartado.
    public class EntradaTecleadaTests
    {
        // secreto=true: ningún control C0 ni DEL (CV6). Con enter=false se admite un '\n' o un '\r' al final (el salto que no
        // añade la herramienta); con enter=true, ninguno.
        [Theory]
        [InlineData("clave", true, false)]
        [InlineData("clave", false, false)]
        [InlineData("cla ve~!", true, false)]
        [InlineData("clave\u00e9\u20ac", true, false)]
        [InlineData("", true, false)]
        [InlineData("\n", false, false)]
        [InlineData("clave\n", false, false)]
        [InlineData("clave\r", false, false)]
        [InlineData("cla\nve", true, true)]
        [InlineData("cla\rve", false, true)]
        [InlineData("clave\n", true, true)]
        [InlineData("clave\r\n", false, true)]
        [InlineData("clave\u0004", false, true)]
        [InlineData("cla\u0004ve", true, true)]
        [InlineData("\u0003clave", true, true)]
        [InlineData("cla\0ve", true, true)]
        [InlineData("cla\tve", true, true)]
        [InlineData("cla\u001bve", true, true)]
        [InlineData("cla\u001fve", true, true)]
        [InlineData("cla\u007fve", true, true)]
        [InlineData("clave\u007f", false, true)]
        public void IsSecretoNoAdmitido_CualquierControl(string texto, bool isEnter, bool isNoAdmitido) =>
            _ = HerramientasSsh.IsSecretoNoAdmitido(texto, isEnter).Should().Be(isNoAdmitido);

        // Sin secreto: Ctrl+C, Ctrl+\ y Ctrl+Z nunca; Ctrl+V solo si no es lo último que se envía (enter=false); el resto, sí.
        [Theory]
        [InlineData("\u0003", false, HerramientasSsh.MensajeSenalDelTerminal)]
        [InlineData("uno\u0003dos", true, HerramientasSsh.MensajeSenalDelTerminal)]
        [InlineData("uno\u001c", true, HerramientasSsh.MensajeSenalDelTerminal)]
        [InlineData("uno\u001a", true, HerramientasSsh.MensajeSenalDelTerminal)]
        [InlineData("\u001a", false, HerramientasSsh.MensajeSenalDelTerminal)]
        [InlineData("uno\u0016", false, HerramientasSsh.MensajeLnextFinal)]
        [InlineData("uno\u0016", true, null)]
        [InlineData("\u0016x", false, null)]
        [InlineData("\u0004\u0015\u0017\u007f\u0012\u000f\u0011\u0013\0\b\t\u001b", false, null)]
        [InlineData("", false, null)]
        public void DescribirEntradaNoAdmitida_SenalesYCtrlVAlFinal(string texto, bool isEnter, string? esperado) =>
            _ = HerramientasSsh.DescribirEntradaNoAdmitida(texto, isEnter).Should().Be(esperado);

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
