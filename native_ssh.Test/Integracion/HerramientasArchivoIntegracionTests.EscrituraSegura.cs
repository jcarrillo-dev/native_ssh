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
