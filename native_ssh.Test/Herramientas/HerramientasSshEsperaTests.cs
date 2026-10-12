using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Sesion.Falsos;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Herramientas
{
    // Tope de 'esperarSegundos' en ssh_exec, ssh_output y ssh_input: se acota entre 0 y HerramientasSsh.MaxSegundosEspera.
    // Sin la cota superior, int.MaxValue llegaría a Task.WaitAsync como un plazo de 68 años, más del que admite (unos
    // 49,7 días), y la herramienta fallaría con ArgumentOutOfRangeException en lugar de esperar y atender la cancelación;
    // sin la inferior, un valor negativo daría el mismo error en lugar de responder sin esperar.
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El gestor se libera en IAsyncLifetime.DisposeAsync.")]
    public sealed class HerramientasSshEsperaTests : IAsyncLifetime
    {
        private static readonly TimeSpan Limite = TimeSpan.FromSeconds(5);

        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;

        public HerramientasSshEsperaTests()
        {
            FabricaFalsa fabrica = new(comando => comando == "colgado"
                ? new RespuestaFalsa("empezando\n", 0, ComportamientoFalsoEnum.Colgado)
                : new RespuestaFalsa($"eco:{comando}\n"));
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana" };
            _gestor = new GestorSesiones(fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            _herramientas = new HerramientasSsh(_gestor, opciones);
        }

        // IAsyncLifetime (no IAsyncDisposable): es lo que xUnit v2 invoca al terminar cada prueba.
        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync() => _gestor.DisposeAsync().AsTask();

        [Fact]
        public async Task Exec_EsperaEnormeConElTokenCancelado_LanzaOperationCanceled()
        {
            // Arrange
            using CancellationTokenSource cancelado = new();
            await cancelado.CancelAsync();

            // Act & Assert
            _ = await FluentActions.Awaiting(() => _herramientas.EjecutarAsync("servidor", "colgado", esperarSegundos: int.MaxValue, cancellationToken: cancelado.Token).ComoComando())
                .Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task Output_EsperaEnormeConElTokenCancelado_LanzaOperationCanceled()
        {
            // Arrange
            string id = await LanzarComandoColgadoAsync();
            using CancellationTokenSource cancelado = new();
            await cancelado.CancelAsync();

            // Act & Assert
            _ = await FluentActions.Awaiting(() => _herramientas.LeerSalidaAsync(id, esperarSegundos: int.MaxValue, cancellationToken: cancelado.Token).ComoComando())
                .Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task Input_EsperaEnormeConElTokenCancelado_LanzaOperationCanceled()
        {
            // Arrange
            string id = await LanzarComandoColgadoAsync();
            using CancellationTokenSource cancelado = new();
            await cancelado.CancelAsync();

            // Act & Assert
            _ = await FluentActions.Awaiting(() => _herramientas.EnviarEntradaAsync(id, "s", esperarSegundos: int.MaxValue, cancellationToken: cancelado.Token).ComoComando())
                .Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task Output_EsperaEnorme_EsperaHastaQueSeCancela()
        {
            // Arrange
            string id = await LanzarComandoColgadoAsync();
            using CancellationTokenSource cancelacion = new();

            // Act
            Task<ResultadoComando> lectura = _herramientas.LeerSalidaAsync(id, esperarSegundos: int.MaxValue, cancellationToken: cancelacion.Token).ComoComando();
            bool isEsperando = !lectura.IsCompleted;
            await cancelacion.CancelAsync();

            // Assert: la espera empieza (no falla al momento) y termina con la cancelación, no con el tope de 120 s.
            _ = isEsperando.Should().BeTrue();
            _ = await FluentActions.Awaiting(() => lectura.WaitAsync(Limite)).Should().ThrowAsync<OperationCanceledException>();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public async Task Output_EsperaCeroONegativa_DevuelveElEstadoSinEsperar(int esperarSegundos)
        {
            // Arrange
            string id = await LanzarComandoColgadoAsync();

            // Act
            ResultadoComando resultado = await _herramientas.LeerSalidaAsync(id, esperarSegundos: esperarSegundos).ComoComando().WaitAsync(Limite);

            // Assert
            _ = resultado.Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
        }

        // Un comando que no termina, ya en el shell: ssh_input solo admite entrada cuando su salida es la que se está
        // leyendo, y la primera línea ("empezando") lo confirma.
        private async Task<string> LanzarComandoColgadoAsync()
        {
            ResultadoComando lanzado = await _herramientas.EjecutarAsync("servidor", "colgado", esperarSegundos: 0).ComoComando();
            Peticion peticion = _gestor.BuscarPeticion(lanzado.Id);
            await EsperasPrueba.EsperarHastaAsync(() => peticion.Estado == EstadoPeticionEnum.Ejecutando && peticion.Salida.Longitud > 0);
            return lanzado.Id;
        }
    }
}
