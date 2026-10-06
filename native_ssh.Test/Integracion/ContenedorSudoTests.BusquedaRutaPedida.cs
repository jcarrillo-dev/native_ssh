using System.Linq;
using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // ssh_search con la ruta pedida en Debian (grep de GNU, -P) y Alpine (busybox: recorrido con find + grep -E), Bloque
    // A-nonies. Solo pasan con las correcciones de r2-anoveno: esta rama (desde r2-sudo) aporta las pruebas.
    public sealed partial class ContenedorSudoTests
    {
        private static string Motor(string distro) => distro == ContenedorSudo.Debian ? "grep -P" : "grep -E";

        private static string PrepararArbolRutaPedida(Entorno entorno)
        {
            _ = entorno.Servidor.ComoUsuario("""
                cd "$1"
                mkdir -p arbol/./-d 'arbol/(' 'arbol/)' 'arbol/!' 'arbol/,' arbol/.git arbol/src/build arbol/node_modules/x/node_modules \
                  arbol/build arbol/dist arbol/real arbol/herramientas arbol/bin arbol/cerrado
                printf 'valor guion\n' > arbol/./-d/f.txt
                printf 'valor paren\n' > 'arbol/(/p.txt'
                printf 'valor cierre\n' > 'arbol/)/q.txt'
                printf 'valor excl\n' > 'arbol/!/e.txt'
                printf 'valor coma\n' > 'arbol/,/c.txt'
                printf 'valor archivo\n' > arbol/./-x.txt
                printf 'valor git\n' > arbol/.git/g.txt
                printf 'valor nm\n' > arbol/node_modules/n.txt
                printf 'valor anidado\n' > arbol/node_modules/x/node_modules/m.txt
                printf 'valor build\n' > arbol/build/b.txt
                printf 'valor dist\n' > arbol/dist/d.txt
                printf 'valor src build\n' > arbol/src/build/s.txt
                printf 'valor py\n' > arbol/src/a.py
                printf 'valor lock\n' > arbol/package-lock.json
                printf 'valor min\n' > arbol/f.min.js
                printf 'valor objeto\n' > arbol/g.o
                printf 'valor real\n' > arbol/real/r.txt
                ln -s real arbol/enlace
                printf 'valor script bin\n' > arbol/herramientas/bin
                printf 'valor dir bin\n' > arbol/bin/x.txt
                printf 'valor cerrado\n' > arbol/cerrado/z.txt
                chmod 000 arbol/cerrado
                """, entorno.DirectorioUsuario);
            return $"{entorno.DirectorioUsuario}/arbol";
        }

        // Z01 de la auditoría del A-octies (B1): ruta relativa (cwd fijado con ssh_set_cwd) que find tomaría por una opción
        // o un operador. Con busybox, '-d' y '(' daban «sin coincidencias» y '!' buscaba en todo el directorio actual.
        // Ahora lleva './' delante en todos los motores y solo sale lo de dentro, igual en las dos distros.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian, "-d", "./-d/f.txt:1:valor guion")]
        [InlineData(ContenedorSudo.Alpine, "-d", "./-d/f.txt:1:valor guion")]
        [InlineData(ContenedorSudo.Debian, "(", "./(/p.txt:1:valor paren")]
        [InlineData(ContenedorSudo.Alpine, "(", "./(/p.txt:1:valor paren")]
        [InlineData(ContenedorSudo.Debian, ")", "./)/q.txt:1:valor cierre")]
        [InlineData(ContenedorSudo.Alpine, ")", "./)/q.txt:1:valor cierre")]
        [InlineData(ContenedorSudo.Debian, "!", "./!/e.txt:1:valor excl")]
        [InlineData(ContenedorSudo.Alpine, "!", "./!/e.txt:1:valor excl")]
        [InlineData(ContenedorSudo.Debian, ",", "./,/c.txt:1:valor coma")]
        [InlineData(ContenedorSudo.Alpine, ",", "./,/c.txt:1:valor coma")]
        [InlineData(ContenedorSudo.Debian, "-x.txt", "./-x.txt:1:valor archivo")]
        [InlineData(ContenedorSudo.Alpine, "-x.txt", "./-x.txt:1:valor archivo")]
        public async Task Buscar_RutaRelativaQueFindTomaPorOpcion_SoloLoDeDentro(string distro, string ruta, string esperado)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            _ = await herramientas.FijarDirectorioAsync(entorno.Destino, PrepararArbolRutaPedida(entorno));

            // Act
            string resultado = await herramientas.BuscarAsync(entorno.Destino, "valor [a-z]+", ruta);

            // Assert
            _ = resultado.Should().Be($"{esperado}\n\n[motor: {Motor(distro)}]");
        }

        // AN2 y AN3: la ruta pedida nunca se excluye (directorio que se llama como uno excluido, con o sin barra final;
        // archivo excluido por nombre o por extensión de binario; archivo cuya extensión no casa con el filtro). El grep de
        // GNU 3.11 de Debian aplicaba las exclusiones y --include también a la ruta pedida; con la ruta como archivo, la
        // salida es 'ruta:línea:texto' en las dos.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task Buscar_RutaPedidaQueSeExcluiriaDentroDelArbol_IgualEnLasDosDistros(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            string arbol = PrepararArbolRutaPedida(entorno);
            (string Ruta, string? Extension, string Esperado)[] casos =
            [
                ("node_modules", null, "node_modules/n.txt:1:valor nm"),
                ("node_modules/", null, "node_modules/n.txt:1:valor nm"),
                ("build", null, "build/b.txt:1:valor build"),
                ("dist", "txt", "dist/d.txt:1:valor dist"),
                ("src/build", null, "src/build/s.txt:1:valor src build"),
                ("package-lock.json", null, "package-lock.json:1:valor lock"),
                ("f.min.js", null, "f.min.js:1:valor min"),
                ("g.o", null, "g.o:1:valor objeto"),
                ("src/a.py", "js", "src/a.py:1:valor py"),
            ];

            foreach ((string ruta, string? extension, string esperado) in casos)
            {
                // Act
                string resultado = await herramientas.BuscarAsync(entorno.Destino, "valor [a-z]+", $"{arbol}/{ruta}", extension);

                // Assert
                _ = resultado.Should().Be($"{arbol}/{esperado}\n\n[motor: {Motor(distro)}]", "ruta '{0}', extensión '{1}'", ruta, extension);
            }
        }

        // Dentro del árbol, las mismas exclusiones en las dos distros: directorios podados a cualquier profundidad, un
        // archivo que se llama como uno de ellos sí se busca, un enlace a un directorio no se sigue y el subdirectorio sin
        // permiso se omite sin error (find lo avisa por stderr; ahora se filtra, y el resto de sus errores no se calla).
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task Buscar_DentroDelArbol_MismasExclusionesSinEnlacesNiErrores(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            string arbol = PrepararArbolRutaPedida(entorno);

            // Act
            string resultado = await herramientas.BuscarAsync(entorno.Destino, "valor [a-z]+", arbol, maxResultados: 200);

            // Assert
            string[] lineas = resultado.Split('\n');
            _ = lineas[^1].Should().Be($"[motor: {Motor(distro)}]");
            // g.o es texto: el grep de GNU omite los binarios por su contenido (-I) y el recorrido con find, por extensión.
            string[] esperadas =
            [
                $"{arbol}/-d/f.txt:1:valor guion", $"{arbol}/(/p.txt:1:valor paren", $"{arbol}/)/q.txt:1:valor cierre",
                $"{arbol}/!/e.txt:1:valor excl", $"{arbol}/,/c.txt:1:valor coma", $"{arbol}/-x.txt:1:valor archivo",
                $"{arbol}/src/a.py:1:valor py", $"{arbol}/real/r.txt:1:valor real", $"{arbol}/herramientas/bin:1:valor script bin",
                .. distro == ContenedorSudo.Debian ? [$"{arbol}/g.o:1:valor objeto"] : Array.Empty<string>(),
            ];
            _ = lineas[..^2].Should().BeEquivalentTo(esperadas);
        }

        // El límite corta la salida (head se cierra y grep muere por SIGPIPE) sin que find lo convierta en un error.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task Buscar_LimiteConMuchosArchivos_SinErrorDelCorte(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            _ = entorno.Servidor.ComoUsuario("""
                mkdir -p "$1/muchos" && cd "$1/muchos" && i=0
                while [ "$i" -lt 400 ]; do printf 'valor %s\n' "$i" > "f$i.txt"; i=$((i + 1)); done
                """, entorno.DirectorioUsuario);

            // Act
            string resultado = await herramientas.BuscarAsync(entorno.Destino, "valor", $"{entorno.DirectorioUsuario}/muchos", maxResultados: 2);

            // Assert
            _ = resultado.Should().Contain("[Límite alcanzado: se muestran 2 coincidencias.").And.EndWith($"[motor: {Motor(distro)}]");
            _ = resultado.Split('\n').Where(l => l.StartsWith($"{entorno.DirectorioUsuario}/muchos/f", StringComparison.Ordinal)).Should().HaveCount(2);
        }
    }
}
