using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Sesion
{
    public class PeticionTests
    {
        private static Peticion Nueva(TimeProvider? reloj = null)
        {
            _ = DestinoSsh.TryParse("ana@servidor", "x", out DestinoSsh destino);
            return new Peticion(destino, "ls", TimeSpan.FromSeconds(10), 1024, reloj ?? TimeProvider.System);
        }

        [Fact]
        public void Nueva_EstaEncolada()
        {
            // Arrange
            Peticion peticion = Nueva();

            // Act & Assert
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Encolado);
            _ = peticion.CodigoSalida.Should().Be(-1);
            _ = peticion.IdTexto.Length.Should().Be(32);
            _ = peticion.TiempoSinSalida.Should().BeNull();
        }

        [Fact]
        public void TryMarcarEjecutando_SoloUnaVez()
        {
            // Arrange
            Peticion peticion = Nueva();

            // Act & Assert
            _ = peticion.TryMarcarEjecutando().Should().BeTrue();
            _ = peticion.TryMarcarEjecutando().Should().BeFalse();
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
        }

        [Fact]
        public async Task TryFinalizar_GanaLaPrimera()
        {
            // Arrange
            Peticion peticion = Nueva();
            _ = peticion.TryMarcarEjecutando();

            // Act
            _ = peticion.TryFinalizar(EstadoPeticionEnum.Completado, 3, "aviso").Should().BeTrue();
            _ = peticion.TryFinalizar(EstadoPeticionEnum.Error, -1, "tarde").Should().BeFalse();
            await peticion.Finalizacion.WaitAsync(TimeSpan.FromSeconds(1));

            // Assert
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = peticion.CodigoSalida.Should().Be(3);
            _ = peticion.Aviso.Should().Be("aviso");
            _ = peticion.IsFinalizada.Should().BeTrue();
        }

        [Fact]
        public void TryFinalizar_ConEstadoNoFinal_Lanza() => _ = FluentActions.Invoking(() => Nueva().TryFinalizar(EstadoPeticionEnum.Ejecutando, 0, null)).Should().Throw<ArgumentException>();

        [Fact]
        public void Cancelar_Encolada_FinalizaAlMomento()
        {
            // Arrange
            Peticion peticion = Nueva();

            // Act
            _ = peticion.SolicitarCancelacion().Should().BeTrue();

            // Assert
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = peticion.TryMarcarEjecutando().Should().BeFalse();
            _ = peticion.SolicitarCancelacion().Should().BeFalse();
        }

        [Fact]
        public void Cancelar_EnEjecucion_SoloDisparaElToken()
        {
            // Arrange
            Peticion peticion = Nueva();
            _ = peticion.TryMarcarEjecutando();

            // Act
            _ = peticion.SolicitarCancelacion().Should().BeTrue();

            // Assert
            _ = peticion.TokenInterrupcion.IsCancellationRequested.Should().BeTrue();
            _ = peticion.IsCancelacionSolicitada.Should().BeTrue();
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
        }

        [Fact]
        public void Tiempos_UsanElReloj()
        {
            // Arrange
            RelojFalso reloj = new();
            Peticion peticion = Nueva(reloj);
            reloj.Avanzar(TimeSpan.FromSeconds(2));
            _ = peticion.TryMarcarEjecutando();

            // Act
            reloj.Avanzar(TimeSpan.FromSeconds(3));
            _ = peticion.TiempoSinSalida.Should().Be(TimeSpan.FromSeconds(3));

            peticion.Salida.Escribir([1]);
            reloj.Avanzar(TimeSpan.FromSeconds(1));
            _ = peticion.TiempoSinSalida.Should().Be(TimeSpan.FromSeconds(1));

            // Assert
            _ = peticion.TryFinalizar(EstadoPeticionEnum.Completado, 0, null);
            reloj.Avanzar(TimeSpan.FromSeconds(10));
            _ = peticion.TiempoEjecucion.Should().Be(TimeSpan.FromSeconds(4));
            _ = peticion.TiempoSinSalida.Should().BeNull();
        }
    }
}
