using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
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
            ResultadoComando export = await herramientas.EjecutarAsync(Destino, $"export PATH='{granja}'; hash -r", esperarSegundos: 10).ComoComando();
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
            ResultadoComando export = await herramientas.EjecutarAsync(Destino, $"export TMPDIR='{tmpdir}'", esperarSegundos: 10).ComoComando();
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
            _ = error!.Message.Should().Contain($"No se llegó a modificar '{archivo}'").And.MatchRegex("petición [0-9a-f]{32}").And.NotContain("pueden quedar");
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

        // Acción del 'cat' falso que se queda esperando hasta 'segundos' (un 'read' de bash sobre una FIFO sin escritor, en
        // el propio proceso del falso, que así se puede localizar por su pid anotado) y después falla: solo termina antes si una
        // señal lo mata (no si la hereda ignorada, como en la trampa de salida), y nunca da por buena una copia a medias.
        private string EsperaInterrumpible(int segundos = 20)
        {
            string fifo = Ruta("espera");
            if (!File.Exists(fifo))
                _ = Bash($"mkfifo '{fifo}'");
            return $"read -t {segundos} -r _ <> '{fifo}'; exit 1";
        }

        // Comandos falsos vivos cuya línea de órdenes contiene 'texto' (el primero, el que arrancó antes).
        private string[] ProcesosFalsos(string texto) => _falsos.Vivos(o => o.Contains(texto, StringComparison.Ordinal));

        private bool HayProcesos(string texto) => ProcesosFalsos(texto).Length > 0;

        [GeneratedRegex("petición ([0-9a-f]{32})", RegexOptions.CultureInvariant)]
        private static partial Regex RegexIdPeticion();

        // Id de la petición que nombra el error de una escritura interrumpida.
        private static string IdDelError(string mensaje) => RegexIdPeticion().Match(mensaje).Groups[1].Value;

        // La petición interna de la escritura en ssh_status (ssh_output no da la de una petición interna).
        private static ResumenPeticion EstadoDe(HerramientasSsh herramientas, string id) => herramientas.Estado().Peticiones.Single(p => p.Id == id);

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
            _ = error.Message.Should().Contain("falló la escritura en el sitio").And.Contain("se ha restaurado su contenido original").And.NotContain("__NS_");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("original completo\n");
            _ = Stat(archivo).Split(' ')[2].Should().Be(stat.Split(' ')[2]);
            _ = Directory.GetFileSystemEntries(carpeta).Should().HaveCount(2);
            _ = Directory.GetFileSystemEntries(tmpdir).Should().BeEmpty();
        }

        // El 'cat' que escribe en el sitio muere por una señal que genera core (SIGXFSZ al superar 'ulimit -f 1', como en un
        // disco con cuota o un servicio con LimitFSIZE): bash avisa en su stderr de la muerte del hijo («línea N: PID
        // Superado el límite de tamaño de fichero (`core' generado) cat ...»), pero ese aviso no llega al error, que es el
        // del script: escritura fallida y original restaurado.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EscrituraEnElSitio_CatMuertoPorUnaSenalConCore_ElErrorNoLlevaElAvisoDeBash(bool isSudo)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            (string carpeta, string archivo, string tmpdir) = await PrepararEscrituraEnElSitioAsync(herramientas);
            string stat = Stat(archivo);
            await SustituirComandoAsync(herramientas, "cat", "case \"$2\" in *.tmp.*) ulimit -f 1 ;; esac\nexec /usr/bin/cat \"$@\"");
            if (isSudo)
                await SimularSudoAsync(herramientas);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, archivo, new string('n', 6000) + "\n", overwrite: true, sudo: isSudo))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be($"Error: falló la escritura en el sitio de {archivo}; se ha restaurado su contenido original.");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("original completo\n");
            _ = Stat(archivo).Should().Be(stat);
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
            _ = error.Message.Should().Contain("no se pudo restaurar").And.Contain(copia).And.Contain(nuevo).And.NotContain("__NS_");
            _ = Bash($"stat -c %a '{copia}'").Should().Be("600");
        }

        // Interrumpida a mitad (Ctrl+C al vencer el timeout), la trampa de salida restaura el original desde la copia y lo
        // dice el error, con el id de la petición.
        [Fact]
        public async Task EscrituraEnElSitio_InterrumpidaAMitadPorElTimeout_RestauraElOriginalYLoDice()
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(2));
            (string carpeta, string archivo, string tmpdir) = await PrepararEscrituraEnElSitioAsync(herramientas);
            await SustituirComandoAsync(herramientas, "cat", CatFalso(EsperaInterrumpible()));

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo\n", overwrite: true))
                .Should().ThrowAsync<NativeSshException>()).Which;
            ResumenPeticion salida = EstadoDe(herramientas, IdDelError(error.Message));

            // Assert: el script restaura y limpia al salir, tras el Ctrl+C.
            _ = error.Message.Should().Contain("TiempoAgotado").And.Contain($"Se restauró el contenido original de '{archivo}'").And.NotContain("__NS_");
            _ = salida.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("original completo\n");
            _ = Directory.GetFileSystemEntries(carpeta).Should().HaveCount(2);
            _ = Directory.GetFileSystemEntries(tmpdir).Should().BeEmpty();
        }

        // Falla la escritura en el sitio y la restauración se interrumpe (Ctrl+C al vencer el timeout): la trampa la reintenta,
        // ya sin que la corten las señales (tampoco el cierre del canal cuando el Ctrl+C no responde), y también se cuelga
        // hasta que el cat falso se rinde (6 s) y falla. La copia del original no se borra nunca mientras el destino no esté
        // restaurado, y el error da su ruta, la del contenido nuevo y el id de la petición.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EscrituraEnElSitio_FallaLaEscrituraYSeInterrumpeLaRestauracion_ConservaLaCopiaYDaSusRutas(bool isReplace)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(2));
            (string carpeta, string archivo, _) = await PrepararEscrituraEnElSitioAsync(herramientas);
            await SustituirComandoAsync(herramientas, "cat", CatFalso("exit 1", EsperaInterrumpible(6)));

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => isReplace
                    ? herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "original completo", "nuevo contenido")
                    : herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo contenido\n", overwrite: true))
                .Should().ThrowAsync<NativeSshException>()).Which;
            await EsperasPrueba.EsperarHastaAsync(() => !HayProcesos(Ruta("bin/cat")), TimeSpan.FromSeconds(15));
            ResumenPeticion salida = EstadoDe(herramientas, IdDelError(error.Message));
            ResultadoComando siguiente = await herramientas.EjecutarAsync(Destino, "echo vivo", esperarSegundos: 10).ComoComando();

            // Assert
            string copia = Directory.GetFiles(carpeta, ".f.txt.orig.*").Should().ContainSingle().Which;
            string nuevo = Directory.GetFiles(carpeta, ".f.txt.tmp.*").Should().ContainSingle().Which;
            _ = (await File.ReadAllTextAsync(copia)).Should().Be("original completo\n");
            _ = (await File.ReadAllTextAsync(nuevo)).Should().Be("nuevo contenido\n");
            _ = error.Message.Should().Contain("TiempoAgotado").And.Contain("a medio escribir").And.Contain($"'{copia}'").And.Contain($"'{nuevo}'").And.NotContain("__NS_");
            // La salida del script va en el error, pero nunca el contenido ni su base64.
            _ = error.Message.Should().NotContain("nuevo contenido").And.NotContain("original completo").And.NotContain(Convert.ToBase64String("nuevo contenido\n"u8.ToArray())[..12]);
            _ = salida.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            _ = siguiente.Salida.Should().Be("vivo\n");
        }

        // Se interrumpe la escritura en el sitio (al vencer el timeout o al cancelar el token) y falla la restauración: no se
        // borra nada y el error da las dos rutas y el id de la petición.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EscrituraEnElSitio_SeInterrumpeLaEscrituraYFallaLaRestauracion_ConservaAmbasCopiasYDaSusRutas(bool isPorToken)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: isPorToken ? null : TimeSpan.FromSeconds(2));
            (string carpeta, string archivo, _) = await PrepararEscrituraEnElSitioAsync(herramientas);
            await SustituirComandoAsync(herramientas, "cat", CatFalso(EsperaInterrumpible(), "exit 1"));
            using CancellationTokenSource cancelacion = new(isPorToken ? TimeSpan.FromSeconds(2) : Timeout.InfiniteTimeSpan);

            // Act
            Exception? error = await Record.ExceptionAsync(() => herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo contenido\n", overwrite: true, cancellationToken: cancelacion.Token));
            await EsperasPrueba.EsperarHastaAsync(() => !HayProcesos(Ruta("bin/cat")), TimeSpan.FromSeconds(15));

            // Assert
            _ = error.Should().BeAssignableTo(isPorToken ? typeof(OperationCanceledException) : typeof(NativeSshException));
            string copia = Directory.GetFiles(carpeta, ".f.txt.orig.*").Should().ContainSingle().Which;
            string nuevo = Directory.GetFiles(carpeta, ".f.txt.tmp.*").Should().ContainSingle().Which;
            _ = (await File.ReadAllTextAsync(copia)).Should().Be("original completo\n");
            _ = (await File.ReadAllTextAsync(nuevo)).Should().Be("nuevo contenido\n");
            _ = error!.Message.Should().Contain(isPorToken ? "Cancelado" : "TiempoAgotado").And.Contain("no se pudo restaurar").And.Contain($"'{copia}'").And.Contain($"'{nuevo}'").And.NotContain("__NS_");
            _ = EstadoDe(herramientas, IdDelError(error.Message)).Estado.Should().Be(isPorToken ? EstadoPeticionEnum.Cancelado : EstadoPeticionEnum.TiempoAgotado);
        }

        // Al escribir en un archivo, el núcleo le quita los bits setuid y setgid (este, si el grupo tiene ejecución): la
        // escritura en el sitio los devuelve (chmod) y lo comprueba. Un setgid sin ejecución de grupo no se toca.
        [Theory]
        [InlineData("4755", false)]
        [InlineData("6755", false)]
        [InlineData("2755", true)]
        [InlineData("2745", false)]
        [InlineData("4750", true)]
        public async Task EscrituraEnElSitio_ArchivoSetuidOSetgid_LeDevuelveElModo(string modo, bool isReplace)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            (_, string archivo, _) = await PrepararEscrituraEnElSitioAsync(herramientas);
            _ = Bash($"chmod {modo} '{archivo}'");

            // Act
            ResultadoEscritura resultado = isReplace
                ? await herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "original", "nuevo")
                : await herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo completo\n", overwrite: true);

            // Assert
            _ = resultado.IsAtomica.Should().BeFalse();
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("nuevo completo\n");
            _ = Stat(archivo).Should().StartWith($"{modo} ");
            _ = resultado.Mensaje.Should().Contain("setuid y setgid").And.NotContain("ATENCIÓN");
        }

        // Si la escritura en el sitio falla y se restaura el original, también se le devuelven los bits que el núcleo le
        // quitó al restaurarlo.
        [Fact]
        public async Task EscrituraEnElSitio_FallaEnUnArchivoSetuid_RestauraContenidoYModo()
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            (_, string archivo, _) = await PrepararEscrituraEnElSitioAsync(herramientas);
            _ = Bash($"chmod 4755 '{archivo}'");
            await SustituirComandoAsync(herramientas, "cat", CatFalso("exit 1"));

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo\n", overwrite: true))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("se ha restaurado su contenido original").And.NotContain("ATENCIÓN");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("original completo\n");
            _ = Stat(archivo).Should().StartWith("4755 ");
        }

        // Sin sudo, el propietario no puede devolver el setgid de un archivo de un grupo al que no pertenece (chmod lo ignora
        // en silencio): la respuesta lo dice. Necesita los subgid de la simulación de sudo para tener un grupo ajeno.
        [Fact]
        public async Task EscrituraEnElSitio_SetgidQueNoSePuedeDevolver_LoDiceLaRespuesta()
        {
            // Arrange: el archivo es del usuario y de un grupo ajeno (gid 2 de la simulación), 2775.
            if (!SudoSimulado.HasOtrosUsuarios)
                return;
            HerramientasSsh herramientas = Herramientas();
            (_, string archivo, _) = await PrepararEscrituraEnElSitioAsync(herramientas);
            _ = SudoSimulado.ComoRoot("chgrp 2 -- \"$1\" && chmod 2775 -- \"$1\"", archivo);

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo completo\n", overwrite: true);

            // Assert
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("nuevo completo\n");
            _ = Stat(archivo).Should().StartWith("775 ");
            _ = resultado.Mensaje.Should().Contain("ATENCIÓN").And.Contain("bit setgid").And.NotContain("bit setuid");
        }

        // El núcleo borra security.capability al escribir, incluso como root. Con sudo se devuelve (setfattr) y se comprueba;
        // sin sudo no se puede, y la respuesta lo dice con el valor original para poder devolverlo.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task EscrituraEnElSitio_ArchivoConCapacidades_LasDevuelveConSudoOAvisaDeQueSePerdieron(bool isSudo)
        {
            // Arrange: un ejecutable con una capacidad (puesta como "root" de la simulación) y un enlace duro.
            HerramientasSsh herramientas = Herramientas();
            (_, string archivo, _) = await PrepararEscrituraEnElSitioAsync(herramientas);
            _ = Bash($"chmod 755 '{archivo}'");
            _ = SudoSimulado.ComoRoot("setcap cap_net_bind_service+ep \"$1\"", archivo);
            string capacidad = Bash($"getcap '{archivo}'");
            if (isSudo)
                await SimularSudoAsync(herramientas);

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo completo\n", overwrite: true, sudo: isSudo);

            // Assert
            _ = capacidad.Should().Be($"{archivo} cap_net_bind_service=ep");
            _ = resultado.IsAtomica.Should().BeFalse();
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("nuevo completo\n");
            _ = Bash($"getcap '{archivo}'").Should().Be(isSudo ? capacidad : string.Empty);
            _ = Stat(archivo).Should().StartWith("755 ");
            _ = isSudo
                ? resultado.Mensaje.Should().NotContain("ATENCIÓN")
                : resultado.Mensaje.Should().Contain("ATENCIÓN").And.Contain("security.capability").And.Contain("setfattr -n security.capability -v 0x01000003");
        }

        // Sin getfattr ni getcap no se puede saber si el archivo tenía capacidades, sea o no ejecutable (el núcleo las borra al
        // escribir en cualquiera): la respuesta lo dice y no afirma que se devolvieron ni que se comprobaron. Con una
        // capacidad real en un archivo no ejecutable, se pierde de verdad.
        [Theory]
        [InlineData("755", false)]
        [InlineData("644", false)]
        [InlineData("644", true)]
        [SupportedOSPlatform("linux")]
        public async Task EscrituraEnElSitio_SinGetfattrNiGetcap_AvisaDeQueNoSePudieronComprobarLasCapacidades(string modo, bool hasCapacidad)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            (_, string archivo, _) = await PrepararEscrituraEnElSitioAsync(herramientas);
            _ = Bash($"chmod {modo} '{archivo}'");
            if (hasCapacidad)
                _ = SudoSimulado.ComoRoot("setcap cap_net_bind_service+ep \"$1\"", archivo);
            string capacidad = Bash($"getcap '{archivo}'");
            await QuitarDelPathAsync(herramientas, "getfattr", "getcap");

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo completo\n", overwrite: true);

            // Assert
            _ = capacidad.Should().Be(hasCapacidad ? $"{archivo} cap_net_bind_service=ep" : string.Empty);
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("nuevo completo\n");
            _ = Bash($"getcap '{archivo}'").Should().BeEmpty();
            _ = Stat(archivo).Should().StartWith($"{modo} ");
            _ = resultado.Mensaje.Should().Contain("ATENCIÓN").And.Contain("no se pudo comprobar si el archivo tenía capacidades")
                .And.NotContain("se devolvieron y se comprobaron");
        }

        // Escrito en el sitio sin sudo, un archivo setuid de otro usuario (4777) pierde el bit (el núcleo lo quita a quien no
        // tiene CAP_FSETID) y su escritor no puede devolverlo (chmod u+s solo lo puede hacer el propietario): la respuesta
        // lo dice. Necesita los subuid de la simulación de sudo para tener un propietario ajeno.
        [Fact]
        public async Task EscrituraEnElSitio_SetuidQueNoSePuedeDevolver_LoDiceLaRespuesta()
        {
            // Arrange: el archivo es del uid 1 de la simulación, 4777.
            if (!SudoSimulado.HasOtrosUsuarios)
                return;
            HerramientasSsh herramientas = Herramientas();
            (_, string archivo, _) = await PrepararEscrituraEnElSitioAsync(herramientas);
            _ = SudoSimulado.ComoRoot("chown 1 -- \"$1\" && chmod 4777 -- \"$1\"", archivo);

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo completo\n", overwrite: true);
            string modo = Stat(archivo).Split(' ')[0];
            _ = SudoSimulado.ComoRoot("chown 0 -- \"$1\"", archivo);

            // Assert
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("nuevo completo\n");
            _ = modo.Should().Be("777");
            _ = resultado.IsAtomica.Should().BeFalse();
            _ = resultado.Mensaje.Should().Contain("ATENCIÓN").And.Contain("bit setuid").And.NotContain("bit setgid")
                .And.NotContain("se devolvieron y se comprobaron");
        }

        // Si el destino se convierte en un directorio entre las comprobaciones y la colocación, el temporal no puede acabar
        // dentro de él: mv -T y ln -T (GNU) fallan. La sustitución se inyecta con un 'mv' falso (justo antes del renombrado
        // de un archivo existente) o con un 'chmod' falso (justo antes de colocar un archivo nuevo, tras darle su modo).
        [Theory]
        [InlineData("sobrescribir_existente")]
        [InlineData("sobrescribir_nuevo")]
        [InlineData("crear")]
        public async Task Colocacion_DestinoConvertidoEnDirectorioJustoAntes_NoMeteElTemporalDentro(string caso)
        {
            // Arrange
            string carpeta = Ruta("datos");
            _ = Directory.CreateDirectory(carpeta);
            string archivo = Path.Combine(carpeta, "f.txt");
            if (caso == "sobrescribir_existente")
                await File.WriteAllTextAsync(archivo, "antes\n");
            HerramientasSsh herramientas = Herramientas();
            if (caso == "sobrescribir_existente")
                await SustituirComandoAsync(herramientas, "mv", $"case \" $* \" in *' --help '*) exec /usr/bin/mv \"$@\" ;; esac\nrm -f -- '{archivo}'; mkdir -- '{archivo}'\nexec /usr/bin/mv \"$@\"");
            else
                await SustituirComandoAsync(herramientas, "chmod", $"[ -e '{archivo}' ] || mkdir -- '{archivo}'\nexec /usr/bin/chmod \"$@\"");

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo\n", overwrite: caso != "crear"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert: el directorio sigue vacío y en la carpeta no queda ningún temporal.
            _ = error.Message.Should().Contain(caso == "crear" ? "ya existe" : "se ha convertido en un directorio");
            _ = Directory.Exists(archivo).Should().BeTrue();
            _ = Directory.GetFileSystemEntries(archivo).Should().BeEmpty();
            _ = Directory.GetFileSystemEntries(carpeta).Should().ContainSingle();
        }

        // Con un mv o un ln sin -T (no de GNU) se comprueba justo antes de colocar que el destino no es un directorio (queda
        // una ventana entre esa comprobación y el renombrado, que solo -T cierra).
        [Theory]
        [InlineData("mv", true)]
        [InlineData("ln", false)]
        public async Task Colocacion_SinOpcionT_CompruebaJustoAntesQueElDestinoNoEsUnDirectorio(string programa, bool isSobrescribir)
        {
            // Arrange
            string carpeta = Ruta("datos");
            _ = Directory.CreateDirectory(carpeta);
            string archivo = Path.Combine(carpeta, "f.txt");
            HerramientasSsh herramientas = Herramientas();
            await SustituirComandoAsync(herramientas, programa, $"case \" $* \" in *' -T '*) echo \"{programa}: invalid option -- 'T'\" >&2; exit 1 ;; esac\nexec /usr/bin/{programa} \"$@\"");
            await SustituirComandoAsync(herramientas, "chmod", $"[ -e '{archivo}' ] || mkdir -- '{archivo}'\nexec /usr/bin/chmod \"$@\"");

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo\n", overwrite: isSobrescribir))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain(isSobrescribir ? "se ha convertido en un directorio" : "ya existe");
            _ = Directory.GetFileSystemEntries(archivo).Should().BeEmpty();
            _ = Directory.GetFileSystemEntries(carpeta).Should().ContainSingle();
        }

        // Nombres de 255 bytes (NAME_MAX): ASCII, con caracteres de 3 bytes que el recorte a 242 bytes partiría tras su
        // primer byte o tras el segundo, y con caracteres de 4 bytes.
        public static TheoryData<string> NombresDe255Bytes => [new string('a', 251) + ".txt", "a" + string.Concat(Enumerable.Repeat("€", 84)) + "ñ", string.Concat(Enumerable.Repeat("€", 85)), string.Concat(Enumerable.Repeat("😀", 63)) + "abc"];

        // El temporal ".<nombre>.tmp.XXXXXX" y la copia ".<nombre>.orig.XXXXXX" deben caber en NAME_MAX: con un nombre de 255
        // bytes la parte del nombre se recorta por bytes sin partir caracteres UTF-8. Sin recorte, mktemp fallaba: un
        // archivo nuevo no se podía crear y uno existente se escribía en el sitio (temporal en TMPDIR) en vez de atómico.
        [Theory]
        [MemberData(nameof(NombresDe255Bytes))]
        public async Task Escrituras_NombreDe255Bytes_CreanSobrescribenYReemplazanConElTemporalJuntoAlArchivo(string nombre)
        {
            // Arrange
            string carpeta = Ruta("datos");
            _ = Directory.CreateDirectory(carpeta);
            string archivo = Path.Combine(carpeta, nombre);
            HerramientasSsh herramientas = Herramientas();
            string tmpdir = await FijarTmpdirAsync(herramientas);

            // Act
            ResultadoEscritura creado = await herramientas.EscribirArchivoAsync(Destino, archivo, "uno\n");
            ResultadoEscritura sobrescrito = await herramientas.EscribirArchivoAsync(Destino, archivo, "dos\n", overwrite: true);
            ResultadoEscritura reemplazado = await herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "dos", "tres");

            // Assert
            _ = Encoding.UTF8.GetByteCount(nombre).Should().Be(255);
            _ = new[] { creado, sobrescrito, reemplazado }.Should().AllSatisfy(resultado => resultado.IsAtomica.Should().BeTrue());
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("tres\n");
            _ = Directory.GetFileSystemEntries(carpeta).Should().ContainSingle();
            _ = Directory.GetFileSystemEntries(tmpdir).Should().BeEmpty();
        }

        // Escritura en el sitio de un archivo con un nombre de 255 bytes que falla y no se puede restaurar: la copia y el
        // temporal quedan en su directorio, con nombres que caben en NAME_MAX y son UTF-8 válido (sin caracteres partidos).
        [Theory]
        [MemberData(nameof(NombresDe255Bytes))]
        public async Task EscrituraEnElSitio_NombreDe255Bytes_LaCopiaYElTemporalVanAlDirectorioConNombresValidos(string nombre)
        {
            // Arrange
            string carpeta = Ruta("datos");
            _ = Directory.CreateDirectory(carpeta);
            string archivo = Path.Combine(carpeta, nombre);
            await File.WriteAllTextAsync(archivo, "original completo\n");
            _ = Bash($"ln '{archivo}' '{Path.Combine(carpeta, "f2.txt")}'");
            HerramientasSsh herramientas = Herramientas();
            string tmpdir = await FijarTmpdirAsync(herramientas);
            await SustituirComandoAsync(herramientas, "cat", CatFalso("exit 1", "exit 1"));

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo contenido\n", overwrite: true))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            string copia = Directory.GetFiles(carpeta, "*.orig.*").Should().ContainSingle().Which;
            string nuevo = Directory.GetFiles(carpeta, "*.tmp.*").Should().ContainSingle().Which;
            _ = new[] { Path.GetFileName(copia), Path.GetFileName(nuevo) }.Should().AllSatisfy(restante =>
            {
                _ = Encoding.UTF8.GetByteCount(restante).Should().BeLessThanOrEqualTo(255);
                _ = restante.Should().NotContain("\uFFFD").And.StartWith("." + nombre[..10]);
            });
            _ = (await File.ReadAllTextAsync(copia)).Should().Be("original completo\n");
            _ = (await File.ReadAllTextAsync(nuevo)).Should().Be("nuevo contenido\n");
            _ = error.Message.Should().Contain("no se pudo restaurar").And.Contain(copia).And.Contain(nuevo);
            _ = Directory.GetFileSystemEntries(tmpdir).Should().BeEmpty();
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
