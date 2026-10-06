using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using Xunit.Abstractions;

namespace NativeSsh.Test.Integracion
{
    // Pruebas con sudo REAL (con contraseña, sin NOPASSWD) y en otras distribuciones (Debian estable y Alpine con busybox),
    // contra los contenedores de pruebas/contenedor-sudo.sh (Bloque S). Se omiten solas si no hay podman.
    [Collection(ColeccionContenedores.Nombre)]
    [Trait("Categoria", "Contenedor")]
    [SupportedOSPlatform("linux")]
    public sealed partial class ContenedorSudoTests(ContenedorSudo contenedores, ITestOutputHelper salida)
    {
        private Entorno Preparar(string distro) => new(contenedores.Servidor(distro));

        // Lo de una prueba: su conexión (gestor propio, así que su shell y su terminal: la caché de credenciales de sudo es
        // por terminal) y sus directorios en el contenedor, uno del usuario de prueba y otro de root (0755), que se borran
        // al terminar.
        [SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed", Justification = "El servidor es de la colección.")]
        private sealed class Entorno : IAsyncDisposable
        {
            public Entorno(ServidorContenedor servidor)
            {
                Servidor = servidor;
                Gestor = servidor.Gestor();
                string id = Guid.NewGuid().ToString("N");
                DirectorioUsuario = $"/home/{servidor.Usuario}/ns_{id}";
                DirectorioRoot = $"/srv/ns_{id}";
                _ = servidor.Root("mkdir -p -m 0755 \"$2\" && mkdir -m 0755 \"$1\" && chown \"$3:$3\" \"$1\"", DirectorioUsuario, DirectorioRoot, servidor.Usuario);
            }

            public ServidorContenedor Servidor { get; }
            public GestorSesiones Gestor { get; }
            public string DirectorioUsuario { get; }
            public string DirectorioRoot { get; }
            public string Destino => Servidor.Destino;

            public HerramientasSsh Herramientas(TimeSpan? timeout = null) => Servidor.Herramientas(Gestor, timeout);

            public string Root(string script, params string[] argumentos) => Servidor.Root(script, argumentos);

            // Autentica sudo en el terminal del shell: 'sudo -v' pide la contraseña por el TTY y ssh_input la responde.
            public async Task AutenticarSudoAsync(HerramientasSsh herramientas)
            {
                ResultadoComando pregunta = await herramientas.EjecutarAsync(Destino, "sudo -k; sudo -v && echo autenticado", esperarSegundos: 3);
                _ = pregunta.Estado.Should().Be(EstadoPeticionEnum.Ejecutando, "sudo espera la contraseña: {0}", pregunta.Salida);
                ResultadoComando respuesta = await herramientas.EnviarEntradaAsync(pregunta.Id, Servidor.Contrasena, esperarSegundos: 15);
                _ = respuesta.Estado.Should().Be(EstadoPeticionEnum.Completado, respuesta.Salida);
                _ = respuesta.CodigoSalida.Should().Be(0, respuesta.Salida);
            }

            public async ValueTask DisposeAsync()
            {
                await Gestor.DisposeAsync();
                _ = Servidor.RootConCodigo("rm -rf -- \"$1\" \"$2\"", DirectorioUsuario, DirectorioRoot);
            }
        }

        // Flujo real de la contraseña: sudo la pide por el terminal del shell, ssh_input la responde (sin que aparezca en la
        // salida) y la caché de credenciales del terminal sirve a los comandos siguientes y a las escrituras con sudo=true,
        // que van por el mismo shell.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task Sudo_ContrasenaPorElTerminalRespondidaConSshInput_LaCacheSirveALasEscrituras(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            string archivo = $"{entorno.DirectorioRoot}/config";
            _ = entorno.Root("printf 'antes\\n' > \"$1\" && chmod 0640 \"$1\"", archivo);

            // Act
            ResultadoComando pregunta = await herramientas.EjecutarAsync(entorno.Destino, "sudo -k; sudo -v && echo autenticado", esperarSegundos: 3);
            ResultadoComando respuesta = await herramientas.EnviarEntradaAsync(pregunta.Id, entorno.Servidor.Contrasena, esperarSegundos: 15);
            ResultadoComando cache = await herramientas.EjecutarAsync(entorno.Destino, "sudo -n true && echo con_cache", esperarSegundos: 15);
            ResultadoEscritura escritura = await herramientas.EscribirArchivoAsync(entorno.Destino, archivo, "despues\n", overwrite: true, sudo: true);

            // Assert
            _ = pregunta.Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
            _ = pregunta.Salida.Should().MatchRegex(entorno.Servidor.IsDebian ? @"\[sudo\] password for prueba: ?$" : @"[Pp]assword.*: ?$");
            _ = respuesta.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = respuesta.CodigoSalida.Should().Be(0);
            _ = respuesta.Salida.Should().Contain("autenticado").And.NotContain(entorno.Servidor.Contrasena);
            _ = cache.Salida.Should().Be("con_cache\n");
            _ = escritura.Mensaje.Should().StartWith("Archivo escrito correctamente");
            _ = entorno.Root("cat \"$1\"; stat -c '%U:%G %a' \"$1\"", archivo).Should().Be("despues\nroot:root 640\n");
        }

