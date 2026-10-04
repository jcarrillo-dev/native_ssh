using FluentAssertions;
using System.Text;
using NativeSsh.Buffers;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Herramientas
{
    public class PaginadorSalidaTests
    {
        private static BufferSalida Con(string texto, int capacidad = 1 << 20)
        {
            BufferSalida salida = new(capacidad, TimeProvider.System);
            salida.Escribir(Encoding.UTF8.GetBytes(texto));
            return salida;
        }

        [Fact]
        public void PaginaCompleta_LimpiaAnsi()
        {
            // Arrange
            PaginaSalida pagina = PaginadorSalida.Leer(Con("\u001b[32mok\u001b[0m\r\n"), 0, 1024, isFinalizada: true);

            // Act & Assert
            _ = pagina.Texto.Should().Be("ok\n");
            _ = pagina.Desde.Should().Be(0);
            _ = pagina.HasMas.Should().BeFalse();
        }

        [Fact]
        public void Paginas_NoPartenCaracteres_YCubrenTodo()
        {
            // Arrange
            string texto = string.Concat(Enumerable.Repeat("añ✓😀", 50));
            BufferSalida salida = Con(texto);
            StringBuilder resultado = new();
            long desde = 0;
            int paginas = 0;

            // Act
            while (true)
            {
                PaginaSalida pagina = PaginadorSalida.Leer(salida, desde, 7, isFinalizada: true);
                _ = pagina.Texto.Should().NotContain("�");
                _ = resultado.Append(pagina.Texto);
                desde = pagina.Siguiente;
                paginas++;
                if (!pagina.HasMas)
                    break;
            }

            // Assert
            _ = resultado.ToString().Should().Be(texto);
            _ = (paginas > 1).Should().BeTrue();
        }

        [Fact]
        public void DesdeEnMitadDeUnCaracter_SaltaLasContinuaciones()
        {
            // Arrange
            PaginaSalida pagina = PaginadorSalida.Leer(Con("ñb"), 1, 1024, isFinalizada: true);

            // Act & Assert
            _ = pagina.Texto.Should().Be("b");
            _ = pagina.Siguiente.Should().Be(3);
        }

        [Fact]
        public void SecuenciaIncompletaAlFinal_EnEjecucion_SeRetiene()
        {
            // Arrange
            BufferSalida salida = new(1024, TimeProvider.System);
            salida.Escribir([(byte)'a', 0xC3]);

            // Act
            PaginaSalida pagina = PaginadorSalida.Leer(salida, 0, 1024, isFinalizada: false);

            // Assert
            _ = pagina.Texto.Should().Be("a");
            _ = pagina.Siguiente.Should().Be(1);
            _ = pagina.HasMas.Should().BeTrue();
        }

        [Fact]
        public void SecuenciaIncompletaAlFinal_Finalizada_SeEntrega()
        {
            // Arrange
            BufferSalida salida = new(1024, TimeProvider.System);
            salida.Escribir([(byte)'a', 0xC3]);

            // Act
            PaginaSalida pagina = PaginadorSalida.Leer(salida, 0, 1024, isFinalizada: true);

            // Assert
            _ = pagina.Texto.Should().Be("a�");
            _ = pagina.HasMas.Should().BeFalse();
        }

        [Fact]
        public void DatosDescartados_SeIndican()
        {
            // Arrange
            BufferSalida salida = Con(new string('x', 100_000), capacidad: 16 * 1024);

            // Act
            PaginaSalida pagina = PaginadorSalida.Leer(salida, 0, 1024, isFinalizada: true);

            // Assert
            _ = pagina.HasDatosDescartados.Should().BeTrue();
            _ = (pagina.Desde > 0).Should().BeTrue();
        }
    }
}
