using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using NativeSsh.Ssh;
using Renci.SshNet.Common;

namespace NativeSsh.Test.Ssh
{
    public class ConexionSshNetTests
    {
        [SuppressMessage("Usage", "CA2208:Instantiate argument exceptions correctly", Justification = "Reproduce las excepciones de SSH.NET con sus nombres de parámetro.")]
        private static Exception Error(string caso) => caso switch
        {
            "conexion" => new SshConnectionException("Client not connected."),
            "handleNulo" => new ArgumentNullException("waitHandle"),
            "liberado" => new ObjectDisposedException("Renci.SshNet.SshClient"),
            "pty" => new SshException("The pseudo-terminal request was not accepted by the server. Consult the server log for more information."),
            "maxSessions" => new SshException("Failed to open a channel after 1 attempts."),
            "otroArgumento" => new ArgumentNullException("terminalName"),
            _ => throw new ArgumentOutOfRangeException(nameof(caso), caso, null)
        };

        [Theory]
        [InlineData("conexion", true)]
        [InlineData("handleNulo", true)]
        [InlineData("liberado", true)]
        [InlineData("pty", false)]
        [InlineData("maxSessions", false)]
        [InlineData("otroArgumento", false)]
        public void IsCaidaDeConexion_SoloLosErroresDeSshNetQueSignificanQueLaConexionCayo(string caso, bool isCaida)
        {
            // Act
            bool resultado = ConexionSshNet.IsCaidaDeConexion(Error(caso));

            // Assert
            _ = resultado.Should().Be(isCaida);
        }
    }
}
