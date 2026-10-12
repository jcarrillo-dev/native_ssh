using FluentAssertions;
using System.Linq;
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

        // Con el directorio de trabajo fijado (largo, como el que deja ssh_set_cwd), la inicialización con el login lento y su
        // reenvío sigue funcionando: el bash nuevo lee de una vez las dos copias de la línea de inicialización y el centinela
        // con una sola lectura de su búfer (unos 1024 bytes), así que esa línea no lleva el directorio (va en la captura).
        [Theory]
        [InlineData(40)]
        [InlineData(400)]
        public async Task ConDirectorioDeTrabajoLargo_ElPrimerComandoSeEjecutaEnEl(int longitud)
        {
            // Arrange
            // Tramos de como mucho 200 caracteres (NAME_MAX es 255).
            string[] tramos = [.. Enumerable.Range(0, (longitud + 199) / 200).Select(i => new string((char)('d' + i), Math.Min(200, longitud - (i * 200))))];
            string directorio = Directory.CreateDirectory(Path.Combine([Path.GetTempPath(), $"ns_lento_{Guid.NewGuid():N}", .. tramos])).FullName;
            try
            {
                await using SesionShell sesion = _sshd.Sesion();
                sesion.DirectorioTrabajo = directorio;

                // Act
                Peticion primera = await EjecutarAsync(sesion, "pwd");
                Peticion segunda = await EjecutarAsync(sesion, "echo dos");

                // Assert
                _ = primera.Estado.Should().Be(EstadoPeticionEnum.Completado, primera.Aviso);
                _ = Texto(primera).Should().Be($"{directorio}\n");
                _ = Texto(segunda).Should().Be("dos\n");
            }
            finally
            {
                Directory.Delete(Path.Combine(Path.GetTempPath(), directorio[Path.GetTempPath().Length..].Split('/')[0]), recursive: true);
            }
        }
    }
}
