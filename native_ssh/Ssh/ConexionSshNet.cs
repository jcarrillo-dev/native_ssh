using Renci.SshNet;
using Renci.SshNet.Common;
using NativeSsh.Sesion;

namespace NativeSsh.Ssh
{
    public sealed class ConexionSshNet : IConexionSsh
    {
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
        // que quien vea el canal cerrado vea también la conexión perdida. La suscripción es de la conexión, no solo del
        // canal del shell: si el aviso llega mientras AbrirShell construye el canal (después de que el ShellStream se
        // suscriba al error de la sesión y antes de que CanalShellSshNet se suscriba al suyo), nadie cerraría ese canal y
        // un comando esperaría hasta su plazo; la comprobación que sigue a la suscripción lo ve porque la conexión ya se dio
        // por perdida con el propio aviso, sin depender de que SSH.NET haya cerrado ya el socket.
        //
        // Con la conexión ya establecida, SSH.NET no abandona ninguna espera mientras la conexión viva: ConnectionInfo.Timeout
        // (TimeoutConexion, que sigue acotando el handshake) pasa a infinito. Session.WaitOnHandle lo lee en cada espera,
        // y con un plazo una apertura de canal sin respuesta del servidor se daba por fallida e ignoraba la confirmación
        // tardía: el canal quedaba abierto en el servidor, ocupando una plaza de su MaxSessions, sin que nadie lo cerrara.
        // Ahora la apertura termina con la confirmación, el rechazo o la caída de la conexión, y el límite para quien
        // espera es local (el plazo de cada lectura y el timeout de cada comando, que no esperan a SSH.NET). Esperas
        // afectadas tras conectar:
        // - apertura de canal y peticiones pty-req, shell y exec (ChannelSession): SesionShell las hace fuera del bucle de
        //   comandos y sin candados, y un canal cuya apertura termina tarde se cierra o se reutiliza;
        // - escribir en el canal del shell con la ventana del servidor agotada (Channel.SendData) y cualquier envío durante
        //   una renegociación de claves (Session.SendMessage): esperan a que el servidor responda o a que caiga la conexión.
        //   No es la única escritura que puede no volver: si el servidor deja de leer, el envío TCP se llena y Socket.Send
        //   se bloquea sin plazo (eso no depende de Timeout). Liberar la conexión (ssh_disconnect) las desbloquea todas
        //   gracias al parche de externo/SSH.NET (rama native-ssh, "Do not block Session.Dispose on a send that the server
        //   does not accept"): Session.Dispose no espera más de 1 s al candado de escritura que retiene el envío bloqueado
        //   (sin él no envía SSH_MSG_DISCONNECT) y cierra el socket, lo que hace fallar ese Send; sin el parche Dispose se
        //   quedaba esperando ese candado para siempre. Con el búfer TCP lleno y ningún envío en curso, el propio
        //   SSH_MSG_DISCONNECT tiene un plazo de 1 s ("Bound the send of the disconnect message");
        // - keepalive: solo durante una renegociación sin respuesta, y los siguientes se saltan mientras tanto.
        // No cambia la desconexión: DisconnectMessage se admite durante una renegociación y la espera del hilo de mensajes
        // no tiene plazo. El hilo de mensajes de SSH.NET no puede quedarse esperando una renegociación: entre KEXINIT y
        // NEWKEYS el servidor solo puede enviar mensajes genéricos de transporte (IGNORE, DEBUG...) y los de la propia
        // renegociación (RFC 4253, 7.1), y ninguno le hace contestar con un mensaje que tenga que esperarla. SSH.NET
        // 2026.0.0 aplicaba strict-kex también a las renegociaciones y cortaba la conexión ante el SSH_MSG_IGNORE que
        // OpenSSH 10.5 envía en ese intervalo; corregido en externo/SSH.NET (rama native-ssh, "Apply the strict key exchange
        // termination rule only to the initial key exchange"): la regla estricta solo en el intercambio inicial, como dice
        // la especificación de OpenSSH (RenegociacionStrictKexTests).
        //
        // Cerrar un canal tampoco tiene plazo (ChannelCloseTimeout, 1 s por defecto, pasa a infinito): Channel.Close envía
        // nuestro CLOSE y espera el del servidor, que OpenSSH no manda hasta que termina el proceso de la sesión (un proceso
        // que no escribe vive hasta su plazo remoto). Así, cerrar un canal exec abortado termina cuando el servidor libera
        // de verdad su sesión (o cae la conexión), y su cupo se libera entonces: con 1 s se daba por cerrado mientras el
        // servidor seguía contándolo en su MaxSessions. Quien cierra el canal del shell no lo espera más de un segundo
        // (SesionShell.CerrarCanal): un bash remoto que no muere al perder el terminal no puede bloquear la cola.
        public ConexionSshNet(SshClient cliente)
        {
            _cliente = cliente;
            cliente.ConnectionInfo.Timeout = Timeout.InfiniteTimeSpan;
            cliente.ConnectionInfo.ChannelCloseTimeout = Timeout.InfiniteTimeSpan;
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
                // SesionShell dice qué ocupa las sesiones del servidor (lecturas, shells abandonados...): aquí no se sabe.
                throw new CanalRechazadoException("El servidor rechazó abrir el canal del shell.", ex);
            }
            catch (Exception ex) when (IsCaidaDeConexion(ex))
            {
                // La conexión cayó mientras se abría el shell: se da por perdida antes de que SesionShell lo vea, para que
                // lo diga así (y no con el texto de SSH.NET) y el siguiente comando reconecte.
                MarcarPerdida();
                throw;
            }

            CanalShellSshNet canal = new(flujo, MarcarPerdida);
            // Si la conexión cayó entre la apertura y la suscripción a ErrorOccurred, nadie cerraría el canal.
            if (!IsConectada)
                canal.Dispose();
            return canal;
        }

        public Task<ResultadoCanalExec> EjecutarExecAsync(string comando, int maxBytesSalida, Action alConfirmarseApertura, CancellationToken cancellationToken) =>
            CanalExecSshNet.EjecutarAsync(_cliente, comando, maxBytesSalida, alConfirmarseApertura, cancellationToken);

        public void Dispose()
        {
            _cliente.ErrorOccurred -= OnErrorConexionEvent;
            _cliente.Dispose();
        }

        // Lo que lanza SSH.NET al abrir un canal cuando la conexión cae a mitad: su error de conexión ("Client not
        // connected.", "The connection was closed by the server..."), el cliente ya liberado o, si la caída llega mientras
        // se construye el ShellStream (Session_Disconnected lo libera y ChannelSession.Dispose anula sus esperas), el
        // ArgumentNullException de WaitOnHandle con el handle ya nulo.
        internal static bool IsCaidaDeConexion(Exception ex) =>
            ex is SshConnectionException or ObjectDisposedException or ArgumentNullException { ParamName: "waitHandle" };

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
