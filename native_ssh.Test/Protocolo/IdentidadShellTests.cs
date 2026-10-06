using System.Buffers;
using System.Linq;
using System.Text;
using FluentAssertions;
using NativeSsh.Protocolo;

namespace NativeSsh.Test.Protocolo
{
    public class IdentidadShellTests
    {
        private static readonly Guid Id = Guid.Parse("0123456789abcdef0123456789abcdef");
        private static readonly Guid Testigo = Guid.Parse("fedcba9876543210fedcba9876543210");

        private static bool TryLeer(string salida, out IdentidadShell identidad) => IdentidadShell.TryLeer(Encoding.UTF8.GetBytes(salida), Id, Testigo, out identidad);

        [Theory]
        [InlineData("__NSI_0123456789abcdef0123456789abcdef_4242_1__\n", 4242, 1)]
        [InlineData("ruido\n__NSI_0123456789abcdef0123456789abcdef_7_0__\n__NSP_x", 7, 0)]
        [InlineData("__NSI_ffffffffffffffffffffffffffffffff_1_1__\n__NSI_0123456789abcdef0123456789abcdef_2147483647_3__", 2147483647, 3)]
        public void TryLeer_ConElMarcadorDeSuIdUnaVez_DevuelvePidPadreYElTestigoDelCanal(string salida, int pid, int pidPadre)
        {
            // Act
            bool isLeida = TryLeer(salida, out IdentidadShell identidad);

            // Assert
            _ = isLeida.Should().BeTrue();
            _ = identidad.Should().Be(new IdentidadShell(pid, pidPadre, Testigo));
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
        // La captura imprime algo tras "__NSI" si el bash no guardó el testigo del canal.
        [InlineData("__NSIx_0123456789abcdef0123456789abcdef_12_34__\n")]
        public void TryLeer_SinElMarcadorExactoUnaVez_NoDevuelveNada(string salida)
        {
            // Act
            bool isLeida = TryLeer(salida, out IdentidadShell identidad);

            // Assert
            _ = isLeida.Should().BeFalse();
            _ = identidad.Should().Be(default(IdentidadShell));
        }

        [Fact]
        public void Escribir_PidPadreYTestigoSeparadosPorUnEspacio()
        {
            // Arrange
            ArrayBufferWriter<byte> escritor = new();

            // Act
            new IdentidadShell(2147483647, 0, Testigo).Escribir(escritor);

            // Assert
            _ = Encoding.ASCII.GetString(escritor.WrittenSpan).Should().Be("2147483647 0 fedcba9876543210fedcba9876543210");
        }

        [Fact]
        public void NuevoTestigo_DistintoCadaVezYConLos128BitsAleatorios()
        {
            // Act
            HashSet<Guid> testigos = [.. Enumerable.Range(0, 1000).Select(_ => IdentidadShell.NuevoTestigo())];

            // Assert: no es un Guid v4 (que fija 6 bits): todos los bits salen del generador criptográfico.
            _ = testigos.Should().HaveCount(1000).And.NotContain(Guid.Empty);
            _ = testigos.Select(t => t.ToString("N")[12]).Distinct().Should().HaveCountGreaterThan(1);
        }
    }
}
