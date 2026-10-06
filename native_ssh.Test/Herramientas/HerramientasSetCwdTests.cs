using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Sesion.Falsos;

namespace NativeSsh.Test.Herramientas
{
    // ssh_set_cwd guarda el directorio en C# (lo usan los canales exec y se reexporta al recrear el shell) solo cuando el
    // shell ya lo ha exportado: si el comando no llega al shell o no termina, el directorio de trabajo de native_ssh no
    // cambia y el error dice cuál sigue siendo (A-decies, AD1).
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El gestor se libera en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    public sealed class HerramientasSetCwdTests : IAsyncLifetime
    {
        private const string Anterior = "/var/www/proyecto";
        private static readonly string RutaLarga = "/srv/larga/" + new string('x', 1100);
        private const string Destino = "ana@servidor:22";
        private const string Bloqueo = "bloqueo";
        private readonly FabricaFalsa _fabrica;
        private readonly OpcionesNativeSsh _opciones;
        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;
        // Respuesta del shell al comando de volver a $HOME: por defecto, el shell sale (ShellPerdido).
        private RespuestaFalsa _respuestaVolverAHome = new("/home/ana\n", 0, ComportamientoFalsoEnum.Salir);

        public HerramientasSetCwdTests()
        {
            _fabrica = new(Guion);
            _opciones = new() { UsuarioPorDefecto = "ana", EsperaTrasCtrlC = TimeSpan.FromMilliseconds(300) };
            _gestor = new GestorSesiones(_fabrica, new LimitadorConexiones(_opciones, TimeProvider.System), new RegistroPeticiones(_opciones, TimeProvider.System), _opciones, TimeProvider.System, NullLoggerFactory.Instance);
            _herramientas = new HerramientasSsh(_gestor, _opciones);
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync() => _gestor.DisposeAsync().AsTask();

        private RespuestaFalsa Guion(string comando)
        {
            if (comando.Contains("pwd -P", StringComparison.Ordinal))
            {
                if (comando.Contains("/srv/validacion_colgada", StringComparison.Ordinal))
                    return new(string.Empty, 0, ComportamientoFalsoEnum.Colgado);

                if (comando.Contains("/srv/no_existe", StringComparison.Ordinal))
                    return new("Error: la ruta no existe: /srv/no_existe\n", 1);

                // Mientras el shell valida, otro comando que no termina entra en la cola: el export queda detrás.
                if (comando.Contains("/srv/en_cola", StringComparison.Ordinal))
                {
                    _ = _gestor.Ejecutar("servidor", Bloqueo, TimeSpan.FromMinutes(5));
                    return MarcarDirectorio(comando, "/srv/en_cola");
                }

                string directorio = comando.Contains("/srv/se_cae_tras_validar", StringComparison.Ordinal) ? "/srv/se_cae_tras_validar"
                    : comando.Contains("/srv/perdido", StringComparison.Ordinal) ? "/srv/perdido"
                    : comando.Contains("/srv/colgado", StringComparison.Ordinal) ? "/srv/colgado"
                    : comando.Contains("/srv/export_falla", StringComparison.Ordinal) ? "/srv/export_falla"
                    : comando.Contains("/srv/larga", StringComparison.Ordinal) ? RutaLarga
                    : Anterior;
                // Tras validar, el shell sale y ni se puede volver a abrir ni a conectar: el export no llega a enviarse.
                if (directorio == "/srv/se_cae_tras_validar")
                {
                    _fabrica.Ultima!.CaerAlAbrirShell();
                    _fabrica.ErrorAlConectar = new InvalidOperationException("sin red");
                    return MarcarDirectorio(comando, directorio) with { Comportamiento = ComportamientoFalsoEnum.ResponderYSalir };
                }

                return MarcarDirectorio(comando, directorio);
            }

            return comando.StartsWith("export __NS_CWD='/srv/perdido'", StringComparison.Ordinal) ? new("", 0, ComportamientoFalsoEnum.Salir)
                : comando.StartsWith("export __NS_CWD='/srv/colgado'", StringComparison.Ordinal) ? new("", 0, ComportamientoFalsoEnum.Colgado)
                : comando.StartsWith("export __NS_CWD='/srv/export_falla'", StringComparison.Ordinal) ? new("bash: __NS_CWD: variable de sólo lectura\n", 1)
                : comando.Contains("unset __NS_CWD", StringComparison.Ordinal) ? _respuestaVolverAHome
                : comando == Bloqueo ? new(string.Empty, 0, ComportamientoFalsoEnum.Colgado)
                : new(string.Empty, 0);
        }

        // Como el script real: la ruta resuelta en base64 tras el marcador con el id de la petición.
        private static RespuestaFalsa MarcarDirectorio(string comando, string directorio)
        {
            const string Formato = "__NS_CWD_%s__ %s\\n' ";
            int posicion = comando.IndexOf(Formato, StringComparison.Ordinal) + Formato.Length;
            string id = comando[posicion..(posicion + 32)];
            return new($"__NS_CWD_{id}__ {Convert.ToBase64String(Encoding.UTF8.GetBytes(directorio))}\n", 0);
        }

        private string? DirectorioDeLaSesion() => _gestor.ListarSesiones().Single().DirectorioTrabajo;

        // Espera a que el shell esté ejecutando el comando que empieza por 'inicio' (como mucho 10 s).
        private async Task EsperarEjecutandoAsync(string inicio)
        {
            for (int i = 0; i < 200; i++)
            {
                if (_gestor.ListarPeticiones().Any(p => p.Estado == EstadoPeticionEnum.Ejecutando && p.Comando.StartsWith(inicio, StringComparison.Ordinal)))
                    return;

                await Task.Delay(50);
            }

            throw new TimeoutException($"El shell no llegó a ejecutar '{inicio}'.");
        }

        // La conexión cae entre la validación y el export: el export no llega al shell, así que nada cambió.
        [Fact]
        public async Task ConexionQueCaeEntreLaValidacionYElExport_NoCambiaElDirectorio()
        {
            // Arrange
            _ = await _herramientas.FijarDirectorioAsync("servidor", Anterior);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.FijarDirectorioAsync("servidor", "/srv/se_cae_tras_validar"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().StartWith("La operación no se hizo: el comando no llegó a ejecutarse")
                .And.Contain($"El directorio de trabajo sigue siendo {Anterior}.").And.NotContain("puede haber pasado");
            _ = DirectorioDeLaSesion().Should().Be(Anterior);
        }

        // El shell se pierde con el export ya enviado: no se sabe si llegó a ejecutarlo, pero ese shell ya no existe y el
        // nuevo se inicializa con el directorio de C#, que no cambió. El error no dice que no se envió.
        [Fact]
        public async Task ShellPerdidoConElExportEnviado_NoCambiaElDirectorioYElShellNuevoUsaElAnterior()
        {
            // Arrange
            _ = await _herramientas.FijarDirectorioAsync("servidor", Anterior);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.FijarDirectorioAsync("servidor", "/srv/perdido"))
                .Should().ThrowAsync<NativeSshException>()).Which;
            _ = await _herramientas.EjecutarAsync("servidor", "hola");

            // Assert
            _ = error.Message.Should().Contain("estado: ShellPerdido").And.NotContain("no llegó a ejecutarse")
                .And.Contain($"El directorio de trabajo sigue siendo {Anterior}: lo usan las herramientas de lectura y el shell nuevo se abrirá en él.")
                .And.NotContain("puede haber pasado");
            _ = DirectorioDeLaSesion().Should().Be(Anterior);
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Anterior));
            _ = _fabrica.Ultima!.CanalActual!.Inicializaciones.Should().ContainSingle().Which.Should().Contain(base64);
        }

        // Volver a $HOME con el shell perdido a mitad: el directorio sigue siendo el anterior.
        [Fact]
        public async Task VolverAHomeConElShellPerdido_NoOlvidaElDirectorio()
        {
            // Arrange
            _ = await _herramientas.FijarDirectorioAsync("servidor", Anterior);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.FijarDirectorioAsync("servidor", null))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("estado: ShellPerdido").And.Contain($"El directorio de trabajo sigue siendo {Anterior}");
            _ = DirectorioDeLaSesion().Should().Be(Anterior);
        }

        // El export no termina (el token lo cancela) con el shell vivo: C# no cambia y el error dice que el shell de
        // ssh_exec puede haberlo cambiado ya y cómo dejarlos iguales.
        [Fact]
        public async Task ExportCanceladoConElShellVivo_NoCambiaElDirectorioYDiceQueElShellPuedeHaberlo()
        {
            // Arrange
            _ = await _herramientas.FijarDirectorioAsync("servidor", Anterior);
            using CancellationTokenSource cancelacion = new(TimeSpan.FromMilliseconds(500));

            // Act
            OperationCanceledException error = (await FluentActions.Awaiting(() => _herramientas.FijarDirectorioAsync("servidor", "/srv/colgado", cancelacion.Token))
                .Should().ThrowAsync<OperationCanceledException>()).Which;

            // Assert
            _ = error.Message.Should().Contain($"El directorio de trabajo sigue siendo {Anterior} para las herramientas de lectura y para un shell nuevo, pero el shell actual de ssh_exec puede haber pasado ya a /srv/colgado: repite ssh_set_cwd (o usa ssh_reset_shell para recrearlo en {Anterior}).");
            _ = DirectorioDeLaSesion().Should().Be(Anterior);
        }

        // La validación no termina (el token la cancela): va en un subshell, así que nada cambió, y el error lo dice.
        [Fact]
        public async Task ValidacionCancelada_DiceQueElDirectorioNoCambio()
        {
            // Arrange
            _ = await _herramientas.FijarDirectorioAsync("servidor", Anterior);
            using CancellationTokenSource cancelacion = new(TimeSpan.FromMilliseconds(500));

            // Act
            OperationCanceledException error = (await FluentActions.Awaiting(() => _herramientas.FijarDirectorioAsync("servidor", "/srv/validacion_colgada", cancelacion.Token))
                .Should().ThrowAsync<OperationCanceledException>()).Which;

            // Assert
            _ = error.Message.Should().EndWith($"El directorio de trabajo sigue siendo {Anterior}.");
            _ = DirectorioDeLaSesion().Should().Be(Anterior);
        }

        // El export termina con error (p. ej. __NS_CWD de solo lectura): el shell no cambió y C# tampoco.
        [Fact]
        public async Task ExportQueFalla_NoCambiaElDirectorio()
        {
            // Arrange
            _ = await _herramientas.FijarDirectorioAsync("servidor", Anterior);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.FijarDirectorioAsync("servidor", "/srv/export_falla"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be($"bash: __NS_CWD: variable de sólo lectura El directorio de trabajo sigue siendo {Anterior}.");
            _ = DirectorioDeLaSesion().Should().Be(Anterior);
        }

        // Un directorio resuelto que supera el máximo que la sesión puede reexportar se rechaza antes del export: ni el
        // shell ni C# cambian.
        [Fact]
        public async Task DirectorioDemasiadoLargo_SeRechazaSinExportarlo()
        {
            // Arrange
            _ = await _herramientas.FijarDirectorioAsync("servidor", Anterior);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.FijarDirectorioAsync("servidor", "/srv/larga"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be($"La ruta del directorio de trabajo supera 1024 bytes. El directorio de trabajo sigue siendo {Anterior}.");
            _ = _fabrica.Ultima!.Comandos.Should().NotContain(c => c.StartsWith("export __NS_CWD='/srv/larga", StringComparison.Ordinal));
            _ = DirectorioDeLaSesion().Should().Be(Anterior);
        }

        // ssh_disconnect a mitad del export (que no termina): la sesión ya no existe y la próxima conexión empieza en $HOME.
        // El error lo dice; no afirma que el directorio siga siendo el anterior ni propone ssh_reset_shell (A-undecies, AU1;
        // sonda Q02 de la auditoría del A-decies).
        [Fact]
        public async Task DisconnectDuranteElExport_DiceQueLaSesionSeCerroYLaProximaEmpiezaEnHome()
        {
            // Arrange
            _ = await _herramientas.FijarDirectorioAsync("servidor", Anterior);
            Task<ResultadoDirectorioTrabajo> fijar = _herramientas.FijarDirectorioAsync("servidor", "/srv/colgado");
            await EsperarEjecutandoAsync("export __NS_CWD='/srv/colgado'");

            // Act
            _ = await _herramientas.DesconectarAsync("servidor");
            NativeSshException error = (await FluentActions.Awaiting(() => fijar).Should().ThrowAsync<NativeSshException>()).Which;
            bool hasSesionTrasElError = _gestor.ListarSesiones().Count > 0;
            _ = await _herramientas.EjecutarAsync("servidor", "hola");

            // Assert
            _ = error.Message.Should().EndWith($"La sesión con {Destino} se cerró mientras tanto (ssh_disconnect) y con ella se olvidó el directorio de trabajo: la próxima herramienta abrirá una conexión nueva en $HOME (~).")
                .And.NotContain("sigue siendo").And.NotContain("ssh_reset_shell");
            _ = hasSesionTrasElError.Should().BeFalse();
            _ = DirectorioDeLaSesion().Should().BeNull();
        }

        // Lo mismo al volver a $HOME (sonda Q10).
        [Fact]
        public async Task DisconnectAlVolverAHome_DiceQueLaSesionSeCerroYLaProximaEmpiezaEnHome()
        {
            // Arrange
            _respuestaVolverAHome = new(string.Empty, 0, ComportamientoFalsoEnum.Colgado);
            _ = await _herramientas.FijarDirectorioAsync("servidor", Anterior);
            Task<ResultadoDirectorioTrabajo> fijar = _herramientas.FijarDirectorioAsync("servidor", null);
            await EsperarEjecutandoAsync("unset __NS_CWD");

            // Act
            _ = await _herramientas.DesconectarAsync("servidor");
            NativeSshException error = (await FluentActions.Awaiting(() => fijar).Should().ThrowAsync<NativeSshException>()).Which;
            _ = await _herramientas.EjecutarAsync("servidor", "hola");

            // Assert
            _ = error.Message.Should().EndWith($"La sesión con {Destino} se cerró mientras tanto (ssh_disconnect) y con ella se olvidó el directorio de trabajo: la próxima herramienta abrirá una conexión nueva en $HOME (~).")
                .And.NotContain("sigue siendo").And.NotContain("ssh_reset_shell");
            _ = DirectorioDeLaSesion().Should().BeNull();
        }

        // Volver a $HOME falla en el shell (p. ej. __NS_CWD de solo lectura: unset falla): C# no olvida el directorio y el
        // error dice cuál sigue siendo (A-undecies, AU2; mutación T02).
        [Fact]
        public async Task VolverAHomeQueFalla_NoOlvidaElDirectorio()
        {
            // Arrange
            _respuestaVolverAHome = new("bash: unset: __NS_CWD: cannot unset: readonly variable\n", 1);
            _ = await _herramientas.FijarDirectorioAsync("servidor", Anterior);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.FijarDirectorioAsync("servidor", null))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be($"bash: unset: __NS_CWD: cannot unset: readonly variable El directorio de trabajo sigue siendo {Anterior}.");
            _ = DirectorioDeLaSesion().Should().Be(Anterior);
            _ = _fabrica.Ultima!.Comandos.Should().Contain("unset __NS_CWD && export __NS_CWD=\"\" && pwd");
        }

        // La validación falla (la ruta no existe): el error dice también qué directorio sigue (A-undecies, AU3).
        [Fact]
        public async Task ValidacionQueFalla_DiceQueElDirectorioSigueSiendoElAnterior()
        {
            // Arrange
            _ = await _herramientas.FijarDirectorioAsync("servidor", Anterior);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.FijarDirectorioAsync("servidor", "/srv/no_existe"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be($"Error: la ruta no existe: /srv/no_existe El directorio de trabajo sigue siendo {Anterior}.");
            _ = DirectorioDeLaSesion().Should().Be(Anterior);
        }

        // El export vence en la cola, detrás de otro comando: no llegó a enviarse al shell, así que nada cambió y el error lo
        // dice con certeza, sin «puede haber pasado» (A-undecies, AU3).
        [Fact]
        public async Task ExportQueVenceEnLaCola_DiceQueNoSeEnvioYQueSigueEnElAnterior()
        {
            // Arrange
            _ = await _herramientas.FijarDirectorioAsync("servidor", Anterior);
            HerramientasSsh herramientas = new(_gestor, _opciones) { TimeoutForzado = TimeSpan.FromSeconds(1) };

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.FijarDirectorioAsync("servidor", "/srv/en_cola"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("(estado: Encolado)")
                .And.EndWith($"El comando no llegó a enviarse al shell (seguía en la cola): el directorio de trabajo sigue siendo {Anterior}.")
                .And.NotContain("puede haber pasado");
            _ = _fabrica.Ultima!.Comandos.Should().NotContain(c => c.StartsWith("export __NS_CWD='/srv/en_cola'", StringComparison.Ordinal));
            _ = DirectorioDeLaSesion().Should().Be(Anterior);
        }

        // Lo mismo si el token cancela el export mientras espera en la cola.
        [Fact]
        public async Task ExportCanceladoEnLaCola_DiceQueNoSeEnvioYQueSigueEnElAnterior()
        {
            // Arrange
            _ = await _herramientas.FijarDirectorioAsync("servidor", Anterior);
            using CancellationTokenSource cancelacion = new(TimeSpan.FromMilliseconds(500));

            // Act
            OperationCanceledException error = (await FluentActions.Awaiting(() => _herramientas.FijarDirectorioAsync("servidor", "/srv/en_cola", cancelacion.Token))
                .Should().ThrowAsync<OperationCanceledException>()).Which;

            // Assert
            _ = error.Message.Should().EndWith($"El comando no llegó a enviarse al shell (seguía en la cola): el directorio de trabajo sigue siendo {Anterior}.")
                .And.NotContain("puede haber pasado");
            _ = _fabrica.Ultima!.Comandos.Should().NotContain(c => c.StartsWith("export __NS_CWD='/srv/en_cola'", StringComparison.Ordinal));
            _ = DirectorioDeLaSesion().Should().Be(Anterior);
        }
    }
}
