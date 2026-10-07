using System.Diagnostics;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;

namespace NativeSsh.Test.Integracion
{
    // Cancelación e interrupción con sudo REAL (use_pty: sudo ejecuta el comando en un terminal propio y le reenvía las
    // señales) y el candado por archivo con y sin sudo.
    public sealed partial class ContenedorSudoTests
    {
        // Nombre de los archivos cuya escritura en el sitio se queda a medias con el 'cat' lento.
        private const string NombreLento = "lento";

        // Con sudo, secure_path descarta el PATH del usuario: el 'cat' falso va, como root, en /usr/local/bin (el primero de
        // secure_path en las dos distribuciones). Solo actúa una vez (la primera que consigue borrar su marca) y al copiar el
        // temporal de un archivo llamado 'lento' sobre el destino (escritura en el sitio): escribe 3 bytes y se queda
        // esperando hasta 30 s; cualquier otro uso es el cat real. Se quita al terminar la prueba (la colección es
        // secuencial).
        private static async Task<T> ConCatLentoAsync<T>(Entorno entorno, Func<Task<T>> prueba)
        {
            _ = entorno.Root($$"""
                rm -f /usr/local/bin/cat
                cat > /usr/local/bin/cat <<'FIN'
                #!/bin/sh
                case "$2" in
                  */.{{NombreLento}}.tmp.*)
                    if rm /run/ns_cat_lento 2>/dev/null; then
                      head -c 3 -- "$2"; sleep 30; exit 1
                    fi ;;
                esac
                exec /bin/cat "$@"
                FIN
                chmod 0755 /usr/local/bin/cat
                : > /run/ns_cat_lento
                """);
            try
            {
                return await prueba();
            }
            finally
            {
                _ = entorno.Root("rm -f /usr/local/bin/cat /run/ns_cat_lento");
            }
        }

        // El 'cat' lento ya está a mitad de la escritura (su 'sleep 30' en marcha). Los patrones de pgrep llevan un corchete
        // para no encontrarse a sí mismos en la línea de órdenes del 'sh -c' que los ejecuta.
        private static async Task EsperarCatLentoAsync(Entorno entorno) =>
            await EsperarEnContenedorAsync(entorno, "pgrep -f '^[s]leep 30$' >/dev/null");

        // Espera a que el directorio de root de la prueba vuelva a tener 'entradas' entradas (sin copias ni temporales).
        private static async Task EsperarEntradasAsync(Entorno entorno, int entradas) =>
            await EsperarEnContenedorAsync(entorno, "[ \"$(ls -A \"$1\" | wc -l)\" -eq \"$2\" ]", 15, entorno.DirectorioRoot, entradas.ToString(System.Globalization.CultureInfo.InvariantCulture));

        private static async Task EsperarEnContenedorAsync(Entorno entorno, string condicion, int segundos = 20, params string[] argumentos)
        {
            Stopwatch reloj = Stopwatch.StartNew();
            while (entorno.Servidor.RootConCodigo(condicion, argumentos).Codigo != 0)
            {
                if (reloj.Elapsed > TimeSpan.FromSeconds(segundos))
                    throw new TimeoutException($"No se cumplió en el contenedor: {condicion}");
                await Task.Delay(100);
            }
        }

        // ssh_cancel de un comando lanzado con sudo (credenciales en caché): el comando termina cancelado, su proceso muere
        // y el comando siguiente se ejecuta.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task CancelarComandoConSudo_ElComandoMuereYElSiguienteSeEjecuta(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            await entorno.AutenticarSudoAsync(herramientas);
            ResultadoComando largo = await herramientas.EjecutarAsync(entorno.Destino, "sudo sleep 301", esperarSegundos: 1);
            await EsperarEnContenedorAsync(entorno, "pgrep -f '^[s]leep 301$' >/dev/null");

            // Act
            ResultadoComando cancelado = await herramientas.CancelarAsync(largo.Id);
            ResultadoComando siguiente = await herramientas.EjecutarAsync(entorno.Destino, "echo siguiente", esperarSegundos: 15);

