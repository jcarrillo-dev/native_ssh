using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    // EQQ2: cerrar el canal del shell mientras el servidor lo inunda de salida (ssh_reset_shell, ssh_disconnect y caída de
    // la conexión). ShellStream.Dispose de SSH.NET retenía su candado mientras esperaba el CLOSE del servidor, y el hilo
    // de mensajes necesita ese candado para entregar los datos que llegan entretanto: con ChannelCloseTimeout infinito
    // (ConexionSshNet), un interbloqueo de toda la conexión. Corregido en externo/SSH.NET (el canal se cierra fuera del
    // candado); la prueba determinista está en la suite de SSH.NET (ShellStreamTest), aquí se comprueba de extremo a
    // extremo que cada cierre vuelve y que la conexión sigue atendiendo.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El sshd se libera en IAsyncLifetime.DisposeAsync.")]
    public sealed class CierreShellConSalidaTests(ITestOutputHelper salida) : IAsyncLifetime
    {
        private const string Chorro = "exec yes 0123456789abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz";

        // Tope de cada cierre y de la comprobación posterior: un interbloqueo no vuelve nunca.
        private static readonly TimeSpan Tope = TimeSpan.FromSeconds(20);

        private readonly SshdPropio _sshd = new("");

        private enum CierreEnum
        {
            Reinicio,
            Desconexion,
            Caida
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync()
        {
            _sshd.Dispose();
            return Task.CompletedTask;
        }

        [Fact]
        public Task CerrarElShellMientrasLlegaSalida_CadaCierreVuelveYLaConexionSigueAtendiendo() => CerrarConSalidaAsync(rondasPorCierre: 4);

        [FactEstres]
        public Task Estres_CerrarElShellMientrasLlegaSalida_CadaCierreVuelveYLaConexionSigueAtendiendo() => CerrarConSalidaAsync(rondasPorCierre: 60);

        private async Task CerrarConSalidaAsync(int rondasPorCierre)
        {
            // Arrange: sin freno del limitador (las desconexiones y caídas abren conexiones nuevas en el sshd de la prueba).
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones(maxConexiones: 1000) with { EsperaTrasCtrlC = TimeSpan.FromMilliseconds(300) };
            await using GestorSesiones gestor = new(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);
            string destino = _sshd.Sshd.Destino.Clave;
            List<string> fallos = [];
            Stopwatch total = Stopwatch.StartNew();
            TimeSpan peor = TimeSpan.Zero;

            // Act
            foreach (CierreEnum cierre in Enum.GetValues<CierreEnum>())
            {
                for (int ronda = 0; ronda < rondasPorCierre; ronda++)
                {
                    ResultadoComando chorro = await herramientas.EjecutarAsync(destino, Chorro, timeoutSegundos: 60, esperarSegundos: 0);
                    Peticion peticion = gestor.BuscarPeticion(chorro.Id);
                    await EsperasPrueba.EsperarHastaAsync(() => peticion.Salida.Longitud > 64 * 1024, Tope);
                    await Task.Delay(Random.Shared.Next(0, 30));
                    Stopwatch reloj = Stopwatch.StartNew();
                    Task operacion = cierre switch
                    {
                        CierreEnum.Reinicio => Task.FromResult(herramientas.ReiniciarShell(destino)),
                        CierreEnum.Desconexion => herramientas.DesconectarAsync(destino),
                        CierreEnum.Caida => Task.Run(() => BashLocal.Ejecutar($"kill -KILL {string.Join(' ', _sshd.Sshd.ProcesosDeConexion())}")),
                        _ => throw new InvalidOperationException($"Cierre desconocido: {cierre}.")
                    };
                    bool isCerrado = await Task.WhenAny(operacion, Task.Delay(Tope)) == operacion;
                    bool isTerminado = await Task.WhenAny(peticion.Finalizacion, Task.Delay(Tope)) == peticion.Finalizacion;
                    ResultadoComando vivo = await herramientas.EjecutarAsync(destino, "echo vivo", timeoutSegundos: 15, esperarSegundos: 20).WaitAsync(Tope);
                    TimeSpan duracion = reloj.Elapsed;
                    peor = duracion > peor ? duracion : peor;
                    if (!isCerrado || !isTerminado || vivo.Salida != "vivo\n")
                        fallos.Add($"{cierre} ronda {ronda}: cierre vuelto={isCerrado}, comando terminado={isTerminado} ({peticion.Estado}), siguiente={vivo.Estado} '{vivo.Salida.Trim()}' '{vivo.Aviso}'");
                }
            }

            salida.WriteLine($"{3 * rondasPorCierre} cierres con salida en curso en {total.Elapsed.TotalSeconds:0} s; el peor (cierre + comando siguiente) {peor.TotalSeconds:0.0} s; fallos {fallos.Count}");

            // Assert
            _ = fallos.Should().BeEmpty();
        }
    }
}
