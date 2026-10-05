using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Sesion
{
    public class LimitadorConexionesTests
    {
        private readonly RelojFalso _reloj = new();
        private readonly LimitadorConexiones _limitador;

        public LimitadorConexionesTests() => _limitador = new LimitadorConexiones(new OpcionesNativeSsh { MaxConexionesPorVentana = 4, VentanaConexiones = TimeSpan.FromSeconds(30) }, _reloj);

        [Fact]
        public void Ventana_RechazaLaQuintaYSeLiberaAlCaducar()
        {
            // Arrange
            for (int i = 0; i < 4; i++)
            {
                _ = _limitador.TryReservar("a", out _).Should().BeTrue();
                _reloj.Avanzar(TimeSpan.FromSeconds(1));
            }

            // Act
            _ = _limitador.TryReservar("a", out TimeSpan espera).Should().BeFalse();
            _ = espera.Should().Be(TimeSpan.FromSeconds(26));

            // Assert
            _reloj.Avanzar(TimeSpan.FromSeconds(26));
            _ = _limitador.TryReservar("a", out _).Should().BeTrue();
        }

        [Fact]
        public void Claves_SonIndependientes()
        {
            // Arrange
            for (int i = 0; i < 4; i++)
                _ = _limitador.TryReservar("a", out _);

            // Act & Assert
            _ = _limitador.TryReservar("b", out _).Should().BeTrue();
        }

        [Fact]
        public void Fallos_EsperaExponencialConTope()
        {
            // Arrange
            _limitador.RegistrarFallo("a");
            _ = _limitador.TryReservar("a", out TimeSpan primera).Should().BeFalse();
            _ = primera.Should().Be(TimeSpan.FromSeconds(2));

            // Act
            _limitador.RegistrarFallo("a");
            _ = _limitador.TryReservar("a", out TimeSpan segunda).Should().BeFalse();
            _ = segunda.Should().Be(TimeSpan.FromSeconds(4));

            // Assert
            for (int i = 0; i < 10; i++)
                _limitador.RegistrarFallo("a");
            _ = _limitador.TryReservar("a", out TimeSpan tope).Should().BeFalse();
            _ = tope.Should().Be(TimeSpan.FromSeconds(60));
        }

        [Fact]
        public void Exito_BorraLaEsperaPorFallos()
        {
            // Arrange
            _limitador.RegistrarFallo("a");
            _limitador.RegistrarExito("a");

            // Act & Assert
            _ = _limitador.TryReservar("a", out _).Should().BeTrue();
        }

        [Fact]
        public void Exito_BorraLaEsperaPorFallosPeroLaVentanaSigueContandoTodosLosIntentos()
        {
            // Arrange: ufw cuenta cada TCP nuevo, haya ido bien o mal. Un intento fallido y tres con éxito llenan la ventana.
            _ = _limitador.TryReservar("a", out _).Should().BeTrue();
            _limitador.RegistrarFallo("a");
            _reloj.Avanzar(TimeSpan.FromSeconds(2));
            for (int i = 0; i < 3; i++)
            {
                _ = _limitador.TryReservar("a", out _).Should().BeTrue();
                _limitador.RegistrarExito("a");
                _reloj.Avanzar(TimeSpan.FromSeconds(1));
            }

            // Act
            bool isReservada = _limitador.TryReservar("a", out TimeSpan espera);

            // Assert: RegistrarExito no vacía la ventana; el primer intento caduca a los 30 s.
            _ = isReservada.Should().BeFalse();
            _ = espera.Should().Be(TimeSpan.FromSeconds(25));
            _reloj.Avanzar(TimeSpan.FromSeconds(25));
            _ = _limitador.TryReservar("a", out _).Should().BeTrue();
        }

        [Fact]
        public void DestinosEquivalentes_MismoServidorYDistintoUsuarioOMayusculas_CompartenClaveServidor()
        {
            // Arrange
            _ = DestinoSsh.TryParse("user1@Servidor.Com:2222", "root", out DestinoSsh d1);
            _ = DestinoSsh.TryParse("user2@servidor.com:2222", "root", out DestinoSsh d2);

            // Act & Assert
            _ = d1.ClaveServidor.Should().Be("servidor.com:2222");
            _ = d2.ClaveServidor.Should().Be("servidor.com:2222");
            _ = d1.ClaveServidor.Should().Be(d2.ClaveServidor);

            for (int i = 0; i < 4; i++)
                _ = _limitador.TryReservar(d1.ClaveServidor, out _).Should().BeTrue();

            _ = _limitador.TryReservar(d2.ClaveServidor, out _).Should().BeFalse();
        }
    }
}
