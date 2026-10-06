using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // Las exclusiones de archivos (*.min.js, *.map, *.lock, package-lock.json...) son solo para archivos: un directorio
    // que se llama como una de ellas se recorre en los cuatro caminos, como en grep de GNU (--exclude no se aplica a los
    // directorios) y en el recorrido con find ('! -name' junto a '-type f'). rg omitía el directorio entero porque sus
    // globs, sin barra final, casan también con directorios (sonda N03 de la auditoría del A-nonies; A-decies, AD2).
    public sealed partial class MotoresBusquedaTests
    {
        private void CrearArbolConDirectoriosQueSeLlamanComoArchivosExcluidos()
        {
            Escribir("t/deps.lock/a.txt", "valor deplock");
            Escribir("t/deps.lock/z.lock", "valor candado dentro");
            Escribir("t/src.map/b.txt", "valor mapa");
            Escribir("t/m.min.js/c.txt", "valor minificado");
            Escribir("t/pkg/package-lock.json/d.txt", "valor paquete");
            Escribir("t/c.txt", "valor raiz");
            Escribir("t/.oculto.txt", "valor oculto");
            Escribir("t/x.lock", "valor candado");
            Escribir("t/y.min.js", "valor min");
            Escribir("t/node_modules/n.txt", "valor modulos");
            Escribir("t/sub/build/e.txt", "valor build");
        }

        [Theory]
        [MemberData(nameof(CasosDirectorioConNombreDeArchivoExcluido))]
        public void DirectorioQueSeLlamaComoUnaExclusionDeArchivos_SeRecorreEnLosCuatroMotores(string motor, string? extension, string[] esperadas)
        {
            // Arrange
            string? anunciado = InstalarMotor(motor);
            if (anunciado is null)
                return;
            CrearArbolConDirectoriosQueSeLlamanComoArchivosExcluidos();

            // Act
            (_, string salida, string errores) = EjecutarConErrores(HerramientasSsh.ConstruirComandoBusqueda("valor [a-z]+", "t", extension, 50));

            // Assert
            _ = errores.Should().BeEmpty();
            string[] lineas = salida.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _ = lineas[0].Should().Be($"__NS_MOTOR:{anunciado}");
            _ = lineas[1..].Should().BeEquivalentTo(esperadas);
        }

        public static TheoryData<string, string?, string[]> CasosDirectorioConNombreDeArchivoExcluido()
        {
            string[] todas =
            [
                "t/deps.lock/a.txt:1:valor deplock",
                "t/src.map/b.txt:1:valor mapa",
                "t/m.min.js/c.txt:1:valor minificado",
                "t/pkg/package-lock.json/d.txt:1:valor paquete",
                "t/c.txt:1:valor raiz",
                "t/.oculto.txt:1:valor oculto",
            ];
            TheoryData<string, string?, string[]> casos = [];
            foreach (string motor in new[] { "rg", "grep -P", "grep -E", "find" })
            {
                casos.Add(motor, null, todas);
                casos.Add(motor, "txt", todas);
                // Las exclusiones pesan más que el filtro de extensión: ningún motor da los *.lock.
                casos.Add(motor, "lock", []);
            }

            return casos;
        }
    }
}
