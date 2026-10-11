using System.Net;
using FluentAssertions;
using NativeSsh.Comun;

namespace NativeSsh.Test.Comun
{
    // Formato y comparación de NATIVE_SSH_SOLO_LECTURA (CI6).
    public sealed class DestinosSoloLecturaTests
    {
        private static DestinoSsh Destino(string texto)
        {
            _ = DestinoSsh.TryParse(texto, "ana", out DestinoSsh destino).Should().BeTrue();
            return destino;
        }

        // Resolución falsa: prod.ejemplo → 10.0.0.5; cualquier otro nombre no se resuelve.
        private static Task<IPAddress[]> ResolverFalso(string nombre, CancellationToken _) =>
            nombre == "prod.ejemplo" ? Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") }) : Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException(11001));

        private static async Task<bool> EsSoloLecturaAsync(string configuracion, string destino)
        {
            _ = DestinosSoloLectura.TryParsear(configuracion, out DestinosSoloLectura destinos, out string? error, ResolverFalso).Should().BeTrue(error);
            return await destinos.IsSoloLecturaAsync(Destino(destino), CancellationToken.None);
        }

        [Theory]
        [InlineData("prod.ejemplo", "root@prod.ejemplo", true)]
        [InlineData("prod.ejemplo", "deploy@PROD.Ejemplo:2222", true)]
        [InlineData("prod.ejemplo.", "root@prod.ejemplo", true)]
        [InlineData("prod.ejemplo", "root@prod.ejemplo.", true)]
        [InlineData("prod.ejemplo", "root@otro.ejemplo", false)]
        [InlineData("deploy@prod.ejemplo", "deploy@prod.ejemplo", true)]
        [InlineData("deploy@prod.ejemplo", "root@prod.ejemplo", false)]
        [InlineData("prod.ejemplo:2222", "root@prod.ejemplo:2222", true)]
        [InlineData("prod.ejemplo:2222", "root@prod.ejemplo", false)]
        [InlineData("otro, root@prod.ejemplo:22", "root@prod.ejemplo", true)]
        [InlineData("*", "cualquiera@servidor:2200", true)]
        [InlineData("[::1]:22", "ana@[0:0:0:0:0:0:0:1]", true)]
        [InlineData("::1", "ana@[::1]:2200", true)]
        [InlineData("10.0.0.5", "ana@::ffff:10.0.0.5", true)]
        // Por dirección: el nombre cubre su IP y la IP cubre el nombre.
        [InlineData("prod.ejemplo", "root@10.0.0.5", true)]
        [InlineData("10.0.0.5", "root@prod.ejemplo", true)]
        [InlineData("deploy@prod.ejemplo", "root@10.0.0.5", false)]
        [InlineData("prod.ejemplo", "root@10.0.0.6", false)]
        public async Task Comparacion_PorUsuarioPuertoYServidor(string configuracion, string destino, bool isSoloLectura) =>
            _ = (await EsSoloLecturaAsync(configuracion, destino)).Should().Be(isSoloLectura);

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(" , ,")]
        public void SinConfiguracion_NingunDestino(string? configuracion)
        {
            // Act
            bool isValida = DestinosSoloLectura.TryParsear(configuracion, out DestinosSoloLectura destinos, out string? error);

            // Assert
            _ = isValida.Should().BeTrue();
            _ = error.Should().BeNull();
            _ = destinos.IsVacio.Should().BeTrue();
        }

        [Theory]
        [InlineData("@prod")]
        [InlineData("prod:0")]
        [InlineData("prod:70000")]
        [InlineData("prod:abc")]
        [InlineData("[::1")]
        [InlineData("[::1]x")]
        [InlineData("root@")]
        [InlineData("prod, mal entrada")]
        public void EntradaNoValida_ErrorQueLaNombra(string configuracion)
        {
            // Act
            bool isValida = DestinosSoloLectura.TryParsear(configuracion, out _, out string? error);

            // Assert
            _ = isValida.Should().BeFalse();
            _ = error.Should().StartWith("NATIVE_SSH_SOLO_LECTURA: la entrada '").And.Contain("no es válida. Usa '[usuario@]servidor[:puerto]'");
        }

        // Lo resuelto (también lo que no se pudo resolver) se recuerda: cada ssh_exec a otro destino no espera otra vez al DNS.
        [Fact]
        public async Task Resolucion_SeRecuerda()
        {
            // Arrange
            int consultas = 0;
            Task<IPAddress[]> Contador(string nombre, CancellationToken token)
            {
                _ = Interlocked.Increment(ref consultas);
                return ResolverFalso(nombre, token);
            }

            _ = DestinosSoloLectura.TryParsear("prod.ejemplo", out DestinosSoloLectura destinos, out _, Contador).Should().BeTrue();

            // Act
            bool primera = await destinos.IsSoloLecturaAsync(Destino("ana@otro.ejemplo"), CancellationToken.None);
            bool segunda = await destinos.IsSoloLecturaAsync(Destino("ana@otro.ejemplo"), CancellationToken.None);

            // Assert
            _ = primera.Should().BeFalse();
            _ = segunda.Should().BeFalse();
            _ = consultas.Should().Be(2, "una por nombre, solo la primera vez");
        }

        // Con DNS real (/etc/hosts): localhost cubre 127.0.0.1.
        [Fact]
        public async Task Localhost_CubreSuDireccion()
        {
            // Arrange
            _ = DestinosSoloLectura.TryParsear("localhost", out DestinosSoloLectura destinos, out _).Should().BeTrue();

            // Act & Assert
            _ = (await destinos.IsSoloLecturaAsync(Destino("ana@127.0.0.1:22042"), CancellationToken.None)).Should().BeTrue();
        }
    }
}
