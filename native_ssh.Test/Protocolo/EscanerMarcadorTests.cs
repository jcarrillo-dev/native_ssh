using FluentAssertions;
using System.Linq;
using System.Text;
using NativeSsh.Buffers;
using NativeSsh.Protocolo;

namespace NativeSsh.Test.Protocolo
{
    public class EscanerMarcadorTests
    {
        private static readonly Guid Id = Guid.Parse("0123456789abcdef0123456789abcdef");
        private static readonly Guid Otro = Guid.Parse("fedcba9876543210fedcba9876543210");

        private static string Marcador(Guid id, int codigo) => $"__NSX_{id:N}_{codigo}__\n";

        private static BufferSalida NuevaSalida() => new(1 << 20, TimeProvider.System);

        private static string Texto(BufferSalida salida)
        {
            byte[] destino = new byte[(int)salida.Longitud];
            LecturaSalida lectura = salida.Leer(0, destino);
            return Encoding.UTF8.GetString(destino, 0, lectura.Copiados);
        }

        private static EscanerMarcador Nuevo(Guid id, Guid? ignorado = null)
        {
            EscanerMarcador escaner = new(1024);
            escaner.Reiniciar(id, ignorado);
            return escaner;
        }

        private static (bool IsCompleto, int Codigo) Alimentar(EscanerMarcador escaner, BufferSalida salida, params string[] bloques)
        {
            foreach (string bloque in bloques)
            {
                if (escaner.Procesar(Encoding.UTF8.GetBytes(bloque), salida, out int codigo))
                    return (true, codigo);
            }

            return (false, 0);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(42)]
        [InlineData(255)]
        public void BloqueUnico_DetectaYExtraeCodigo(int codigo)
        {
            // Arrange
            EscanerMarcador escaner = Nuevo(Id);
            BufferSalida salida = NuevaSalida();

            // Act
            (bool IsCompleto, int Codigo) resultado = Alimentar(escaner, salida, "hola\n" + Marcador(Id, codigo));

            // Assert
            _ = resultado.Should().Be((true, codigo));
            _ = Texto(salida).Should().Be("hola\n");
            _ = escaner.IsActivo.Should().BeFalse();
        }

        [Fact]
        public void MarcadorPartido_EnCualquierPosicion()
        {
            // Arrange
            string flujo = "salida previa\n" + Marcador(Id, 7);
            for (int corte = 1; corte < flujo.Length; corte++)
            {
                EscanerMarcador escaner = Nuevo(Id);
                BufferSalida salida = NuevaSalida();

                // Act
                (bool isCompleto, int codigo) = Alimentar(escaner, salida, flujo[..corte], flujo[corte..]);

                // Assert
                _ = isCompleto.Should().BeTrue($"corte {corte}");
                _ = codigo.Should().Be(7);
                _ = Texto(salida).Should().Be("salida previa\n");
            }
        }

        [Fact]
        public void ByteAByte()
        {
            // Arrange
            EscanerMarcador escaner = Nuevo(Id);
            BufferSalida salida = NuevaSalida();
            string flujo = "abc" + Marcador(Id, 3);

            // Act
            (bool IsCompleto, int Codigo) resultado = Alimentar(escaner, salida, [.. flujo.Select(c => c.ToString())]);

            // Assert
            _ = resultado.Should().Be((true, 3));
            _ = Texto(salida).Should().Be("abc");
        }

        // Marcador con el recuento de líneas tecleadas que el comando no leyó y la cola descartó (EscribirComando):
        // "__NSX_<id>_<código>_<descartadas>__"; sin recuento, Descartadas = 0.
        [Theory]
        [InlineData(0, 1)]
        [InlineData(7, 2)]
        [InlineData(130, 999999)]
        public void MarcadorConDescartadas_DaCodigoYRecuento(int codigo, int descartadas)
        {
            // Arrange
            EscanerMarcador escaner = Nuevo(Id);
            BufferSalida salida = NuevaSalida();

            // Act
            (bool IsCompleto, int Codigo) resultado = Alimentar(escaner, salida, $"hola\n__NSX_{Id:N}_{codigo}_{descartadas}__\n");

            // Assert
            _ = resultado.Should().Be((true, codigo));
            _ = escaner.LineasDescartadas.Should().Be(descartadas);
            _ = Texto(salida).Should().Be("hola\n");
        }

