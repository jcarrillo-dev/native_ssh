using System.Buffers;
using Renci.SshNet;
using Renci.SshNet.Common;
using NativeSsh.Comun;
using NativeSsh.Sesion;

namespace NativeSsh.Ssh
{
    // Canal exec con SshCommand de SSH.NET 2026.0.0 (CreateCommand + ExecuteAsync):
    // - ExecuteAsync abre el canal de forma síncrona (Open espera la confirmación y el semáforo de MaxSessions sin atender
    //   ningún token y, tras conectar, sin plazo: ConexionSshNet quita ConnectionInfo.Timeout para que SSH.NET no dé por
    //   fallida una apertura que el servidor confirmará después, lo que dejaba el canal abierto en el servidor sin que
    //   nadie lo cerrara): se invoca en el pool (Task.Factory.StartNew) y se espera después la tarea que devuelve. Si el
    //   token se cancela durante la apertura, esta tarea espera a que termine (confirmación, rechazo o caída de la
    //   conexión) y cierra el canal: solo acaba con el canal cerrado (contrato de IConexionSsh), y SesionShell falla la
    //   operación al momento sin liberar su cupo hasta entonces. Si se abre tarde, el comando llega a arrancar y lo acota
    //   su plazo remoto.
    // - OutputStream y ExtendedOutputStream son PipeStream sin límite: SSH.NET encola todo lo que llega aunque nadie
    //   lea. Se leen a la vez (lectura bloqueante, un hilo dedicado por flujo) contando los bytes de ambos; al superar
    //   el tope se aborta en lugar de seguir acumulando.
    // - Abortar (tope, plazo local, token del llamante o cierre de la sesión) no envía señales: se deja de leer y se
    //   cierra el canal con SshCommand.Dispose (EOF si procede y CLOSE, bajo el candado del canal), que también cierra
    //   los flujos y despierta a los lectores. Ese Dispose vuelve cuando llega el CLOSE del servidor (ChannelCloseTimeout
    //   infinito, ConexionSshNet), que OpenSSH no envía hasta que termina el proceso de la sesión: hasta N+2 s con el plazo
    //   remoto de N s si el proceso no escribe (uno que escribe muere enseguida por SIGPIPE). Se hace en un hilo propio
    //   para no retener un hilo del pool, y la tarea de esta operación no termina hasta entonces, así que el cupo del
    //   canal en SesionShell sigue ocupado mientras el servidor mantiene su sesión.
    //   SshCommand.CancelAsync enviaba antes una petición 'signal', y SSH.NET puede sacarla después del CLOSE:
    //   Channel.Close envía CHANNEL_CLOSE antes de marcar IsOpen=false y SendMessage comprueba IsOpen sin candado. Si el
    //   servidor ya había cerrado su lado, OpenSSH libera el canal al recibir el CLOSE y corta la conexión entera al
    //   llegar la señal ("server_input_channel_req: unknown channel N"): se pierden el shell PTY (y sus 'export') y las
    //   lecturas en vuelo, y se gasta una conexión del cupo de 'ufw limit'.
    //   El Dispose nunca va en el callback del token: Close espera el CLOSE del servidor (hasta el plazo remoto) y el
    //   callback corre en el hilo de quien cancela, que quedaría bloqueado todo ese tiempo.
    // - Tras nuestro CLOSE, SSH.NET solo puede enviar por este canal un WINDOW_ADJUST (al recibir datos; con la ventana
    //   local de 2 GiB de SSH.NET no llega a ocurrir) o un CHANNEL_FAILURE (respuesta a una petición con want_reply), y
    //   OpenSSH 10.5 los descarta con un aviso en su log si el canal ya no existe. Los mensajes que sí cortan la
    //   conexión (DATA, EOF, CLOSE y peticiones) no pueden seguir al CLOSE: un canal exec no escribe en stdin, el EOF
    //   solo sale dentro de Close (antes del CLOSE), el CLOSE una sola vez y la única petición es el exec al abrir.
    // - Cerrar el canal no termina un proceso que no escribe (uno que escriba recibe SIGPIPE), y la señal tampoco servía
    //   de barrera (comprobado con OpenSSH 10.5): sin ForceCommand, OpenSSH la aplica con killpg al grupo de procesos de
    //   la sesión (el del shell de login que ejecuta el comando) y con ForceCommand la rechaza ("refusing to send
    //   signal"). Aplicada, tampoco llega al script si está en otro grupo, que es lo habitual: 'timeout' se pone en el
    //   suyo con setpgid (salvo que sea el líder de la sesión, si el shell de login hace exec del comando) y el watchdog
    //   lo ejecuta como trabajo en otro grupo (set -m). La barrera real es el plazo remoto del propio script ('timeout
    //   -k' o el watchdog, ConstructorComandoExec): tras abortar, el proceso remoto vive hasta ese plazo.
    // - Con el token de ExecuteAsync SSH.NET llamaría a CancelAsync (señal y espera síncrona de 500 ms): no se usa.
    // - Si el servidor rechaza abrir el canal (MaxSessions de sshd), Open lanza un SshException genérico ("Failed to
    //   open a channel after N attempts.") con la conexión intacta: se traduce a un mensaje que lo explica.
    internal static class CanalExecSshNet
    {
        private const int TamanoBloque = 16 * 1024;

