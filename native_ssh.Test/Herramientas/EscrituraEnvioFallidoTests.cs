using System.Diagnostics.CodeAnalysis;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Sesion.Falsos;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Herramientas
{
    // Escritura cuyo comando falla al escribirse en el canal del shell: la petición termina como ShellPerdido sin la señal de
    // comando enviado, pero la escritura pudo quedarse a medias (parte del script pudo llegar al shell), así que el error no
    // puede afirmar que el archivo no se ha modificado ni que no se ejecutó nada (I04 de la revisión de la integración). Es
    // lo que distingue IsInterrumpidaSinEnviar (Cancelado o TiempoAgotado sin la señal) de un «sin la señal» a secas.
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El gestor se libera en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    public sealed class EscrituraEnvioFallidoTests : IAsyncLifetime
    {
        private readonly FabricaFalsa _fabrica = new(_ => new RespuestaFalsa(string.Empty, 0));
        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;

        public EscrituraEnvioFallidoTests()
        {
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana", EsperaTrasCtrlC = TimeSpan.FromMilliseconds(400) };
            _gestor = new GestorSesiones(_fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            _herramientas = new HerramientasSsh(_gestor, opciones);
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync() => _gestor.DisposeAsync().AsTask();

        [Fact]
        public async Task EnvioDelComandoFallido_NoAfirmaQueNoSeModifico()
        {
            // Arrange
            _ = await _herramientas.EjecutarAsync("servidor", "true", esperarSegundos: 10).ComoComando();
            _fabrica.Ultima!.CanalActual!.RechazarEscrituras();

            // Act
            Exception? error = await Record.ExceptionAsync(() => _herramientas.EscribirArchivoAsync("servidor", "/srv/a.txt", "hola\n", overwrite: true));
            Peticion escritura = _gestor.ListarPeticiones().First(p => p.Comando.Contains("a.txt", StringComparison.Ordinal));

            // Assert
            _ = escritura.Estado.Should().Be(EstadoPeticionEnum.ShellPerdido);
            _ = escritura.IsComandoEnviado.Should().BeFalse();
            _ = error.Should().BeOfType<NativeSshException>().Which.Message.Should().NotContain("no se ha modificado").And.NotContain("No se llegó a ejecutar").And.NotContain("no llegó a ejecutarse");
        }
    }
}
