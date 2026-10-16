using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    public sealed partial class GuardarSalidaTests
    {
        // Buscar en el directorio del shell: la salida con un byte NUL no aparece (el motor la toma por binaria) y la respuesta
        // avisa de que eso puede pasar; buscando por la ruta de un .out, sin ese aviso.
        [Fact]
        public async Task BuscarEnElDirectorio_UnaSalidaBinariaSeOmiteYLaRespuestaLoAvisa()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando binaria = await GuardarAsync(h, "printf 'clave\\0binaria\\n'");
            ResultadoComando texto = await GuardarAsync(h, "echo clave texto");
            string directorio = Path.GetDirectoryName(texto.Archivo!)!;

            // Act
            string enDirectorio = await h.BuscarAsync(Destino, "clave", directorio);
            string enArchivo = await h.BuscarAsync(Destino, "clave", texto.Archivo!);

            // Assert
            _ = enDirectorio.Should().Contain(Path.GetFileName(texto.Archivo!)).And.NotContain(Path.GetFileName(binaria.Archivo!))
                .And.Contain(HerramientasSsh.AvisoBinariosOmitidos);
            _ = enDirectorio.Split('\n')[^1].Should().StartWith("[motor: ");
            _ = enArchivo.Should().NotContain(HerramientasSsh.AvisoBinariosOmitidos);
        }
    }
}
