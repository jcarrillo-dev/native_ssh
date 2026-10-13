using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using Xunit.Abstractions;

namespace NativeSsh.Test.Integracion
{
    // EQQ3: SSH.NET 2026.0.0 aplicaba la regla estricta de strict-kex («cortar ante cualquier mensaje inesperado») también
    // a las renegociaciones de claves, y OpenSSH 10.5 envía SSH_MSG_IGNORE entre KEXINIT y NEWKEYS al renegociar: SSH.NET
    // cortaba la conexión ("Message type 2 is not valid in the current context"), se perdía el shell con sus 'export' y se
    // gastaba una conexión (medido: 25 cortes en 56 renegociaciones en 60 s). Corregido en externo/SSH.NET (rama
    // native-ssh): la regla estricta solo en el intercambio inicial. Con 'RekeyLimit 64K 2s' el servidor renegocia cada
    // pocos segundos; LogLevel DEBUG para contar las renegociaciones en su log.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El sshd se libera en IAsyncLifetime.DisposeAsync.")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class RenegociacionStrictKexTests(ITestOutputHelper salida) : IAsyncLifetime
    {
        private static readonly TimeSpan Duracion = TimeSpan.FromSeconds(30);

        // Antes de la corrección, la primera renegociación con IGNORE cortaba la conexión a los pocos segundos.
        private const int MinRenegociaciones = 5;

        private readonly SshdPropio _sshd = new("RekeyLimit 64K 2s\nLogLevel DEBUG");

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync()
        {
            _sshd.Dispose();
            return Task.CompletedTask;
        }

        [Fact]
        public async Task RenegociacionesConIgnoreDeOpenSsh_NoCortanLaConexionNiSePierdeElShell()
        {
            // Arrange
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones(maxConexiones: 1000);
            await using GestorSesiones gestor = new(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);
            string destino = _sshd.Sshd.Destino.Clave;
            _ = await herramientas.EjecutarAsync(destino, "export NS_RENEGOCIACION=si");
            int aceptadas = _sshd.Sshd.ConexionesAceptadas();
            int renegociacionesAntes = _sshd.Sshd.ContarLineasLog("SSH2_MSG_KEXINIT sent");
            Stopwatch reloj = Stopwatch.StartNew();
            int rondas = 0;
            List<string> fallos = [];

            // Act: tráfico (lecturas por canales exec y comandos) durante el tiempo de varias renegociaciones.
            while (reloj.Elapsed < Duracion)
            {
                Task<string>[] lecturas = [.. Enumerable.Range(0, 3).Select(_ => MensajeDeFalloAsync(gestor.EjecutarExecAsync(destino, "head -c 100000 /dev/zero | base64 | wc -c", TimeSpan.FromSeconds(30), CancellationToken.None)))];
                ResultadoComando comando = await herramientas.EjecutarAsync(destino, "echo \"v=$NS_RENEGOCIACION\"", esperarSegundos: 30);
                if (comando.Salida != "v=si\n")
                    fallos.Add($"comando: {comando.Estado} '{comando.Salida.Trim()}' '{comando.Aviso}'");
                fallos.AddRange((await Task.WhenAll(lecturas)).Where(m => m != "sin error"));
                rondas++;
            }

            int renegociaciones = _sshd.Sshd.ContarLineasLog("SSH2_MSG_KEXINIT sent") - renegociacionesAntes;
            ResultadoComando despues = await herramientas.EjecutarAsync(destino, "echo \"v=$NS_RENEGOCIACION\"", esperarSegundos: 30);
            salida.WriteLine($"{rondas} rondas en {reloj.Elapsed.TotalSeconds:0} s; renegociaciones del servidor: {renegociaciones}; conexiones nuevas: {_sshd.Sshd.ConexionesAceptadas() - aceptadas}; fallos: {fallos.Count}");

            // Assert
            _ = renegociaciones.Should().BeGreaterThanOrEqualTo(MinRenegociaciones);
            _ = fallos.Should().BeEmpty();
            _ = _sshd.Sshd.ConexionesAceptadas().Should().Be(aceptadas);
            _ = despues.Salida.Should().Be("v=si\n");
            _ = despues.Aviso.Should().BeNull();
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
    }
}
