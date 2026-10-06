using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Ssh;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Servidor que acepta la conexión y envía su identificación, pero no responde al intercambio de claves (congelado o
    // saturado tras el banner). SSH.NET 2026.0.0 solo atendía el token de ConnectAsync hasta el intercambio de versiones:
    // el intercambio de claves, la petición del servicio y la autenticación esperaban hasta ConnectionInfo.Timeout cada
    // uno, así que ni ssh_disconnect ni el límite de TimeoutConexion de SesionShell cortaban la conexión antes. Corregido
    // en externo/SSH.NET ("Observe the cancellation token of ConnectAsync during the handshake"): cancelar cierra el socket.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    public sealed class HandshakeSinRespuestaTests
    {
        private static readonly TimeSpan Cancelacion = TimeSpan.FromSeconds(1);

        // Muy por encima de la cancelación: sin la corrección, ConnectAsync vuelve al vencer este plazo de SSH.NET.
        private static readonly TimeSpan TimeoutConexion = TimeSpan.FromSeconds(30);

        [Fact]
        public async Task ServidorQueNoRespondeTrasSuIdentificacion_LaCancelacionCortaElHandshakeAlMomento()
        {
            // Arrange
            using TcpListener servidor = new(IPAddress.Loopback, PuertosPruebas.Libre());
            servidor.Start();
            int puerto = ((IPEndPoint)servidor.LocalEndpoint).Port;
            OpcionesNativeSsh opciones = new() { Contrasena = "sin-uso", TimeoutConexion = TimeoutConexion };
            string knownHosts = Path.Combine(Path.GetTempPath(), $"ns_known_hosts_{Guid.NewGuid():N}");
            using FabricaConexionSsh fabrica = new(opciones, new VerificadorHostConocido([knownHosts]), NullLogger<FabricaConexionSsh>.Instance);
            _ = DestinoSsh.TryParse($"{Environment.UserName}@127.0.0.1:{puerto}", "x", out DestinoSsh destino);
            using CancellationTokenSource cancelacion = new(Cancelacion);
            Stopwatch reloj = Stopwatch.StartNew();
            Task<IConexionSsh> conexion = fabrica.ConectarAsync(destino, cancelacion.Token);
            using Socket aceptada = await servidor.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(10));
            _ = aceptada.Send("SSH-2.0-OpenSSH_10.0\r\n"u8);

            // Act
            Func<Task> conectar = () => conexion.WaitAsync(TimeoutConexion + TimeSpan.FromSeconds(10));

            // Assert: la cancelación (no el plazo de SSH.NET) termina la conexión, y el socket se cierra.
            _ = await conectar.Should().ThrowAsync<OperationCanceledException>();
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            aceptada.ReceiveTimeout = 10_000;
            byte[] bloque = new byte[4096];
            int leidos;
            do
                leidos = aceptada.Receive(bloque);
            while (leidos > 0);
            _ = leidos.Should().Be(0);
        }
    }
}