            // Assert
            _ = cancelado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = siguiente.Salida.Should().Be("siguiente\n");
            await EsperarEnContenedorAsync(entorno, "! pgrep -f '^[s]leep 301$' >/dev/null", 10);
        }

        // ssh_cancel (y el plazo de ssh_exec) de un comando lanzado con sudo conserva el shell y sus variables. sudo, con
        // use_pty (lo de serie en Debian y desde sudo 1.9.14), lee el terminal del usuario y reenvía lo que llega al terminal
        // propio del comando: cuando la interrupción escribía el Ctrl+C y la línea de recuperación de una vez, sudo se la
        // tragaba antes de morir, su marcador no llegaba y se cerraba el canal del shell. Ahora el Ctrl+C va en su propia
        // escritura y la línea de recuperación 0,3 s después (ConstructorComando.EscribirCtrlC y EscribirRecuperacion; Bloque
        // S, P1; ET3; era un fallo conocido). Reproducción a mano del fallo: con 'ssh -tt', enviar "sudo sleep 301" y
        // después "\x03\necho MARCA\n" en una sola escritura (MARCA no se ejecuta); con la pausa, sí.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task CancelarComandoConSudo_ConservaElShell(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            await entorno.AutenticarSudoAsync(herramientas);
            _ = await herramientas.EjecutarAsync(entorno.Destino, "export NS_VIVO=si", esperarSegundos: 10);
            ResultadoComando largo = await herramientas.EjecutarAsync(entorno.Destino, "sudo sleep 301", esperarSegundos: 1);
            await EsperarEnContenedorAsync(entorno, "pgrep -f '^[s]leep 301$' >/dev/null");

            // Act
            ResultadoComando cancelado = await herramientas.CancelarAsync(largo.Id);
            ResultadoComando siguiente = await herramientas.EjecutarAsync(entorno.Destino, "echo \"vivo=$NS_VIVO\"", esperarSegundos: 15);

