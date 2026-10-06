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
        private readonly FabricaFalsa _fabrica;
        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;

        public HerramientasSetCwdTests()
        {
            _fabrica = new(Guion);
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana", EsperaTrasCtrlC = TimeSpan.FromMilliseconds(300) };
            _gestor = new GestorSesiones(_fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            _herramientas = new HerramientasSsh(_gestor, opciones);
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync() => _gestor.DisposeAsync().AsTask();

        private RespuestaFalsa Guion(string comando)
        {
            if (comando.Contains("pwd -P", StringComparison.Ordinal))
            {
                string directorio = comando.Contains("/srv/se_cae_tras_validar", StringComparison.Ordinal) ? "/srv/se_cae_tras_validar"
                    : comando.Contains("/srv/perdido", StringComparison.Ordinal) ? "/srv/perdido"
                    : comando.Contains("/srv/colgado", StringComparison.Ordinal) ? "/srv/colgado"
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
                : comando.Contains("unset __NS_CWD", StringComparison.Ordinal) ? new("/home/ana\n", 0, ComportamientoFalsoEnum.Salir)
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
                .And.Contain($"El directorio de trabajo sigue siendo {Anterior}.");
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
                .And.Contain($"El directorio de trabajo sigue siendo {Anterior}");
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
            _ = error.Message.Should().Contain($"El directorio de trabajo sigue siendo {Anterior}")
                .And.Contain("/srv/colgado").And.Contain("ssh_set_cwd");
            _ = DirectorioDeLaSesion().Should().Be(Anterior);
        }
    }
}
