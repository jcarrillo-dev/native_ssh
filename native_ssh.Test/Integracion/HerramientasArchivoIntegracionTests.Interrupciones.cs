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

            // El script suelta el candado del archivo al salir, justo después de borrar la copia y el temporal; mientras lo
            // tiene, .NET no puede abrir el archivo (emula FileShare con flock).
            await EsperasPrueba.EsperarHastaAsync(() => IsCandadoLibre(archivo), TimeSpan.FromSeconds(10));

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

        // Tras un Ctrl+C (SIGINT al grupo del terminal) con la escritura en el sitio a medias, la trampa restaura el original;
        // una segunda señal en ese momento (otro SIGINT, SIGTERM o SIGHUP, al script y a sus hijos) no la corta: ni al bash
        // del script, que sin trampas moría al instante (bash mata al shell que recibe una señal mortal mientras maneja otra),
        // ni al 'cat' que restaura (aquí tarda 2 s). El script termina con el código 130 tras restaurar y limpiar, y el error
        // es el suyo.
        [Theory]
        [InlineData("INT")]
        [InlineData("TERM")]
        [InlineData("HUP")]
        public async Task EscrituraEnElSitio_SegundaSenalDuranteLaRestauracionDeLaTrampa_NoLaCorta(string senal)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(40));
            (string carpeta, string archivo, string tmpdir) = await PrepararEscrituraEnElSitioAsync(herramientas);
            string stat = Stat(archivo);
            await SustituirComandoAsync(herramientas, "cat", CatConRestauracionLenta(2));

            // Act
            Task<ResultadoEscritura> escritura = herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo contenido\n", overwrite: true);
            await EsperasPrueba.EsperarHastaAsync(() => HayProcesos($"{Ruta("bin/cat")} -- {carpeta}/.f.txt.tmp."), TimeSpan.FromSeconds(20));
            string script = Bash($"ps -o ppid= -p \"$(pgrep -f -- '{Ruta("bin/cat")} -- {carpeta}/.f.txt.tmp.' | head -n 1)\"");
            _ = Bash($"kill -INT -- -\"$(ps -o pgid= -p {script} | tr -d ' ')\"");
            await EsperasPrueba.EsperarHastaAsync(() => HayProcesos($"{Ruta("bin/cat")} -- {carpeta}/.f.txt.orig."), TimeSpan.FromSeconds(10));
            _ = Bash($"pkill -{senal} -P {script}; kill -{senal} {script}");
            NativeSshException error = (await FluentActions.Awaiting(() => escritura).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be($"Error: se interrumpió la escritura en el sitio de {archivo}; se ha restaurado su contenido original.");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("original completo\n");
            _ = Stat(archivo).Should().Be(stat);
            _ = Directory.GetFileSystemEntries(carpeta).Should().HaveCount(2);
            _ = Directory.GetFileSystemEntries(tmpdir).Should().BeEmpty();
        }

        // Tras una escritura en el sitio correcta, la trampa de salida borra la copia y el temporal y lo anuncia. Una señal en
        // ese momento (el 'rm' de la limpieza tarda aquí 2 s) no la corta: sin ignorar las señales desde la primera orden de
        // la trampa, la de la señal salía en medio de la limpieza, sin terminarla ni confirmarla, y la escritura correcta
        // acababa en un error.
        [Theory]
        [InlineData("INT")]
        [InlineData("HUP")]
        public async Task EscrituraEnElSitio_SenalDuranteLaLimpiezaTrasEscribir_NoLaCorta(string senal)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(40));
            (string carpeta, string archivo, string tmpdir) = await PrepararEscrituraEnElSitioAsync(herramientas);
            string marca = Ruta("limpiando");
            await SustituirComandoAsync(herramientas, "rm", $"case \" $* \" in *.f.txt.tmp.*) : > '{marca}'; sleep 2 ;; esac\nexec /usr/bin/rm \"$@\"");

            // Act
            Task<ResultadoEscritura> escritura = herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo contenido\n", overwrite: true);
            await EsperasPrueba.EsperarHastaAsync(() => File.Exists(marca), TimeSpan.FromSeconds(20));
            string script = Bash($"ps -o ppid= -p \"$(pgrep -f -- '{Ruta("bin/rm")} -f -- ' | head -n 1)\"");
            _ = Bash($"pkill -{senal} -P {script}; kill -{senal} {script}");
            ResultadoEscritura resultado = await escritura;

            // Assert
            _ = resultado.IsAtomica.Should().BeFalse();
            _ = resultado.Mensaje.Should().Contain("ya borrada");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("nuevo contenido\n");
            _ = Directory.GetFileSystemEntries(carpeta).Should().HaveCount(2);
            _ = Directory.GetFileSystemEntries(tmpdir).Should().BeEmpty();
        }

        // SIGKILL al bash del script (el OOM killer o un kill -9) con la escritura en el sitio a medias: no hay trampa que
        // restaure ni anuncio final, y la petición termina con el código 137 sin un error propio del script. El error debe
        // decir que el archivo puede haber quedado a medio escribir, dónde están la copia del original y el contenido nuevo
        // (que siguen ahí), el código y el id de la petición.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EscrituraEnElSitio_ScriptMuertoConSigkillAMitad_ElErrorDaElEstadoLasRutasYElId(bool isReplace)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(40));
            (string carpeta, string archivo, _) = await PrepararEscrituraEnElSitioAsync(herramientas);
            await SustituirComandoAsync(herramientas, "cat", CatFalso(EsperaInterrumpible()));

            // Act
            Task<ResultadoEscritura> escritura = isReplace
                ? herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "original completo", "nuevo contenido")
                : herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo contenido\n", overwrite: true);
            await EsperasPrueba.EsperarHastaAsync(() => HayProcesos($"{Ruta("bin/cat")} -- {carpeta}/.f.txt.tmp."), TimeSpan.FromSeconds(20));
            string cat = Bash($"pgrep -f -- '{Ruta("bin/cat")} -- {carpeta}/.f.txt.tmp.' | head -n 1");
            _ = Bash($"kill -KILL \"$(ps -o ppid= -p {cat})\"");
            NativeSshException error = (await FluentActions.Awaiting(() => escritura).Should().ThrowAsync<NativeSshException>()).Which;
            _ = Bash($"kill -INT {cat} 2>/dev/null; true");
            await EsperasPrueba.EsperarHastaAsync(() => IsCandadoLibre(archivo), TimeSpan.FromSeconds(10));

            // Assert
            string copia = Directory.GetFiles(carpeta, ".f.txt.orig.*").Should().ContainSingle().Which;
            string nuevo = Directory.GetFiles(carpeta, ".f.txt.tmp.*").Should().ContainSingle().Which;
            _ = (await File.ReadAllTextAsync(copia)).Should().Be("original completo\n");
            _ = (await File.ReadAllTextAsync(nuevo)).Should().Be("nuevo contenido\n");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("nue");
            _ = error.Message.Should().MatchRegex("terminó con el código 137 .*petición [0-9a-f]{32}")
                .And.Contain($"'{archivo}' puede haber quedado a medio escribir y el script ya no lo restaurará")
                .And.Contain($"'{copia}'")
                .And.Contain($"'{nuevo}'")
                .And.NotContain("__NS_");
        }

        // La lectura previa de ssh_replace_file_content (un 'base64' falso que se queda esperando al codificar) no termina:
        // por el timeout, porque la sesión se cierra (ssh_disconnect) o por el token. La sesión finaliza la petición antes
        // de que venza la espera de la herramienta, y el error debe decir igualmente que el archivo no se modificó y dar el
        // id de la petición para ver su salida con ssh_output.
        [Theory]
        [InlineData("timeout")]
        [InlineData("desconexion")]
        [InlineData("token")]
        public async Task ReemplazarContenido_LecturaPreviaQueNoTermina_DiceQueNoSeModificoYDaElId(string interrupcion)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: interrupcion == "timeout" ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(40));
            string archivo = Ruta("f.txt");
            await File.WriteAllTextAsync(archivo, "original\n");
            string marca = Ruta("leyendo");
            await SustituirComandoAsync(herramientas, "base64", $"[ \"$1\" = -d ] && exec /usr/bin/base64 \"$@\"\n: > '{marca}'\n{EsperaInterrumpible()}");
            using CancellationTokenSource cancelacion = new();

            // Act
            Task<ResultadoEscritura> reemplazo = herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "original", "cambiado", cancellationToken: cancelacion.Token);
            await EsperasPrueba.EsperarHastaAsync(() => File.Exists(marca), TimeSpan.FromSeconds(20));
            if (interrupcion == "desconexion")
                _ = await herramientas.DesconectarAsync(Destino);
            else if (interrupcion == "token")
                await cancelacion.CancelAsync();
            Exception? error = await Record.ExceptionAsync(() => reemplazo);
            await EsperasPrueba.EsperarHastaAsync(() => !HayProcesos(Ruta("bin/base64")), TimeSpan.FromSeconds(25));

            // Assert
            _ = error.Should().BeAssignableTo(interrupcion == "token" ? typeof(OperationCanceledException) : typeof(NativeSshException));
            _ = error!.Message.Should().MatchRegex("petición [0-9a-f]{32}").And.EndWith($"No se ha modificado '{archivo}'.");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("original\n");
        }
    }
}
