using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // Candado de las escrituras en un servidor sin /proc montado (chroot tipo jailkit, contenedores mínimos): el '-ef' de
    // bash sobre /dev/fd/N lo resuelve en /proc/self/fd/N y sería siempre falso. La comprobación de que el candado quedó
    // sobre el archivo que sigue en la ruta se hace entonces comparando dispositivo e inodo con stat de GNU coreutils,
    // python3 o perl; si ninguno puede, se conserva el candado y la respuesta lo avisa, sin afirmar que el archivo se
    // sustituyó (auditoría del A-sexies, sonda X02).
    public sealed partial class HerramientasArchivoIntegracionTests
    {
        private const string AvisoCandadoSinComprobar = " El script tomó el candado del archivo (flock), pero no se pudo comprobar que quedara sobre el archivo que está en la ruta (en el servidor no hay /proc, ni stat de GNU coreutils, python3 o perl con que comparar los inodos, o no se pudo consultar la ruta): si otro proceso lo sustituyó justo mientras se tomaba, la escritura no estuvo protegida frente a otra simultánea sobre el mismo archivo.";

        // Antes, sin /proc, toda sobrescritura de un archivo existente fallaba tras 3 intentos con «se sustituyó por otro
        // archivo cada vez que se tomaba su candado… reintenta», y reintentar nunca servía. Las cinco operaciones funcionan y
        // el candado se comprueba (con stat de GNU coreutils), así que la respuesta no avisa de nada.
        [Theory]
        [InlineData("atomica")]
        [InlineData("en_sitio")]
        [InlineData("reemplazo")]
        [InlineData("anexado")]
        [InlineData("nuevo")]
        public async Task SinProc_EscribirSobrescribirAnexarYReemplazar_FuncionanConElCandadoComprobado(string operacion)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(40));
            string archivo = Ruta("p.txt");
            if (operacion != "nuevo")
                await File.WriteAllTextAsync(archivo, "original\n");
            if (operacion == "en_sitio")
                _ = Bash($"ln '{archivo}' '{Ruta("p2.txt")}'");
            await SinProcAsync(herramientas);

            // Act
            ResultadoEscritura resultado = await (operacion switch
            {
                "reemplazo" => herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "original", "NUEVO"),
                "anexado" => herramientas.EscribirArchivoAsync(Destino, archivo, "NUEVO\n", append: true),
                "nuevo" => herramientas.EscribirArchivoAsync(Destino, archivo, "NUEVO\n"),
                _ => herramientas.EscribirArchivoAsync(Destino, archivo, "NUEVO\n", overwrite: true)
            });

            // Assert
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be(operacion == "anexado" ? "original\nNUEVO\n" : "NUEVO\n");
            _ = resultado.Mensaje.Should().NotContain("candado").And.NotContain("sustituy");
            _ = resultado.IsAtomica.Should().Be(operacion is "atomica" or "reemplazo" or "nuevo");
        }

        // Cada herramienta de la comparación de inodos basta por sí sola (las otras dos no funcionan): la escritura en el
        // sitio se hace con el candado comprobado. Sin ninguna, se escribe con el candado tomado y la respuesta dice que no se
        // pudo comprobar, en vez de fallar diciendo que el archivo se sustituyó.
        [Theory]
        [InlineData("stat")]
        [InlineData("python3")]
        [InlineData("perl")]
        [InlineData("ninguna")]
        public async Task SinProc_ComprobacionDelInodo_CadaHerramientaBastaYSinNingunaSeAvisa(string herramienta)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(40));
            (_, string archivo, _) = await PrepararEscrituraEnElSitioAsync(herramientas);
            await SinProcAsync(herramientas);
            await DejarSoloComparadorDeInodosAsync(herramientas, herramienta);

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(Destino, archivo, "ESCRITA\n", overwrite: true);

            // Assert
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("ESCRITA\n");
            _ = resultado.IsAtomica.Should().BeFalse();
            _ = herramienta == "ninguna"
                ? resultado.Mensaje.Should().EndWith(AvisoCandadoSinComprobar)
                : resultado.Mensaje.Should().NotContain("candado");
        }

        // Sin /proc, la sustitución del archivo mientras se toma el candado se sigue detectando con cada herramienta: se
        // vuelve a tomar sobre el archivo nuevo, que aquí tiene otro proceso, y la escritura falla sin tocarlo. Sin ninguna
        // herramienta no se puede detectar: el candado se queda en el archivo viejo, se escribe en el nuevo y la respuesta
        // avisa de exactamente ese hueco.
        [Theory]
        [InlineData("stat")]
        [InlineData("python3")]
        [InlineData("perl")]
        [InlineData("ninguna")]
        public async Task SinProc_ArchivoSustituidoMientrasSeTomaElCandado_SeDetectaConCadaHerramienta(string herramienta)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(40));
            string archivo = Ruta("f.txt");
            await File.WriteAllTextAsync(archivo, "viejo\n");
            string marca = Ruta("sustituido");
            await SinProcAsync(herramientas);
            await DejarSoloComparadorDeInodosAsync(herramientas, herramienta);
            SleepPropio dormido = SleepPropio.Crear("21.5");
            await SustituirComandoAsync(herramientas, "flock", $"if [ ! -e '{marca}' ]; then\n: > '{marca}'\nprintf 'nuevo\\n' > '{archivo}.otro'\nmv -f '{archivo}.otro' '{archivo}'\n( eval \"exec $5<&-\"; exec /usr/bin/flock -n '{archivo}' sleep {dormido.Duracion} ) </dev/null >/dev/null 2>&1 &\nsleep 0.5\nfi\nexec /usr/bin/flock \"$@\"");

            try
            {
                // Act
                Exception? error = await Record.ExceptionAsync(() => herramientas.EscribirArchivoAsync(Destino, archivo, "ESCRITA\n", overwrite: true));
                string contenido = Bash($"cat '{archivo}'");

                // Assert
                _ = File.Exists(marca).Should().BeTrue();
                if (herramienta == "ninguna")
                {
                    _ = error.Should().BeNull();
                    _ = contenido.Should().Be("ESCRITA");
                }
                else
                {
                    _ = error!.Message.Should().StartWith($"Error: {TextoCandadoOcupado} {archivo} ");
                    _ = contenido.Should().Be("nuevo");
                }
            }
            finally
            {
                _ = Bash($"pkill -f -- '{dormido.Patron}'; true");
            }
        }

        // El 'bash' del PATH del shell remoto monta un tmpfs vacío sobre /proc en un espacio de nombres de montaje propio
        // (mismo usuario; mount(2) con python3 y --keep-caps, porque mount(8) exige root) y ejecuta el bash real. Si /proc
        // sigue ahí, sale con 98: la prueba falla en lugar de pasar por el motivo equivocado.
        private async Task SinProcAsync(HerramientasSsh herramientas) =>
            await SustituirComandoAsync(herramientas, "bash", "exec unshare --map-current-user --keep-caps -m /usr/bin/python3 -c 'import ctypes,os,sys; l=ctypes.CDLL(None,use_errno=True); l.mount(b\"none\",b\"/proc\",b\"tmpfs\",0,None)==0 or sys.exit(99); os.path.exists(\"/proc/self\") and sys.exit(98); os.execv(\"/usr/bin/bash\",[\"bash\"]+sys.argv[1:])' \"$@\"");

        // Deja en el servidor solo una de las herramientas con que se comparan los inodos sin /proc ('stat' de GNU
        // coreutils con '-', python3 o perl), o ninguna: a las otras las sustituye una falsa que sale con 127, como una orden
        // que no existe (al stat, solo con el operando '-', que es lo único que rompe). flock(1) sigue tomando el candado.
        private async Task DejarSoloComparadorDeInodosAsync(HerramientasSsh herramientas, string herramienta)
        {
            if (herramienta != "stat")
                await SustituirComandoAsync(herramientas, "stat", "for a; do [ \"$a\" = - ] && exit 127; done\nexec /usr/bin/stat \"$@\"");
            foreach (string nombre in new[] { "python3", "perl" })
                if (nombre != herramienta)
                    await SustituirComandoAsync(herramientas, nombre, "exit 127");
        }
    }
}
