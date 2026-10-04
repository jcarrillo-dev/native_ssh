using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Sesion
{
    public class RegistroPeticionesTests
    {
        private readonly RelojFalso _reloj = new();

        private Peticion Nueva()
        {
            _ = DestinoSsh.TryParse("ana@servidor", "x", out DestinoSsh destino);
            return new Peticion(destino, "ls", TimeSpan.FromSeconds(10), 1024, _reloj);
        }

        [Fact]
        public void RegistrarYBuscar()
        {
            // Arrange
            RegistroPeticiones registro = new(new OpcionesNativeSsh(), _reloj);
            Peticion peticion = Nueva();

            // Act
            registro.Registrar(peticion);

            // Assert
            _ = registro.Buscar(peticion.IdTexto).Should().BeSameAs(peticion);
            _ = registro.Buscar("no-existe").Should().BeNull();
        }

        [Fact]
        public void Podar_PorRetencion_LiberaLaSalida()
        {
            // Arrange
            RegistroPeticiones registro = new(new OpcionesNativeSsh { RetencionPeticionesFinalizadas = TimeSpan.FromMinutes(30) }, _reloj);
            Peticion peticion = Nueva();
            registro.Registrar(peticion);
            peticion.Salida.Escribir([1, 2, 3]);
            _ = peticion.TryFinalizar(EstadoPeticionEnum.Completado, 0, null);

            // Act
            _reloj.Avanzar(TimeSpan.FromMinutes(29));
            registro.Podar();
            _ = registro.Buscar(peticion.IdTexto).Should().NotBeNull();

            // Assert
            _reloj.Avanzar(TimeSpan.FromMinutes(2));
            registro.Podar();
            _ = registro.Buscar(peticion.IdTexto).Should().BeNull();
            _ = peticion.Salida.Leer(0, new byte[10]).HasDatosDescartados.Should().BeTrue();
        }

        [Fact]
        public void Podar_PorCantidad_QuitaLasFinalizadasMasAntiguas()
        {
            // Arrange
            RegistroPeticiones registro = new(new OpcionesNativeSsh { MaxPeticionesRetenidas = 2 }, _reloj);
            Peticion primera = Nueva();
            Peticion segunda = Nueva();
            registro.Registrar(primera);
            registro.Registrar(segunda);
            _ = primera.TryFinalizar(EstadoPeticionEnum.Completado, 0, null);
            _reloj.Avanzar(TimeSpan.FromSeconds(1));
            _ = segunda.TryFinalizar(EstadoPeticionEnum.Completado, 0, null);

            // Act
            registro.Registrar(Nueva());

            // Assert
            _ = registro.Buscar(primera.IdTexto).Should().BeNull();
            _ = registro.Buscar(segunda.IdTexto).Should().NotBeNull();
            _ = registro.Cantidad.Should().Be(2);
        }

        [Fact]
        public void Podar_NuncaQuitaPeticionesEnCurso()
        {
            // Arrange
            RegistroPeticiones registro = new(new OpcionesNativeSsh { MaxPeticionesRetenidas = 2 }, _reloj);

            // Act
            for (int i = 0; i < 3; i++)
                registro.Registrar(Nueva());

            // Assert
            _ = registro.Cantidad.Should().Be(3);
        }

        [Fact]
        public void ListarRecientes_MasRecientePrimero()
        {
            // Arrange
            RegistroPeticiones registro = new(new OpcionesNativeSsh(), _reloj);
            Peticion antigua = Nueva();
            _reloj.Avanzar(TimeSpan.FromSeconds(1));
            Peticion reciente = Nueva();
            registro.Registrar(antigua);
            registro.Registrar(reciente);

            // Act & Assert
            _ = registro.ListarRecientes().Should().Equal([reciente, antigua]);
        }
    }
}
