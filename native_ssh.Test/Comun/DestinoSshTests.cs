using FluentAssertions;
using NativeSsh.Comun;

namespace NativeSsh.Test.Comun
{
    public class DestinoSshTests
    {
        [Theory]
        [InlineData("root@10.0.0.1", "root", "10.0.0.1", 22)]
        [InlineData("admin@host.local:2222", "admin", "host.local", 2222)]
        [InlineData("servidor", "local", "servidor", 22)]
        [InlineData("servidor:2200", "local", "servidor", 2200)]
        [InlineData("ana@[::1]:2222", "ana", "::1", 2222)]
        [InlineData("ana@fe80::1", "ana", "fe80::1", 22)]
        [InlineData("  root@h  ", "root", "h", 22)]
        public void TryParse_EntradasValidas(string entrada, string usuario, string servidor, int puerto)
        {
            // Arrange
            bool isValido = DestinoSsh.TryParse(entrada, "local", out DestinoSsh destino);

            // Act & Assert
            _ = isValido.Should().BeTrue();
            _ = destino.Usuario.Should().Be(usuario);
            _ = destino.Servidor.Should().Be(servidor);
            _ = destino.Puerto.Should().Be(puerto);
            _ = destino.Clave.Should().Be(servidor.Contains(':') ? $"{usuario}@[{servidor}]:{puerto}" : $"{usuario}@{servidor}:{puerto}");
        }

        // La clave que se muestra (ssh_status, mensajes) se puede volver a usar como destino: una IPv6 va entre corchetes. Sin
        // ellos, 'ana@::1:2222' se leía como la IPv6 '::1:2222' en el puerto 22.
        [Theory]
        [InlineData("ana@[::1]:2222", "ana@[::1]:2222")]
        [InlineData("ana@fe80::1", "ana@[fe80::1]:22")]
        [InlineData("ana@10.0.0.1:2200", "ana@10.0.0.1:2200")]
        public void Clave_SeVuelveALeerComoElMismoDestino(string entrada, string clave)
        {
            // Arrange
            _ = DestinoSsh.TryParse(entrada, "local", out DestinoSsh destino).Should().BeTrue();

            // Act
            bool isValida = DestinoSsh.TryParse(destino.Clave, "local", out DestinoSsh releido);

            // Assert
            _ = destino.Clave.Should().Be(clave);
            _ = isValida.Should().BeTrue();
            _ = (releido.Usuario, releido.Servidor, releido.Puerto).Should().Be((destino.Usuario, destino.Servidor, destino.Puerto));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("root@")]
        [InlineData("@host")]
        [InlineData("host:")]
        [InlineData("host:0")]
        [InlineData("host:70000")]
        [InlineData("host:abc")]
        [InlineData("ro ot@host")]
        [InlineData("[::1")]
        [InlineData("[::1]x")]
        public void TryParse_EntradasInvalidas(string entrada) => _ = DestinoSsh.TryParse(entrada, "local", out _).Should().BeFalse();

        [Fact]
        public void TryParse_SinUsuarioPorDefecto_Falla() => _ = DestinoSsh.TryParse("host", "", out _).Should().BeFalse();

        [Fact]
        public void IsFinal_SoloEstadosTerminales()
        {
            // Act & Assert
            _ = EstadoPeticionEnum.Encolado.IsFinal().Should().BeFalse();
            _ = EstadoPeticionEnum.Ejecutando.IsFinal().Should().BeFalse();
            _ = EstadoPeticionEnum.Completado.IsFinal().Should().BeTrue();
            _ = EstadoPeticionEnum.Error.IsFinal().Should().BeTrue();
        }
    }
}
