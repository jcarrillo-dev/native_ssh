using System.Linq;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // 'test -e' falla igual si la ruta no existe que si un directorio de la ruta no se puede atravesar: las herramientas
    // que leen distinguen «no existe» de «sin permiso», y una ruta sin permiso de lectura no se toma por vacía.
    public sealed partial class HerramientasArchivoIntegracionTests
    {
        // Lo que cada herramienta responde para 'archivo' (un archivo) y 'directorio' (un directorio).
        private static async Task<Dictionary<string, string>> ErroresDeLecturaAsync(HerramientasSsh herramientas, string destino, string archivo, string directorio)
        {
            Dictionary<string, Func<Task<object>>> llamadas = new()
            {
                ["ssh_read_file"] = async () => await herramientas.LeerArchivoAsync(destino, archivo),
                ["ssh_stat"] = async () => await herramientas.ConsultarMetadatosAsync(destino, archivo),
                ["ssh_search archivo"] = async () => await herramientas.BuscarAsync(destino, "x", archivo),
                ["ssh_search directorio"] = async () => await herramientas.BuscarAsync(destino, "x", directorio),
                ["ssh_find"] = async () => await herramientas.BuscarPorNombreAsync(destino, "*", directorio),
                ["ssh_list_dir"] = async () => await herramientas.ListarDirectoriosAsync(destino, directorio, incluirArchivos: true),
                ["ssh_set_cwd"] = async () => await herramientas.FijarDirectorioAsync(destino, directorio),
                ["ssh_replace_file_content"] = async () => await herramientas.ReemplazarContenidoArchivoAsync(destino, archivo, "x", "y"),
            };
            Dictionary<string, string> errores = [];
            foreach ((string herramienta, Func<Task<object>> llamada) in llamadas)
            {
                try
                {
                    errores[herramienta] = $"SIN ERROR: {await llamada()}";
                }
                catch (NativeSshException ex)
                {
                    errores[herramienta] = ex.Message;
                }
            }

            return errores;
        }

        [Fact]
        public async Task HerramientasDeLectura_RutaTrasUnDirectorioQueNoSePuedeAtravesar_DicenSinPermisoYNoQueNoExiste()
        {
            // Arrange
            string cerrado = Ruta("cerrado");
            string directorio = Path.Combine(cerrado, "sub");
            string archivo = Path.Combine(directorio, "archivo.txt");
            _ = Directory.CreateDirectory(directorio);
            await File.WriteAllTextAsync(archivo, "x\n");
            _ = Bash($"chmod 000 '{cerrado}'");

            // Act
            Dictionary<string, string> errores = await ErroresDeLecturaAsync(Herramientas(), Destino, archivo, directorio);

            // Assert
            _ = errores.Should().HaveCount(8).And.AllSatisfy(error => error.Value.Should().Contain("sin permiso para acceder", error.Key).And.NotContain("no existe", error.Key));
        }

        [Fact]
        public async Task HerramientasDeLectura_RutaQueNoExiste_DicenQueNoExiste()
        {
            // Arrange
            string directorio = Ruta("no_existe");
            string archivo = Path.Combine(directorio, "archivo.txt");
            HerramientasSsh herramientas = Herramientas();

            // Act
            Dictionary<string, string> errores = await ErroresDeLecturaAsync(herramientas, Destino, archivo, directorio);
            ResultadoMetadatos metadatos = await herramientas.ConsultarMetadatosAsync(Destino, archivo);

            // Assert: ssh_stat no es un error, sino existe=false.
            _ = errores.Remove("ssh_stat");
            _ = errores.Should().HaveCount(7).And.AllSatisfy(error => error.Value.Should().Contain("no existe", error.Key).And.NotContain("sin permiso", error.Key));
            _ = metadatos.Existe.Should().BeFalse();
        }

        // La ruta existe y se puede atravesar, pero ella misma no se puede leer (o recorrer, si es un directorio): error,
        // nunca contenido vacío ni «No se encontraron...».
        [Fact]
        public async Task HerramientasDeLectura_RutaSinPermisoDeLectura_DevuelvenErrorDePermiso()
        {
            // Arrange: archivo 0000 y directorio 0300 (se puede entrar, no listar).
            string directorio = Ruta("sin_lectura");
            string archivo = Ruta("sin_lectura.txt");
            _ = Directory.CreateDirectory(directorio);
            await File.WriteAllTextAsync(Path.Combine(directorio, "dentro.txt"), "x\n");
            await File.WriteAllTextAsync(archivo, "x\n");
            _ = Bash($"chmod 000 '{archivo}' && chmod 300 '{directorio}'");

            // Act
            Dictionary<string, string> errores = await ErroresDeLecturaAsync(Herramientas(), Destino, archivo, directorio);

            // Assert: ssh_stat sí puede ver los metadatos, y ssh_set_cwd sí puede fijar un directorio en el que se puede entrar.
            _ = errores["ssh_stat"].Should().StartWith("SIN ERROR");
            _ = errores["ssh_set_cwd"].Should().StartWith("SIN ERROR");
            _ = errores.Where(error => error.Key is not ("ssh_stat" or "ssh_set_cwd")).Should().HaveCount(6)
                .And.AllSatisfy(error => error.Value.Should().Contain("sin permiso", error.Key));
        }
    }
}
