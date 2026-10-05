using Renci.SshNet;
using Renci.SshNet.Common;
using NativeSsh.Comun;
using NativeSsh.Sesion;

namespace NativeSsh.Ssh
{
    public sealed class ConexionSshNet : IConexionSsh
    {
        internal const string MensajeShellRechazado = "El servidor rechazó abrir el canal del shell de ssh_exec en la conexión SSH: probablemente se alcanzó su límite de sesiones por conexión (MaxSessions de sshd), ocupadas ahora por canales de las herramientas de lectura (native_ssh usa como mucho 4 a la vez además del shell). La conexión sigue abierta: reintenta ssh_exec en unos segundos, cuando terminen las lecturas en curso.";

        // Mensaje de SSH.NET 2026.0.0 cuando el servidor responde a CHANNEL_OPEN con un rechazo (Open lanza un
        // SshException genérico, el mismo tipo que para un pty-req o shell rechazados).
        private const string PrefijoCanalRechazado = "Failed to open a channel";

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
            ShellStream flujo;
            try
            {
                flujo = _cliente.CreateShellStream("dumb", 200, 50, 0, 0, SesionShell.TamanoBloqueLectura, ModosTerminal);
            }
            catch (SshException ex) when (ex.GetType() == typeof(SshException) && ex.Message.StartsWith(PrefijoCanalRechazado, StringComparison.Ordinal) && IsConectada)
            {
                throw new NativeSshException(MensajeShellRechazado, ex);
            }

            CanalShellSshNet canal = new(flujo, MarcarPerdida);
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
