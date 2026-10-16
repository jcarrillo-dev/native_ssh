using FluentAssertions;
using System.Text;
using NativeSsh.Protocolo;

namespace NativeSsh.Test.Protocolo
{
    public class ConversorTextoTests
    {
        [Theory]
        [InlineData(new byte[] { 0x61, 0x62 }, 2)]
        [InlineData(new byte[] { 0x61, 0xC3 }, 1)]
        [InlineData(new byte[] { 0x61, 0xC3, 0xB1 }, 3)]
        [InlineData(new byte[] { 0x61, 0xE2, 0x9C }, 1)]
        [InlineData(new byte[] { 0xF0, 0x9F, 0x98 }, 0)]
        [InlineData(new byte[] { 0xF0, 0x9F, 0x98, 0x80 }, 4)]
        [InlineData(new byte[] { }, 0)]
        public void AjustarFinUtf8_NoCortaCaracteres(byte[] datos, int esperado) => _ = ConversorTexto.AjustarFinUtf8(datos).Should().Be(esperado);

        [Theory]
        [InlineData(new byte[] { 0xB1, 0x61 }, 1)]
        [InlineData(new byte[] { 0x9C, 0x93, 0x61 }, 2)]
        [InlineData(new byte[] { 0x61 }, 0)]
        [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80 }, 3)]
        public void ContarContinuacionesIniciales(byte[] datos, int esperado) => _ = ConversorTexto.ContarContinuacionesIniciales(datos).Should().Be(esperado);

        [Theory]
        [InlineData("\u001b[31mrojo\u001b[0m", "rojo")]
        [InlineData("a\r\nb\r\n", "a\nb\n")]
        [InlineData("progreso\r50%\r100%\n", "progreso\r50%\r100%\n")]
        [InlineData("\u001b]0;titulo\u0007texto", "texto")]
        [InlineData("\u001b]0;titulo\u001b\\texto", "texto")]
        [InlineData("\u001b(Btexto", "texto")]
        [InlineData("campana\u0007", "campana")]
        [InlineData("ñandú ✓", "ñandú ✓")]
        [InlineData("incompleto\u001b[3", "incompleto")]
        [InlineData("", "")]
        public void LimpiarAnsi_EnElSitio(string entrada, string esperado)
        {
            // Arrange
            byte[] bytes = Encoding.UTF8.GetBytes(entrada);

            // Act
            int longitud = ConversorTexto.LimpiarAnsi(bytes);

            // Assert
            _ = Encoding.UTF8.GetString(bytes, 0, longitud).Should().Be(esperado);
        }

        // RC3: lo que ve el cliente de ssh_exec trata un '\r' suelto como un terminal: lo escrito tras el último '\r' de la línea
        // sustituye a lo anterior (barras de progreso de apt, dpkg o curl). Un '\r' al final de los datos o de la línea no
        // borra nada; el CRLF sigue siendo un salto.
        [Theory]
        [InlineData("progreso\r50%\r100%\n", "100%\n")]
        [InlineData("a\nb 10%\rb 20%\rb 30%\nc\n", "a\nb 30%\nc\n")]
        [InlineData("linea\r\n", "linea\n")]
        [InlineData("descargando 99%\r", "descargando 99%")]
        [InlineData("x\r\ry", "y")]
        [InlineData("x 50%\r\r\nsiguiente", "x 50%\nsiguiente")]
        [InlineData("x 50%\r\u001b[K\nsiguiente", "x 50%\nsiguiente")]
        [InlineData("\u001b[32m10%\u001b[0m\r\u001b[32m20%\u001b[0m\n", "20%\n")]
        [InlineData("sin retornos\n", "sin retornos\n")]
        [InlineData("\rprincipio", "principio")]
        public void LimpiarAnsi_ComoTerminal_ElRetornoSueltoSustituyeLaLinea(string entrada, string esperado)
        {
            // Arrange
            byte[] bytes = Encoding.UTF8.GetBytes(entrada);

            // Act
            int longitud = ConversorTexto.LimpiarAnsi(bytes, isComoTerminal: true);

            // Assert
            _ = Encoding.UTF8.GetString(bytes, 0, longitud).Should().Be(esperado);
        }

        [Fact]
        public void ATexto_DecodificaUtf8() => _ = ConversorTexto.ATexto([0xC3, 0xB1]).Should().Be("ñ");
    }
}
