using FluentAssertions;
using System.Text;
using NativeSsh.Comun;
using NativeSsh.Sesion;

namespace NativeSsh.Test.Integracion
{
    // El login tarda más que el reenvío de la inicialización: bash -l (readline) y el bash nuevo ejecutan las dos líneas.
    [Collection(ColeccionSshdLoginLento.Nombre)]
    [Trait("Categoria", "Integracion")]
    public class SesionShellLoginLentoTests(SshdLocalLoginLento fixture)
    {
        private static readonly TimeSpan Limite = TimeSpan.FromSeconds(40);

        private readonly SshdLocal _sshd = fixture.Sshd;

        private async Task<Peticion> EjecutarAsync(SesionShell sesion, string comando)
        {
            Peticion peticion = new(_sshd.Destino, comando, TimeSpan.FromSeconds(30), 1 << 20, TimeProvider.System);
            sesion.Encolar(peticion);
            await peticion.Finalizacion.WaitAsync(Limite);
            return peticion;
        }

        private static string Texto(Peticion peticion)
        {
            byte[] destino = new byte[(int)peticion.Salida.Longitud];
            int copiados = peticion.Salida.Leer(0, destino).Copiados;
            return Encoding.UTF8.GetString(destino, 0, copiados);
        }

        [Fact]
        public async Task PrimerComando_DevuelveSuSalidaYElExportPersiste()
        {
            // Arrange
            await using SesionShell sesion = _sshd.Sesion();

            // Act
            Peticion primera = await EjecutarAsync(sesion, "export NS_LENTO=valor; echo uno");
            Peticion segunda = await EjecutarAsync(sesion, "echo \"dos:$NS_LENTO\"");
            Peticion fallida = await EjecutarAsync(sesion, "echo tres; false");

            // Assert
            _ = primera.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = primera.CodigoSalida.Should().Be(0);
            _ = Texto(primera).Should().Be("uno\n");
            _ = segunda.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = Texto(segunda).Should().Be("dos:valor\n");
            _ = fallida.CodigoSalida.Should().Be(1);
            _ = Texto(fallida).Should().Be("tres\n");
        }
    }
}
