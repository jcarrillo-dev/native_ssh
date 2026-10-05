using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Servidor congelado (SIGSTOP a los sshd-session de la conexión de la prueba) más tiempo que el ConnectionInfo.Timeout
    // de SSH.NET, que es TimeoutConexion. Cada prueba arranca su propio sshd con MaxSessions 5 (el shell y los 4 canales
    // exec, sin margen: cualquier canal que quede abierto en el servidor se nota) y LogLevel DEBUG (para contar sesiones).
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El sshd se libera en IAsyncLifetime.DisposeAsync.")]
    public sealed class ServidorCongeladoTests : IAsyncLifetime
    {
        private const string Configuracion = "LogLevel DEBUG\nMaxSessions 5";

        // Tope de cualquier espera con el servidor congelado: si el código no respetara su límite local, la prueba falla
        // en lugar de colgarse con los procesos detenidos (el kill -CONT del finally siempre llega).
        private static readonly TimeSpan EsperaMaximaCongelado = TimeSpan.FromSeconds(30);

        private readonly SshdPropio _sshd = new(Configuracion);

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync()
        {
            _sshd.Dispose();
            return Task.CompletedTask;
        }

        private OpcionesNativeSsh Opciones(TimeSpan timeoutConexion) => _sshd.Sshd.Opciones() with { TimeoutConexion = timeoutConexion };

        private GestorSesiones Gestor(OpcionesNativeSsh opciones) => new(
            _sshd.Sshd.Fabrica(opciones),
            new LimitadorConexiones(opciones, TimeProvider.System),
            new RegistroPeticiones(opciones, TimeProvider.System),
            opciones,
            TimeProvider.System,
            NullLoggerFactory.Instance);

        private static async Task<string> MensajeDeFalloAsync(Task tarea)
        {
            try
            {
                await tarea;
                return "sin error";
            }
            catch (NativeSshException ex)
            {
                return ex.Message;
            }
        }

        private static async Task EsperarHastaAsync(Stopwatch reloj, TimeSpan instante)
        {
            while (reloj.Elapsed < instante)
                await Task.Delay(100);
        }

        [Fact]
        public async Task CongeladoMasQueElTimeoutDeSshNet_LasAperturasDeLosCanalesExecEsperanYNoQuedanHuerfanos()
        {
            // Arrange: SSH.NET abandonaría una apertura sin respuesta a los 6 s.
            OpcionesNativeSsh opciones = Opciones(TimeSpan.FromSeconds(6));
            await using GestorSesiones gestor = Gestor(opciones);
            HerramientasSsh herramientas = new(gestor, opciones);
            string destino = _sshd.Sshd.Destino.Clave;
            _ = await herramientas.EjecutarAsync(destino, "true");
            SesionShell sesion = gestor.ListarSesiones().Single();
            string[] propios = _sshd.Sshd.ProcesosDeConexion();
            int aceptadas = _sshd.Sshd.ConexionesAceptadas();
            string[] errores;
            int activosCongelado;

            // Act: cuatro lecturas con plazo de 1 s (límite local de 4 s) y el servidor congelado 9 s.
            try
            {
                _ = BashLocal.Ejecutar($"kill -STOP {string.Join(' ', propios)}");
                Stopwatch reloj = Stopwatch.StartNew();
                errores = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => MensajeDeFalloAsync(gestor.EjecutarExecAsync(destino, "echo l", TimeSpan.FromSeconds(1), CancellationToken.None)))).WaitAsync(EsperaMaximaCongelado);
                await EsperarHastaAsync(reloj, TimeSpan.FromSeconds(9));
                activosCongelado = sesion.CanalesExecActivos;
            }
            finally
            {
                _ = BashLocal.Ejecutar($"kill -CONT {string.Join(' ', propios)}");
            }

            await EsperasPrueba.EsperarHastaAsync(() => sesion.CanalesExecActivos == 0, TimeSpan.FromSeconds(15));
            await Task.Delay(300);
            int abiertas = _sshd.SesionesAbiertas();
            string[] rafaga = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => MensajeDeFalloAsync(gestor.EjecutarExecAsync(destino, "echo r", TimeSpan.FromSeconds(10), CancellationToken.None))));

            // Assert: las aperturas siguen en curso (con su cupo) hasta que el servidor responde; después se cierran y en el
            // servidor solo queda el shell.
            _ = errores.Should().AllSatisfy(e => e.Should().Contain("no terminó en 1 s"));
            _ = activosCongelado.Should().Be(SesionShell.MaxCanalesExecSimultaneos);
            _ = abiertas.Should().Be(1);
            _ = rafaga.Should().AllBe("sin error");
            _ = _sshd.Sshd.ContarLineasLog("no more sessions").Should().Be(0);
            _ = _sshd.Sshd.ConexionesAceptadas().Should().Be(aceptadas);
        }

        // R05/R06 de la auditoría del E-quater: con el servidor sin leer, una escritura grande al shell llena el envío TCP y
        // el bucle se queda dentro de ella, con el candado de escritura de SSH.NET tomado (ssh_write_file de 200 KiB como en
        // R06; ssh_exec de 1 MiB como en R05). ssh_input no se cuelga, ssh_cancel avisa de la escritura atascada y
        // ssh_disconnect vuelve en pocos segundos (antes del parche de SSH.NET no volvía nunca); después se reconecta con
        // una conexión nueva.
        [Theory]
        [InlineData(200, true)]
        [InlineData(1024, false)]
        public async Task CongeladoConUnaEscrituraAlShellBloqueada_DisconnectVuelveYLaSiguienteReconecta(int kib, bool isEscrituraArchivo)
        {
            // Arrange
            OpcionesNativeSsh opciones = Opciones(TimeSpan.FromSeconds(15)) with { EsperaTrasCtrlC = TimeSpan.FromSeconds(1) };
            await using GestorSesiones gestor = Gestor(opciones);
            HerramientasSsh herramientas = new(gestor, opciones);
            string destino = _sshd.Sshd.Destino.Clave;
            _ = await herramientas.EjecutarAsync(destino, "true");
            SesionShell sesion = gestor.ListarSesiones().Single();
            string[] propios = _sshd.Sshd.ProcesosDeConexion();
            int aceptadas = _sshd.Sshd.ConexionesAceptadas();
            string relleno = new('a', kib * 1024);
            Task operacion;
            string id;
            string entrada;
            ResultadoComando cancelado;
            bool isDesconectado;
            TimeSpan duracionDesconexion;

            // Act
            try
            {
                _ = BashLocal.Ejecutar($"kill -STOP {string.Join(' ', propios)}");
                operacion = isEscrituraArchivo
                    ? herramientas.EscribirArchivoAsync(destino, Path.Combine(_sshd.Directorio, "destino.txt"), relleno, overwrite: true)
                    : herramientas.EjecutarAsync(destino, $": {relleno}; echo grande", timeoutSegundos: 60, esperarSegundos: 0);
                await EsperasPrueba.EsperarHastaAsync(() => sesion.SegundosEnEscritura >= SesionShell.UmbralAvisoEscrituraBloqueada.TotalSeconds, TimeSpan.FromSeconds(15));
                id = sesion.PeticionActual!.IdTexto;
                entrada = await MensajeDeFalloAsync(herramientas.EnviarEntradaAsync(id, "x", esperarSegundos: 0).WaitAsync(EsperaMaximaCongelado));
                cancelado = await herramientas.CancelarAsync(id).WaitAsync(EsperaMaximaCongelado);
                Stopwatch reloj = Stopwatch.StartNew();
                Task<ResultadoAccion> desconexion = herramientas.DesconectarAsync(destino);
                isDesconectado = await Task.WhenAny(desconexion, Task.Delay(EsperaMaximaCongelado)) == desconexion;
                duracionDesconexion = reloj.Elapsed;
            }
            finally
            {
                _ = BashLocal.Ejecutar($"kill -CONT {string.Join(' ', propios)}");
            }

            string fallo = await MensajeDeFalloAsync(operacion.WaitAsync(EsperaMaximaCongelado));
            Peticion peticion = gestor.BuscarPeticion(id);
            ResultadoComando siguiente = await herramientas.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 20);

            // Assert
            _ = entrada.Should().Contain("no se envió").And.Contain("ssh_disconnect");
            _ = cancelado.Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
            _ = cancelado.Aviso.Should().Contain("ssh_cancel no puede interrumpir").And.Contain("ssh_disconnect");
            _ = isDesconectado.Should().BeTrue();
            _ = duracionDesconexion.Should().BeLessThan(TimeSpan.FromSeconds(8));
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            if (isEscrituraArchivo)
                _ = fallo.Should().Contain(id);
            _ = siguiente.Salida.Should().Be("siguiente\n");
            _ = _sshd.Sshd.ConexionesAceptadas().Should().Be(aceptadas + 1);
        }

        [Fact]
        public async Task CongeladoAlReabrirElShell_ElComandoVenceASuTiempoYElSiguienteFuncionaSinShellHuerfano()
        {
            // Arrange: SSH.NET abandonaría la apertura del shell a los 3 s; el siguiente comando debe abrir uno nuevo.
            OpcionesNativeSsh opciones = Opciones(TimeSpan.FromSeconds(3));
            await using GestorSesiones gestor = Gestor(opciones);
            HerramientasSsh herramientas = new(gestor, opciones);
            string destino = _sshd.Sshd.Destino.Clave;
            _ = await herramientas.EjecutarAsync(destino, "true");
            _ = herramientas.ReiniciarShell(destino);
            string[] propios = _sshd.Sshd.ProcesosDeConexion();
            int aceptadas = _sshd.Sshd.ConexionesAceptadas();
            ResultadoComando primero;
            TimeSpan duracion;

            // Act: congelado 6 s; el comando tiene 2 s.
            try
            {
                _ = BashLocal.Ejecutar($"kill -STOP {string.Join(' ', propios)}");
                Stopwatch reloj = Stopwatch.StartNew();
                primero = await herramientas.EjecutarAsync(destino, "echo uno", timeoutSegundos: 2, esperarSegundos: 20).WaitAsync(EsperaMaximaCongelado);
                duracion = reloj.Elapsed;
                await EsperarHastaAsync(reloj, TimeSpan.FromSeconds(6));
            }
            finally
            {
                _ = BashLocal.Ejecutar($"kill -CONT {string.Join(' ', propios)}");
            }

            ResultadoComando segundo = await herramientas.EjecutarAsync(destino, "echo dos", esperarSegundos: 20);
            await Task.Delay(500);
            int abiertas = _sshd.SesionesAbiertas();

            // Assert
            _ = primero.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            _ = primero.Aviso.Should().Contain("no llegó a enviarse");
            _ = duracion.Should().BeLessThan(TimeSpan.FromSeconds(5));
            _ = segundo.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = segundo.Salida.Should().Be("dos\n");
            _ = abiertas.Should().Be(1);
            _ = _sshd.Sshd.ContarLineasLog("no more sessions").Should().Be(0);
            _ = _sshd.Sshd.ConexionesAceptadas().Should().Be(aceptadas);
        }
    }
}
