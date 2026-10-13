using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;
using Xunit.Abstractions;

namespace NativeSsh.Test.Integracion
{
    // Escritura que no termina mientras el shell se abre (login real de 3 s, SshdLocalLoginLento.ForceCommand): el plazo o
    // la cancelación llegan antes de que el comando se envíe al shell (Peticion.IsComandoEnviado sigue en false). El
    // archivo no se toca, y el error lo afirma con certeza: «No se llegó a ejecutar: no se ha modificado», sin el «No consta
    // ninguna modificación… El script no confirmó cómo terminó… compruébalo con ssh_stat» de un script interrumpido, que
    // contradecía el aviso de la sesión («el comando no llegó a enviarse»). Antes se decidía con TiempoEjecucion > 0, que
    // solo dice si la petición salió de la cola (R07 de la auditoría de la integración).
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El sshd y el gestor se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class EscrituraAperturaLentaTests(ITestOutputHelper salida) : IAsyncLifetime
    {
        private readonly SshdPropio _sshd = new(string.Empty, SshdLocalLoginLento.ForceCommand);
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_lenta_{Guid.NewGuid():N}")).FullName;
        private GestorSesiones? _gestor;
        private OpcionesNativeSsh? _opciones;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (_gestor is not null)
                await _gestor.DisposeAsync();
            _sshd.Dispose();
            Directory.Delete(_directorio, recursive: true);
        }

        private HerramientasSsh Herramientas()
        {
            _opciones = _sshd.Sshd.Opciones(50);
            _gestor = new GestorSesiones(_sshd.Sshd.Fabrica(_opciones), new LimitadorConexiones(_opciones, TimeProvider.System), new RegistroPeticiones(_opciones, TimeProvider.System), _opciones, TimeProvider.System, NullLoggerFactory.Instance);
            return new HerramientasSsh(_gestor, _opciones);
        }

        // El primer comando abre el shell (3 s de login); ssh_reset_shell obliga a que la escritura vuelva a abrirlo.
        private async Task<(HerramientasSsh Normal, HerramientasSsh Rapida, string Destino)> PrepararAsync()
        {
            HerramientasSsh normal = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            ResultadoComando inicial = await normal.EjecutarAsync(destino, "true", esperarSegundos: 30).ComoComando();
            _ = inicial.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = normal.ReiniciarShell(destino);
            return (normal, new HerramientasSsh(_gestor!, _opciones!) { TimeoutForzado = TimeSpan.FromSeconds(1) }, destino);
        }

        private Peticion PeticionDe(string nombre) => _gestor!.ListarPeticiones().First(p => p.Comando.Contains(nombre, StringComparison.Ordinal));

        [Theory]
        [InlineData("sobrescribir")]
        [InlineData("anexar")]
        [InlineData("crear")]
        public async Task PlazoVencidoMientrasElShellSeAbre_DiceQueNoSeEjecutoNiSeModifico(string operacion)
        {
            // Arrange
            (HerramientasSsh _, HerramientasSsh rapida, string destino) = await PrepararAsync();
            string archivo = Path.Combine(_directorio, $"{operacion}.txt");
            if (operacion != "crear")
                await File.WriteAllTextAsync(archivo, "antes\n");

            // Act
            Exception? error = await Record.ExceptionAsync(() => rapida.EscribirArchivoAsync(destino, archivo, "despues\n", overwrite: operacion == "sobrescribir", append: operacion == "anexar"));
            Peticion peticion = PeticionDe($"{operacion}.txt");
            await Task.Delay(4000);
            salida.WriteLine($"{error?.Message}\npetición: {peticion.Estado} enviado={peticion.IsComandoEnviado} t={peticion.TiempoEjecucion}");

            // Assert
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            _ = peticion.IsComandoEnviado.Should().BeFalse();
            _ = peticion.TiempoEjecucion.Should().BePositive("la petición salió de la cola: es el caso que TiempoEjecucion > 0 daba por ejecutado");
            string mensaje = error.Should().BeOfType<NativeSshException>().Which.Message;
            _ = mensaje.Should().Contain($"No se llegó a ejecutar: '{archivo}' no se ha modificado.").And.Contain("no llegó a enviarse");
            _ = mensaje.Should().NotContain("No consta").And.NotContain("no confirmó cómo terminó").And.NotContain("ssh_stat");
            if (operacion == "crear")
                _ = File.Exists(archivo).Should().BeFalse();
            else
                _ = (await File.ReadAllTextAsync(archivo)).Should().Be("antes\n");
        }

        [Fact]
        public async Task CanceladaPorElTokenMientrasElShellSeAbre_DiceQueNoSeEjecutoNiSeModifico()
        {
            // Arrange
            (HerramientasSsh normal, HerramientasSsh _, string destino) = await PrepararAsync();
            string archivo = Path.Combine(_directorio, "token.txt");
            await File.WriteAllTextAsync(archivo, "antes\n");
            using CancellationTokenSource cancelacion = new(TimeSpan.FromMilliseconds(800));

            // Act
            Exception? error = await Record.ExceptionAsync(() => normal.EscribirArchivoAsync(destino, archivo, "despues\n", overwrite: true, cancellationToken: cancelacion.Token));
            Peticion peticion = PeticionDe("token.txt");
            await Task.Delay(4000);
            salida.WriteLine($"{error?.Message}\npetición: {peticion.Estado} enviado={peticion.IsComandoEnviado} t={peticion.TiempoEjecucion}");

            // Assert
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = peticion.IsComandoEnviado.Should().BeFalse();
            string mensaje = error.Should().BeAssignableTo<OperationCanceledException>().Which.Message;
            _ = mensaje.Should().Contain($"No se llegó a ejecutar: '{archivo}' no se ha modificado.").And.NotContain("No consta").And.NotContain("ssh_stat");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("antes\n");
        }
    }
}
