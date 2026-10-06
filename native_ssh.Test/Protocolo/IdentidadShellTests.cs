using System.Buffers;
using System.Text;
using FluentAssertions;
using NativeSsh.Protocolo;

namespace NativeSsh.Test.Protocolo
{
    public class IdentidadShellTests
    {
        private static readonly Guid Id = Guid.Parse("0123456789abcdef0123456789abcdef");

        private static bool TryLeer(string salida, out IdentidadShell identidad) => IdentidadShell.TryLeer(Encoding.UTF8.GetBytes(salida), Id, out identidad);

        [Theory]
        [InlineData("__NSI_0123456789abcdef0123456789abcdef_4242_1__\n", 4242, 1)]
        [InlineData("ruido\n__NSI_0123456789abcdef0123456789abcdef_7_0__\n__NSP_x", 7, 0)]
        [InlineData("__NSI_ffffffffffffffffffffffffffffffff_1_1__\n__NSI_0123456789abcdef0123456789abcdef_2147483647_3__", 2147483647, 3)]
        public void TryLeer_ConElMarcadorDeSuIdUnaVez_DevuelvePidYPadre(string salida, int pid, int pidPadre)
        {
            // Act
            bool isLeida = TryLeer(salida, out IdentidadShell identidad);

            // Assert
            _ = isLeida.Should().BeTrue();
            _ = identidad.Should().Be(new IdentidadShell(pid, pidPadre));
        }

        [Theory]
        [InlineData("")]
        [InlineData("__NSI_0123456789abcdef0123456789abcdef_\n")]
        [InlineData("__NSI_0123456789abcdef0123456789abcdef_12__\n")]
        [InlineData("__NSI_0123456789abcdef0123456789abcdef_12_34_\n")]
        [InlineData("__NSI_0123456789abcdef0123456789abcdef_12_34")]
        [InlineData("__NSI_0123456789abcdef0123456789abcdef_0_34__\n")]
        [InlineData("__NSI_0123456789abcdef0123456789abcdef_-1_34__\n")]
        [InlineData("__NSI_0123456789abcdef0123456789abcdef_1x_34__\n")]
        [InlineData("__NSI_0123456789abcdef0123456789abcdef_99999999999_34__\n")]
        [InlineData("__NSI_0123456789abcdef0123456789abcdef_2147483648_34__\n")]
        [InlineData("__NSI_0123456789abcdef0123456789abcdef_12_34__\n__NSI_0123456789abcdef0123456789abcdef_12_34__\n")]
        [InlineData("__NSI_ffffffffffffffffffffffffffffffff_12_34__\n")]
        public void TryLeer_SinElMarcadorExactoUnaVez_NoDevuelveNada(string salida)
        {
            // Act
            bool isLeida = TryLeer(salida, out IdentidadShell identidad);

            // Assert
            _ = isLeida.Should().BeFalse();
            _ = identidad.Should().Be(default(IdentidadShell));
        }

        [Fact]
        public void Escribir_PidYPadreSeparadosPorUnEspacio()
        {
            // Arrange
            ArrayBufferWriter<byte> escritor = new();

            // Act
            new IdentidadShell(2147483647, 0).Escribir(escritor);

            // Assert
            _ = Encoding.ASCII.GetString(escritor.WrittenSpan).Should().Be("2147483647 0");
        }
    }
}