        // Contraseña errónea: sudo la vuelve a pedir ("Sorry, try again.") y, al tercer fallo, termina con código 1; el
        // shell sigue sirviendo: el comando siguiente se ejecuta (no se toma como una contraseña).
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task Sudo_TresContrasenasErroneas_TerminaConCodigo1YElShellSigueSirviendo(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            ResultadoComando pregunta = await herramientas.EjecutarAsync(entorno.Destino, "sudo -k; sudo -v", esperarSegundos: 3);

            // Act
            ResultadoComando primera = await herramientas.EnviarEntradaAsync(pregunta.Id, "mala-1", esperarSegundos: 5);
            ResultadoComando segunda = await herramientas.EnviarEntradaAsync(pregunta.Id, "mala-2", esperarSegundos: 5);
            ResultadoComando tercera = await herramientas.EnviarEntradaAsync(pregunta.Id, "mala-3", esperarSegundos: 15);
            ResultadoComando siguiente = await herramientas.EjecutarAsync(entorno.Destino, "echo siguiente; sudo -n true 2>/dev/null; echo \"sin_cache=$?\"", esperarSegundos: 15);

            // Assert
            _ = primera.Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
            _ = primera.Salida.Should().Contain("Sorry, try again.");
            _ = segunda.Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
            _ = tercera.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = tercera.CodigoSalida.Should().Be(1);
            _ = tercera.Salida.Should().Contain("incorrect password attempt");
            _ = string.Concat(primera.Salida, segunda.Salida, tercera.Salida).Should().NotContain("mala-");
            _ = siguiente.Salida.Should().Be("siguiente\nsin_cache=1\n");
        }

        // ssh_cancel mientras sudo espera la contraseña: el comando termina cancelado y el siguiente se ejecuta como comando
        // (no llega a sudo como contraseña) sin credenciales en caché.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task Sudo_CanceladoMientrasPideLaContrasena_ElComandoSiguienteSeEjecutaComoComando(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            ResultadoComando pregunta = await herramientas.EjecutarAsync(entorno.Destino, "sudo -k; sudo -v && echo autenticado", esperarSegundos: 3);

            // Act
            ResultadoComando cancelado = await herramientas.CancelarAsync(pregunta.Id);
            ResultadoComando siguiente = await herramientas.EjecutarAsync(entorno.Destino, "echo siguiente; sudo -n true 2>/dev/null; echo \"sin_cache=$?\"", esperarSegundos: 15);

            // Assert
            _ = pregunta.Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
            _ = cancelado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = cancelado.Salida.Should().NotContain("autenticado");
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Salida.Should().Be("siguiente\nsin_cache=1\n");
        }

