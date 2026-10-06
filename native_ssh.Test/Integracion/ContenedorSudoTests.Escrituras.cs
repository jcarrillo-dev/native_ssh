using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // Escrituras con sudo REAL sobre archivos de root: lo que se conserva (propietario, grupo, modo con setuid, atributos
    // extendidos, capacidades, enlaces duros y simbólicos) por el camino atómico (Debian, con GNU coreutils) y en el sitio
    // (Alpine: el cp de busybox no tiene --attributes-only, así que siempre escribe en el sitio).
    public sealed partial class ContenedorSudoTests
    {
        // Propietario, grupo y modo de una ruta (sin seguir enlaces) y su inodo, vistos por root en el contenedor.
        private static string Atributos(Entorno entorno, string ruta) => entorno.Root("stat -c '%U:%G %a %i' -- \"$1\"", ruta).TrimEnd('\n');

        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task EscribirConSudo_ArchivoDeRootConGrupoYModo_LosConservaConIsAtomicaSegunLaDistro(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            await entorno.AutenticarSudoAsync(herramientas);
            string archivo = $"{entorno.DirectorioRoot}/app.conf";
            _ = entorno.Root("printf 'antes\\n' > \"$1\" && chown root:adm \"$1\" && chmod 0640 \"$1\"", archivo);
            string antes = Atributos(entorno, archivo);

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(entorno.Destino, archivo, "despues\n", overwrite: true, sudo: true);
            salida.WriteLine(resultado.Mensaje);

            // Assert: Debian sustituye con mv (inodo nuevo); Alpine escribe en el sitio (mismo inodo) y lo dice.
            string despues = Atributos(entorno, archivo);
            _ = entorno.Root("cat \"$1\"", archivo).Should().Be("despues\n");
            _ = despues[..despues.LastIndexOf(' ')].Should().Be("root:adm 640");
            _ = resultado.IsAtomica.Should().Be(entorno.Servidor.IsDebian, resultado.Mensaje);
            _ = (despues == antes).Should().Be(!entorno.Servidor.IsDebian);
            _ = entorno.Root("ls -A \"$1\"", entorno.DirectorioRoot).Should().Be("app.conf\n");
        }

        // Atributos extendidos de usuario y capacidades (security.capability) de un archivo de root: se conservan al
        // sustituirlo (cp --preserve=xattr y comprobación con getfattr). Solo en Debian: la imagen de Alpine no lleva
        // setfattr/setcap (busybox) para prepararlos.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        public async Task EscribirConSudo_ArchivoDeRootConXattrYCapacidades_LosConserva(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            await entorno.AutenticarSudoAsync(herramientas);
            string archivo = $"{entorno.DirectorioRoot}/herramienta";
            _ = entorno.Root("printf '#!/bin/sh\\necho antes\\n' > \"$1\" && chmod 0755 \"$1\" && setfattr -n user.ns_origen -v prueba \"$1\" && setcap cap_net_raw+ep \"$1\"", archivo);

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(entorno.Destino, archivo, "#!/bin/sh\necho despues\n", overwrite: true, sudo: true);
            salida.WriteLine(resultado.Mensaje);

            // Assert
            _ = entorno.Root("cat \"$1\"; getfattr --only-values -n user.ns_origen \"$1\"; echo; getcap \"$1\"", archivo)
                .Should().Be($"#!/bin/sh\necho despues\nprueba\n{archivo} cap_net_raw=ep\n");
            _ = resultado.Mensaje.Should().StartWith("Archivo escrito correctamente");
            _ = Atributos(entorno, archivo).Should().StartWith("root:root 755 ");
        }

        // Ejecutable setuid de root (4755): escrito con sudo conserva el bit (en el sitio, root no lo pierde; al sustituir,
        // se copia y se comprueba).
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task EscribirConSudo_EjecutableSetuidDeRoot_ConservaElModo4755(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            await entorno.AutenticarSudoAsync(herramientas);
            string archivo = $"{entorno.DirectorioRoot}/setuid";
            _ = entorno.Root("printf 'antes\\n' > \"$1\" && chmod 4755 \"$1\"", archivo);

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(entorno.Destino, archivo, "despues\n", overwrite: true, sudo: true);
            salida.WriteLine(resultado.Mensaje);

            // Assert
            _ = entorno.Root("cat \"$1\"", archivo).Should().Be("despues\n");
            _ = Atributos(entorno, archivo).Should().StartWith("root:root 4755 ", resultado.Mensaje);
        }

        // Archivo de root con un enlace duro: se escribe en el sitio (mismo inodo, el otro nombre ve el contenido nuevo) y la
        // respuesta lo dice.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task EscribirConSudo_ArchivoDeRootConEnlaceDuro_EscribeEnElSitio(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            await entorno.AutenticarSudoAsync(herramientas);
            string archivo = $"{entorno.DirectorioRoot}/datos";
            _ = entorno.Root("printf 'antes\\n' > \"$1\" && chmod 0600 \"$1\" && ln \"$1\" \"$1.enlace\"", archivo);
            string antes = Atributos(entorno, archivo);

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(entorno.Destino, archivo, "despues\n", overwrite: true, sudo: true);
            salida.WriteLine(resultado.Mensaje);

            // Assert
            _ = resultado.IsAtomica.Should().BeFalse();
            _ = Atributos(entorno, archivo).Should().Be(antes);
            _ = entorno.Root("cat \"$1.enlace\"", archivo).Should().Be("despues\n");
            _ = entorno.Root("ls -A \"$1\"", entorno.DirectorioRoot).Should().Be("datos\ndatos.enlace\n");
        }

        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task AnexarConSudo_ArchivoDeRoot0600_AnexaYConservaModoEInodo(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            await entorno.AutenticarSudoAsync(herramientas);
            string archivo = $"{entorno.DirectorioRoot}/registro";
            _ = entorno.Root("printf 'uno\\n' > \"$1\" && chmod 0600 \"$1\"", archivo);
            string antes = Atributos(entorno, archivo);

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(entorno.Destino, archivo, "dos\n", append: true, sudo: true);
            salida.WriteLine(resultado.Mensaje);

            // Assert
            _ = resultado.Mensaje.Should().StartWith("Contenido anexado correctamente");
            _ = entorno.Root("cat \"$1\"", archivo).Should().Be("uno\ndos\n");
            _ = Atributos(entorno, archivo).Should().Be(antes);
        }

        // Archivo 0600 de root dentro de un directorio 0700 de root, editado a través de un enlace simbólico del usuario:
        // con sudo, la lectura previa y la escritura atraviesan el directorio; el enlace se conserva y el archivo mantiene
        // propietario y modo.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task ReemplazarConSudo_AtravesDeUnEnlaceAUnDirectorio0700DeRoot_ConservaEnlacePropietarioYModo(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            await entorno.AutenticarSudoAsync(herramientas);
            string archivo = $"{entorno.DirectorioRoot}/privado/clave.pem";
            string enlace = $"{entorno.DirectorioUsuario}/actual.pem";
            _ = entorno.Root("mkdir -m 0700 \"${1%/*}\" && printf 'linea=vieja\\nfin\\n' > \"$1\" && chmod 0600 \"$1\" && ln -s \"$1\" \"$2\"", archivo, enlace);

            // Act
            ResultadoEscritura resultado = await herramientas.ReemplazarContenidoArchivoAsync(entorno.Destino, enlace, "linea=vieja", "linea=nueva", sudo: true);
            salida.WriteLine(resultado.Mensaje);

            // Assert
            _ = resultado.Reemplazos.Should().Be(1);
            _ = entorno.Root("cat \"$1\"; readlink \"$2\"", archivo, enlace).Should().Be($"linea=nueva\nfin\n{archivo}\n");
            _ = Atributos(entorno, archivo).Should().StartWith("root:root 600 ");
            _ = entorno.Root("ls -A \"${1%/*}\"", archivo).Should().Be("clave.pem\n");
        }

        // Sin sudo, un archivo de root no se puede escribir: error sin tocarlo.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task EscribirSinSudo_ArchivoDeRoot_FallaSinTocarlo(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            string archivo = $"{entorno.DirectorioRoot}/app.conf";
            _ = entorno.Root("printf 'antes\\n' > \"$1\" && chmod 0644 \"$1\"", archivo);
            string antes = Atributos(entorno, archivo);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(entorno.Destino, archivo, "despues\n", overwrite: true))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("No se ha modificado");
            _ = entorno.Root("cat \"$1\"", archivo).Should().Be("antes\n");
            _ = Atributos(entorno, archivo).Should().Be(antes);
        }

        // Archivo nuevo con sudo en un directorio de root: lo crea root con el modo de la umask del usuario (022 → 644).
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task EscribirConSudo_ArchivoNuevoEnDirectorioDeRoot_LoCreaRootConLaUmaskDelUsuario(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            await entorno.AutenticarSudoAsync(herramientas);
            string archivo = $"{entorno.DirectorioRoot}/nuevo.conf";

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(entorno.Destino, archivo, "nuevo\n", sudo: true);
            salida.WriteLine(resultado.Mensaje);

            // Assert
            _ = resultado.Mensaje.Should().StartWith("Archivo escrito correctamente");
            _ = entorno.Root("cat \"$1\"", archivo).Should().Be("nuevo\n");
            _ = Atributos(entorno, archivo).Should().StartWith("root:root 644 ");
        }
    }
}
