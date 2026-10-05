using System.Linq;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Escrituras que no terminan por algo ajeno al script: el canal del shell se cierra (ssh_disconnect, o el Ctrl+C no
    // responde a tiempo) o cae la conexión. El script debe dejar el archivo restaurado o con su copia de seguridad, y el
    // error decir lo que pasó con el id de la petición.
    public sealed partial class HerramientasArchivoIntegracionTests
    {
        // 'cat' falso para la escritura en el sitio cuya restauración tarda 'segundos' pero copia el original completo.
        private string CatConRestauracionLenta(int segundos) =>
            $"case \"$2\" in\n  *.tmp.*) head -c 3 -- \"$2\"; {EsperaInterrumpible()} ;;\n  *.orig.*) sleep {segundos} ;;\nesac\nexec /usr/bin/cat \"$@\"";

        // Cerrado el canal (ssh_disconnect o el Ctrl+C del timeout sin respuesta) o caída la conexión con la escritura en el
        // sitio a medias, el terminal se cuelga y el grupo del script recibe varios SIGHUP casi a la vez: el que reenvía el
        // shell interactivo a sus trabajos, el que el núcleo manda al grupo de primer plano cuando sale el líder de la sesión
        // y, si el líder es otro proceso (el shell de login), el suyo. La trampa debe restaurar el original igualmente, sin
        // que la corte la segunda señal ni mate al 'cat' que restaura, y después borrar la copia y el temporal. El error no
        // puede saber cómo terminó (la salida se cortó): da el id, las rutas y que el script restaura por su cuenta.
        [Theory]
        [InlineData("desconexion", false)]
        [InlineData("desconexion", true)]
        [InlineData("caida", false)]
        [InlineData("caida", true)]
        [InlineData("timeout_con_restauracion_lenta", false)]
        public async Task EscrituraEnElSitio_CanalCerradoOConexionCaidaAMitad_LaTrampaRestauraElOriginal(string interrupcion, bool isSudo)
        {
            // Arrange
            string[] procesosAjenos = _sshd.ProcesosDeConexion();
            bool isTimeout = interrupcion == "timeout_con_restauracion_lenta";
            HerramientasSsh herramientas = Herramientas(timeout: isTimeout ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(40));
            (string carpeta, string archivo, string tmpdir) = await PrepararEscrituraEnElSitioAsync(herramientas);
            string stat = Stat(archivo);
            await SustituirComandoAsync(herramientas, "cat", isTimeout ? CatConRestauracionLenta(3) : CatFalso(EsperaInterrumpible()));
            if (isSudo)
                await SimularSudoAsync(herramientas);

            // Act
            Task<ResultadoEscritura> escritura = herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo contenido\n", overwrite: true, sudo: isSudo);
            await EsperasPrueba.EsperarHastaAsync(() => HayProcesos($"{Ruta("bin/cat")} -- {carpeta}/.f.txt.tmp."), TimeSpan.FromSeconds(20));
            if (interrupcion == "desconexion")
                _ = await herramientas.DesconectarAsync(Destino);
            else if (interrupcion == "caida")
                _ = Bash($"kill -9 {string.Join(' ', _sshd.ProcesosDeConexion().Except(procesosAjenos))}");
            NativeSshException error = (await FluentActions.Awaiting(() => escritura).Should().ThrowAsync<NativeSshException>()).Which;
            await EsperasPrueba.EsperarHastaAsync(() => Directory.GetFileSystemEntries(carpeta).Length == 2, TimeSpan.FromSeconds(15));

            // Assert: restaurado en el sitio (mismo inodo y modo) y sin restos; el error no afirma un estado que no conoce.
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("original completo\n");
            _ = Stat(archivo).Should().Be(stat);
            _ = Directory.GetFileSystemEntries(tmpdir).Should().BeEmpty();
            _ = error.Message.Should().MatchRegex("petición [0-9a-f]{32}")
                .And.Contain("puede haber quedado a medio escribir")
                .And.Contain("restaura el original desde la copia de seguridad")
                .And.Contain(".f.txt.orig.")
                .And.Contain("no confirmó cómo terminó")
                .And.NotContain("Se restauró")
                .And.NotContain("__NS_");
        }
    }
}
