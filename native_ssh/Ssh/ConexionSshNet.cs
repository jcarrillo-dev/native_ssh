using Renci.SshNet;
using NativeSsh.Sesion;

namespace NativeSsh.Ssh
{
    public sealed class ConexionSshNet(SshClient cliente) : IConexionSsh
    {
        // Modos PTY: sin eco desde el primer byte y sin conversión LF→CRLF (D5).
        private static readonly Dictionary<Renci.SshNet.Common.TerminalModes, uint> ModosTerminal = new()
        {
            [Renci.SshNet.Common.TerminalModes.ECHO] = 0,
            [Renci.SshNet.Common.TerminalModes.ONLCR] = 0
        };

        public bool IsConectada
        {
            get
            {
                try
                {
                    return cliente.IsConnected;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        public ICanalShell AbrirShell() =>
            new CanalShellSshNet(cliente.CreateShellStream("dumb", 200, 50, 0, 0, SesionShell.TamanoBloqueLectura, ModosTerminal));

        public Task<ResultadoCanalExec> EjecutarExecAsync(string comando, int maxBytesSalida, CancellationToken cancellationToken) =>
            CanalExecSshNet.EjecutarAsync(cliente, comando, maxBytesSalida, cancellationToken);

        public void Dispose() => cliente.Dispose();
    }
}
