using System.Linq;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Comun
{
    // Tope de avisos de peticiones HTTP rechazadas (CB2d): como mucho N por ventana; el recuento de los callados sale con el
    // siguiente aviso registrado.
    public class LimitadorAvisosTests
    {
        [Fact]
        public void DentroDeUnaVentana_SoloRegistraElMaximo()
        {
            // Arrange
            RelojFalso reloj = new();
            LimitadorAvisos limitador = new(3, TimeSpan.FromMinutes(1), reloj);

            // Act
            bool[] registrados = [.. Enumerable.Range(0, 10).Select(_ => limitador.TryRegistrar(out _))];

            // Assert
            _ = registrados.Should().Equal(true, true, true, false, false, false, false, false, false, false);
        }

        [Fact]
        public void EnLaVentanaSiguiente_VuelveARegistrarYDiceCuantosSeOmitieron()
        {
            // Arrange
            RelojFalso reloj = new();
            LimitadorAvisos limitador = new(2, TimeSpan.FromMinutes(1), reloj);
            for (int i = 0; i < 7; i++)
                _ = limitador.TryRegistrar(out _);

            // Act
            reloj.Avanzar(TimeSpan.FromSeconds(59));
            bool isAntes = limitador.TryRegistrar(out _);
            reloj.Avanzar(TimeSpan.FromSeconds(1));
            bool isDespues = limitador.TryRegistrar(out int omitidos);
            bool isSegundo = limitador.TryRegistrar(out int omitidosSegundo);

            // Assert
            _ = isAntes.Should().BeFalse();
            _ = isDespues.Should().BeTrue();
            _ = omitidos.Should().Be(6, "5 de la primera ventana y 1 justo antes de que acabara");
            _ = isSegundo.Should().BeTrue();
            _ = omitidosSegundo.Should().Be(0);
        }
    }
}