        // FALLO CONOCIDO (producto, Bloque S): ssh_cancel con sudo esperando la contraseña debería interrumpirlo como un
        // Ctrl+C interactivo (sudo sale con «a password is required», sin gastar intentos) y conservar el shell. Pero la
        // interrupción escribe en el terminal "\x03\n__ns_rc=130; ...; read -r __ns_d\n" de una vez
        // (ConstructorComando.EscribirInterrupcion): sudo, que lee la contraseña del terminal, se queda con la línea vacía y
        // con la línea de recuperación del protocolo como contraseñas («Sorry, try again.»: intentos fallidos registrados,
        // que con pam_faillock pueden bloquear la cuenta), no responde al Ctrl+C a tiempo y el producto cierra el canal del
        // shell: se pierden el shell y sus variables exportadas.
        [TheoryContenedor(FalloConocido = "producto: el texto que sigue al Ctrl+C de la interrupción llega a sudo como contraseña; intentos fallidos y shell perdido")]
        [Trait("Categoria", "FalloConocido")]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task FalloConocido_Sudo_CanceladoMientrasPideLaContrasena_NoGastaIntentosYConservaElShell(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            _ = await herramientas.EjecutarAsync(entorno.Destino, "export NS_VIVO=si", esperarSegundos: 10);
            ResultadoComando pregunta = await herramientas.EjecutarAsync(entorno.Destino, "sudo -k; sudo -v", esperarSegundos: 3);

            // Act
            ResultadoComando cancelado = await herramientas.CancelarAsync(pregunta.Id);
            ResultadoComando siguiente = await herramientas.EjecutarAsync(entorno.Destino, "echo \"vivo=$NS_VIVO\"", esperarSegundos: 15);

            // Assert
            _ = cancelado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = cancelado.Salida.Should().NotContain("Sorry, try again", "el Ctrl+C no puede convertirse en intentos de contraseña fallidos");
            _ = cancelado.Aviso.Should().NotContain("se cerró el canal del shell");
            _ = siguiente.Salida.Should().Be("vivo=si\n");
            _ = siguiente.Aviso.Should().BeNull();
        }

