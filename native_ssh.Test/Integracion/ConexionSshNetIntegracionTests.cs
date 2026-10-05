using System.Reflection;
using FluentAssertions;
using NativeSsh.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NativeSsh.Test.Integracion
{
    // ConexionSshNet sobre un SshClient conectado al sshd local.
    [Collection(ColeccionSshd.Nombre)]
    [Trait("Categoria", "Integracion")]
    public sealed class ConexionSshNetIntegracionTests(SshdLocal sshd)
    {
        // Lo que hace SSH.NET al detectar la caída del TCP, antes de cerrar el socket: avisar con ErrorOccurred. Se dispara
        // por reflexión (este proyecto no es AOT) para ver qué hace la conexión con el aviso sin que nada más cambie.
        private static void AvisarCaida(SshClient cliente) =>
            _ = typeof(BaseClient).GetMethod("Session_ErrorOccurred", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(cliente, [null, new ExceptionEventArgs(new SshConnectionException("An established connection was aborted by the server."))]);

        [Fact]
        public async Task AvisoDeCaidaSinShellAbierto_LaConexionSeDaPorPerdidaSinEsperarAQueSshNetCierreElSocket()
        {
            // Arrange: conexión viva y sin canal del shell, así que solo la propia conexión atiende el aviso. Es lo que
            // necesita AbrirShell cuando la caída llega entre que SSH.NET termina de abrir el shell y CanalShellSshNet se
            // suscribe: su comprobación posterior solo ve la caída si la conexión ya se dio por perdida.
            using PrivateKeyFile clave = new(sshd.RutaClave);
            SshClient cliente = new(new Renci.SshNet.ConnectionInfo("127.0.0.1", sshd.Puerto, Environment.UserName, new PrivateKeyAuthenticationMethod(Environment.UserName, clave)));
            cliente.HostKeyReceived += (_, e) => e.CanTrust = true;
            await cliente.ConnectAsync(CancellationToken.None);
            using ConexionSshNet conexion = new(cliente);
            bool isConectadaAntes = conexion.IsConectada;

            // Act
            AvisarCaida(cliente);

            // Assert: el socket sigue abierto; solo el aviso hace que la conexión deje de contar como conectada.
            _ = isConectadaAntes.Should().BeTrue();
            _ = cliente.IsConnected.Should().BeTrue();
            _ = conexion.IsConectada.Should().BeFalse();
        }
    }
}
