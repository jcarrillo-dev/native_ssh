using FluentAssertions;
using NativeSsh.Buffers;

namespace NativeSsh.Test.Buffers
{
    public class BufferAlquiladoTests
    {
        [Fact]
        public void LongitudCero_NoAlquila()
        {
            // Act & Assert
            using BufferAlquilado buffer = new(0);
            _ = buffer.Span.IsEmpty.Should().BeTrue();
        }

        [Fact]
        public void Longitud_ExponeSpanExacto()
        {
            // Act & Assert
            using BufferAlquilado buffer = new(100);
            _ = buffer.Span.Length.Should().Be(100);
        }

        [Fact]
        public void LongitudNegativa_Lanza()
        {
            // Arrange
            Action act = () =>
            {
                using BufferAlquilado buffer = new(-1);
            };

            // Act & Assert
            _ = act.Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
