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
        [InlineData(new[] { "--version", "--http" }, null, "--version va sola")]
        [InlineData(new[] { "--http", "--version" }, null, "--version va sola")]
        public void Invalidos(string[] args, string? entorno, string fragmento)
        {
            // Arrange
            _ = OpcionesArranque.TryParsear(args, entorno, out _, out string? error).Should().BeFalse();

            // Act & Assert
            _ = error.Should().Contain(fragmento);
        }

        // El error de un argumento desconocido da el uso completo, con '--version'.
        [Fact]
        public void ArgumentoDesconocido_ElUsoIncluyeVersion()
        {
            // Act
            bool isValido = OpcionesArranque.TryParsear(["--otro"], null, out _, out string? error);

            // Assert
            _ = isValido.Should().BeFalse();
            _ = error.Should().Be("Argumento no reconocido: '--otro'. Uso: native_ssh [--http | --http-unsecure] [--puerto N] | native_ssh --version");
        }

        // El puerto llega por '--puerto' o por NATIVE_SSH_PUERTO_HTTP y se valida igual: entre 1 y 65535.
        [Theory]
        [InlineData("1", 1)]
        [InlineData("65535", 65535)]
        public void Puerto_ExtremosDelRango_ValidosPorArgumentoYPorEntorno(string texto, int puerto)
        {
            // Act
            bool isArgumentoValido = OpcionesArranque.TryParsear(["--http", "--puerto", texto], null, out OpcionesArranque porArgumento, out string? errorArgumento);
            bool isEntornoValido = OpcionesArranque.TryParsear(["--http"], texto, out OpcionesArranque porEntorno, out string? errorEntorno);

            // Assert
            _ = errorArgumento.Should().BeNull();
            _ = isArgumentoValido.Should().BeTrue();
            _ = porArgumento.Puerto.Should().Be(puerto);
            _ = errorEntorno.Should().BeNull();
            _ = isEntornoValido.Should().BeTrue();
            _ = porEntorno.Puerto.Should().Be(puerto);
        }

        // Solo dígitos ASCII (NumberStyles.None): ni signo ni espacios alrededor, y nada fuera de 1-65535.
        [Theory]
        [InlineData("0")]
        [InlineData("65536")]
        [InlineData("+80")]
        [InlineData(" 80")]
        [InlineData("80 ")]
        [InlineData("-1")]
        [InlineData(" ")]
        public void Puerto_FueraDeRangoConSignoOConEspacios_InvalidoPorArgumentoYPorEntorno(string texto)
        {
            // Act
            bool isArgumentoValido = OpcionesArranque.TryParsear(["--http", "--puerto", texto], null, out _, out string? errorArgumento);
            bool isEntornoValido = OpcionesArranque.TryParsear(["--http"], texto, out _, out string? errorEntorno);

            // Assert
            _ = isArgumentoValido.Should().BeFalse();
            _ = errorArgumento.Should().Be($"Puerto no válido: '{texto}'.");
            _ = isEntornoValido.Should().BeFalse();
            _ = errorEntorno.Should().Be($"Puerto no válido: '{texto}'.");
        }

        // Decisión: NATIVE_SSH_PUERTO_HTTP vacía equivale a no definida y se usa el puerto por defecto. Es la convención de
        // todas las variables del servidor (NATIVE_SSH_DIRECTORIO_SSH, _IDENTIDADES, _PASSWORD y _NIVEL_LOG vacías también
        // se ignoran) y de los scripts de pruebas (${VAR:-defecto}); además el puerto se valida también en modo stdio, así
        // que tratarla como error impediría arrancar el servidor stdio por una variable en blanco que ni siquiera usa.
        // Un valor no vacío inválido, incluido uno de solo espacios, sí es un error.
        [Theory]
        [InlineData(new string[0], ModoTransporteEnum.Stdio)]
        [InlineData(new[] { "--http" }, ModoTransporteEnum.HttpLoopback)]
        [InlineData(new[] { "--http-unsecure" }, ModoTransporteEnum.HttpTodasLasInterfaces)]
        public void PuertoEntorno_Vacio_EquivaleANoDefinidoYUsaElPorDefecto(string[] args, ModoTransporteEnum modo)
        {
            // Act
            bool isValido = OpcionesArranque.TryParsear(args, "", out OpcionesArranque opciones, out string? error);

            // Assert
            _ = error.Should().BeNull();
            _ = isValido.Should().BeTrue();
            _ = opciones.Modo.Should().Be(modo);
            _ = opciones.Puerto.Should().Be(OpcionesArranque.PuertoPorDefecto);
        }

        // Un '--puerto' explícito sin valor útil es un error de quien lanza el servidor, no un "no definido".
        [Fact]
        public void PuertoArgumento_Vacio_EsInvalido()
        {
            // Act
            bool isValido = OpcionesArranque.TryParsear(["--http", "--puerto", ""], null, out _, out string? error);

            // Assert
            _ = isValido.Should().BeFalse();
            _ = error.Should().Be("Puerto no válido: ''.");
        }
    }
}
