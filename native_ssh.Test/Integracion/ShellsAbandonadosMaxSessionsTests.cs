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
    // Z01 de la auditoría del E-septies: cada cancelación sin Ctrl+C (entrada atascada en un proceso con el terminal en
    // modo raw que no lee) deja un shell abandonado que vive hasta ssh_disconnect y ocupa una sesión del MaxSessions del
    // servidor. Al llenarlo, abrir el shell fallaba diciendo que las sesiones las ocupaban «canales de las herramientas de
    // lectura» y que se reintentara «cuando terminen las lecturas en curso», sin ninguna lectura y sin que reintentar
    // sirviera; las lecturas rechazadas tampoco nombraban los shells abandonados. Ahora los dos mensajes los nombran,
    // dicen que reintentar no sirve y que ssh_disconnect los libera.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class ShellsAbandonadosMaxSessionsTests
    {
        // Duración única para poder matar el proceso de la prueba sin tocar otros 'sleep' de la máquina.
        private const string Proceso = "sleep 119.37";

        private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(30);

        private static async Task<string> MensajeDeFalloAsync<T>(Task<T> tarea)
        {
            try
            {
                _ = await tarea;
                return "sin error";
            }
            catch (NativeSshException ex)
            {
                return ex.Message;
            }
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public async Task ShellsAbandonadosQueLlenanElMaxSessions_ElRechazoLosNombraNoMandaEsperarLecturasYSshDisconnectLoRecupera(int maxSessions)
        {
            // Arrange
            using SshdPropio sshd = new($"MaxSessions {maxSessions}");
            OpcionesNativeSsh opciones = sshd.Sshd.Opciones() with { EsperaTrasCtrlC = TimeSpan.FromSeconds(1) };
            await using GestorSesiones gestor = new(sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);
            string destino = sshd.Sshd.Destino.Clave;
            _ = await herramientas.EjecutarAsync(destino, "export NS_Z01=si", esperarSegundos: 20).ComoComando();
            SesionShell sesion = gestor.ListarSesiones().Single();
            List<Task<string>> entradas = [];
            List<ResultadoComando> siguientes = [];
            List<string> lecturas = [];
            int lecturasActivasAlFallar = -1;
            ResultadoAccion desconexion;
            ResultadoComando tras;
            string lecturaTras;

            // Act: tantos abandonos como sesiones admite el servidor; tras cada uno, un comando y una lectura.
            try
            {
                for (int i = 0; i < maxSessions; i++)
                {
                    ResultadoComando comando = await herramientas.EjecutarAsync(destino, $"stty raw -echo; {Proceso}", timeoutSegundos: 200, esperarSegundos: 0).ComoComando();
                    await Task.Delay(1500);
                    entradas.Add(Task.Run(() => MensajeDeFalloAsync(herramientas.EnviarEntradaAsync(comando.Id, new string('b', 3 * 1024 * 1024), esperarSegundos: 0).ComoComando())));
                    await EsperasPrueba.EsperarHastaAsync(() => sesion.SegundosEnEscritura >= SesionShell.UmbralAvisoEscrituraBloqueada.TotalSeconds, TimeSpan.FromSeconds(15));
                    ResultadoComando cancelado = await herramientas.CancelarAsync(comando.Id).ComoComando().WaitAsync(EsperaMaxima);
                    _ = cancelado.Aviso.Should().Contain("no se envió Ctrl+C");
                    ResultadoComando siguiente = await herramientas.EjecutarAsync(destino, "echo \"v=$NS_Z01\"", timeoutSegundos: 30, esperarSegundos: 35).ComoComando();
                    siguientes.Add(siguiente);
                    if (siguiente.Estado != EstadoPeticionEnum.Completado)
                        lecturasActivasAlFallar = sesion.CanalesExecActivos;
                    lecturas.Add(await MensajeDeFalloAsync(herramientas.LeerArchivoAsync(destino, "/etc/hostname")));
                }

                desconexion = await herramientas.DesconectarAsync(destino).WaitAsync(EsperaMaxima);
                _ = await Task.WhenAll(entradas).WaitAsync(EsperaMaxima);
                tras = await herramientas.EjecutarAsync(destino, "echo tras", esperarSegundos: 20).ComoComando();
                lecturaTras = await MensajeDeFalloAsync(herramientas.LeerArchivoAsync(destino, "/etc/hostname"));
            }
            finally
            {
                _ = BashLocal.Ejecutar($"pkill -f '^{Proceso}$' || true");
            }

            // Assert: el último abandono llena el MaxSessions y el shell nuevo se rechaza sin ninguna lectura en curso.
            ResultadoComando ultimo = siguientes[^1];
            _ = ultimo.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = lecturasActivasAlFallar.Should().Be(0);
            _ = ultimo.Aviso.Should().Contain("MaxSessions").And.Contain($"{maxSessions} shell(s) abandonado(s)").And.Contain("reintentar no sirve").And.Contain("ssh_disconnect")
                .And.NotContain("cuando terminen las lecturas en curso").And.NotContain("herramientas de lectura (en curso");
            _ = siguientes.SkipLast(1).Should().OnlyContain(s => s.Estado == EstadoPeticionEnum.Completado && s.Salida == "v=\n");
            string[] rechazadas = [.. lecturas.Where(l => l.Contains("rechazó", StringComparison.Ordinal))];
            _ = rechazadas.Should().NotBeEmpty().And.OnlyContain(l => l.Contains("shell(s) abandonado(s)", StringComparison.Ordinal) && l.Contains("ssh_disconnect", StringComparison.Ordinal));
            _ = lecturas.Should().OnlyContain(l => l == "sin error" || l.Contains("rechazó", StringComparison.Ordinal));
            _ = desconexion.IsHecho.Should().BeTrue();
            _ = tras.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = tras.Salida.Should().Be("tras\n");
            _ = lecturaTras.Should().Be("sin error");
        }

        // O3 de la auditoría del E-octies (EN3): con MaxSessions 1 el shell ocupa la única sesión y las lecturas se rechazan;
        // el mensaje nombra al shell como ocupante y no manda reintentar (no sirve mientras siga abierto). Tras
        // ssh_disconnect, la lectura sí se abre.
        [Fact]
        public async Task MaxSessions1_ElRechazoDeUnaLecturaNombraAlShellYNoMandaReintentar()
        {
            // Arrange
            using SshdPropio sshd = new("MaxSessions 1");
            OpcionesNativeSsh opciones = sshd.Sshd.Opciones();
            await using GestorSesiones gestor = new(sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);
            string destino = sshd.Sshd.Destino.Clave;
            ResultadoComando shell = await herramientas.EjecutarAsync(destino, "echo shell", esperarSegundos: 20).ComoComando();

            // Act
            string rechazo = await MensajeDeFalloAsync(herramientas.LeerArchivoAsync(destino, "/etc/hostname"));
            ResultadoAccion desconexion = await herramientas.DesconectarAsync(destino);
            string tras = await MensajeDeFalloAsync(herramientas.LeerArchivoAsync(destino, "/etc/hostname"));

            // Assert
            _ = shell.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = rechazo.Should().Contain("rechazó").And.Contain("el shell de ssh_exec (1 sesión").And.Contain("MaxSessions 1").And.Contain("reintentar no sirve")
                .And.NotContain("reintenta en unos segundos").And.NotContain("ningún shell");
            _ = desconexion.IsHecho.Should().BeTrue();
            _ = tras.Should().Be("sin error");
        }
    }
}
