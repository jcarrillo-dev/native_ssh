using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // V06 y V08 de la revisión del E-quinquies: la escritura atascada en SSH.NET es la del propio ssh_input (servidor
    // congelado con el envío TCP lleno, o servidor sano con el terminal en modo raw y un proceso que no lee, que agota la
    // ventana del canal). ssh_cancel cancelaba en su propio hilo la continuación del bucle, que esperaba sin límite al
    // candado de escritura retenido por esa entrada: no volvía hasta un ssh_disconnect. Y la entrada, al fallar después,
    // decía que el comando «no admite entrada». Ahora ssh_cancel vuelve en unos segundos sin enviar Ctrl+C (diciendo que el
    // proceso sigue y que ssh_disconnect lo libera) y la entrada falla con su causa real.
    // W01 y W07 de la revisión del E-sexies: después, sin ssh_disconnect, el comando siguiente esperaba a que pasara la
    // entrada y se escribía en la entrada del proceso cancelado (o se ejecutaba en el shell anidado en el que acababa ese
    // proceso). Ahora ese shell queda abandonado y el comando siguiente abre otro sobre la misma conexión.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El sshd se libera en IAsyncLifetime.DisposeAsync.")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class EntradaAtascadaTests : IAsyncLifetime
    {
        // Duración propia para reconocer el proceso de la prueba entre los que cuelgan de su sshd.
        private const string Proceso = "sleep 119.25";

        private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(30);

        private readonly SshdPropio _sshd = new("LogLevel DEBUG");

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync()
        {
            _sshd.Sshd.MatarDescendientes(o => o == Proceso);
            _sshd.Dispose();
            return Task.CompletedTask;
        }

        private static string Lineas(int bytes, char caracter)
        {
            StringBuilder texto = new(bytes);
            while (texto.Length < bytes)
                _ = texto.Append(caracter, 99).Append('\n');
            return texto.ToString();
        }

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

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task EntradaPropiaAtascada_CancelVuelveSinCtrlCYLaEntradaDiceLaCausaReal(bool isCongelado)
        {
            // Arrange
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones() with { EsperaTrasCtrlC = TimeSpan.FromSeconds(1) };
            await using GestorSesiones gestor = new(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);
            string destino = _sshd.Sshd.Destino.Clave;
            _ = await herramientas.EjecutarAsync(destino, "export NS_ENTRADA=si", esperarSegundos: 20).ComoComando();
            SesionShell sesion = gestor.ListarSesiones().Single();
            string[] propios = _sshd.Sshd.ProcesosDeConexion();
            int aceptadas = _sshd.Sshd.ConexionesAceptadas();
            ResultadoComando comando = await herramientas.EjecutarAsync(destino, isCongelado ? Proceso : $"stty raw -echo; {Proceso}", timeoutSegundos: 200, esperarSegundos: 0).ComoComando();
            await Task.Delay(1500);
            string texto = new('b', 6 * 1024 * 1024);
            Task<string> entrada;
            ResultadoComando cancelado;
            TimeSpan duracionCancelacion;
            bool isDesconectado;

            // Act
            try
            {
                if (isCongelado)
                    _ = BashLocal.Ejecutar($"kill -STOP {string.Join(' ', propios)}");
                entrada = Task.Run(() => MensajeDeFalloAsync(herramientas.EnviarEntradaAsync(comando.Id, texto, esperarSegundos: 0).ComoComando()));
                await EsperasPrueba.EsperarHastaAsync(() => sesion.SegundosEnEscritura >= SesionShell.UmbralAvisoEscrituraBloqueada.TotalSeconds, TimeSpan.FromSeconds(15));
                Stopwatch reloj = Stopwatch.StartNew();
                cancelado = await herramientas.CancelarAsync(comando.Id).ComoComando().WaitAsync(EsperaMaxima);
                duracionCancelacion = reloj.Elapsed;
                Task<ResultadoAccion> desconexion = herramientas.DesconectarAsync(destino);
                isDesconectado = await Task.WhenAny(desconexion, Task.Delay(EsperaMaxima)) == desconexion;
            }
            finally
            {
                if (isCongelado)
                    _ = BashLocal.Ejecutar($"kill -CONT {string.Join(' ', propios)}");
            }

            string fallo = await entrada.WaitAsync(EsperaMaxima);
            ResultadoComando siguiente = await herramientas.EjecutarAsync(destino, "echo \"v=$NS_ENTRADA\"", esperarSegundos: 20).ComoComando();

            // Assert
            _ = duracionCancelacion.Should().BeLessThan(TimeSpan.FromSeconds(6));
            _ = cancelado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = cancelado.Aviso.Should().Contain("no se envió Ctrl+C").And.Contain("sigue en ejecución").And.Contain("ssh_disconnect");
            _ = isDesconectado.Should().BeTrue();
            _ = fallo.Should().Contain("parte de la entrada").And.Contain("ssh_disconnect").And.NotContain("no admite entrada");
            _ = siguiente.Salida.Should().Be("v=\n");
            _ = _sshd.Sshd.ConexionesAceptadas().Should().Be(aceptadas + 1);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task EntradaPropiaAtascada_TrasCancelSinCtrlC_ElSiguienteComandoVaAUnShellNuevoYNoALaEntradaDelProceso(bool isCongelado)
        {
            // Arrange: congelado, 'cat' lee en modo canónico (líneas cortas); con el terminal en modo raw, el proceso no lee
            // durante 14 s y la entrada agota la ventana del canal.
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones() with { EsperaTrasCtrlC = TimeSpan.FromSeconds(1) };
            await using GestorSesiones gestor = new(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);
            string destino = _sshd.Sshd.Destino.Clave;
            string archivo = Path.Combine(_sshd.Directorio, "entrada_del_proceso.txt");
            _ = await herramientas.EjecutarAsync(destino, "export NS_ENTRADA=si", esperarSegundos: 20).ComoComando();
            SesionShell sesion = gestor.ListarSesiones().Single();
            string[] propios = _sshd.Sshd.ProcesosDeConexion();
            int aceptadas = _sshd.Sshd.ConexionesAceptadas();
            ResultadoComando comando = await herramientas.EjecutarAsync(destino, isCongelado ? $"cat > '{archivo}'" : $"stty raw -echo; sleep 14; cat > '{archivo}'", timeoutSegundos: 200, esperarSegundos: 0).ComoComando();
            await Task.Delay(1500);
            string texto = isCongelado ? Lineas(1024 * 1024, 'b') : new string('b', 3 * 1024 * 1024);
            Task<string> entrada;
            ResultadoComando cancelado;
            ResultadoComando siguiente;
            bool isEntradaPendienteConElSiguienteTerminado = true;

            // Act
            try
            {
                if (isCongelado)
                    _ = BashLocal.Ejecutar($"kill -STOP {string.Join(' ', propios)}");
                entrada = Task.Run(() => MensajeDeFalloAsync(herramientas.EnviarEntradaAsync(comando.Id, texto, esperarSegundos: 0).ComoComando()));
                await EsperasPrueba.EsperarHastaAsync(() => sesion.SegundosEnEscritura >= SesionShell.UmbralAvisoEscrituraBloqueada.TotalSeconds, TimeSpan.FromSeconds(15));
                cancelado = await herramientas.CancelarAsync(comando.Id).ComoComando().WaitAsync(EsperaMaxima);
                siguiente = await herramientas.EjecutarAsync(destino, "echo \"v=$NS_ENTRADA\"", timeoutSegundos: 60, esperarSegundos: 0).ComoComando();
                if (isCongelado)
                    await Task.Delay(2000);
                else
                {
                    // Con el servidor sano, el shell nuevo no espera a que pase la entrada atascada en el viejo.
                    _ = await herramientas.LeerSalidaAsync(siguiente.Id, esperarSegundos: 8).ComoComando();
                    isEntradaPendienteConElSiguienteTerminado = !entrada.IsCompleted;
                }
            }
            finally
            {
                if (isCongelado)
                    _ = BashLocal.Ejecutar($"kill -CONT {string.Join(' ', propios)}");
            }

            ResultadoComando final = await herramientas.LeerSalidaAsync(siguiente.Id, esperarSegundos: 60).ComoComando();
            string fallo = await entrada.WaitAsync(EsperaMaxima);

            // El shell abandonado se cierra en cuanto pasa la entrada y el servidor libera su sesión: solo queda la del nuevo.
            await EsperasPrueba.EsperarHastaAsync(() => _sshd.SesionesAbiertas() == 1, EsperaMaxima);

            // Assert
            _ = cancelado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = cancelado.Aviso.Should().Contain("no se envió Ctrl+C").And.Contain("shell nuevo").And.Contain("ssh_disconnect");
            _ = final.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = final.Salida.Should().Be("v=\n");
            _ = final.Aviso.Should().Contain("recreado");
            _ = isEntradaPendienteConElSiguienteTerminado.Should().BeTrue();
            _ = fallo.Should().Be("sin error");
            _ = new FileInfo(archivo).Length.Should().BeLessThanOrEqualTo(Encoding.UTF8.GetByteCount(texto) + 1, "el comando siguiente no debe llegar a la entrada del proceso cancelado");
            _ = _sshd.Sshd.ConexionesAceptadas().Should().Be(aceptadas);
        }

        // O1 de la revisión del E-sexies (caso W04 modo 1): con la ventana del canal agotada, SSH.NET esperaba a que el
        // servidor la ampliara y nadie despertaba esa espera al cerrarse el canal; sin ConnectionInfo.Timeout (ConexionSshNet),
        // ssh_input no volvía hasta ssh_disconnect. Corregido en externo/SSH.NET ("Stop waiting for the remote window when the
        // channel is closed"): la escritura falla en cuanto el servidor cierra el canal.
        [Fact]
        public async Task EntradaPropiaAtascada_ElServidorCierraElCanal_LaEntradaFallaAlMomentoConSuCausa()
        {
            // Arrange: terminal en modo raw y un proceso que no lee: la entrada agota la ventana del canal.
            const string ProcesoQueNoLee = "sleep 119.27";
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones();
            await using GestorSesiones gestor = new(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);
            string destino = _sshd.Sshd.Destino.Clave;
            _ = await herramientas.EjecutarAsync(destino, "true", esperarSegundos: 20).ComoComando();
            SesionShell sesion = gestor.ListarSesiones().Single();
            int aceptadas = _sshd.Sshd.ConexionesAceptadas();
            ResultadoComando comando = await herramientas.EjecutarAsync(destino, $"stty raw -echo; {ProcesoQueNoLee}", timeoutSegundos: 200, esperarSegundos: 0).ComoComando();
            await Task.Delay(1500);
            Task<string> entrada = Task.Run(() => MensajeDeFalloAsync(herramientas.EnviarEntradaAsync(comando.Id, new string('b', 6 * 1024 * 1024), esperarSegundos: 0).ComoComando()));
            await EsperasPrueba.EsperarHastaAsync(() => sesion.SegundosEnEscritura >= SesionShell.UmbralAvisoEscrituraBloqueada.TotalSeconds, TimeSpan.FromSeconds(15));

            // Act: mueren el proceso y el shell de la sesión, y el servidor cierra el canal.
            try
            {
                _sshd.Sshd.MatarDescendientes(o => o == ProcesoQueNoLee, "KILL", isConPadre: true);
                Stopwatch reloj = Stopwatch.StartNew();
                Task terminada = await Task.WhenAny(entrada, Task.Delay(EsperaMaxima));
                TimeSpan duracion = reloj.Elapsed;
                ResultadoComando siguiente = await herramientas.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 20).ComoComando();

                // Assert
                _ = terminada.Should().BeSameAs(entrada, "ssh_input no debe esperar a ssh_disconnect");
                _ = duracion.Should().BeLessThan(TimeSpan.FromSeconds(10));
                _ = (await entrada).Should().Contain("dejó de admitir escrituras").And.Contain("parte de la entrada");
                _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
                _ = siguiente.Salida.Should().Be("siguiente\n");
                _ = _sshd.Sshd.ConexionesAceptadas().Should().Be(aceptadas);
            }
            finally
            {
                _sshd.Sshd.MatarDescendientes(o => o == ProcesoQueNoLee);
            }
        }

        [Fact]
        public async Task EntradaPropiaAtascada_TrasCancelSinCtrlC_ElSiguienteComandoNoSeEjecutaEnUnShellAnidado()
        {
            // Arrange: el proceso cancelado acaba en un shell interactivo (como sudo -i, su o ssh a otra máquina).
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones() with { EsperaTrasCtrlC = TimeSpan.FromSeconds(1) };
            await using GestorSesiones gestor = new(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);
            string destino = _sshd.Sshd.Destino.Clave;
            const string Padre = "echo PADRE=$(ps -o comm= -p $PPID)";
            ResultadoComando enLaSesion = await herramientas.EjecutarAsync(destino, Padre, esperarSegundos: 20).ComoComando();
            SesionShell sesion = gestor.ListarSesiones().Single();
            ResultadoComando comando = await herramientas.EjecutarAsync(destino, "stty raw -echo; sleep 14; stty sane -echo; env -i PS1= PS2= TERM=dumb bash --norc --noprofile -i", timeoutSegundos: 200, esperarSegundos: 0).ComoComando();
            await Task.Delay(1500);
            Task<string> entrada = Task.Run(() => MensajeDeFalloAsync(herramientas.EnviarEntradaAsync(comando.Id, Lineas(3 * 1024 * 1024, '#'), enter: false, esperarSegundos: 0).ComoComando()));
            await EsperasPrueba.EsperarHastaAsync(() => sesion.SegundosEnEscritura >= SesionShell.UmbralAvisoEscrituraBloqueada.TotalSeconds, TimeSpan.FromSeconds(15));
            ResultadoComando cancelado = await herramientas.CancelarAsync(comando.Id).ComoComando().WaitAsync(EsperaMaxima);

            // Act
            ResultadoComando siguiente = await herramientas.EjecutarAsync(destino, Padre, timeoutSegundos: 60, esperarSegundos: 0).ComoComando();
            ResultadoComando final = await herramientas.LeerSalidaAsync(siguiente.Id, esperarSegundos: 60).ComoComando();
            string fallo = await entrada.WaitAsync(EsperaMaxima);
            await EsperasPrueba.EsperarHastaAsync(() => _sshd.SesionesAbiertas() == 1, EsperaMaxima);

            // Assert
            _ = cancelado.Aviso.Should().Contain("no se envió Ctrl+C");
            _ = final.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = final.Salida.Should().Be(enLaSesion.Salida, "el comando siguiente debe ejecutarse en el shell de la sesión, no en el proceso cancelado");
            _ = fallo.Should().Be("sin error");
        }
    }
}