        internal const string MensajeCanalRechazado = "El servidor rechazó abrir otro canal en la conexión SSH: probablemente se alcanzó su límite de sesiones por conexión (MaxSessions de sshd; por defecto 10, y native_ssh usa como mucho el shell y 4 canales para las herramientas de lectura). La conexión sigue abierta y el shell de ssh_exec sigue disponible: reintenta en unos segundos o lanza menos operaciones a la vez.";

        public static async Task<ResultadoCanalExec> EjecutarAsync(SshClient cliente, string comando, int maxBytesSalida, Action alConfirmarseApertura, CancellationToken cancellationToken)
        {
            SshCommand orden = cliente.CreateCommand(comando);
            try
            {
                Task ejecucion;
                try
                {
                    ejecucion = await Task.Factory.StartNew(() => orden.ExecuteAsync(CancellationToken.None), cancellationToken, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).ConfigureAwait(false);
                }
                catch (SshException ex) when (ex.GetType() == typeof(SshException) && IsConectado(cliente))
                {
                    throw new NativeSshException(MensajeCanalRechazado, ex);
                }

                // ExecuteAsync vuelve cuando el servidor ha confirmado la apertura del canal y respondido a la petición exec.
                alConfirmarseApertura();
                return await LeerAsync(orden, ejecucion, maxBytesSalida, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                orden.Dispose();
            }
        }

        private static async Task<ResultadoCanalExec> LeerAsync(SshCommand orden, Task ejecucion, int maxBytesSalida, CancellationToken cancellationToken)
        {
            ControlLectura control = new(maxBytesSalida);
            Task<MemoryStream> salida = Task.Factory.StartNew(() => Leer(orden.OutputStream, control), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task<MemoryStream> errores = Task.Factory.StartNew(() => Leer(orden.ExtendedOutputStream, control), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task lecturas = Task.WhenAll(salida, errores);
            using (cancellationToken.Register(static estado => ((ControlLectura)estado!).Abortar(), control))
                _ = await Task.WhenAny(lecturas, control.Aborto).ConfigureAwait(false);

            if (control.IsAbortado)
            {
                await Task.Factory.StartNew(orden.Dispose, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
                try
                {
                    await ejecucion.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Dispose la deja fallida (ObjectDisposedException) si no había terminado: se observa y se descarta.
                }
            }
            else
            {
                // Los lectores solo terminan sin abortar cuando SSH.NET da el comando por terminado: propaga sus errores
                // (por ejemplo, la caída de la conexión).
                await ejecucion.ConfigureAwait(false);
            }

            await lecturas.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            MemoryStream bytesSalida = await salida.ConfigureAwait(false);
            MemoryStream bytesErrores = await errores.ConfigureAwait(false);
            return control.HasExcedido
                ? new ResultadoCanalExec(null, null, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, true)
                : new ResultadoCanalExec(orden.ExitStatus, orden.ExitSignal, bytesSalida.GetBuffer().AsMemory(0, (int)bytesSalida.Length), bytesErrores.GetBuffer().AsMemory(0, (int)bytesErrores.Length), false);
        }

        private static bool IsConectado(SshClient cliente)
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

        // Read bloquea hasta que hay datos y devuelve 0 cuando SSH.NET cierra el flujo (fin del comando o Dispose).
        private static MemoryStream Leer(Stream flujo, ControlLectura control)
        {
            MemoryStream destino = new();
            byte[] bloque = ArrayPool<byte>.Shared.Rent(TamanoBloque);
            try
            {
                while (true)
                {
                    int leidos = flujo.Read(bloque, 0, TamanoBloque);
                    if (leidos <= 0 || !control.TrySumar(leidos))
                        break;

                    destino.Write(bloque, 0, leidos);
                }
            }
            catch (ObjectDisposedException)
            {
                // El comando se liberó mientras se leía.
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bloque);
            }

            return destino;
        }

        // Tope de bytes de stdout+stderr y aviso de aborto. Abortar solo avisa (nunca toca el canal ni lanza): se puede
        // llamar desde los lectores y desde el callback del token.
        private sealed class ControlLectura(int maxBytes)
        {
            private readonly TaskCompletionSource _aborto = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private long _total;
            private volatile bool _hasExcedido;

            public bool HasExcedido => _hasExcedido;
            public bool IsAbortado => _aborto.Task.IsCompleted;
            public Task Aborto => _aborto.Task;

            public bool TrySumar(int bytes)
            {
                if (Interlocked.Add(ref _total, bytes) <= maxBytes)
                    return true;

                _hasExcedido = true;
                Abortar();
                return false;
            }

            public void Abortar() => _ = _aborto.TrySetResult();
        }
    }
}
