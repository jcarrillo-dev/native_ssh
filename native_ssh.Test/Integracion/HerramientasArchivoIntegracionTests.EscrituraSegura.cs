using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Garantías de seguridad de datos de las escrituras: tipo del destino, atributos que se conservan, escritura en el sitio
    // con copia de seguridad y comprobación de que el archivo no cambió entre la lectura y la escritura.
    public sealed partial class HerramientasArchivoIntegracionTests
    {
        public static TheoryData<string, string, bool> DestinosNoRegulares
        {
            get
            {
                TheoryData<string, string, bool> datos = [];
                foreach (string destino in new[] { "fifo", "enlace_fifo", "enlace_dev_null" })
                    foreach (string operacion in new[] { "sobrescribir", "reemplazar", "anexar" })
                        foreach (bool isSudo in new[] { false, true })
                            datos.Add(destino, operacion, isSudo);
                return datos;
            }
        }

        // Una FIFO, un socket o un dispositivo no se sustituyen por un archivo regular ni se abren para escribir o leer: una
        // FIFO sin lector bloquearía hasta el timeout y /dev/null se "escribiría" sin guardar nada. Error sin tocar nada.
        [Theory]
        [MemberData(nameof(DestinosNoRegulares))]
        public async Task Escrituras_DestinoQueNoEsUnArchivoRegular_FallanSinTocarlo(string destino, string operacion, bool isSudo)
        {
            // Arrange
            string carpeta = Ruta("datos");
            _ = Directory.CreateDirectory(carpeta);
            string fifo = Path.Combine(carpeta, "fifo");
            _ = Bash($"mkfifo '{fifo}'");
            string ruta = Path.Combine(carpeta, destino);
            if (destino == "enlace_fifo")
                _ = File.CreateSymbolicLink(ruta, "fifo");
            else if (destino == "enlace_dev_null")
                _ = File.CreateSymbolicLink(ruta, "/dev/null");
            string[] entradas = Directory.GetFileSystemEntries(carpeta);
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(10));
            if (isSudo)
                await SimularSudoAsync(herramientas);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => operacion switch
                {
                    "sobrescribir" => herramientas.EscribirArchivoAsync(Destino, ruta, "x\n", overwrite: true, sudo: isSudo),
                    "reemplazar" => herramientas.ReemplazarContenidoArchivoAsync(Destino, ruta, "x", "y", sudo: isSudo),
                    _ => herramientas.EscribirArchivoAsync(Destino, ruta, "x\n", append: true, sudo: isSudo)
                })
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("no es un archivo regular").And.Contain("No se ha modificado");
            _ = Bash($"[ -p '{fifo}' ] && echo fifo").Should().Be("fifo");
            _ = Directory.GetFileSystemEntries(carpeta).Should().BeEquivalentTo(entradas);
            if (destino != "fifo")
                _ = new FileInfo(ruta).LinkTarget.Should().Be(destino == "enlace_fifo" ? "fifo" : "/dev/null");
        }

        // PATH del shell remoto con todo /usr/bin salvo las herramientas indicadas.
        [SupportedOSPlatform("linux")]
        private async Task QuitarDelPathAsync(HerramientasSsh herramientas, params string[] excluidas)
        {
            string granja = GranjaBinarios.Crear(Ruta("granja"), excluidas);
            ResultadoComando export = await herramientas.EjecutarAsync(Destino, $"export PATH='{granja}'; hash -r", esperarSegundos: 10);
            _ = export.CodigoSalida.Should().Be(0);
        }

        // Los atributos extendidos (y las ACL) se copian al temporal con cp de GNU coreutils y se renombra encima: la
        // escritura es atómica aunque no haya getfattr ni getfacl para detectarlos. La fecha de modificación es la de la
        // escritura, no la del original.
        [Theory]
        [InlineData("getfattr", false, false)]
        [InlineData("getfattr", true, false)]
        [InlineData("getfattr getfacl", false, false)]
        [InlineData("getfattr getfacl", true, false)]
        [InlineData("getfattr", false, true)]
        [InlineData("getfattr getfacl", true, true)]
        [SupportedOSPlatform("linux")]
        public async Task Sobrescribir_ConAtributosExtendidosSinGetfattrEnElPath_LosConservaYEsAtomica(string excluidas, bool isReplace, bool hasAcl)
        {
            // Arrange
            string carpeta = Ruta("datos");
            _ = Directory.CreateDirectory(carpeta);
            string archivo = Path.Combine(carpeta, "con_atributos.txt");
            await File.WriteAllTextAsync(archivo, "antes\n");
            _ = Bash($"chmod 640 '{archivo}' && setfattr -n user.marca -v valor '{archivo}' && touch -d '2001-01-01' '{archivo}'");
            if (hasAcl)
                _ = Bash($"setfacl -m u:nobody:r '{archivo}'");
            string atributos = AclYAtributos(archivo);
            HerramientasSsh herramientas = Herramientas();
            await QuitarDelPathAsync(herramientas, excluidas.Split(' '));
            DateTime antesDeEscribir = DateTime.UtcNow.AddSeconds(-2);

            // Act
            ResultadoEscritura resultado = isReplace
                ? await herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "antes", "despues")
                : await herramientas.EscribirArchivoAsync(Destino, archivo, "despues\n", overwrite: true);

            // Assert
            _ = atributos.Should().Contain("user.marca=\"valor\"");
            _ = hasAcl ? atributos.Should().Contain("user:nobody:r--") : atributos.Should().NotContain("nobody");
            _ = resultado.IsAtomica.Should().BeTrue();
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("despues\n");
            _ = AclYAtributos(archivo).Should().Be(atributos);
            _ = Stat(archivo).Should().StartWith("640 ");
            _ = File.GetLastWriteTimeUtc(archivo).Should().BeAfter(antesDeEscribir);
            _ = Directory.GetFileSystemEntries(carpeta).Should().ContainSingle();
        }

        // Sin cp de GNU coreutils (sin --attributes-only, como el de busybox) no se pueden copiar ni comprobar los
        // atributos: se escribe en el sitio, que los conserva todos.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Sobrescribir_SinCpDeGnuCoreutils_EscribeEnElSitioConservandoLosAtributos(bool isReplace)
        {
            // Arrange
            string archivo = Ruta("con_atributos.txt");
            await File.WriteAllTextAsync(archivo, "antes\n");
            _ = Bash($"setfattr -n user.marca -v valor '{archivo}'");
            string atributos = AclYAtributos(archivo);
            string stat = Stat(archivo);
            HerramientasSsh herramientas = Herramientas();
            await SustituirComandoAsync(herramientas, "cp", "case \" $* \" in *' --attributes-only '*) echo \"cp: unrecognized option '--attributes-only'\" >&2; exit 1 ;; esac\nexec /usr/bin/cp \"$@\"");

            // Act
            ResultadoEscritura resultado = isReplace
                ? await herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "antes", "despues")
                : await herramientas.EscribirArchivoAsync(Destino, archivo, "despues\n", overwrite: true);

            // Assert
            _ = resultado.IsAtomica.Should().BeFalse();
            _ = resultado.Mensaje.Should().Contain("no atómica");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("despues\n");
            _ = AclYAtributos(archivo).Should().Be(atributos);
            _ = Stat(archivo).Should().Be(stat);
        }

        // TMPDIR propio en el shell remoto, para comprobar qué temporales se crean fuera del directorio del archivo.
        private async Task<string> FijarTmpdirAsync(HerramientasSsh herramientas)
        {
            string tmpdir = Ruta("tmpdir");
            _ = Directory.CreateDirectory(tmpdir);
            ResultadoComando export = await herramientas.EjecutarAsync(Destino, $"export TMPDIR='{tmpdir}'", esperarSegundos: 10);
            _ = export.CodigoSalida.Should().Be(0);
            return tmpdir;
        }

        // Un directorio sin permiso de escritura no admite el temporal ni el renombrado, pero el archivo sí se puede
        // escribir: se sobrescribe en el sitio (con el temporal en ${TMPDIR:-/tmp}) en lugar de fallar. Un archivo nuevo
        // no se puede crear ahí.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Sobrescribir_DirectorioSinPermisoDeEscrituraConArchivoEscribible_EscribeEnElSitio(bool isReplace)
        {
            // Arrange
            string carpeta = Ruta("solo_lectura");
            _ = Directory.CreateDirectory(carpeta);
            string archivo = Path.Combine(carpeta, "f.txt");
            await File.WriteAllTextAsync(archivo, "antes\n");
            string stat = Stat(archivo);
            _ = Bash($"chmod 555 '{carpeta}'");
            HerramientasSsh herramientas = Herramientas();
            string tmpdir = await FijarTmpdirAsync(herramientas);

            // Act
            ResultadoEscritura resultado = isReplace
                ? await herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "antes", "despues")
                : await herramientas.EscribirArchivoAsync(Destino, archivo, "despues\n", overwrite: true);
            NativeSshException errorNuevo = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, Path.Combine(carpeta, "nuevo.txt"), "x\n", overwrite: true))
                .Should().ThrowAsync<NativeSshException>()).Which;
            _ = Bash($"chmod 755 '{carpeta}'");

            // Assert
            _ = resultado.IsAtomica.Should().BeFalse();
            _ = resultado.Mensaje.Should().Contain("no atómica");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("despues\n");
            _ = Stat(archivo).Should().Be(stat);
            _ = errorNuevo.Message.Should().Contain("no se pudo crear el archivo temporal");
            _ = Directory.GetFileSystemEntries(carpeta).Should().ContainSingle();
            _ = Directory.GetFileSystemEntries(tmpdir).Should().BeEmpty();
        }

        // Archivo con ACL propia en un directorio con ACL por defecto: el temporal hereda la del directorio, pero cp le pone la
        // del original y la comprobación ve que coinciden: atómica, con la ACL y el modo del original.
        [Fact]
        public async Task Sobrescribir_ArchivoConAclEnDirectorioConAclPorDefecto_ConservaSuAclYEsAtomica()
        {
            // Arrange
            string carpeta = Ruta("con_acl_defecto");
            _ = Directory.CreateDirectory(carpeta);
            string archivo = Path.Combine(carpeta, "f.txt");
            await File.WriteAllTextAsync(archivo, "antes\n");
            _ = Bash($"chmod 640 '{archivo}' && setfacl -m u:nobody:r '{archivo}' && setfacl -d -m u:nobody:rwx '{carpeta}'");
            string atributos = AclYAtributos(archivo);
            string stat = Stat(archivo);
            HerramientasSsh herramientas = Herramientas();

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(Destino, archivo, "despues\n", overwrite: true);

            // Assert
            _ = atributos.Should().Contain("user:nobody:r--").And.NotContain("rwx");
            _ = resultado.IsAtomica.Should().BeTrue();
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("despues\n");
            _ = AclYAtributos(archivo).Should().Be(atributos);
            _ = Stat(archivo).Split(' ')[..2].Should().Equal(stat.Split(' ')[..2]);
            _ = Directory.GetFileSystemEntries(carpeta).Should().ContainSingle();
        }

        // Interrumpida antes de colocar el temporal (un 'cp' lento; Ctrl+C al vencer el timeout o al cancelar el token):
        // el original no se toca y la trampa de salida borra el temporal.
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task Sobrescribir_InterrumpidaAntesDeColocarElTemporal_NoTocaElArchivoNiDejaTemporales(bool isSudo, bool isPorToken)
        {
            // Arrange
            string carpeta = Ruta("datos");
            _ = Directory.CreateDirectory(carpeta);
            string archivo = Path.Combine(carpeta, "f.txt");
            await File.WriteAllTextAsync(archivo, "antes\n");
            string stat = Stat(archivo);
            HerramientasSsh herramientas = Herramientas(timeout: isPorToken ? null : TimeSpan.FromSeconds(2));
            string marca = Ruta("marca");
            await SustituirComandoAsync(herramientas, "cp", $"case \" $* \" in *' --attributes-only '*) : > '{marca}'; sleep 30 ;; esac\nexec /usr/bin/cp \"$@\"");
            if (isSudo)
                await SimularSudoAsync(herramientas);
            using CancellationTokenSource cancelacion = new(isPorToken ? TimeSpan.FromSeconds(2) : Timeout.InfiniteTimeSpan);

            // Act
            Exception? error = await Record.ExceptionAsync(() => herramientas.EscribirArchivoAsync(Destino, archivo, "despues\n", overwrite: true, sudo: isSudo, cancellationToken: cancelacion.Token));

            // Assert
            _ = error.Should().BeAssignableTo(isPorToken ? typeof(OperationCanceledException) : typeof(NativeSshException));
            _ = File.Exists(marca).Should().BeTrue("el temporal ya existía cuando se interrumpió");
            await EsperasPrueba.EsperarHastaAsync(() => Directory.GetFileSystemEntries(carpeta).Length == 1, TimeSpan.FromSeconds(10));
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("antes\n");
            _ = Stat(archivo).Should().Be(stat);
        }

        // Archivo con un enlace duro (se sobrescribe en el sitio) en su propia carpeta, con TMPDIR propio.
        private async Task<(string Carpeta, string Archivo, string Tmpdir)> PrepararEscrituraEnElSitioAsync(HerramientasSsh herramientas)
        {
            string carpeta = Ruta("datos");
            _ = Directory.CreateDirectory(carpeta);
            string archivo = Path.Combine(carpeta, "f.txt");
            await File.WriteAllTextAsync(archivo, "original completo\n");
            _ = Bash($"ln '{archivo}' '{Path.Combine(carpeta, "f2.txt")}'");
            return (carpeta, archivo, await FijarTmpdirAsync(herramientas));
        }

        // 'cat' falso para la escritura en el sitio: al copiar el contenido nuevo (el temporal, '*.tmp.*') o la copia de
        // seguridad ('*.orig.*') sobre el destino escribe solo 3 bytes y después ejecuta la acción indicada (null = el cat
        // real). Cualquier otra copia, como la que hace la copia de seguridad, usa el cat real.
        private static string CatFalso(string? accionEscritura, string? accionRestauracion = null) =>
            $"case \"$2\" in\n" +
            (accionEscritura is null ? string.Empty : $"  *.tmp.*) head -c 3 -- \"$2\"; {accionEscritura} ;;\n") +
            (accionRestauracion is null ? string.Empty : $"  *.orig.*) head -c 3 -- \"$2\"; {accionRestauracion} ;;\n") +
            "esac\nexec /usr/bin/cat \"$@\"";

        // Acción del 'cat' falso que se queda esperando hasta 20 s (un 'read' de bash sobre una FIFO sin escritor, en el
        // propio proceso del falso, que así se puede localizar con pgrep) y después falla: solo termina antes si una señal
        // lo mata, y nunca da por buena una copia a medias.
        private string EsperaInterrumpible()
        {
            string fifo = Ruta("espera");
            if (!File.Exists(fifo))
                _ = Bash($"mkfifo '{fifo}'");
            return $"read -t 20 -r _ <> '{fifo}'; exit 1";
        }

        // Procesos cuya línea de órdenes contiene 'texto' (sin pasar por un shell, que también lo contendría).
        private static bool HayProcesos(string texto)
        {
            ProcessStartInfo inicio = new("pgrep", ["-f", "--", texto]) { RedirectStandardOutput = true };
            using Process proceso = Process.Start(inicio)!;
            string salida = proceso.StandardOutput.ReadToEnd();
            proceso.WaitForExit();
            return salida.Trim().Length > 0;
        }

        // La escritura en el sitio trunca el destino: si falla a mitad, el original se restaura desde la copia de seguridad
        // hecha antes (mktemp 0600 en su directorio) y no queda ninguna de las dos copias.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EscrituraEnElSitio_FallaAMitad_RestauraElOriginalYNoDejaCopias(bool isReplace)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            (string carpeta, string archivo, string tmpdir) = await PrepararEscrituraEnElSitioAsync(herramientas);
            string stat = Stat(archivo);
            await SustituirComandoAsync(herramientas, "cat", CatFalso("exit 1"));

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => isReplace
                    ? herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "original", "nuevo")
                    : herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo\n", overwrite: true))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("falló la escritura en el sitio").And.Contain("se ha restaurado su contenido original");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("original completo\n");
            _ = Stat(archivo).Split(' ')[2].Should().Be(stat.Split(' ')[2]);
            _ = Directory.GetFileSystemEntries(carpeta).Should().HaveCount(2);
            _ = Directory.GetFileSystemEntries(tmpdir).Should().BeEmpty();
        }

        // Si tampoco se puede restaurar, no se borra nada: el error da las rutas de la copia del original y del contenido
        // nuevo, que siguen ahí.
        [Fact]
        public async Task EscrituraEnElSitio_FallanLaEscrituraYLaRestauracion_ConservaAmbasCopiasYDaSusRutas()
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            (string carpeta, string archivo, _) = await PrepararEscrituraEnElSitioAsync(herramientas);
            await SustituirComandoAsync(herramientas, "cat", CatFalso("exit 1", "exit 1"));

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo contenido\n", overwrite: true))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            string copia = Directory.GetFiles(carpeta, ".f.txt.orig.*").Should().ContainSingle().Which;
            string nuevo = Directory.GetFiles(carpeta, ".f.txt.tmp.*").Should().ContainSingle().Which;
            _ = (await File.ReadAllTextAsync(copia)).Should().Be("original completo\n");
            _ = (await File.ReadAllTextAsync(nuevo)).Should().Be("nuevo contenido\n");
            _ = error.Message.Should().Contain("no se pudo restaurar").And.Contain(copia).And.Contain(nuevo);
            _ = Bash($"stat -c %a '{copia}'").Should().Be("600");
        }

        // Interrumpida a mitad (Ctrl+C al vencer el timeout), la trampa de salida restaura el original desde la copia.
        [Fact]
        public async Task EscrituraEnElSitio_InterrumpidaAMitadPorElTimeout_RestauraElOriginal()
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(2));
            (string carpeta, string archivo, string tmpdir) = await PrepararEscrituraEnElSitioAsync(herramientas);
            await SustituirComandoAsync(herramientas, "cat", CatFalso(EsperaInterrumpible()));

            // Act
            _ = await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo\n", overwrite: true))
                .Should().ThrowAsync<NativeSshException>();

            // Assert: el script restaura y limpia al salir, tras el Ctrl+C.
            await EsperasPrueba.EsperarHastaAsync(() => Directory.GetFileSystemEntries(carpeta).Length == 2, TimeSpan.FromSeconds(10));
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("original completo\n");
            _ = Directory.GetFileSystemEntries(tmpdir).Should().BeEmpty();
        }

        // Falla la escritura en el sitio y la restauración se interrumpe (Ctrl+C al vencer el timeout; la trampa la reintenta
        // y también se cuelga, hasta que el cierre del canal lo mata todo): la copia del original no se borra nunca mientras
        // el destino no esté restaurado, ni el temporal con el contenido nuevo.
        [Fact]
        public async Task EscrituraEnElSitio_FallaLaEscrituraYSeInterrumpeLaRestauracion_ConservaLaCopia()
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(2));
            (string carpeta, string archivo, _) = await PrepararEscrituraEnElSitioAsync(herramientas);
            await SustituirComandoAsync(herramientas, "cat", CatFalso("exit 1", EsperaInterrumpible()));

            // Act
            _ = await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo contenido\n", overwrite: true))
                .Should().ThrowAsync<NativeSshException>();
            await EsperasPrueba.EsperarHastaAsync(() => !HayProcesos(Ruta("bin/cat")), TimeSpan.FromSeconds(15));
            ResultadoComando siguiente = await herramientas.EjecutarAsync(Destino, "echo vivo", esperarSegundos: 10);

            // Assert
            string copia = Directory.GetFiles(carpeta, ".f.txt.orig.*").Should().ContainSingle().Which;
            string nuevo = Directory.GetFiles(carpeta, ".f.txt.tmp.*").Should().ContainSingle().Which;
            _ = (await File.ReadAllTextAsync(copia)).Should().Be("original completo\n");
            _ = (await File.ReadAllTextAsync(nuevo)).Should().Be("nuevo contenido\n");
            _ = siguiente.Salida.Should().Be("vivo\n");
        }

        // Sin copia de seguridad no se escribe en el sitio: si no se puede crear (ni en el directorio ni en TMPDIR), error
        // sin tocar nada.
        [Fact]
        public async Task EscrituraEnElSitio_SinPoderCrearLaCopiaDeSeguridad_FallaSinTocarlo()
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            (string carpeta, string archivo, string tmpdir) = await PrepararEscrituraEnElSitioAsync(herramientas);
            string stat = Stat(archivo);
            await SustituirComandoAsync(herramientas, "mktemp", "case \"$*\" in *.orig.*|*.ns_original.*) exit 1 ;; esac\nexec /usr/bin/mktemp \"$@\"");

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo\n", overwrite: true))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("copia de seguridad").And.Contain("No se ha modificado");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("original completo\n");
            _ = Stat(archivo).Should().Be(stat);
            _ = Directory.GetFileSystemEntries(carpeta).Should().HaveCount(2);
            _ = Directory.GetFileSystemEntries(tmpdir).Should().BeEmpty();
        }

        // Concurrencia en ssh_replace_file_content: si el archivo cambia (o desaparece) entre la lectura y la escritura, no
        // se escribe. La modificación se inyecta con un 'mktemp' falso, que solo usa el paso de escritura (una sola vez).
        // Sin sha256sum en el PATH se compara con cksum.
        [Theory]
        [InlineData("modificado", "", false)]
        [InlineData("modificado", "", true)]
        [InlineData("modificado", "sha256sum", false)]
        [InlineData("borrado", "", false)]
        [InlineData("borrado", "sha256sum", false)]
        [SupportedOSPlatform("linux")]
        public async Task ReemplazarContenido_ArchivoQueCambiaEntreLaLecturaYLaEscritura_NoEscribe(string cambio, string excluidas, bool isSudo)
        {
            // Arrange
            string carpeta = Ruta("datos");
            _ = Directory.CreateDirectory(carpeta);
            string archivo = Path.Combine(carpeta, "f.txt");
            await File.WriteAllTextAsync(archivo, "objetivo\nsegunda\n");
            HerramientasSsh herramientas = Herramientas();
            if (excluidas.Length > 0)
                await QuitarDelPathAsync(herramientas, excluidas);
            if (isSudo)
                await SimularSudoAsync(herramientas);
            string accion = cambio == "modificado" ? $"printf 'concurrente\\n' >> '{archivo}'" : $"rm -f -- '{archivo}'";
            string marca = Ruta("marca");
            await SustituirComandoAsync(herramientas, "mktemp", $"[ -e '{marca}' ] || {{ {accion}; : > '{marca}'; }}\nexec /usr/bin/mktemp \"$@\"");

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "objetivo", "cambiado", sudo: isSudo))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("cambió desde la lectura").And.Contain("No se ha modificado");
            _ = File.Exists(marca).Should().BeTrue();
            if (cambio == "modificado")
                _ = (await File.ReadAllTextAsync(archivo)).Should().Be("objetivo\nsegunda\nconcurrente\n");
            else
                _ = File.Exists(archivo).Should().BeFalse();
            _ = Directory.GetFileSystemEntries(carpeta).Should().HaveCount(cambio == "modificado" ? 1 : 0);
        }

        // Sin sha256sum, la comprobación con cksum deja pasar el archivo que no cambió.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        [SupportedOSPlatform("linux")]
        public async Task ReemplazarContenido_SinSha256sumEnElPath_CompruebaConCksumYEscribe(bool isEnElSitio)
        {
            // Arrange
            string carpeta = Ruta("datos");
            _ = Directory.CreateDirectory(carpeta);
            string archivo = Path.Combine(carpeta, "f.txt");
            await File.WriteAllTextAsync(archivo, "objetivo\nsegunda ñ\n");
            if (isEnElSitio)
                _ = Bash($"ln '{archivo}' '{Path.Combine(carpeta, "f2.txt")}'");
            HerramientasSsh herramientas = Herramientas();
            await QuitarDelPathAsync(herramientas, "sha256sum");

            // Act
            ResultadoEscritura resultado = await herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "objetivo", "cambiado");

            // Assert
            _ = resultado.IsAtomica.Should().Be(!isEnElSitio);
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("cambiado\nsegunda ñ\n");
        }

        private static readonly string[] HerramientasDeConservarAtributos = ["bash", "grep", "sort", "ls", "cut"];

        // Lógica de conservar_atributos aislada, con un PATH mínimo: el 'cp' de cada caso (real o falso) y las herramientas
        // de comprobación disponibles. SELinux se simula (no lo hay en esta máquina) con un 'stat' falso que da el contexto
        // de cada archivo desde '<archivo>.ctx'. "si" = el temporal tiene todos los atributos y puede renombrarse encima.
        [Theory]
        [InlineData("cp_real", "getfattr getfacl", true)]
        [InlineData("contexto_copiado", "getfattr getfacl", true)]
        [InlineData("contexto_no_copiable", "getfattr getfacl", false)]
        [InlineData("xattr_omitido", "getfattr getfacl", false)]
        [InlineData("xattr_omitido", "getfacl", true)]
        [InlineData("modo_distinto", "getfattr getfacl", false)]
        [InlineData("acl_heredada", "getfattr getfacl", false)]
        [InlineData("acl_heredada", "getfacl", false)]
        [InlineData("acl_heredada", "", false)]
        [InlineData("sin_attributes_only", "getfattr getfacl", false)]
        public async Task ConservarAtributos_SegunLoQueCopiaCpYLoQueSePuedeComprobar(string caso, string verificadores, bool isConservado)
        {
            // Arrange: original 0640 con un atributo extendido; temporal 0600 con el contenido nuevo.
            string original = Ruta("original.txt");
            string temporal = Ruta(".original.txt.tmp.abc123");
            await File.WriteAllTextAsync(original, "antes\n");
            await File.WriteAllTextAsync(temporal, "despues\n");
            _ = Bash($"chmod 640 '{original}' && chmod 600 '{temporal}' && setfattr -n user.marca -v valor '{original}'");
            if (caso is "contexto_copiado" or "contexto_no_copiable")
            {
                await File.WriteAllTextAsync(original + ".ctx", "system_u:object_r:httpd_config_t:s0");
                await File.WriteAllTextAsync(temporal + ".ctx", "unconfined_u:object_r:user_tmp_t:s0");
            }

            if (caso == "acl_heredada")
                _ = Bash($"setfacl -m u:nobody:rw '{temporal}'");

            string bin = Ruta("bin_aislado");
            _ = Directory.CreateDirectory(bin);
            foreach (string herramienta in HerramientasDeConservarAtributos.Concat(verificadores.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
                _ = File.CreateSymbolicLink(Path.Combine(bin, herramienta), Bash($"command -v {herramienta}"));
            string cp = Bash("command -v cp");
            string cuerpoCp = caso switch
            {
                "contexto_copiado" => $"case \"$2\" in *context*) '{cp}' -f -- \"$4.ctx\" \"$5.ctx\"; exec '{cp}' --attributes-only --preserve=mode,ownership,xattr -- \"$4\" \"$5\" ;; esac\nexec '{cp}' \"$@\"",
                "contexto_no_copiable" => $"case \"$2\" in *context*) echo 'cp: no se pudo conservar el contexto' >&2; exit 1 ;; esac\nexec '{cp}' \"$@\"",
                "xattr_omitido" => $"'{cp}' \"$@\" || exit\n'{Bash("command -v setfattr")}' -x user.marca -- \"$5\"",
                "modo_distinto" => $"'{cp}' \"$@\" || exit\n'{Bash("command -v chmod")}' 600 -- \"$5\"",
                "sin_attributes_only" => "echo \"cp: unrecognized option '--attributes-only'\" >&2; exit 1",
                _ => $"exec '{cp}' \"$@\""
            };
            string stat = Bash("command -v stat");
            await File.WriteAllTextAsync(Path.Combine(bin, "cp"), $"#!{Bash("command -v bash")}\n{cuerpoCp}\n");
            await File.WriteAllTextAsync(Path.Combine(bin, "stat"), $"#!{Bash("command -v bash")}\nif [ \"$1\" = -c ] && [ \"$2\" = %C ]; then\n  if [ -f \"$4.ctx\" ]; then '{Bash("command -v cat")}' -- \"$4.ctx\"; echo; exit 0; fi\n  echo '?'; exit 1\nfi\nexec '{stat}' \"$@\"\n");
            _ = Bash($"chmod 700 '{bin}/cp' '{bin}/stat'");
            string script = Ruta("conservar.sh");
            await File.WriteAllTextAsync(script, HerramientasSsh.DefinicionConservarAtributos + "\nif conservar_atributos \"$1\" \"$2\"; then echo si; else echo no; fi\n");

            // Act
            string resultado = Bash($"PATH='{bin}' '{bin}/bash' '{script}' '{original}' '{temporal}'");

            // Assert: sin getfattr, un atributo extendido que cp omita sin error no se detecta (se confía en cp).
            _ = resultado.Should().Be(isConservado ? "si" : "no");
            _ = (await File.ReadAllTextAsync(temporal)).Should().Be("despues\n");
            if (caso is "cp_real" or "contexto_copiado")
                _ = AclYAtributos(temporal).Should().Be(AclYAtributos(original));
        }

        // SELinux simulado (no lo hay en esta máquina): un 'stat' falso da el contexto de cada archivo desde '<archivo>.ctx'.
        // El temporal no tiene el contexto del original (cp no puede copiarlo sin SELinux en el núcleo): renombrarlo
        // cambiaría la etiqueta del archivo, así que se escribe en el sitio.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Sobrescribir_ContextoSelinuxQueNoSePuedeCopiar_EscribeEnElSitio(bool isReplace)
        {
            // Arrange
            string carpeta = Ruta("datos");
            _ = Directory.CreateDirectory(carpeta);
            string archivo = Path.Combine(carpeta, "web.conf");
            await File.WriteAllTextAsync(archivo, "antes\n");
            await File.WriteAllTextAsync(archivo + ".ctx", "system_u:object_r:httpd_config_t:s0");
            string stat = Stat(archivo);
            HerramientasSsh herramientas = Herramientas();
            await SustituirComandoAsync(herramientas, "stat", "if [ \"$1\" = -c ] && [ \"$2\" = %C ]; then\n  if [ -f \"$4.ctx\" ]; then cat -- \"$4.ctx\"; echo; exit 0; fi\n  echo '?'; exit 1\nfi\nexec /usr/bin/stat \"$@\"");

            // Act
            ResultadoEscritura resultado = isReplace
                ? await herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "antes", "despues")
                : await herramientas.EscribirArchivoAsync(Destino, archivo, "despues\n", overwrite: true);

            // Assert
            _ = resultado.IsAtomica.Should().BeFalse();
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("despues\n");
            _ = Stat(archivo).Should().Be(stat);
            _ = Directory.GetFileSystemEntries(carpeta).Should().BeEquivalentTo([archivo, archivo + ".ctx"]);
        }
    }
}