        [Fact]
        public void MarcadorSinDescartadas_TrasUnoConEllas_DaCero()
        {
            // Arrange
            EscanerMarcador escaner = Nuevo(Id);
            BufferSalida salida = NuevaSalida();
            _ = Alimentar(escaner, salida, $"__NSX_{Id:N}_0_3__\n").IsCompleto.Should().BeTrue();

            // Act
            escaner.Reiniciar(Otro);
            (bool isCompleto, _) = Alimentar(escaner, salida, Marcador(Otro, 0));

            // Assert
            _ = isCompleto.Should().BeTrue();
            _ = escaner.LineasDescartadas.Should().Be(0);
        }

        [Fact]
        public void MarcadorConDescartadas_PartidoEnCualquierPosicion()
        {
            // Arrange
            string flujo = $"salida previa\n__NSX_{Id:N}_7_12__\n";
            for (int corte = 1; corte < flujo.Length; corte++)
            {
                EscanerMarcador escaner = Nuevo(Id);
                BufferSalida salida = NuevaSalida();

                // Act
                (bool isCompleto, int codigo) = Alimentar(escaner, salida, flujo[..corte], flujo[corte..]);

                // Assert
                _ = isCompleto.Should().BeTrue($"corte {corte}");
                _ = (codigo, escaner.LineasDescartadas).Should().Be((7, 12), $"corte {corte}");
                _ = Texto(salida).Should().Be("salida previa\n");
            }
        }

        // El recuento lleva como mucho MarcadorFin.MaxDigitosDescartadas cifras y termina en "__": lo demás es texto.
        [Fact]
        public void MarcadorConDescartadasMalFormado_SeEmiteComoTexto()
        {
            // Arrange
            EscanerMarcador escaner = Nuevo(Id);
            BufferSalida salida = NuevaSalida();
            string imitaciones = $"__NSX_{Id:N}_0_1234567__\n__NSX_{Id:N}_0_x__\n__NSX_{Id:N}_0_12_x\n__NSX_{Id:N}_0_12x__\n";

            // Act & Assert
            _ = Alimentar(escaner, salida, imitaciones).IsCompleto.Should().BeFalse();
            _ = Alimentar(escaner, salida, Marcador(Id, 0)).IsCompleto.Should().BeTrue();
            _ = Texto(salida).Should().Be(imitaciones);
        }

        [Fact]
        public void SaltoDeLineaDelMarcadorConDescartadas_EnElBloqueSiguiente_SeDescarta()
        {
            // Arrange
            EscanerMarcador escaner = Nuevo(Id);
            BufferSalida primera = NuevaSalida();
            _ = Alimentar(escaner, primera, $"__NSX_{Id:N}_0_2__").IsCompleto.Should().BeTrue();

            // Act
            escaner.Reiniciar(Otro);
            BufferSalida segunda = NuevaSalida();
            _ = Alimentar(escaner, segunda, "\nresto");

            // Assert
            _ = Texto(segunda).Should().Be("resto");
        }

        // El marcador ignorado (reenvío de una recuperación) se traga aunque lleve recuento, y no pisa el del bueno.
        [Fact]
        public void IdIgnoradoConDescartadas_SeTragaYNoPisaElRecuento()
        {
            // Arrange
            EscanerMarcador escaner = Nuevo(Otro, Id);
            BufferSalida salida = NuevaSalida();

            // Act
            (bool IsCompleto, int Codigo) resultado = Alimentar(escaner, salida, $"a__NSX_{Id:N}_0_5__\nb" + Marcador(Otro, 0));

            // Assert
            _ = resultado.Should().Be((true, 0));
            _ = escaner.LineasDescartadas.Should().Be(0);
            _ = Texto(salida).Should().Be("ab");
        }

        [Fact]
        public void MarcadorDeOtraPeticion_SeTrataComoTexto()
        {
            // Arrange
            EscanerMarcador escaner = Nuevo(Id);
            BufferSalida salida = NuevaSalida();
            string ajeno = "a" + Marcador(Otro, 0) + "b";

            // Act & Assert
            _ = Alimentar(escaner, salida, ajeno).IsCompleto.Should().BeFalse();
            _ = Alimentar(escaner, salida, Marcador(Id, 0)).IsCompleto.Should().BeTrue();
            _ = Texto(salida).Should().Be(ajeno);
        }

