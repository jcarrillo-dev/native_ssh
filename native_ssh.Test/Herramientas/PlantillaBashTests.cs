using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Herramientas
{
    public class PlantillaBashTests
    {
        // Si los fuentes pasan a CRLF, los literales de las plantillas llevarán '\r': se normalizan los tramos literales,
        // nunca los valores interpolados.
        [Fact]
        public void Crear_NormalizaLosTramosLiteralesYCopiaLosValoresTalCual()
        {
            // Arrange
            string valor = "'x\ry\r\nz\fa\u0085b\u2028c\u2029d'";
            int numero = -5;

            // Act
            string script = PlantillaBash.Crear($"linea 1\r\nruta={valor}\rlinea 3 {numero}\u2028fin\r\n");

            // Assert
            _ = script.Should().Be($"linea 1\nruta={valor}\nlinea 3 -5\nfin\n");
        }

        [Fact]
        public void Crear_SinValores_DevuelveElLiteralNormalizado()
        {
            // Act
            string script = PlantillaBash.Crear($$"""
                if [ -e "$ruta" ]; then
                  exit 1
                fi
                """);

            // Assert
            _ = script.Should().Be("if [ -e \"$ruta\" ]; then\n  exit 1\nfi");
        }
    }
}
