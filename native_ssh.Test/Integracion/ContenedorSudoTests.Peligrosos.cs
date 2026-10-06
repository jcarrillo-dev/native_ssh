using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // Objetivos peligrosos con sudo REAL: dispositivos (/dev/null, /dev/zero), una FIFO de root y enlaces a ellos. Con
    // root, un 'mv' encima de /dev/null lo sustituiría por un archivo regular para todo el sistema y un 'cat >' a /dev/zero
    // no guardaría nada: las herramientas deben rechazarlos sin tocarlos (también con append y en ssh_replace_file_content).
    // Después de cada caso se comprueba que el objetivo sigue siendo lo que era. (En un contenedor sin privilegios no hay
    // dispositivos de bloques.)
    public sealed partial class ContenedorSudoTests
    {
        public static TheoryData<string, string, string> CasosPeligrosos()
        {
            TheoryData<string, string, string> casos = [];
            foreach (string distro in new[] { ContenedorSudo.Debian, ContenedorSudo.Alpine })
            {
                foreach (string objetivo in new[] { "/dev/null", "/dev/zero", "enlace_null", "fifo", "enlace_fifo" })
                {
                    foreach (string operacion in new[] { "sobrescribir", "anexar", "reemplazar" })
                        casos.Add(distro, objetivo, operacion);
                }
            }

            return casos;
        }

        [TheoryContenedor]
        [MemberData(nameof(CasosPeligrosos))]
        public async Task EscrituraConSudo_ObjetivoQueNoEsUnArchivoRegular_SeRechazaSinTocarlo(string distro, string objetivo, string operacion)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas(TimeSpan.FromSeconds(20));
            await entorno.AutenticarSudoAsync(herramientas);
            string fifo = $"{entorno.DirectorioRoot}/fifo";
            _ = entorno.Root("mkfifo -m 0600 \"$1\" && ln -s /dev/null \"$2/enlace_null\" && ln -s \"$1\" \"$2/enlace_fifo\"", fifo, entorno.DirectorioUsuario);
            string ruta = objetivo.StartsWith('/') ? objetivo
                : objetivo == "fifo" ? fifo
                : $"{entorno.DirectorioUsuario}/{objetivo}";
            string real = objetivo.Contains("null", StringComparison.Ordinal) ? "/dev/null" : objetivo.Contains("fifo", StringComparison.Ordinal) ? fifo : ruta;
            string antes = TipoEInodo(entorno, real);
            Task operacionEnCurso = operacion switch
            {
                "sobrescribir" => herramientas.EscribirArchivoAsync(entorno.Destino, ruta, "basura\n", overwrite: true, sudo: true),
                "anexar" => herramientas.EscribirArchivoAsync(entorno.Destino, ruta, "basura\n", append: true, sudo: true),
                _ => herramientas.ReemplazarContenidoArchivoAsync(entorno.Destino, ruta, "x", "y", sudo: true)
            };

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => operacionEnCurso).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("no es un archivo regular");
            _ = TipoEInodo(entorno, real).Should().Be(antes);
            _ = entorno.Root("[ -L \"$1/enlace_null\" ] && [ -L \"$1/enlace_fifo\" ] && echo enlaces", entorno.DirectorioUsuario).Should().Be("enlaces\n");
        }

        // Tipo (sin seguir enlaces), inodo y modo de una ruta, vistos por root.
        private static string TipoEInodo(Entorno entorno, string ruta) =>
            entorno.Root("stat -c '%F %i %a %U' -- \"$1\"", ruta).TrimEnd('\n');
    }
}
