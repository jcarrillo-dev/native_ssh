using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Caídas de la conexión TCP contra el sshd local: se matan los sshd-session de la conexión, como un corte con FIN.
    [Collection(ColeccionSshd.Nombre)]
    [Trait("Categoria", "Integracion")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El gestor se libera en IAsyncLifetime.DisposeAsync.")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class CaidaConexionIntegracionTests : IAsyncLifetime
    {
        private readonly SshdLocal _sshd;
        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;

        public CaidaConexionIntegracionTests(SshdLocal sshd)
        {
            _sshd = sshd;
            // Cada caída obliga a reconectar; el limitador no debe frenar la prueba.
            OpcionesNativeSsh opciones = sshd.Opciones(maxConexiones: 1000);
            _gestor = new GestorSesiones(
                sshd.Fabrica(opciones),
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            _herramientas = new HerramientasSsh(_gestor, opciones);
        }

        private string Destino => _sshd.Destino.Clave;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync() => await _gestor.DisposeAsync();

        private void MatarConexiones() => _ = BashLocal.Ejecutar($"kill -9 {string.Join(' ', _sshd.ProcesosDeConexion())} 2>/dev/null || true");

        private async Task<string> LeerAsync()
        {
            try
            {
                ResultadoCanalExec resultado = await _gestor.EjecutarExecAsync(Destino, "echo leida", TimeSpan.FromSeconds(10), CancellationToken.None);
                return resultado.CodigoSalida == 0 ? "leida" : $"código {resultado.CodigoSalida}";
            }
            catch (NativeSshException ex)
            {
                return $"error: {ex.Message}";
            }
        }

        // Variante de la prueba de EB1 sin esperar a que se detecte la caída: la lectura puede fallar (si aún no se
        // detectó), y sin lectura el comando puede acabar como shell perdido si salió antes de detectarla; en ese caso
        // el siguiente debe funcionar. Con lectura previa la caída ya se detectó: el comando nunca es un shell perdido.
        [Fact]
        public async Task CaidaDeLaConexion_SinEsperarASuDeteccion_ElSiguienteExecSeEjecutaEnUnShellNuevo()
        {
            // Arrange
            _ = await _herramientas.EjecutarAsync(Destino, "echo previo");
            int aceptadas = _sshd.ConexionesAceptadas();
            List<string> fallos = [];
            List<string> lecturas = [];

            // Act
            for (int i = 0; i < 10; i++)
            {
                bool isConLectura = i % 2 == 1;
                MatarConexiones();
                if (isConLectura)
                    lecturas.Add(await LeerAsync());
                ResultadoComando comando = await _herramientas.EjecutarAsync(Destino, $"echo tras_{i}", esperarSegundos: 15);
                if (!isConLectura && comando.Estado == EstadoPeticionEnum.ShellPerdido)
                    comando = await _herramientas.EjecutarAsync(Destino, $"echo tras_{i}", esperarSegundos: 15);
                if (comando.Estado != EstadoPeticionEnum.Completado || comando.Salida != $"tras_{i}\n" || comando.Aviso?.Contains("Shell remoto recreado", StringComparison.Ordinal) != true)
                    fallos.Add($"{i} (lectura previa: {isConLectura}): estado={comando.Estado} salida='{comando.Salida}' aviso='{comando.Aviso}'");
            }

            // Assert: una conexión nueva por caída.
            _ = fallos.Should().BeEmpty();
            _ = lecturas.Should().AllSatisfy(l => l.Should().Match(x => x == "leida" || x.StartsWith("error: ", StringComparison.Ordinal)));
            _ = (_sshd.ConexionesAceptadas() - aceptadas).Should().Be(10);
        }

        [Fact]
        public async Task CaidaDeLaConexion_ConUnComandoLargoYSinLecturas_TerminaComoShellPerdidoAlMomento()
        {
            // Arrange
            long sleep = 640_000 + Random.Shared.Next(10_000);
            string[] antes = _sshd.ProcesosDeConexion();
            ResultadoComando largo = await _herramientas.EjecutarAsync(Destino, $"sleep {sleep}; echo fin", esperarSegundos: 0);
            await EsperasPrueba.EsperarHastaAsync(() => CanalesExecIntegracionTests.ProcesosSleepVivos(sleep, sleep).Length > 0);
            string[] propios = [.. _sshd.ProcesosDeConexion().Except(antes)];
            int aceptadas = _sshd.ConexionesAceptadas();

            try
            {
                // Act
                _ = BashLocal.Ejecutar($"kill -9 {string.Join(' ', propios)}");
                Stopwatch reloj = Stopwatch.StartNew();
                ResultadoComando fin = await _herramientas.LeerSalidaAsync(largo.Id, esperarSegundos: 20);
                reloj.Stop();
                EstadoSesion estado = _herramientas.Estado().Sesiones.Single();
                ResultadoComando despues = await _herramientas.EjecutarAsync(Destino, "echo despues");

                // Assert
                _ = propios.Should().NotBeEmpty();
                _ = fin.Estado.Should().Be(EstadoPeticionEnum.ShellPerdido);
                _ = fin.Aviso.Should().Contain("Se perdió la conexión SSH").And.Contain("antes de que terminara el comando, así que puede que no llegara a ejecutarse");
                _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
                _ = estado.IsConectada.Should().BeFalse();
                _ = estado.IsShellActivo.Should().BeFalse();
                _ = despues.Estado.Should().Be(EstadoPeticionEnum.Completado);
                _ = despues.Salida.Should().Be("despues\n");
                _ = despues.Aviso.Should().Contain("Shell remoto recreado");
                _ = (_sshd.ConexionesAceptadas() - aceptadas).Should().Be(1);
            }
            finally
            {
                CanalesExecIntegracionTests.MatarProcesosSleep(sleep, sleep);
            }
        }
    }
}