            // Assert
            salida.WriteLine($"{cancelado.Estado}: {cancelado.Aviso}");
            _ = cancelado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = cancelado.Aviso.Should().NotContain("se cerró el canal del shell");
            _ = siguiente.Salida.Should().Be("vivo=si\n");
        }

        // Escritura en el sitio con sudo que vence su plazo a mitad (el 'cat' lento): el Ctrl+C llega al script a través de
        // sudo, su trampa restaura el original y el error lo dice; no quedan copias ni temporales.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task EscrituraEnElSitioConSudo_PlazoVencidoAMitad_RestauraElOriginal(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas(TimeSpan.FromSeconds(4));
            await entorno.AutenticarSudoAsync(herramientas);
            _ = await herramientas.EjecutarAsync(entorno.Destino, "export NS_VIVO=si", esperarSegundos: 10);
            string archivo = $"{entorno.DirectorioRoot}/{NombreLento}";
            _ = entorno.Root("printf 'original completo\\n' > \"$1\" && chmod 0640 \"$1\" && ln \"$1\" \"$1.enlace\"", archivo);
            string antes = Atributos(entorno, archivo);

            // Act
            NativeSshException error = await ConCatLentoAsync(entorno, async () =>
                (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(entorno.Destino, archivo, "nuevo contenido\n", overwrite: true, sudo: true))
                    .Should().ThrowAsync<NativeSshException>()).Which);
            salida.WriteLine(error.Message);
            await EsperarEntradasAsync(entorno, 2);

            ResultadoComando siguiente = await herramientas.EjecutarAsync(entorno.Destino, "echo \"vivo=$NS_VIVO\"", esperarSegundos: 15);

            // Assert: restaurado y con el shell intacto (con la entrada del pipe, sudo no lee el terminal).
            _ = entorno.Root("cat \"$1\"", archivo).Should().Be("original completo\n");
            _ = Atributos(entorno, archivo).Should().Be(antes);
            _ = error.Message.Should().MatchRegex("petición [0-9a-f]{32}").And.Contain("Se restauró el contenido original").And.NotContain("__NS_");
            _ = siguiente.Salida.Should().Be("vivo=si\n");
        }

        // ssh_disconnect con una escritura en el sitio con sudo a medias: el terminal se cuelga, sudo reenvía la señal al
        // script y su trampa restaura el original por su cuenta.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task EscrituraEnElSitioConSudo_DesconexionAMitad_LaTrampaRestauraElOriginal(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas(TimeSpan.FromSeconds(40));
            await entorno.AutenticarSudoAsync(herramientas);
            string archivo = $"{entorno.DirectorioRoot}/{NombreLento}";
            _ = entorno.Root("printf 'original completo\\n' > \"$1\" && chmod 0640 \"$1\" && ln \"$1\" \"$1.enlace\"", archivo);
            string antes = Atributos(entorno, archivo);

            // Act
            NativeSshException error = await ConCatLentoAsync(entorno, async () =>
            {
                Task<ResultadoEscritura> escritura = herramientas.EscribirArchivoAsync(entorno.Destino, archivo, "nuevo contenido\n", overwrite: true, sudo: true);
                await EsperarCatLentoAsync(entorno);
                _ = await herramientas.DesconectarAsync(entorno.Destino);
                return (await FluentActions.Awaiting(() => escritura).Should().ThrowAsync<NativeSshException>()).Which;
            });
            salida.WriteLine(error.Message);
            await EsperarEntradasAsync(entorno, 2);

            // Assert
            _ = entorno.Root("cat \"$1\"", archivo).Should().Be("original completo\n");
            _ = Atributos(entorno, archivo).Should().Be(antes);
            _ = error.Message.Should().MatchRegex("petición [0-9a-f]{32}");
        }

        // Candado por archivo: otro proceso (aquí root, con flock) tiene tomado el candado del archivo; las escrituras, con
        // y sin sudo, fallan al momento sin tocarlo (Debian: flock de util-linux y perl; Alpine: el flock de busybox, sin -E).
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian, true)]
        [InlineData(ContenedorSudo.Debian, false)]
        [InlineData(ContenedorSudo.Alpine, true)]
        [InlineData(ContenedorSudo.Alpine, false)]
        public async Task Escritura_CandadoDelArchivoTomadoPorOtroProceso_FallaAlMomentoSinTocarlo(string distro, bool isSudo)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas(TimeSpan.FromSeconds(20));
            if (isSudo)
                await entorno.AutenticarSudoAsync(herramientas);
            string archivo = isSudo ? $"{entorno.DirectorioRoot}/app.conf" : $"{entorno.DirectorioUsuario}/app.conf";
            _ = entorno.Root("printf 'antes\\n' > \"$1\" && chown \"$2\" \"$1\"", archivo, isSudo ? "root" : entorno.Servidor.Usuario);
            using Process retenedor = entorno.Servidor.RootEnSegundoPlano("exec flock -x \"$1\" sleep 30", archivo);
            await EsperarEnContenedorAsync(entorno, "pgrep -f '^[f]lock -x ' >/dev/null");
            try
            {
                // Act
                Stopwatch reloj = Stopwatch.StartNew();
                NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(entorno.Destino, archivo, "despues\n", overwrite: true, sudo: isSudo))
                    .Should().ThrowAsync<NativeSshException>()).Which;
                NativeSshException errorAnexo = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(entorno.Destino, archivo, "mas\n", append: true, sudo: isSudo))
                    .Should().ThrowAsync<NativeSshException>()).Which;

                // Assert
                _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
                _ = error.Message.Should().Contain("candado");
                _ = errorAnexo.Message.Should().Contain("candado");
                _ = entorno.Root("cat \"$1\"", archivo).Should().Be("antes\n");
            }
            finally
            {
                _ = entorno.Root("pkill -f '^[s]leep 30$' || true");
            }
        }

        // En Alpine (sin python3 ni perl) el candado lo toma el flock de busybox, que no tiene -E pero funciona (A-octies,
        // AO3): la escritura queda protegida y la respuesta no avisa de que no hubo exclusión. Antes del AO3 se escribía sin
        // candado y con ese aviso (decisión del A-sexies).
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Alpine, true)]
        [InlineData(ContenedorSudo.Alpine, false)]
        public async Task Escritura_ConElFlockDeBusybox_EscribeConCandadoYSinAvisoDeFaltaDeExclusion(string distro, bool isSudo)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas(TimeSpan.FromSeconds(20));
            if (isSudo)
                await entorno.AutenticarSudoAsync(herramientas);
            string archivo = isSudo ? $"{entorno.DirectorioRoot}/app.conf" : $"{entorno.DirectorioUsuario}/app.conf";
            _ = entorno.Root("printf 'antes\\n' > \"$1\" && chown \"$2\" \"$1\"", archivo, isSudo ? "root" : entorno.Servidor.Usuario);

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(entorno.Destino, archivo, "despues\n", overwrite: true, sudo: isSudo);

            // Assert
            _ = resultado.Mensaje.Should().NotContain("candado").And.NotContain("no estuvo protegida");
            _ = entorno.Root("cat \"$1\"", archivo).Should().Be("despues\n");
        }

        // Dos escrituras con sudo sobre el mismo archivo desde dos conexiones: la primera se queda a medias en el sitio (el
        // 'cat' lento, solo para ella); la segunda debe fallar al momento por el candado y, cuando la primera vence su plazo,
        // su trampa restaura el original. En Alpine (sin python3 ni perl) el candado lo toma el flock de busybox, sin -E,
        // tras comprobar sobre un temporal que funciona (AO3); antes se escribía sin candado y la segunda escritura,
        // confirmada como correcta, se perdía bajo la restauración de la primera (era un fallo conocido).
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task EscrituraConSudo_OtraEscrituraEnCursoSobreElMismoArchivo_LaSegundaFallaAlMomento(string distro) =>
            await DosEscriturasConcurrentesAsync(distro);

        private async Task DosEscriturasConcurrentesAsync(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh primera = entorno.Herramientas(TimeSpan.FromSeconds(8));
            await entorno.AutenticarSudoAsync(primera);
            await using GestorSesiones otroGestor = entorno.Servidor.Gestor();
            HerramientasSsh segunda = entorno.Servidor.Herramientas(otroGestor, TimeSpan.FromSeconds(20));
            ResultadoComando pregunta = await segunda.EjecutarAsync(entorno.Destino, "sudo -v", esperarSegundos: 3);
            _ = await segunda.EnviarEntradaAsync(pregunta.Id, entorno.Servidor.Contrasena, esperarSegundos: 15);
            string archivo = $"{entorno.DirectorioRoot}/{NombreLento}";
            _ = entorno.Root("printf 'original completo\\n' > \"$1\" && chmod 0640 \"$1\" && ln \"$1\" \"$1.enlace\"", archivo);

            // Act
            (Exception? errorSegunda, NativeSshException errorPrimera) = await ConCatLentoAsync(entorno, async () =>
            {
                Task<ResultadoEscritura> enCurso = primera.EscribirArchivoAsync(entorno.Destino, archivo, "primera\n", overwrite: true, sudo: true);
                await EsperarCatLentoAsync(entorno);
                Exception? error = await Record.ExceptionAsync(() => segunda.EscribirArchivoAsync(entorno.Destino, archivo, "segunda\n", overwrite: true, sudo: true));
                NativeSshException errorPrimera = (await FluentActions.Awaiting(() => enCurso).Should().ThrowAsync<NativeSshException>()).Which;
                return (error, errorPrimera);
            });
            await EsperarEntradasAsync(entorno, 2);
            string contenido = entorno.Root("cat \"$1\"", archivo);
            salida.WriteLine($"segunda: {errorSegunda?.Message ?? "sin error"}\nprimera: {errorPrimera.Message}\ncontenido final: {contenido}");

            // Assert: o la segunda se rechazó por el candado (y queda el original), o se confirmó y su contenido es el que
            // queda: una escritura confirmada como correcta nunca puede perderse.
            if (errorSegunda is null)
            {
                _ = contenido.Should().Be("segunda\n", "la segunda escritura se confirmó como correcta");
            }
            else
            {
                _ = errorSegunda.Message.Should().Contain("candado");
                _ = contenido.Should().Be("original completo\n");
            }
        }
    }
}
