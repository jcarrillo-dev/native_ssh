using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // RC9: ssh_find con tamanoMin/tamanoMax (inclusivos; entero con k, M o G opcional, en potencias de 1024) solo da los
    // archivos de ese tamaño, cada uno con su tamaño delante, de mayor a menor; con el tope, los más grandes.
    public sealed partial class HerramientasArchivoIntegracionTests
    {
        [Fact]
        public async Task Find_ConTamano_MuestraElTamanoYOrdenaDeMayorAMenor()
        {
            // Arrange
            string carpeta = Ruta("tamanos");
            _ = Directory.CreateDirectory(Path.Combine(carpeta, "sub"));
            await File.WriteAllBytesAsync(Path.Combine(carpeta, "mil.log"), new byte[1000]);
            await File.WriteAllBytesAsync(Path.Combine(carpeta, "un_k.log"), new byte[1024]);
            await File.WriteAllBytesAsync(Path.Combine(carpeta, "sub", "grande.log"), new byte[3 * 1024 * 1024]);
            await File.WriteAllBytesAsync(Path.Combine(carpeta, "medio.log"), new byte[200 * 1024]);
            await File.WriteAllBytesAsync(Path.Combine(carpeta, "vacio.log"), []);
            HerramientasSsh herramientas = Herramientas();

            // Act
            string minimo = await herramientas.BuscarPorNombreAsync(Destino, "*.log", carpeta, tamanoMin: "1k");
            string maximo = await herramientas.BuscarPorNombreAsync(Destino, "*.log", carpeta, tamanoMax: "1000");
            string rango = await herramientas.BuscarPorNombreAsync(Destino, "*", carpeta, tamanoMin: "1k", tamanoMax: "200k");
            string tope = await herramientas.BuscarPorNombreAsync(Destino, "*.log", carpeta, maxResultados: 2, tamanoMin: "0");

            // Assert
            _ = minimo.Should().Be("3 MiB\tsub/grande.log\n200 KiB\tmedio.log\n1 KiB\tun_k.log");
            _ = maximo.Should().Be("1000 B\tmil.log\n0 B\tvacio.log");
            _ = rango.Should().Be("200 KiB\tmedio.log\n1 KiB\tun_k.log");
            _ = tope.Should().StartWith("3 MiB\tsub/grande.log\n200 KiB\tmedio.log\n\n[Límite alcanzado: se muestran 2 resultados");
        }

        [Theory]
        [InlineData("50MB")]
        [InlineData("1.5G")]
        [InlineData("-1")]
        [InlineData("50m")]
        [InlineData(" 5k")]
        [InlineData("99999999999G")]
        public async Task Find_TamanoNoValido_SeRechazaSinEjecutarNada(string tamano)
        {
            // Act
            Func<Task> accion = () => Herramientas().BuscarPorNombreAsync(Destino, "*", _directorio, tamanoMin: tamano);

            // Assert
            _ = (await accion.Should().ThrowAsync<NativeSshException>()).Which.Message.Should().Contain("tamanoMin").And.Contain("k, M o G");
        }

        [Fact]
        public async Task Find_TamanoConOtroTipoOMinimoMayorQueMaximo_SeRechaza()
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();

            // Act
            Func<Task> conDirectorios = () => herramientas.BuscarPorNombreAsync(Destino, "*", _directorio, tipo: "dir", tamanoMin: "1k");
            Func<Task> invertido = () => herramientas.BuscarPorNombreAsync(Destino, "*", _directorio, tamanoMin: "2M", tamanoMax: "1M");

            // Assert
            _ = (await conDirectorios.Should().ThrowAsync<NativeSshException>()).Which.Message.Should().Contain("tipo='file'");
            _ = (await invertido.Should().ThrowAsync<NativeSshException>()).Which.Message.Should().Contain("mayor que tamanoMax");
        }
    }
}