        // ssh_write_file con sudo=true sin credenciales en caché: sudo pide la contraseña por el terminal y nadie la
        // responde. Al vencer el plazo la escritura falla sin tocar el archivo, el error lo dice, y el shell sigue sirviendo.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task EscribirConSudo_SinCredencialesYSinRespuesta_FallaSinTocarElArchivoYElShellSigueSirviendo(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas(TimeSpan.FromSeconds(4));
            string archivo = $"{entorno.DirectorioRoot}/config";
            _ = entorno.Root("printf 'antes\\n' > \"$1\" && chmod 0600 \"$1\"", archivo);
            _ = await herramientas.EjecutarAsync(entorno.Destino, "sudo -k", esperarSegundos: 10);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(entorno.Destino, archivo, "despues\n", overwrite: true, sudo: true))
                .Should().ThrowAsync<NativeSshException>()).Which;
            ResultadoComando siguiente = await herramientas.EjecutarAsync(entorno.Destino, "echo siguiente", esperarSegundos: 15);
            salida.WriteLine(error.Message);

            // Assert
            _ = error.Message.Should().MatchRegex("petición [0-9a-f]{32}").And.Contain("Tiempo agotado").And.NotContain(entorno.Servidor.Contrasena);
            _ = entorno.Root("cat \"$1\"", archivo).Should().Be("antes\n");
            _ = siguiente.Salida.Should().Be("siguiente\n");
            _ = entorno.Root("ls -A \"$1\"", entorno.DirectorioRoot).Should().Be("config\n");
        }

        // FALLO CONOCIDO (producto, Bloque S; misma causa que el de ssh_cancel): al vencer el plazo de una escritura con
        // sudo=true que espera la contraseña, la interrupción ("\x03\n__ns_rc=130; ...") llega a sudo como contraseñas
        // (en Debian, «Sorry, try again.» y un segundo intento con la línea de recuperación), sudo no muere a tiempo y el
        // producto cierra el canal del shell: intentos fallidos registrados y shell perdido con sus variables.
        [TheoryContenedor(FalloConocido = "producto: el texto que sigue al Ctrl+C de la interrupción llega a sudo como contraseña; intentos fallidos y shell perdido")]
        [Trait("Categoria", "FalloConocido")]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task FalloConocido_EscribirConSudo_PlazoVencidoPidiendoLaContrasena_NoGastaIntentosYConservaElShell(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas(TimeSpan.FromSeconds(4));
            string archivo = $"{entorno.DirectorioRoot}/config";
            _ = entorno.Root("printf 'antes\\n' > \"$1\" && chmod 0600 \"$1\"", archivo);
            _ = await herramientas.EjecutarAsync(entorno.Destino, "sudo -k; export NS_VIVO=si", esperarSegundos: 10);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(entorno.Destino, archivo, "despues\n", overwrite: true, sudo: true))
                .Should().ThrowAsync<NativeSshException>()).Which;
            ResultadoComando siguiente = await herramientas.EjecutarAsync(entorno.Destino, "echo \"vivo=$NS_VIVO\"", esperarSegundos: 15);
            salida.WriteLine(error.Message);

            // Assert
            _ = error.Message.Should().NotContain("Sorry, try again").And.NotContain("se cerró el canal del shell");
            _ = siguiente.Salida.Should().Be("vivo=si\n");
        }

        // La contraseña también se puede responder con ssh_input mientras la propia ssh_write_file con sudo=true espera:
        // la escritura termina bien y el prompt de sudo no estropea su informe.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task EscribirConSudo_ContrasenaRespondidaConSshInputDuranteLaEscritura_TerminaBien(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas(TimeSpan.FromSeconds(30));
            string archivo = $"{entorno.DirectorioRoot}/config";
            _ = entorno.Root("printf 'antes\\n' > \"$1\" && chmod 0600 \"$1\"", archivo);
            _ = await herramientas.EjecutarAsync(entorno.Destino, "sudo -k", esperarSegundos: 10);

            // Act
            Task<ResultadoEscritura> escritura = herramientas.EscribirArchivoAsync(entorno.Destino, archivo, "despues\n", overwrite: true, sudo: true);
            string id = await EsperarPromptAsync(entorno, herramientas);
            _ = await herramientas.EnviarEntradaAsync(id, entorno.Servidor.Contrasena, esperarSegundos: 0);
            ResultadoEscritura resultado = await escritura;

            // Assert
            _ = resultado.Mensaje.Should().StartWith("Archivo escrito correctamente").And.NotContain(entorno.Servidor.Contrasena);
            _ = entorno.Root("cat \"$1\"; stat -c '%U:%G %a' \"$1\"", archivo).Should().Be("despues\nroot:root 600\n");
        }

        // ssh_replace_file_content con sudo=true: la lectura previa pide la contraseña (respondida con ssh_input) y la
        // escritura usa ya la caché del terminal.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task ReemplazarConSudo_ContrasenaRespondidaEnLaLecturaPrevia_LaEscrituraUsaLaCache(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas(TimeSpan.FromSeconds(30));
            string archivo = $"{entorno.DirectorioRoot}/config";
            _ = entorno.Root("printf 'clave=1\\notra=2\\n' > \"$1\" && chmod 0600 \"$1\"", archivo);
            _ = await herramientas.EjecutarAsync(entorno.Destino, "sudo -k", esperarSegundos: 10);

            // Act
            Task<ResultadoEscritura> reemplazo = herramientas.ReemplazarContenidoArchivoAsync(entorno.Destino, archivo, "clave=1", "clave=2", sudo: true);
            string id = await EsperarPromptAsync(entorno, herramientas);
            _ = await herramientas.EnviarEntradaAsync(id, entorno.Servidor.Contrasena, esperarSegundos: 0);
            ResultadoEscritura resultado = await reemplazo;

            // Assert
            _ = resultado.Reemplazos.Should().Be(1);
            _ = entorno.Root("cat \"$1\"; stat -c '%U:%G %a' \"$1\"", archivo).Should().Be("clave=2\notra=2\nroot:root 600\n");
        }

        // La caché de credenciales es del terminal: ssh_reset_shell abre otro y sudo vuelve a pedir la contraseña.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task Sudo_TrasReiniciarElShell_VuelveAPedirLaContrasena(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            await entorno.AutenticarSudoAsync(herramientas);

            // Act
            ResultadoComando antes = await herramientas.EjecutarAsync(entorno.Destino, "sudo -n true; echo \"codigo=$?\"", esperarSegundos: 15);
            _ = herramientas.ReiniciarShell(entorno.Destino);
            ResultadoComando despues = await herramientas.EjecutarAsync(entorno.Destino, "sudo -n true 2>/dev/null; echo \"codigo=$?\"", esperarSegundos: 15);

            // Assert
            _ = antes.Salida.Should().Be("codigo=0\n");
            _ = despues.Salida.Should().Be("codigo=1\n");
        }

        // Id de la petición en ejecución en el shell de la prueba cuando su salida ya muestra el prompt de sudo.
        private static async Task<string> EsperarPromptAsync(Entorno entorno, HerramientasSsh herramientas)
        {
            DateTime tope = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < tope)
            {
                foreach (Peticion p in entorno.Gestor.ListarPeticiones())
                {
                    if (p.Estado != EstadoPeticionEnum.Ejecutando)
                        continue;

                    ResultadoComando salida = await herramientas.LeerSalidaAsync(p.IdTexto);
                    if (salida.Salida.Contains("assword", StringComparison.Ordinal))
                        return p.IdTexto;
                }

                await Task.Delay(50);
            }

            throw new TimeoutException("sudo no llegó a pedir la contraseña.");
        }
    }
}
