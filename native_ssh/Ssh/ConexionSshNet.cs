using Renci.SshNet;
using Renci.SshNet.Common;
using NativeSsh.Sesion;

namespace NativeSsh.Ssh
{
    public sealed class ConexionSshNet : IConexionSsh
    {
        // Modos PTY: sin eco desde el primer byte y sin conversión LF→CRLF (D5).
        private static readonly Dictionary<TerminalModes, uint> ModosTerminal = new()
        {
            [TerminalModes.ECHO] = 0,
            [TerminalModes.ONLCR] = 0
        };

        private readonly SshClient _cliente;
        private volatile bool _isPerdida;

        // Tras la caída del TCP, SshClient.IsConnected puede seguir siendo true un instante (hasta que SSH.NET cierra el
        // socket): la conexión se da por perdida en cuanto llega ErrorOccurred, antes de cerrar el canal del shell, para
        // que quien vea el canal cerrado vea también la conexión perdida.
        public ConexionSshNet(SshClient cliente)
        {
            _cliente = cliente;
            cliente.ErrorOccurred += OnErrorConexionEvent;
        }

        public bool IsConectada => !_isPerdida && IsClienteConectado();

        public ICanalShell AbrirShell()
        {
            CanalShellSshNet canal = new(_cliente.CreateShellStream("dumb", 200, 50, 0, 0, SesionShell.TamanoBloqueLectura, ModosTerminal), MarcarPerdida);
            // Si la conexión cayó entre la apertura y la suscripción a ErrorOccurred, nadie cerraría el canal.
            if (!IsConectada)
                canal.Dispose();
            return canal;
        }

        public Task<ResultadoCanalExec> EjecutarExecAsync(string comando, int maxBytesSalida, CancellationToken cancellationToken) =>
            CanalExecSshNet.EjecutarAsync(_cliente, comando, maxBytesSalida, cancellationToken);

        public void Dispose()
        {
            _cliente.ErrorOccurred -= OnErrorConexionEvent;
            _cliente.Dispose();
        }

        private void MarcarPerdida() => _isPerdida = true;

        private void OnErrorConexionEvent(object? sender, ExceptionEventArgs e) => MarcarPerdida();

        private bool IsClienteConectado()
        {
            try
            {
                return _cliente.IsConnected;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }
}
