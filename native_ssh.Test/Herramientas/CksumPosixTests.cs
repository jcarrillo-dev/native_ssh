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
        public void Calcular_CoincideConElCksumDelSistema(int longitud)
        {
            // Arrange
            byte[] datos = new byte[longitud];
            new Random(longitud).NextBytes(datos);
            string archivo = Path.Combine(Path.GetTempPath(), $"ns_cksum_{Guid.NewGuid():N}");
            File.WriteAllBytes(archivo, datos);
            string sistema;
            try
            {
                ProcessStartInfo inicio = new("bash", ["-c", "cksum < \"$1\"", "ns", archivo]) { RedirectStandardOutput = true };
                using Process proceso = Process.Start(inicio)!;
                sistema = proceso.StandardOutput.ReadToEnd().Trim();
                proceso.WaitForExit();
            }
            finally
            {
                File.Delete(archivo);
            }

            // Act
            uint suma = CksumPosix.Calcular(datos);

            // Assert
            _ = $"{suma.ToString(CultureInfo.InvariantCulture)} {longitud.ToString(CultureInfo.InvariantCulture)}".Should().Be(sistema);
        }
    }
}
