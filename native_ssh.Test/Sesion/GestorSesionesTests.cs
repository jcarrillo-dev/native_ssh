using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Test.Sesion.Falsos;

namespace NativeSsh.Test.Sesion
{
    public class GestorSesionesTests
    {
        private static readonly TimeSpan Limite = TimeSpan.FromSeconds(5);

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
            _ = (await gestor.DesconectarAsync("servidor")).Should().BeTrue();
            _ = (await gestor.DesconectarAsync("servidor")).Should().BeFalse();
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
