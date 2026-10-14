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
    [Trait("Categoria", "SoloLinux")]
    public sealed class ConexionSshNetIntegracionTests(SshdLocal sshd)
    {
        // Lo que hace SSH.NET al detectar la caída del TCP, antes de cerrar el socket: avisar con ErrorOccurred. Se dispara
        // por reflexión (este proyecto no es AOT) para ver qué hace la conexión con el aviso sin que nada más cambie.
        private static void AvisarCaida(SshClient cliente) =>
            _ = typeof(BaseClient).GetMethod("Session_ErrorOccurred", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(cliente, [null, new ExceptionEventArgs(new SshConnectionException("An established connection was aborted by the server."))]);

        // WS1: la versión que anuncia el servidor decide IsServidorWindows; el OpenSSH de Linux no es Windows.
        [Fact]
        public async Task IsServidorWindows_ConElOpenSshDeLinux_EsFalsoYLaVersionLlega()
        {
            using PrivateKeyFile clave = new(sshd.RutaClave);
            SshClient cliente = new(new Renci.SshNet.ConnectionInfo("127.0.0.1", sshd.Puerto, Environment.UserName, new PrivateKeyAuthenticationMethod(Environment.UserName, clave)));
            cliente.HostKeyReceived += (_, e) => e.CanTrust = true;
            await cliente.ConnectAsync(CancellationToken.None);
            using ConexionSshNet conexion = new(cliente, System.Net.IPAddress.Loopback);

            _ = cliente.ConnectionInfo.ServerVersion.Should().StartWith("SSH-2.0-OpenSSH_");
            _ = conexion.IsServidorWindows.Should().BeFalse();
        }

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
            using ConexionSshNet conexion = new(cliente, System.Net.IPAddress.Loopback);
            bool isConectadaAntes = conexion.IsConectada;

            // Act
            AvisarCaida(cliente);

            // Assert: el socket sigue abierto; solo el aviso hace que la conexión deje de contar como conectada.
            _ = isConectadaAntes.Should().BeTrue();
            _ = cliente.IsConnected.Should().BeTrue();
            _ = conexion.IsConectada.Should().BeFalse();
        }

        // Lo que hace Session.RaiseError de SSH.NET al detectar la caída, sin el aviso: guardar la excepción y señalar
        // _exceptionWaitHandle, que despierta las esperas (WaitOnHandle relanza la excepción). El aviso ErrorOccurred llega
        // después, y el socket se cierra más tarde aún.
        private static void SenalarCaidaSinAvisar(SshClient cliente)
        {
            object sesion = typeof(BaseClient).GetProperty("Session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cliente)!;
            Type tipo = sesion.GetType();
            tipo.GetField("_exception", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sesion, new SshConnectionException("An established connection was aborted by the server."));
            _ = ((EventWaitHandle)tipo.GetField("_exceptionWaitHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sesion)!).Set();
        }

        [Fact]
        public async Task CaidaQueDespiertaLaAperturaDelShellAntesDelAviso_LaConexionSeDaPorPerdida()
        {
            // Arrange: la apertura del shell falla por la caída antes de que llegue ErrorOccurred y con el socket aún abierto,
            // así que solo el propio AbrirShell (MarcarPerdida) puede dar la conexión por perdida a tiempo: si no, SesionShell
            // mostraría el texto de SSH.NET como si la conexión siguiera viva y el siguiente comando no reconectaría.
            using PrivateKeyFile clave = new(sshd.RutaClave);
            SshClient cliente = new(new Renci.SshNet.ConnectionInfo("127.0.0.1", sshd.Puerto, Environment.UserName, new PrivateKeyAuthenticationMethod(Environment.UserName, clave)));
            cliente.HostKeyReceived += (_, e) => e.CanTrust = true;
            await cliente.ConnectAsync(CancellationToken.None);
            using ConexionSshNet conexion = new(cliente, System.Net.IPAddress.Loopback);
            SenalarCaidaSinAvisar(cliente);

            // Act
            Action abrir = () => conexion.AbrirShell(isSinPerfil: false).Dispose();

            // Assert
            _ = abrir.Should().Throw<SshConnectionException>();
            _ = cliente.IsConnected.Should().BeTrue();
            _ = conexion.IsConectada.Should().BeFalse();
        }
    }
}
