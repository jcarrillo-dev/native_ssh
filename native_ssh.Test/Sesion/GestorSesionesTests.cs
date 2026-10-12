using FluentAssertions;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Test.Sesion.Falsos;

namespace NativeSsh.Test.Sesion
{
    public class GestorSesionesTests
    {
        private static readonly TimeSpan Limite = TimeSpan.FromSeconds(5);

        // Destinos que el gestor resuelve a la misma sesión (el usuario por defecto es "ana").
        private static readonly string[] DestinosEquivalentes = ["servidor", "ana@servidor", "ana@servidor:22", " ana@servidor "];

        private static GestorSesiones Crear(FabricaFalsa fabrica)
        {
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana" };
            return new GestorSesiones(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
        }

        private static FabricaFalsa Fabrica() => new(comando => new RespuestaFalsa($"eco:{comando}\n"));

        [Fact]
        public async Task Ejecutar_ReutilizaLaSesionDelMismoDestino()
        {
            // Arrange
            FabricaFalsa fabrica = Fabrica();
            await using GestorSesiones gestor = Crear(fabrica);

            // Act
            Peticion primera = gestor.Ejecutar("servidor", "a", TimeSpan.FromSeconds(10));
            Peticion segunda = gestor.Ejecutar("ana@servidor:22", "b", TimeSpan.FromSeconds(10));
            await Task.WhenAll(primera.Finalizacion, segunda.Finalizacion).WaitAsync(Limite);

            // Assert
            _ = gestor.ListarSesiones().Should().ContainSingle();
            _ = fabrica.Conexiones.Should().Be(1);
            _ = gestor.BuscarPeticion(primera.IdTexto).Should().BeSameAs(primera);
        }

        [Fact]
        public async Task Ejecutar_DestinosDistintos_SesionesDistintas()
        {
            // Arrange
            FabricaFalsa fabrica = Fabrica();
            await using GestorSesiones gestor = Crear(fabrica);

            // Act
            Peticion a = gestor.Ejecutar("ana@uno", "x", TimeSpan.FromSeconds(10));
            Peticion b = gestor.Ejecutar("ana@dos", "x", TimeSpan.FromSeconds(10));
            await Task.WhenAll(a.Finalizacion, b.Finalizacion).WaitAsync(Limite);

            // Assert
            _ = gestor.ListarSesiones().Count.Should().Be(2);
            _ = fabrica.Conexiones.Should().Be(2);
        }

        [Theory]
        [InlineData("ana@", "ls")]
        [InlineData("servidor", "   ")]
        public async Task Ejecutar_EntradaNoValida_Lanza(string destino, string comando)
        {
            // Arrange
            await using GestorSesiones gestor = Crear(Fabrica());

            // Act & Assert
            _ = FluentActions.Invoking(() => gestor.Ejecutar(destino, comando, TimeSpan.FromSeconds(10))).Should().Throw<NativeSshException>();
            _ = gestor.ListarSesiones().Should().BeEmpty();
        }

        [Fact]
        public async Task Ejecutar_AjustaElTimeout()
        {
            // Arrange
            await using GestorSesiones gestor = Crear(Fabrica());

            // Act
            Peticion corta = gestor.Ejecutar("servidor", "a", TimeSpan.Zero);
            Peticion larga = gestor.Ejecutar("servidor", "b", TimeSpan.FromDays(1));

            // Assert
            _ = corta.Timeout.Should().Be(GestorSesiones.TimeoutMinimo);
            _ = larga.Timeout.Should().Be(GestorSesiones.TimeoutMaximo);
        }

        [Fact]
        public async Task BuscarPeticion_Inexistente_Lanza()
        {
            // Arrange
            await using GestorSesiones gestor = Crear(Fabrica());

            // Act & Assert
            NativeSshException error = FluentActions.Invoking(() => gestor.BuscarPeticion("nada")).Should().Throw<NativeSshException>().Which;
            _ = error.Message.Should().Contain("ssh_status");
        }

        [Fact]
        public async Task Desconectar_QuitaLaSesionYLaSiguienteConectaDeNuevo()
        {
            // Arrange
            FabricaFalsa fabrica = Fabrica();
            await using GestorSesiones gestor = Crear(fabrica);
            await gestor.Ejecutar("servidor", "a", TimeSpan.FromSeconds(10)).Finalizacion.WaitAsync(Limite);

            // Act
            _ = (await gestor.DesconectarAsync("servidor")).Should().ContainSingle();
            _ = (await gestor.DesconectarAsync("servidor")).Should().BeEmpty();
            _ = gestor.ListarSesiones().Should().BeEmpty();

            // Assert
            await gestor.Ejecutar("servidor", "b", TimeSpan.FromSeconds(10)).Finalizacion.WaitAsync(Limite);
            _ = fabrica.Conexiones.Should().Be(2);
        }

        [Fact]
        public async Task ReiniciarShell_SoloSiHaySesion()
        {
            // Arrange
            FabricaFalsa fabrica = Fabrica();
            await using GestorSesiones gestor = Crear(fabrica);

            // Act
            _ = gestor.ReiniciarShell("servidor").Should().BeFalse();
            await gestor.Ejecutar("servidor", "a", TimeSpan.FromSeconds(10)).Finalizacion.WaitAsync(Limite);
            _ = gestor.ReiniciarShell("servidor").Should().BeTrue();
            await gestor.Ejecutar("servidor", "b", TimeSpan.FromSeconds(10)).Finalizacion.WaitAsync(Limite);

            // Assert
            _ = fabrica.Ultima!.ShellsAbiertos.Should().Be(2);
        }

        [Fact]
        public async Task Dispose_CierraTodoYRechazaNuevasPeticiones()
        {
            // Arrange
            GestorSesiones gestor = Crear(Fabrica());
            await gestor.Ejecutar("servidor", "a", TimeSpan.FromSeconds(10)).Finalizacion.WaitAsync(Limite);

            // Act
            await gestor.DisposeAsync();

            // Assert
            _ = gestor.ListarSesiones().Should().BeEmpty();
            _ = FluentActions.Invoking(() => gestor.Ejecutar("servidor", "b", TimeSpan.FromSeconds(10))).Should().Throw<ObjectDisposedException>();
        }

        [Fact]
        public async Task Concurrencia_32HilosDestinosEquivalentes_UnaSolaSesionYConexion()
        {
            // Arrange
            FabricaFalsa fabrica = Fabrica();
            await using GestorSesiones gestor = Crear(fabrica);
            const int hilos = 32;
            Task<Peticion>[] tareas = new Task<Peticion>[hilos];

            // Act
            for (int i = 0; i < hilos; i++)
            {
                int index = i;
                string destino = index % 2 == 0 ? "servidor" : "ana@servidor:22";
                tareas[index] = Task.Run(() => gestor.Ejecutar(destino, $"cmd{index}", TimeSpan.FromSeconds(10)));
            }

            Peticion[] peticiones = await Task.WhenAll(tareas).WaitAsync(Limite);
            await Task.WhenAll(peticiones.Select(p => p.Finalizacion)).WaitAsync(Limite);

            // Assert
            _ = gestor.ListarSesiones().Should().ContainSingle();
            _ = fabrica.Conexiones.Should().Be(1);
        }

        [Fact]
        public async Task Concurrencia_HilosQueSalenALaVezConComandosYCanalesExec_UnaSolaSesionYUnaSolaConexion()
        {
            // Arrange: hilos propios que salen a la vez de una barrera mientras la primera conexión tarda en establecerse;
            // la mitad encola comandos en el shell y la otra mitad abre canales exec, sobre destinos equivalentes.
            FabricaFalsa fabrica = Fabrica();
            fabrica.RetrasoConexion = TimeSpan.FromMilliseconds(200);
            await using GestorSesiones gestor = Crear(fabrica);
            const int hilos = 48;
            using Barrier barrera = new(hilos);
            Task[] tareas = new Task[hilos];

            // Act
            for (int i = 0; i < hilos; i++)
            {
                int indice = i;
                string destino = DestinosEquivalentes[indice % DestinosEquivalentes.Length];
                tareas[indice] = Task.Factory.StartNew(
                    () =>
                    {
                        _ = barrera.SignalAndWait(Limite);
                        return indice % 2 == 0
                            ? ComprobarComandoAsync(gestor, destino, $"cmd{indice}")
                            : ComprobarExecAsync(gestor, destino, $"lectura{indice}");
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap();
            }

            await Task.WhenAll(tareas).WaitAsync(TimeSpan.FromSeconds(15));

            // Assert
            _ = gestor.ListarSesiones().Should().ContainSingle().Which.ConexionesAbiertas.Should().Be(1);
            _ = fabrica.Conexiones.Should().Be(1);
        }

        private static async Task ComprobarComandoAsync(GestorSesiones gestor, string destino, string comando)
        {
            Peticion peticion = gestor.Ejecutar(destino, comando, TimeSpan.FromSeconds(10));
            await peticion.Finalizacion.WaitAsync(TimeSpan.FromSeconds(10));
            byte[] salida = new byte[64];
            int copiados = peticion.Salida.Leer(0, salida).Copiados;
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = Encoding.UTF8.GetString(salida, 0, copiados).Should().Be($"eco:{comando}\n");
        }

        // El falso responde "eco:" y el script que le llega: el del llamante tras el 'cd' al directorio de trabajo.
        private static async Task ComprobarExecAsync(GestorSesiones gestor, string destino, string script)
        {
            ResultadoCanalExec resultado = await gestor.EjecutarExecAsync(destino, script, TimeSpan.FromSeconds(10), CancellationToken.None);
            _ = resultado.CodigoSalida.Should().Be(0);
            _ = Encoding.UTF8.GetString(resultado.Salida.Span).Should().StartWith("eco:").And.EndWith($"\n{script}\n");
        }

        [Fact]
        public async Task Dispose_NoRegistraPeticionesEnRegistro()
        {
            // Arrange
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana" };
            RegistroPeticiones registro = new(opciones, TimeProvider.System);
            GestorSesiones gestor = new(
                Fabrica(),
                new LimitadorConexiones(opciones, TimeProvider.System),
                registro,
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);

            // Act
            await gestor.DisposeAsync();
            _ = FluentActions.Invoking(() => gestor.Ejecutar("servidor", "cmd", TimeSpan.FromSeconds(10)))
                .Should().Throw<ObjectDisposedException>();

            // Assert
            _ = registro.ListarRecientes().Should().BeEmpty();
        }
    }
}
