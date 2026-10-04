using FluentAssertions;
using NativeSsh.Comun;

namespace NativeSsh.Test.Comun
{
    public class OpcionesArranqueTests
    {
        [Theory]
        [InlineData(new string[0], null, ModoTransporteEnum.Stdio, OpcionesArranque.PuertoPorDefecto)]
        [InlineData(new[] { "--http" }, null, ModoTransporteEnum.HttpLoopback, OpcionesArranque.PuertoPorDefecto)]
        [InlineData(new[] { "--http-unsecure" }, null, ModoTransporteEnum.HttpTodasLasInterfaces, OpcionesArranque.PuertoPorDefecto)]
        [InlineData(new[] { "--http", "--puerto", "9000" }, null, ModoTransporteEnum.HttpLoopback, 9000)]
        [InlineData(new[] { "--http" }, "9100", ModoTransporteEnum.HttpLoopback, 9100)]
        [InlineData(new[] { "--puerto", "9200", "--http" }, "9100", ModoTransporteEnum.HttpLoopback, 9200)]
        public void Validos(string[] args, string? entorno, ModoTransporteEnum modo, int puerto)
        {
            // Arrange
            _ = OpcionesArranque.TryParsear(args, entorno, out OpcionesArranque opciones, out string? error).Should().BeTrue();

            // Act & Assert
            _ = error.Should().BeNull();
            _ = opciones.Modo.Should().Be(modo);
            _ = opciones.Puerto.Should().Be(puerto);
            _ = opciones.IsHttp.Should().Be(modo != ModoTransporteEnum.Stdio);
        }

        [Theory]
        [InlineData(new[] { "--http", "--http-unsecure" }, null, "solo uno")]
        [InlineData(new[] { "--http", "--http" }, null, "solo uno")]
        [InlineData(new[] { "--otro" }, null, "no reconocido")]
        [InlineData(new[] { "--puerto" }, null, "no reconocido")]
        [InlineData(new[] { "--http", "--puerto", "0" }, null, "Puerto no válido")]
        [InlineData(new[] { "--http", "--puerto", "70000" }, null, "Puerto no válido")]
        [InlineData(new[] { "--http" }, "abc", "Puerto no válido")]
        public void Invalidos(string[] args, string? entorno, string fragmento)
        {
            // Arrange
            _ = OpcionesArranque.TryParsear(args, entorno, out _, out string? error).Should().BeFalse();

            // Act & Assert
            _ = error.Should().Contain(fragmento);
        }
    }
}
