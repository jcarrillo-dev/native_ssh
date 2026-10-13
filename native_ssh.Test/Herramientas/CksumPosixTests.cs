using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Herramientas
{
    public class CksumPosixTests
    {
        [Theory]
        [InlineData("", 4294967295u)]
        [InlineData("hola\n", 4003563917u)]
        public void Calcular_ValoresConocidosDeCksum(string texto, uint esperado) =>
            CksumPosix.Calcular(System.Text.Encoding.UTF8.GetBytes(texto)).Should().Be(esperado);

        // Contra el 'cksum' real (coreutils) con datos aleatorios de varios tamaños (la longitud entra en la suma).
        [Theory]
        [InlineData(1)]
        [InlineData(255)]
        [InlineData(256)]
        [InlineData(65_537)]
        [InlineData(773_184)]
        [Trait("Categoria", "SoloLinux")]
        public async Task Calcular_CoincideConElCksumDelSistema(int longitud)
        {
            // Arrange: los datos llegan a cksum por su entrada estándar, sin archivos temporales.
            byte[] datos = new byte[longitud];
            new Random(longitud).NextBytes(datos);
            ProcessStartInfo inicio = new("cksum") { RedirectStandardInput = true, RedirectStandardOutput = true };
            using Process proceso = Process.Start(inicio)!;
            Task<string> salida = proceso.StandardOutput.ReadToEndAsync();
            await proceso.StandardInput.BaseStream.WriteAsync(datos);
            proceso.StandardInput.Close();
            await proceso.WaitForExitAsync();
            string sistema = (await salida).Trim();

            // Act
            uint suma = CksumPosix.Calcular(datos);

            // Assert
            _ = $"{suma.ToString(CultureInfo.InvariantCulture)} {longitud.ToString(CultureInfo.InvariantCulture)}".Should().Be(sistema);
        }
    }
}
