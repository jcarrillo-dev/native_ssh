using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // RC7: ssh_list_dir con incluirArchivos=true marca los directorios con '/' al final (un nombre no puede contener '/', así
    // que no es ambiguo); un enlace simbólico a un directorio no la lleva (es un enlace: ssh_stat lo dice). Los directorios
    // pesados que se muestran sin recorrer (.git, node_modules) también son directorios. Un nombre que sale escapado como
    // cadena JSON lleva la barra dentro de las comillas. Sin incluirArchivos todo son directorios y no se marca.
    public sealed partial class HerramientasArchivoIntegracionTests
    {
        [Fact]
        public async Task ListDir_ConArchivos_MarcaLosDirectoriosConBarraYNoLosEnlaces()
        {
            // Arrange
            string carpeta = Ruta("tipos");
            _ = Directory.CreateDirectory(Path.Combine(carpeta, "sub", "profundo"));
            _ = Directory.CreateDirectory(Path.Combine(carpeta, "node_modules", "paquete"));
            _ = Directory.CreateDirectory(Path.Combine(carpeta, "linea\nnueva"));
            await File.WriteAllTextAsync(Path.Combine(carpeta, "archivo.txt"), "x\n");
            await File.WriteAllTextAsync(Path.Combine(carpeta, "sub", "dentro.txt"), "x\n");
            _ = File.CreateSymbolicLink(Path.Combine(carpeta, "enlace_dir"), "sub");
            _ = File.CreateSymbolicLink(Path.Combine(carpeta, "enlace_archivo"), "archivo.txt");
            HerramientasSsh herramientas = Herramientas();

            // Act
            string conArchivos = await herramientas.ListarDirectoriosAsync(Destino, carpeta, profundidadMax: 2, incluirArchivos: true);
            string soloDirectorios = await herramientas.ListarDirectoriosAsync(Destino, carpeta, profundidadMax: 1);

            // Assert
            _ = conArchivos.Should().Be("archivo.txt\nenlace_archivo\nenlace_dir\nenlace_dir/dentro.txt\nenlace_dir/profundo/\n\"linea\\nnueva/\"\nnode_modules/\nsub/\nsub/dentro.txt\nsub/profundo/");
            _ = soloDirectorios.Should().Be("enlace_dir\n\"linea\\nnueva\"\nnode_modules\nsub");
        }
    }
}
