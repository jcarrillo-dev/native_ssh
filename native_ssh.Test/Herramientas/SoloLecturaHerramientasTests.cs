using System.Linq;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Sesion.Falsos;
using NativeSsh.Test.Utilidades;

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
            _ = DestinosSoloLectura.TryParsear("prod", out DestinosSoloLectura soloLectura, out _, ResolverFalso).Should().BeTrue();
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana", SoloLectura = soloLectura };
            _gestor = new GestorSesiones(_fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            _herramientas = new HerramientasSsh(_gestor, opciones);
        }

        public ValueTask DisposeAsync() => _gestor.DisposeAsync();

        // prod → 10.0.0.5 y pruebas → 10.0.0.9; cualquier otro nombre no se resuelve.
        private static Task<IPAddress[]> ResolverFalso(string nombre, CancellationToken _) => nombre switch
        {
            "prod" => Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") }),
            "pruebas" => Task.FromResult(new[] { IPAddress.Parse("10.0.0.9") }),
            _ => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound))
        };

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

        // ssh_exec con guardarSalida también se rechaza entero, antes de registrar la petición o conectar: nada que limpiar.
        [Fact]
        public async Task GuardarSalida_DestinoDeSoloLectura_SeRechazaSinRegistrarNiConectar()
        {
            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.EjecutarAsync("prod", "echo hola", guardarSalida: true)).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be(Rechazo("ssh_exec"));
            _ = _gestor.ListarPeticiones().Should().BeEmpty();
            _ = _gestor.ListarSesiones().Should().BeEmpty();
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
            ResultadoComando otro = await _herramientas.EjecutarAsync("pruebas", "echo hola").ComoComando();

            // Assert
            _ = lectura.Should().Be("[Líneas 1-1 de 1]\nhola\n");
            _ = cwd.Directorio.Should().Be("/srv");
            _ = otro.Estado.Should().Be(EstadoPeticionEnum.Completado);
        }

        // Un destino que no se puede comparar (su nombre no se resuelve) se rechaza por seguridad, sin conectar, y el mensaje
        // dice qué nombre no se resolvió.
        [Fact]
        public async Task Escritura_DestinoNoResuelto_SeRechazaPorSeguridad()
        {
            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.EjecutarAsync("nadie.ejemplo", "echo hola")).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be(HerramientasSsh.MensajeSoloLecturaNoComprobado("ana@nadie.ejemplo:22", "ssh_exec", "nadie.ejemplo"));
            _ = _fabrica.Conexiones.Should().Be(0);
        }

        [Fact]
        public void MensajeNoComprobado_DiceQueNoSeResolvioYQueSeRechazaPorSeguridad() =>
            _ = HerramientasSsh.MensajeSoloLecturaNoComprobado("ana@prod:22", "ssh_exec", "prod").Should().Be("Rechazado: ana@prod:22: no se pudo comprobar si es de solo lectura (no se resolvió prod); se rechaza por seguridad (configuración local de native_ssh, variable NATIVE_SSH_SOLO_LECTURA), así que ssh_exec no se ha ejecutado y no se ha enviado nada al servidor. ssh_read_file, ssh_search, ssh_find, ssh_list_dir, ssh_stat y ssh_set_cwd siguen disponibles. No busques otra vía para modificar el servidor: la comprobación se repite en cada llamada y pasará cuando el nombre se resuelva (DNS o /etc/hosts).");

        // Con el cupo de resoluciones lleno, ssh_exec se rechaza sin conectar con el mensaje del cupo, no con «no se resolvió».
        [Fact]
        public async Task Escritura_ConElCupoDeResolucionesLleno_LoDice()
        {
            // Arrange
            TaskCompletionSource<IPAddress[]> nunca = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<IPAddress[]> Resolver(string nombre, CancellationToken token) =>
                nombre.StartsWith("colgado", StringComparison.Ordinal) ? nunca.Task : ResolverFalso(nombre, token);

            _ = DestinosSoloLectura.TryParsear("10.0.0.5", out DestinosSoloLectura soloLectura, out _, Resolver).Should().BeTrue();
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana", SoloLectura = soloLectura };
            FabricaFalsa fabrica = new(Guion);
            await using GestorSesiones gestor = new(fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);
            Task<ComprobacionSoloLectura>[] colgados = [.. Enumerable.Range(0, DestinosSoloLectura.MaxResolucionesEnCurso).Select(i => soloLectura.ComprobarAsync(gestor.ParsearDestino($"colgado{i}"), CancellationToken.None))];

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EjecutarAsync("pruebas", "echo hola")).Should().ThrowAsync<NativeSshException>()).Which;
            nunca.SetResult([IPAddress.Parse("10.0.0.9")]);
            _ = await Task.WhenAll(colgados);

            // Assert
            _ = error.Message.Should().Be(HerramientasSsh.MensajeSoloLecturaNoComprobado("ana@pruebas:22", "ssh_exec", "pruebas", isCupoLleno: true));
            _ = fabrica.Conexiones.Should().Be(0);
        }

        // Sin cupo para resolver, el mensaje no culpa al DNS: dice que no se llegó a resolver y que se vuelva a intentar.
        [Fact]
        public void MensajeCupoLleno_DiceQueNoSeLlegoAResolverYQueSeVuelvaAIntentar() =>
            _ = HerramientasSsh.MensajeSoloLecturaNoComprobado("ana@prod:22", "ssh_exec", "prod", isCupoLleno: true).Should().Be("Rechazado: ana@prod:22: no se pudo comprobar si es de solo lectura (no se llegó a resolver prod: demasiadas resoluciones a la vez); se rechaza por seguridad (configuración local de native_ssh, variable NATIVE_SSH_SOLO_LECTURA), así que ssh_exec no se ha ejecutado y no se ha enviado nada al servidor. ssh_read_file, ssh_search, ssh_find, ssh_list_dir, ssh_stat y ssh_set_cwd siguen disponibles. No busques otra vía para modificar el servidor: vuelve a intentarlo en unos segundos, cuando terminen las resoluciones en curso; la comprobación se repite en cada llamada.");

        // ssh_status dice qué destinos están configurados en solo lectura y marca cada sesión.
        [Fact]
        public async Task Estado_MarcaLosDestinosDeSoloLectura()
        {
            // Arrange
            _ = await _herramientas.LeerArchivoAsync("prod", "a.txt");
            _ = await _herramientas.EjecutarAsync("pruebas", "echo hola");

            // Act
            ResultadoEstado estado = _herramientas.Estado();
            string json = System.Text.Json.JsonSerializer.Serialize(estado, ContextoJsonNativeSsh.Default.ResultadoEstado);

            // Assert
            _ = estado.SoloLectura.Should().Equal("prod");
            _ = estado.Sesiones.Single(s => s.Destino == "ana@prod:22").IsSoloLectura.Should().BeTrue();
            _ = estado.Sesiones.Single(s => s.Destino == "ana@pruebas:22").IsSoloLectura.Should().BeNull("el nombre del destino se resuelve en cada llamada, no se recuerda");
            _ = json.Should().Contain("\"soloLectura\":[\"prod\"]").And.Contain("\"isSoloLectura\":true");
        }

        // Destino IPv6 con zona ('%1', '%lo') de una entrada de loopback: se rechaza sin conectar y ssh_status lo marca.
        [Theory]
        [InlineData("localhost", "[::1%1]")]
        [InlineData("::1", "ana@[::1%lo]:22")]
        [InlineData("[::1]:22", "::1%1")]
        public async Task Ipv6ConZona_SeRechazaYEstadoLoMarca(string configuracion, string destino)
        {
            // Arrange
            _ = DestinosSoloLectura.TryParsear(configuracion, out DestinosSoloLectura soloLectura, out _, ResolverFalso).Should().BeTrue();
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana", SoloLectura = soloLectura };
            FabricaFalsa fabrica = new(Guion);
            await using GestorSesiones gestor = new(fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(destino, "a.txt", "x", overwrite: true)).Should().ThrowAsync<NativeSshException>()).Which;
            int conexionesTrasRechazo = fabrica.Conexiones;
            _ = await herramientas.LeerArchivoAsync(destino, "a.txt");
            ResultadoEstado estado = herramientas.Estado();

            // Assert
            _ = error.Message.Should().StartWith("Rechazado: ").And.Contain("está en modo solo lectura");
            _ = conexionesTrasRechazo.Should().Be(0);
            _ = estado.Sesiones.Should().ContainSingle().Which.IsSoloLectura.Should().BeTrue();
        }

        [Fact]
        public void Mensaje_DiceQueHacerYQueNoSePuedeCambiar() =>
            _ = Rechazo("ssh_exec").Should().Be("Rechazado: ana@prod:22 está en modo solo lectura (configuración local de native_ssh, variable NATIVE_SSH_SOLO_LECTURA), así que ssh_exec no se ha ejecutado y no se ha enviado nada al servidor. Para ese destino están desactivadas ssh_exec, ssh_input, ssh_write_file y ssh_replace_file_content: usa ssh_read_file, ssh_search, ssh_find, ssh_list_dir, ssh_stat y ssh_set_cwd. No reintentes ni busques otra vía para modificar el servidor: este modo solo lo puede quitar quien configura native_ssh.");
    }
}
