using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Exclusión por archivo: mientras un script de escritura (ssh_write_file, también con append, o
    // ssh_replace_file_content) sigue en marcha sobre un archivo, incluida la restauración que hace por su cuenta tras una
    // interrupción, tiene tomado su candado (flock sobre el destino resuelto) y cualquier otra escritura, anexado o
    // reemplazo sobre él falla al momento sin tocar nada.
    public sealed partial class HerramientasArchivoIntegracionTests
    {
        private const string TextoCandadoOcupado = "hay otra escritura, reemplazo o restauración en curso sobre";

        // Una escritura en el sitio interrumpida por ssh_disconnect restaura el original por su cuenta (aquí la restauración
        // tarda 6 s, como con un archivo grande o un disco lento). Una operación nueva sobre el mismo archivo antes de que
        // termine no puede pisarla ni mezclarse con ella: falla al momento con un mensaje claro sin crear temporal ni copia,
        // con o sin sudo en cualquiera de las dos (el candado es del archivo, no del usuario). Al terminar la restauración
        // el archivo es el original y una escritura nueva funciona. Antes, la segunda se confirmaba como correcta y la
        // restauración la pisaba después (auditoría del A-quinquies, sonda N01).
        [Theory]
        [InlineData("en_sitio", false, false)]
        [InlineData("atomica", false, false)]
        [InlineData("en_sitio", true, false)]
        [InlineData("atomica", false, true)]
        [InlineData("en_sitio", true, true)]
        [InlineData("reemplazo", false, false)]
        [InlineData("reemplazo", true, true)]
        [InlineData("anexado", false, false)]
        [InlineData("anexado", false, true)]
        public async Task OperacionNueva_DuranteLaRestauracionDeUnaEscrituraInterrumpida_FallaAlMomentoSinTocarNada(string operacion, bool isSudoPrimera, bool isSudoSegunda)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(40));
            (string carpeta, string archivo, _) = await PrepararEscrituraEnElSitioAsync(herramientas);
            await SustituirComandoAsync(herramientas, "cat", CatConRestauracionLenta(6));
            if (isSudoPrimera)
                await SimularSudoAsync(herramientas);
            Task<ResultadoEscritura> primera = herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo contenido\n", overwrite: true, sudo: isSudoPrimera);
            await EsperasPrueba.EsperarHastaAsync(() => HayProcesos($"{Ruta("bin/cat")} -- {carpeta}/.f.txt.tmp."), TimeSpan.FromSeconds(20));
            _ = await herramientas.DesconectarAsync(Destino);
            NativeSshException errorPrimera = (await FluentActions.Awaiting(() => primera).Should().ThrowAsync<NativeSshException>()).Which;
            await EsperasPrueba.EsperarHastaAsync(() => HayProcesos($"{Ruta("bin/cat")} -- {carpeta}/.f.txt.orig."), TimeSpan.FromSeconds(10));
            if (operacion == "atomica")
                File.Delete(Path.Combine(carpeta, "f2.txt"));
            // El shell es nuevo (sin el PATH del cat falso); el sudo simulado vuelve a poner su carpeta en el PATH.
            File.Delete(Ruta("bin/cat"));
            if (isSudoSegunda)
                await SimularSudoAsync(herramientas);
            string[] entradas = Directory.GetFileSystemEntries(carpeta);

            // Act: con la restauración en marcha.
            Exception? errorSegunda = await Record.ExceptionAsync(() => operacion switch
            {
                "reemplazo" => herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "original completo", "SEGUNDA", sudo: isSudoSegunda),
                "anexado" => herramientas.EscribirArchivoAsync(Destino, archivo, "SEGUNDA\n", append: true, sudo: isSudoSegunda),
                _ => herramientas.EscribirArchivoAsync(Destino, archivo, "SEGUNDA\n", overwrite: true, sudo: isSudoSegunda)
            });
            bool isRestaurandoTodavia = HayProcesos($"{Ruta("bin/cat")} -- {carpeta}/.f.txt.orig.");
            string[] entradasTrasSegunda = Directory.GetFileSystemEntries(carpeta);
            await EsperasPrueba.EsperarHastaAsync(() => !HayProcesos(Ruta("bin/cat")), TimeSpan.FromSeconds(30));
            await EsperasPrueba.EsperarHastaAsync(() => IsCandadoLibre(archivo), TimeSpan.FromSeconds(10));
            string[] entradasTrasRestaurar = Directory.GetFileSystemEntries(carpeta);
            string trasRestaurar = await File.ReadAllTextAsync(archivo);
            ResultadoEscritura tercera = await herramientas.EscribirArchivoAsync(Destino, archivo, "TERCERA\n", overwrite: true, sudo: isSudoSegunda);

            // Assert
            _ = isRestaurandoTodavia.Should().BeTrue("la segunda operación tenía que llegar durante la restauración");
            _ = errorSegunda.Should().BeOfType<NativeSshException>();
            _ = errorSegunda!.Message.Should().Be($"Error: {TextoCandadoOcupado} {archivo} (otro proceso tiene tomado su candado, flock: otra operación sobre el archivo o un programa que lo tiene abierto con candado, como una aplicación .NET). No se ha modificado: reintenta cuando termine.");
            _ = entradasTrasSegunda.Should().BeEquivalentTo(entradas, "la segunda operación no puede crear un temporal ni una copia");
            _ = trasRestaurar.Should().Be("original completo\n");
            _ = entradasTrasRestaurar.Should().HaveCount(entradas.Length - 2, "la restauración borra la copia y el temporal al terminar");
            _ = tercera.Mensaje.Should().StartWith("Archivo escrito correctamente");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("TERCERA\n");
            _ = errorPrimera.Message.Should().Contain("las escrituras, los anexados y los reemplazos nuevos sobre él fallan al momento");
        }

        // El candado nunca queda huérfano: lo suelta el núcleo al cerrarse el último descriptor que lo comparte. Tras un
        // SIGKILL al script y a su 'cat', la siguiente escritura funciona al momento. Con SIGKILL solo al bash del script,
        // el 'cat' que escribía o restauraba sigue vivo con el descriptor heredado y la exclusión se mantiene hasta que él
        // termina (sigue escribiendo en el archivo). Tras caer la conexión, el script restaura el original por su cuenta y lo
        // suelta al salir.
        [Theory]
        [InlineData("sigkill_todo", false)]
        [InlineData("sigkill_todo", true)]
        [InlineData("sigkill_bash", false)]
        [InlineData("sigkill_bash_restaurando", false)]
        [InlineData("caida", false)]
        [InlineData("caida", true)]
        public async Task EscrituraEnElSitioInterrumpida_ElCandadoSeSueltaAlTerminarSusProcesos(string interrupcion, bool isSudo)
        {
            // Arrange
            string[] procesosAjenos = _sshd.ProcesosDeConexion();
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(40));
            (string carpeta, string archivo, _) = await PrepararEscrituraEnElSitioAsync(herramientas);
            bool isRestaurando = interrupcion == "sigkill_bash_restaurando";
            bool isSoloBash = interrupcion.StartsWith("sigkill_bash", StringComparison.Ordinal);
            await SustituirComandoAsync(herramientas, "cat", isRestaurando ? CatFalso("exit 1", EsperaInterrumpible()) : CatFalso(EsperaInterrumpible()));
            if (isSudo)
                await SimularSudoAsync(herramientas);
            Task<ResultadoEscritura> escritura = herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo contenido\n", overwrite: true, sudo: isSudo);
            string catVivo = $"{Ruta("bin/cat")} -- {carpeta}/.f.txt.{(isRestaurando ? "orig" : "tmp")}.";
            await EsperasPrueba.EsperarHastaAsync(() => HayProcesos(catVivo), TimeSpan.FromSeconds(20));
            string cat = Bash($"pgrep -f -- '{catVivo}' | head -n 1");
            string script = Bash($"ps -o ppid= -p {cat}");
            bool isCandadoTomado = !IsCandadoLibre(archivo);

            // Act
            string matar = interrupcion switch
            {
                "caida" => $"kill -9 {string.Join(' ', _sshd.ProcesosDeConexion().Except(procesosAjenos))}",
                "sigkill_todo" => $"kill -KILL {script} {cat}",
                _ => $"kill -KILL {script}"
            };
            _ = Bash(matar);
            _ = await Record.ExceptionAsync(() => escritura);
            Exception? mientrasEscribeElCat = null;
            if (isSoloBash)
            {
                await Task.Delay(500);
                _ = IsCandadoLibre(archivo).Should().BeFalse("el 'cat' que escribía o restauraba sigue vivo con el descriptor del candado");

                // Sin reiniciar el shell, que con su SIGHUP mataría al 'cat'.
                mientrasEscribeElCat = await Record.ExceptionAsync(() => herramientas.EscribirArchivoAsync(Destino, archivo, "SIGUIENTE\n", overwrite: true));
                _ = Bash($"kill -KILL {cat}");
            }

            await EsperasPrueba.EsperarHastaAsync(() => IsCandadoLibre(archivo), TimeSpan.FromSeconds(15));
            string trasInterrupcion = await File.ReadAllTextAsync(archivo);
            File.Delete(Ruta("bin/cat"));
            _ = herramientas.ReiniciarShell(Destino);
            if (isSudo)
                await SimularSudoAsync(herramientas);
            ResultadoEscritura siguiente = await herramientas.EscribirArchivoAsync(Destino, archivo, "SIGUIENTE\n", overwrite: true, sudo: isSudo);

            // Assert
            _ = isCandadoTomado.Should().BeTrue();
            _ = trasInterrupcion.Should().Be(interrupcion switch
            {
                "caida" => "original completo\n",
                "sigkill_bash_restaurando" => "ori",
                _ => "nue"
            });
            if (isSoloBash)
                _ = mientrasEscribeElCat!.Message.Should().StartWith($"Error: {TextoCandadoOcupado} {archivo} ");
            _ = siguiente.Mensaje.Should().StartWith("Archivo escrito correctamente");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("SIGUIENTE\n");
        }

        // Cadena de herramientas del candado: flock de util-linux; si no funciona, python3; si tampoco, perl. Cada una debe
        // excluir de verdad: con el flock del archivo en manos de otro proceso (aquí, un 'flock ... sleep' lanzado por la
        // prueba), la escritura, el anexado y el reemplazo fallan sin tocarlo, y al soltarlo funcionan. Una herramienta que
        // no funciona (aquí, una falsa que sale con 127, como una orden que no existe) nunca cuenta como «ocupado»: se prueba
        // la siguiente. Sin ninguna, se escribe sin exclusión y la respuesta lo avisa.
        [Theory]
        [InlineData("flock", false)]
        [InlineData("flock", true)]
        [InlineData("python3", false)]
        [InlineData("python3", true)]
        [InlineData("perl", false)]
        [InlineData("ninguna", false)]
        public async Task CandadoEnManosDeOtroProceso_LasOperacionesFallanSinTocarYAlSoltarloFuncionan(string herramienta, bool isSudo)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(40));
            (string carpeta, string archivo, _) = await PrepararEscrituraEnElSitioAsync(herramientas);
            string[] inservibles = herramienta switch
            {
                "flock" => [],
                "python3" => ["flock"],
                "perl" => ["flock", "python3"],
                _ => ["flock", "python3", "perl"]
            };
            foreach (string nombre in inservibles)
                await SustituirComandoAsync(herramientas, nombre, "exit 127");
            if (isSudo)
                await SimularSudoAsync(herramientas);
            string[] entradas = Directory.GetFileSystemEntries(carpeta);
            using Process ajeno = Process.Start("flock", ["-n", archivo, "sleep", "37.5"]);
            await EsperasPrueba.EsperarHastaAsync(() => !IsCandadoLibre(archivo), TimeSpan.FromSeconds(10));

            try
            {
                // Act
                Exception? escritura = await Record.ExceptionAsync(() => herramientas.EscribirArchivoAsync(Destino, archivo, "ESCRITA\n", overwrite: true, sudo: isSudo));
                string trasEscritura = Bash($"cat '{archivo}'");
                Exception? anexado = await Record.ExceptionAsync(() => herramientas.EscribirArchivoAsync(Destino, archivo, "ANEXADA\n", append: true, sudo: isSudo));
                Exception? reemplazo = await Record.ExceptionAsync(() => herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "ESCRITA", "REEMPLAZADA", sudo: isSudo));
                string[] entradasConCandado = Directory.GetFileSystemEntries(carpeta);
                SoltarCandadoAjeno(ajeno);
                ResultadoEscritura libre = await herramientas.EscribirArchivoAsync(Destino, archivo, "LIBRE\n", overwrite: true, sudo: isSudo);
                ResultadoEscritura libreAnexado = await herramientas.EscribirArchivoAsync(Destino, archivo, "MAS\n", append: true, sudo: isSudo);

                // Assert
                if (herramienta == "ninguna")
                {
                    _ = escritura.Should().BeNull();
                    _ = anexado.Should().BeNull();
                    _ = reemplazo.Should().BeNull();
                    _ = trasEscritura.Should().Be("ESCRITA");
                    _ = libre.Mensaje.Should().Contain("El script no pudo tomar el candado del archivo (flock)").And.EndWith("La escritura no estuvo protegida frente a otra simultánea sobre el mismo archivo.");
                    _ = libreAnexado.Mensaje.Should().Contain("El script no pudo tomar el candado del archivo (flock)");
                }
                else
                {
                    string ocupado = $"Error: {TextoCandadoOcupado} {archivo} (otro proceso tiene tomado su candado, flock: otra operación sobre el archivo o un programa que lo tiene abierto con candado, como una aplicación .NET). No se ha modificado: reintenta cuando termine.";
                    _ = escritura!.Message.Should().Be(ocupado);
                    _ = anexado!.Message.Should().Be(ocupado);
                    _ = reemplazo!.Message.Should().Be(ocupado);
                    _ = trasEscritura.Should().Be("original completo");
                    _ = entradasConCandado.Should().BeEquivalentTo(entradas);
                    _ = libre.Mensaje.Should().NotContain("candado");
                    _ = libreAnexado.Mensaje.Should().Be($"Contenido anexado correctamente a: {archivo} (4 bytes).");
                }

                _ = (await File.ReadAllTextAsync(archivo)).Should().Be("LIBRE\nMAS\n");
            }
            finally
            {
                SoltarCandadoAjeno(ajeno);
            }
        }

        // El candado se liga al archivo que está en la ruta: si entre abrirlo y bloquearlo se sustituye por otro (un
        // renombrado encima), el candado del inodo viejo no excluye a nadie y se vuelve a tomar sobre el nuevo. Aquí un flock
        // falso hace la sustitución la primera vez; con el archivo nuevo en manos de otro proceso, la escritura falla sin
        // tocarlo; libre, se escribe en el nuevo.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ArchivoSustituidoMientrasSeTomaElCandado_SeVuelveATomarSobreElNuevo(bool isNuevoOcupado)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(40));
            string archivo = Ruta("f.txt");
            await File.WriteAllTextAsync(archivo, "viejo\n");
            string marca = Ruta("sustituido");
            string ocupar = isNuevoOcupado ? $"( eval \"exec $5<&-\"; exec /usr/bin/flock -n '{archivo}' sleep 21.5 ) </dev/null >/dev/null 2>&1 &\nsleep 0.5\n" : string.Empty;
            await SustituirComandoAsync(herramientas, "flock", $"if [ ! -e '{marca}' ]; then\n: > '{marca}'\nprintf 'nuevo\\n' > '{archivo}.otro'\nmv -f '{archivo}.otro' '{archivo}'\n{ocupar}fi\nexec /usr/bin/flock \"$@\"");

            try
            {
                // Act
                Exception? error = await Record.ExceptionAsync(() => herramientas.EscribirArchivoAsync(Destino, archivo, "ESCRITA\n", overwrite: true));
                string contenido = Bash($"cat '{archivo}'");

                // Assert
                _ = File.Exists(marca).Should().BeTrue();
                if (isNuevoOcupado)
                {
                    _ = error!.Message.Should().StartWith($"Error: {TextoCandadoOcupado} {archivo} ");
                    _ = contenido.Should().Be("nuevo");
                }
                else
                {
                    _ = error.Should().BeNull();
                    _ = contenido.Should().Be("ESCRITA");
                }
            }
            finally
            {
                _ = Bash("pkill -f -- '^sleep 21[.]5$'; true");
            }
        }

        // Un archivo con permiso de escritura pero no de lectura (0200) también lleva candado: se abre para anexar, sin
        // truncarlo (flock no depende del modo de apertura), porque todavía se le puede anexar y, con sudo, escribir en el
        // sitio. Durante la restauración de una escritura en el sitio con sudo interrumpida, un anexado sin sudo (que no
        // puede leerlo) falla al momento sin tocarlo; con sudo, también. Antes, sin permiso de lectura no se tomaba el
        // candado: el anexado se confirmaba como correcto y la restauración lo borraba (auditoría del A-sexies, sonda X01).
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AnexadoSobreUnArchivoSinPermisoDeLectura_DuranteLaRestauracion_FallaAlMomentoSinTocarlo(bool isSudoAnexado)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(40));
            (string carpeta, string archivo, _) = await PrepararEscrituraEnElSitioAsync(herramientas);
            _ = Bash($"chmod 0200 '{archivo}'");
            await SustituirComandoAsync(herramientas, "cat", CatConRestauracionLenta(6));
            await SimularSudoAsync(herramientas);
            Task<ResultadoEscritura> primera = herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo contenido\n", overwrite: true, sudo: true);
            await EsperasPrueba.EsperarHastaAsync(() => HayProcesos($"{Ruta("bin/cat")} -- {carpeta}/.f.txt.tmp."), TimeSpan.FromSeconds(20));
            _ = await herramientas.DesconectarAsync(Destino);
            _ = await Record.ExceptionAsync(() => primera);
            await EsperasPrueba.EsperarHastaAsync(() => HayProcesos($"{Ruta("bin/cat")} -- {carpeta}/.f.txt.orig."), TimeSpan.FromSeconds(10));
            File.Delete(Ruta("bin/cat"));
            if (isSudoAnexado)
                await SimularSudoAsync(herramientas);
            string[] entradas = Directory.GetFileSystemEntries(carpeta);

            // Act
            Exception? error = await Record.ExceptionAsync(() => herramientas.EscribirArchivoAsync(Destino, archivo, "ANEXADO\n", append: true, sudo: isSudoAnexado));
            bool isRestaurandoTodavia = HayProcesos($"{Ruta("bin/cat")} -- {carpeta}/.f.txt.orig.");
            string[] entradasTrasAnexado = Directory.GetFileSystemEntries(carpeta);
            await EsperasPrueba.EsperarHastaAsync(() => !HayProcesos(Ruta("bin/cat")), TimeSpan.FromSeconds(30));
            await EsperasPrueba.EsperarHastaAsync(() => IsCandadoLibre(archivo), TimeSpan.FromSeconds(10));
            ResultadoEscritura libre = await herramientas.EscribirArchivoAsync(Destino, archivo, "ANEXADO\n", append: true, sudo: isSudoAnexado);
            _ = Bash($"chmod 0600 '{archivo}'");

            // Assert
            _ = isRestaurandoTodavia.Should().BeTrue("el anexado tenía que llegar durante la restauración");
            _ = error.Should().BeOfType<NativeSshException>();
            _ = error!.Message.Should().StartWith($"Error: {TextoCandadoOcupado} {archivo} ");
            _ = entradasTrasAnexado.Should().BeEquivalentTo(entradas, "el anexado no puede tocar nada");
            _ = libre.Mensaje.Should().Be($"Contenido anexado correctamente a: {archivo} (8 bytes).", "terminada la restauración, el anexado toma el candado y no avisa de nada");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("original completo\nANEXADO\n");
        }

        // 'flock ARCHIVO sleep 37.5' de la prueba: el candado lo tiene también el 'sleep', que hereda el descriptor.
        private static void SoltarCandadoAjeno(Process ajeno)
        {
            _ = Bash("pkill -f -- '^sleep 37[.]5$'; true");
            ajeno.WaitForExit();
        }

        // Nadie tiene el flock del archivo. Las pruebas leen el archivo después: .NET toma un flock compartido al abrirlo
        // (así emula FileShare en Unix) y falla mientras un script tenga el exclusivo. Se abre para leer o, si no se puede
        // (archivo 0200), para anexar, como el script.
        private static bool IsCandadoLibre(string archivo) => Bash($"{{ exec 9<'{archivo}' || exec 9>>'{archivo}'; }} 2>/dev/null && flock -n -E 75 9; echo $?") == "0";
    }
}
