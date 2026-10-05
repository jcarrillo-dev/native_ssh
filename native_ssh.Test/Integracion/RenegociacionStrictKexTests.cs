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
    // FALLO CONOCIDO (EQQ3, decisión aplazada por el usuario; README, «Limitaciones conocidas»): SSH.NET 2026.0.0 aplica
    // la regla estricta de strict-kex («cortar ante cualquier mensaje inesperado») también a las renegociaciones de
    // claves, y OpenSSH 10.5 envía SSH_MSG_IGNORE entre KEXINIT y NEWKEYS al renegociar: SSH.NET corta la conexión
    // ("Message type 2 is not valid in the current context"), se pierde el shell con sus 'export' y se gasta una
    // conexión. Con 'RekeyLimit 64K 2s' el servidor renegocia cada pocos segundos y el corte llega enseguida (medido:
    // 25 cortes en 56 renegociaciones en 60 s).
    // La prueba comprueba que el fallo sigue ocurriendo. Si falla porque la conexión sobrevive, el fallo se ha corregido
    // (p. ej. al integrar la rama native-ssh-strict-kex del submódulo): hay que invertirla y quitar la limitación del README.
    [Trait("Categoria", "Integracion")]
    [Trait("Categoria", "FalloConocido")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El sshd se libera en IAsyncLifetime.DisposeAsync.")]
    public sealed class RenegociacionStrictKexTests(ITestOutputHelper salida) : IAsyncLifetime
    {
        private static readonly TimeSpan Tope = TimeSpan.FromSeconds(60);

        private readonly SshdPropio _sshd = new("RekeyLimit 64K 2s");

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync()
        {
            _sshd.Dispose();
            return Task.CompletedTask;
        }

        [Fact]
        public async Task FalloConocido_RenegociacionesConIgnoreDeOpenSsh_SshNetCortaLaConexionYSePierdeElShell()
        {
            // Arrange
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones(maxConexiones: 1000);
            await using GestorSesiones gestor = new(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);
            string destino = _sshd.Sshd.Destino.Clave;
            _ = await herramientas.EjecutarAsync(destino, "export NS_RENEGOCIACION=si");
            int aceptadas = _sshd.Sshd.ConexionesAceptadas();
            Stopwatch reloj = Stopwatch.StartNew();
            int rondas = 0;

            // Act: tráfico (lecturas por canales exec y comandos) hasta el primer corte o el tope.
            while (_sshd.Sshd.ConexionesAceptadas() == aceptadas && reloj.Elapsed < Tope)
            {
                Task[] lecturas = [.. Enumerable.Range(0, 3).Select(_ => IntentarAsync(gestor.EjecutarExecAsync(destino, "head -c 100000 /dev/zero | base64 | wc -c", TimeSpan.FromSeconds(30), CancellationToken.None)))];
                _ = await herramientas.EjecutarAsync(destino, "true", esperarSegundos: 30);
                await Task.WhenAll(lecturas);
                rondas++;
            }

            ResultadoComando despues = await herramientas.EjecutarAsync(destino, "echo \"v=$NS_RENEGOCIACION\"", esperarSegundos: 30);
            salida.WriteLine($"{rondas} rondas en {reloj.Elapsed.TotalSeconds:0} s; conexiones nuevas: {_sshd.Sshd.ConexionesAceptadas() - aceptadas}; después: '{despues.Salida.Trim()}' aviso='{despues.Aviso}'");

            // Assert: el fallo conocido sigue ahí (ver el comentario de la clase si deja de ocurrir).
            _ = _sshd.Sshd.ConexionesAceptadas().Should().BeGreaterThan(aceptadas, "SSH.NET corta la conexión al recibir SSH_MSG_IGNORE durante una renegociación (fallo conocido; si ya no ocurre, se ha corregido)");
            _ = despues.Salida.Should().Be("v=\n");
        }

        private static async Task IntentarAsync(Task tarea)
        {
            try
            {
                await tarea;
            }
            catch (NativeSshException)
            {
                // Una lectura en vuelo cuando se corta la conexión falla: es parte del fallo conocido.
            }
        }
    }
}