        [Fact]
        public void EcoDelComando_NoDisparaElMarcador()
        {
            // Arrange
            EscanerMarcador escaner = Nuevo(Id);
            BufferSalida salida = NuevaSalida();
            string eco = $"printf '%s_%s_%d__\\n' '__NSX' '{Id:N}' \"$__ns_rc\"; read -r __ns_d; unset __ns_d\n";

            // Act & Assert
            _ = Alimentar(escaner, salida, eco).IsCompleto.Should().BeFalse();
            _ = Texto(salida).Should().Be(eco);
        }

        [Fact]
        public void IdIgnorado_SeTragaSinEmitir()
        {
            // Arrange
            EscanerMarcador escaner = Nuevo(Otro, Id);
            BufferSalida salida = NuevaSalida();

            // Act
            (bool IsCompleto, int Codigo) resultado = Alimentar(escaner, salida, "a" + Marcador(Id, 0) + "b" + Marcador(Otro, 130));

            // Assert
            _ = resultado.Should().Be((true, 130));
            _ = Texto(salida).Should().Be("ab");
        }

        [Fact]
        public void Imitaciones_SeEmitenComoTexto()
        {
            // Arrange
            EscanerMarcador escaner = Nuevo(Id);
            BufferSalida salida = NuevaSalida();
            string imitaciones = $"__NSX_zz\n__NSX_{Id:N}_1234__\n__NSX_{Id:N}_x__\n";

            // Act & Assert
            _ = Alimentar(escaner, salida, imitaciones).IsCompleto.Should().BeFalse();
            _ = Alimentar(escaner, salida, Marcador(Id, 0)).IsCompleto.Should().BeTrue();
            _ = Texto(salida).Should().Be(imitaciones);
        }

        [Fact]
        public void SaltoDeLineaDelMarcador_EnElBloqueSiguiente_SeDescarta()
        {
            // Arrange
            EscanerMarcador escaner = Nuevo(Id);
            BufferSalida primera = NuevaSalida();
            _ = Alimentar(escaner, primera, Marcador(Id, 0).TrimEnd('\n')).IsCompleto.Should().BeTrue();

            // Act
            escaner.Reiniciar(Otro);
            BufferSalida segunda = NuevaSalida();
            _ = Alimentar(escaner, segunda, "\nresto");

            // Assert
            _ = Texto(segunda).Should().Be("resto");
        }

        [Fact]
        public void PrefijoParcial_SeRetieneHastaResolverse()
        {
            // Arrange
            EscanerMarcador escaner = Nuevo(Id);
            BufferSalida salida = NuevaSalida();

            // Act
            _ = Alimentar(escaner, salida, "texto__NS");
            _ = Texto(salida).Should().Be("texto");

            // Assert
            _ = Alimentar(escaner, salida, "Xfin");
            _ = Texto(salida).Should().Be("texto__NSXfin");
        }

        [Fact]
        public void Inactivo_NoEmiteNada()
        {
            // Arrange
            EscanerMarcador escaner = new(1024);
            BufferSalida salida = NuevaSalida();

            // Act & Assert
            _ = Alimentar(escaner, salida, "hola" + Marcador(Id, 0)).IsCompleto.Should().BeFalse();
            _ = salida.Longitud.Should().Be(0);
        }

        [Fact]
        public void BloqueMayorQueElMaximo_Lanza()
        {
            // Arrange
            EscanerMarcador escaner = new(4);
            escaner.Reiniciar(Id);

            // Act & Assert
            _ = FluentActions.Invoking(() => escaner.Procesar(new byte[5], null, out _)).Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void Reiniciar_ConservaBytesRetenidos()
        {
            // Arrange
            EscanerMarcador escaner = Nuevo(Id);
            BufferSalida primera = NuevaSalida();
            _ = Alimentar(escaner, primera, "texto__NS");
            _ = Texto(primera).Should().Be("texto");

            // Act
            escaner.Reiniciar(Otro);
            BufferSalida segunda = NuevaSalida();
            _ = Alimentar(escaner, segunda, "Xresto");

            // Assert
            _ = Texto(segunda).Should().Be("__NSXresto");
        }
    }
}
