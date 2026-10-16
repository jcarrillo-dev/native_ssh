using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // Comprobación de que el candado quedó sobre el archivo que está en la ruta (mismo_inodo) y anexado por el mismo
    // descriptor que lleva el candado (revisión del A-septies, observaciones 2 y 3).
    public sealed partial class HerramientasArchivoIntegracionTests
    {
        // mismo_inodo aislado, con bash local y un PATH mínimo (con o sin /proc): 0 mismo archivo, 1 distinto, 2 no se
        // puede saber. Que la ruta ya no exista (ENOENT) es «distinto» con cada herramienta; cualquier otro fallo al
        // consultarla (aquí, un stat de GNU que responde «Permission denied») es «no se sabe», nunca «distinto». Con /proc,
        // un '-ef' falso solo es «distinto» si la ruta existe: si no, decide la siguiente vía, y sin ninguna, 2. Un stat que
        // no es de GNU coreutils (aquí uno que toma '-' por otra cosa y responde mal) no se usa: decide python3.
        [Theory]
        [InlineData(true, "mismo", "", 0)]
        [InlineData(true, "sustituido", "", 1)]
        [InlineData(true, "borrado", "", 2)]
        [InlineData(true, "borrado", "stat", 1)]
        [InlineData(true, "borrado", "python3", 1)]
        [InlineData(false, "mismo", "stat", 0)]
        [InlineData(false, "sustituido", "stat", 1)]
        [InlineData(false, "borrado", "stat", 1)]
        [InlineData(false, "borrado", "python3", 1)]
        [InlineData(false, "borrado", "perl", 1)]
        [InlineData(false, "sustituido", "perl", 1)]
        [InlineData(false, "borrado", "", 2)]
        [InlineData(false, "mismo", "stat_eacces", 2)]
        [InlineData(false, "mismo", "stat_no_gnu python3", 0)]
        [InlineData(false, "sustituido", "stat_no_gnu python3", 1)]
        public async Task MismoInodo_SegunLaViaYLoQuePasaConLaRuta(bool isConProc, string accion, string herramientas, int esperado)
        {
            // Arrange
            string archivo = Ruta("inodo.txt");
            await File.WriteAllTextAsync(archivo, "original\n");
            string bin = Ruta("bin_inodo");
            _ = Directory.CreateDirectory(bin);
            foreach (string herramienta in new[] { "bash", "rm", "mv" })
                _ = File.CreateSymbolicLink(Path.Combine(bin, herramienta), Bash($"command -v {herramienta}"));
            string stat = Bash("command -v stat");
            foreach (string herramienta in herramientas.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string? cuerpo = herramienta switch
                {
                    "stat_eacces" => $"case \" $* \" in *' -- '*) for a; do ultimo=$a; done; echo \"stat: cannot statx '$ultimo': Permission denied\" >&2; exit 1 ;; esac\nexec '{stat}' \"$@\"",
                    "stat_no_gnu" => $"[ \"$1\" = --version ] && {{ echo 'stat (uutils coreutils) 0.2.2'; exit 0; }}\nfor a; do [ \"$a\" = - ] && {{ echo 0:0; exit 0; }}; done\nexec '{stat}' \"$@\"",
                    _ => null
                };
                if (cuerpo is null)
                {
                    _ = File.CreateSymbolicLink(Path.Combine(bin, herramienta), Bash($"command -v {herramienta}"));
                    continue;
                }

                string ruta = Path.Combine(bin, "stat");
                await File.WriteAllTextAsync(ruta, $"#!{Bash("command -v bash")}\n{cuerpo}\n");
                _ = Bash($"chmod 700 '{ruta}'");
            }

            string script = Ruta("inodo.sh");
            await File.WriteAllTextAsync(script, HerramientasSsh.DefinicionCandado + """

                destino=$1
                exec {fd}<"$destino"
                case $2 in
                  borrado) rm -f -- "$destino" ;;
                  sustituido) printf 'otro\n' > "$destino.otro"; mv -f -- "$destino.otro" "$destino" ;;
                esac
                mismo_inodo "$fd"
                echo "codigo=$?"
                [ -e /proc/self ] && echo con_proc

                """);
            string bash = Path.Combine(bin, "bash");
            string orden = isConProc
                ? $"PATH='{bin}' '{bash}' '{script}' '{archivo}' {accion}"
                : $"PATH='{bin}' /usr/bin/unshare --map-current-user --keep-caps -m /usr/bin/python3 -c 'import ctypes,os,sys; l=ctypes.CDLL(None,use_errno=True); l.mount(b\"none\",b\"/proc\",b\"tmpfs\",0,None)==0 or sys.exit(99); os.execv(sys.argv[1],sys.argv[1:])' '{bash}' '{script}' '{archivo}' {accion}";

            // Act
            string resultado = Bash(orden);

            // Assert
            _ = resultado.Should().Be(isConProc ? $"codigo={esperado}\ncon_proc" : $"codigo={esperado}");
        }

        // El anexado escribe por el mismo descriptor sobre el que toma el candado y comprueba el inodo. Antes abría el
        // archivo para anexar en un descriptor aparte y después el del candado: si entretanto otro proceso lo sustituía con
        // un renombrado encima (aquí, el 'flock' falso al tomar el candado la primera vez), el candado y la comprobación
        // pasaban al archivo nuevo y el anexado iba al viejo, ya fuera de la ruta: confirmado como correcto y perdido.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Anexar_ArchivoSustituidoMientrasSeTomaElCandado_AnexaAlArchivoQueEstaEnLaRuta(bool isSudo)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(40));
            string archivo = Ruta("anexo.txt");
            await File.WriteAllTextAsync(archivo, "viejo\n");
            string marca = Ruta("sustituido");
            await SustituirComandoAsync(herramientas, "flock", $"if [ ! -e '{marca}' ]; then\n: > '{marca}'\nprintf 'nuevo\\n' > '{archivo}.otro'\nmv -f '{archivo}.otro' '{archivo}'\nfi\nexec /usr/bin/flock \"$@\"");
            if (isSudo)
                await SimularSudoAsync(herramientas);

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(Destino, archivo, "MAS\n", append: true, sudo: isSudo);

            // Assert
            _ = File.Exists(marca).Should().BeTrue();
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("nuevo\nMAS\n");
            _ = resultado.Mensaje.Should().NotContain("candado");
        }
    }
}
