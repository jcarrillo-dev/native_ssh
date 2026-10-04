using FluentAssertions;
using NativeSsh.Buffers;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Buffers
{
    public class BufferSalidaTests
    {
        private static byte[] Bytes(int cantidad)
        {
            byte[] datos = new byte[cantidad];
            for (int i = 0; i < cantidad; i++)
                datos[i] = (byte)(i % 251);
            return datos;
        }

        [Fact]
        public void EscribirYLeer_AtraviesaSegmentos()
        {
            // Arrange
            BufferSalida buffer = new(1 << 20, TimeProvider.System);
            byte[] datos = Bytes(40_000);
            buffer.Escribir(datos.AsSpan(0, 10_000));
            buffer.Escribir(datos.AsSpan(10_000));

            // Act
            byte[] destino = new byte[50_000];
            LecturaSalida lectura = buffer.Leer(0, destino);

            // Assert
            _ = lectura.Should().Be(new LecturaSalida(0, 40_000, 40_000, false));
            _ = destino.AsSpan(0, 40_000).SequenceEqual(datos).Should().BeTrue();
            _ = buffer.Longitud.Should().Be(40_000);
        }

        [Fact]
        public void Leer_DesdeDesplazamientoConDestinoPequeno()
        {
            // Arrange
            BufferSalida buffer = new(1 << 20, TimeProvider.System);
            byte[] datos = Bytes(40_000);
            buffer.Escribir(datos);

            // Act
            byte[] destino = new byte[100];
            LecturaSalida lectura = buffer.Leer(20_000, destino);

            // Assert
            _ = lectura.Should().Be(new LecturaSalida(20_000, 100, 40_000, false));
            _ = destino.AsSpan().SequenceEqual(datos.AsSpan(20_000, 100)).Should().BeTrue();
        }

        [Fact]
        public void Leer_MasAllaDelFin_NoCopiaNada()
        {
            // Arrange
            BufferSalida buffer = new(1 << 20, TimeProvider.System);
            buffer.Escribir(Bytes(1_000));

            // Act
            LecturaSalida lectura = buffer.Leer(5_000, new byte[10]);

            // Assert
            _ = lectura.Should().Be(new LecturaSalida(1_000, 0, 1_000, false));
        }

        [Fact]
        public void Tope_DescartaLosSegmentosMasAntiguos()
        {
            // Arrange
            BufferSalida buffer = new(32 * 1024, TimeProvider.System);
            byte[] datos = Bytes(100_000);
            buffer.Escribir(datos);

            // Act
            byte[] destino = new byte[200_000];
            LecturaSalida lectura = buffer.Leer(0, destino);

            // Assert
            _ = lectura.HasDatosDescartados.Should().BeTrue();
            _ = lectura.Fin.Should().Be(100_000);
            _ = lectura.Copiados.Should().BeInRange(32 * 1024, (48 * 1024) - 1);
            _ = lectura.Desde.Should().Be(100_000 - lectura.Copiados);
            _ = (lectura.Desde % BufferSalida.TamanoSegmento).Should().Be(0);
            _ = destino.AsSpan(0, lectura.Copiados).SequenceEqual(datos.AsSpan((int)lectura.Desde)).Should().BeTrue();
        }

        [Fact]
        public void Liberar_DescartaTodoEIgnoraEscriturasPosteriores()
        {
            // Arrange
            BufferSalida buffer = new(1 << 20, TimeProvider.System);
            buffer.Escribir(Bytes(1_000));
            buffer.Liberar();
            buffer.Escribir(Bytes(10));

            // Act
            LecturaSalida lectura = buffer.Leer(0, new byte[2_000]);

            // Assert
            _ = lectura.Should().Be(new LecturaSalida(1_000, 0, 1_000, true));
        }

        [Fact]
        public void UltimaEscritura_UsaElReloj()
        {
            // Arrange
            RelojFalso reloj = new();
            BufferSalida buffer = new(1024, reloj);
            long inicial = buffer.UltimaEscritura;

            // Act
            reloj.Avanzar(TimeSpan.FromSeconds(5));
            buffer.Escribir([1]);

            // Assert
            _ = reloj.GetElapsedTime(inicial, buffer.UltimaEscritura).Should().Be(TimeSpan.FromSeconds(5));
        }
    }
}
