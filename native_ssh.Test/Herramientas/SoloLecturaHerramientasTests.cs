using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Sesion.Falsos;

namespace NativeSsh.Test.Herramientas
{
    // Modo solo lectura por destino (CI6): ssh_exec, ssh_input, ssh_write_file y ssh_replace_file_content se rechazan sin
    // conectar ni enviar nada; las herramientas de lectura, ssh_set_cwd y los demás destinos funcionan.
    public sealed class SoloLecturaHerramientasTests : IAsyncDisposable
    {
        private readonly FabricaFalsa _fabrica = new(Guion);
        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;

        public SoloLecturaHerramientasTests()
        {
            _ = DestinosSoloLectura.TryParsear("prod", out DestinosSoloLectura soloLectura, out _).Should().BeTrue();
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana", SoloLectura = soloLectura };
            _gestor = new GestorSesiones(_fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            _herramientas = new HerramientasSsh(_gestor, opciones);
        }

        public ValueTask DisposeAsync() => _gestor.DisposeAsync();

        private static RespuestaFalsa Guion(string comando) =>
            comando.Contains("| tail -n +", StringComparison.Ordinal) ? new("[Líneas 1-1 de 1]\nhola\n") :
            comando.Contains("pwd -P", StringComparison.Ordinal) ? MarcarDirectorio(comando) :
            comando == "pregunta" ? new("¿Seguro? ", 0, ComportamientoFalsoEnum.PideEntrada) :
            new("ok\n");

        // Como el script de ssh_set_cwd: la ruta resuelta en base64 tras el marcador con el id de la petición.
        private static RespuestaFalsa MarcarDirectorio(string comando)
        {
            const string Formato = "__NS_CWD_%s__ %s\\n' ";
            int posicion = comando.IndexOf(Formato, StringComparison.Ordinal) + Formato.Length;
            return new($"__NS_CWD_{comando[posicion..(posicion + 32)]}__ {Convert.ToBase64String("/srv"u8)}\n");
        }

        private static string Rechazo(string herramienta, string clave = "ana@prod:22") => HerramientasSsh.MensajeSoloLectura(clave, herramienta);

        [Fact]
        public async Task Escrituras_DestinoDeSoloLectura_SeRechazanSinConectar()
        {
            // Arrange
            (string Mensaje, Func<Task> Llamada)[] llamadas =
            [
                (Rechazo("ssh_exec"), () => _herramientas.EjecutarAsync("prod", "echo hola")),
                (Rechazo("ssh_write_file"), () => _herramientas.EscribirArchivoAsync("prod", "a.txt", "x", overwrite: true)),
                (Rechazo("ssh_write_file"), () => _herramientas.EscribirArchivoAsync("ana@prod:22", "a.txt", "x", append: true)),
                (Rechazo("ssh_replace_file_content"), () => _herramientas.ReemplazarContenidoArchivoAsync("prod", "a.txt", "x", "y")),
                (Rechazo("ssh_replace_file_content", "ana@PROD:22"), () => _herramientas.ReemplazarContenidoArchivoAsync("PROD", "a.txt", ediciones: [new EdicionTexto("x", "y")])),
            ];

            // Act & Assert
            foreach ((string mensaje, Func<Task> llamada) in llamadas)
                _ = (await FluentActions.Awaiting(llamada).Should().ThrowAsync<NativeSshException>()).Which.Message.Should().Be(mensaje);
            _ = _fabrica.Ultima.Should().BeNull();
            _ = _fabrica.Conexiones.Should().Be(0);
        }

        // ssh_input sobre un comando del destino (creado sin pasar por ssh_exec): rechazado sin enviar el texto.
        [Fact]
        public async Task Input_DestinoDeSoloLectura_SeRechazaSinEnviar()
        {
            // Arrange
            Peticion peticion = _gestor.Ejecutar("prod", "pregunta", TimeSpan.FromSeconds(30));
            await Task.Delay(300);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.EnviarEntradaAsync(peticion.IdTexto, "si", secreto: true)).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be(Rechazo("ssh_input"));
            _ = _fabrica.Ultima!.Comandos.Should().NotContain(c => c.Contains("si", StringComparison.Ordinal) && !c.Contains("pregunta", StringComparison.Ordinal));
        }

        // Las de lectura y ssh_set_cwd sí funcionan en el destino de solo lectura; los demás destinos, todo.
        [Fact]
        public async Task LecturaYSetCwd_YOtrosDestinos_Funcionan()
        {
            // Act
            string lectura = await _herramientas.LeerArchivoAsync("prod", "a.txt");
            ResultadoDirectorioTrabajo cwd = await _herramientas.FijarDirectorioAsync("prod", "/srv");
            ResultadoComando otro = await _herramientas.EjecutarAsync("pruebas", "echo hola");

            // Assert
            _ = lectura.Should().Be("[Líneas 1-1 de 1]\nhola\n");
            _ = cwd.Directorio.Should().Be("/srv");
            _ = otro.Estado.Should().Be(EstadoPeticionEnum.Completado);
        }

        [Fact]
        public void Mensaje_DiceQueHacerYQueNoSePuedeCambiar() =>
            _ = Rechazo("ssh_exec").Should().Be("Rechazado: ana@prod:22 está en modo solo lectura (configuración local de native_ssh, variable NATIVE_SSH_SOLO_LECTURA), así que ssh_exec no se ha ejecutado y no se ha enviado nada al servidor. Para ese destino están desactivadas ssh_exec, ssh_input, ssh_write_file y ssh_replace_file_content: usa ssh_read_file, ssh_search, ssh_find, ssh_list_dir, ssh_stat y ssh_set_cwd. No reintentes ni busques otra vía para modificar el servidor: este modo solo lo puede quitar quien configura native_ssh.");
    }
}
