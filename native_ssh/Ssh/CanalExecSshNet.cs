using System.Buffers;
using Renci.SshNet;
using Renci.SshNet.Common;
using NativeSsh.Comun;
using NativeSsh.Sesion;

namespace NativeSsh.Ssh
{
    // Canal exec con SshCommand de SSH.NET 2026.0.0 (CreateCommand + ExecuteAsync):
    // - ExecuteAsync abre el canal de forma síncrona (Open espera la confirmación y el semáforo de MaxSessions): se
    //   invoca en el pool (Task.Factory.StartNew) y se espera después la tarea que devuelve.
    // - OutputStream y ExtendedOutputStream son PipeStream sin límite: SSH.NET encola todo lo que llega aunque nadie
    //   lea. Se leen a la vez (lectura bloqueante, un hilo dedicado por flujo) contando los bytes de ambos; al superar
    //   el tope se cancela el comando y se cierra el canal en lugar de seguir acumulando.
    // - Cancelar: CancelAsync (señal KILL, sin esperar respuesta: OpenSSH puede ignorarla) completa el comando y cierra
    //   los flujos; Dispose cierra el canal. El script remoto va además acotado por 'timeout' (ConstructorComandoExec).
    // - Con el token de ExecuteAsync SSH.NET esperaría 500 ms de forma síncrona dentro del callback: no se usa.
    // - Si el servidor rechaza abrir el canal (MaxSessions de sshd), Open lanza un SshException genérico ("Failed to
    //   open a channel after N attempts.") con la conexión intacta: se traduce a un mensaje que lo explica.
    internal static class CanalExecSshNet
    {
        private const int TamanoBloque = 16 * 1024;

        internal const string MensajeCanalRechazado = "El servidor rechazó abrir otro canal en la conexión SSH: probablemente se alcanzó su límite de sesiones por conexión (MaxSessions de sshd; por defecto 10, y native_ssh usa como mucho el shell y 4 canales para las herramientas de lectura). La conexión sigue abierta y el shell de ssh_exec sigue disponible: reintenta en unos segundos o lanza menos operaciones a la vez.";

        public static async Task<ResultadoCanalExec> EjecutarAsync(SshClient cliente, string comando, int maxBytesSalida, CancellationToken cancellationToken)
        {
            using SshCommand orden = cliente.CreateCommand(comando);
            Task ejecucion;
            try
            {
                ejecucion = await Task.Factory.StartNew(() => orden.ExecuteAsync(CancellationToken.None), cancellationToken, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).ConfigureAwait(false);
            }
            catch (SshException ex) when (ex.GetType() == typeof(SshException) && IsConectado(cliente))
            {
                throw new NativeSshException(MensajeCanalRechazado, ex);
            }

            ContadorTope contador = new(maxBytesSalida, orden);
            Task<MemoryStream> salida = Task.Factory.StartNew(() => Leer(orden.OutputStream, contador), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task<MemoryStream> errores = Task.Factory.StartNew(() => Leer(orden.ExtendedOutputStream, contador), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            using (cancellationToken.Register(static estado => ((ContadorTope)estado!).Cancelar(), contador))
            {
                _ = await Task.WhenAll(salida, errores).ConfigureAwait(false);
                try
                {
                    await ejecucion.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (contador.IsCancelado)
                {
                    // Cancelado por el token o por el tope: se resuelve abajo.
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            MemoryStream bytesSalida = await salida.ConfigureAwait(false);
            MemoryStream bytesErrores = await errores.ConfigureAwait(false);
            return contador.HasExcedido
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

        // Read bloquea hasta que hay datos y devuelve 0 cuando SSH.NET cierra el flujo (fin del comando o cancelación).
        private static MemoryStream Leer(Stream flujo, ContadorTope contador)
        {
            MemoryStream destino = new();
            byte[] bloque = ArrayPool<byte>.Shared.Rent(TamanoBloque);
            try
            {
                while (true)
                {
                    int leidos = flujo.Read(bloque, 0, TamanoBloque);
                    if (leidos <= 0 || !contador.TrySumar(leidos))
                        break;

                    destino.Write(bloque, 0, leidos);
                }
            }
            catch (ObjectDisposedException)
            {
                // El comando se canceló o se liberó mientras se leía.
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bloque);
            }

            return destino;
        }

        private sealed class ContadorTope(int maxBytes, SshCommand orden)
        {
            private long _total;
            private int _isCancelado;
            private volatile bool _hasExcedido;

            public bool HasExcedido => _hasExcedido;
            public bool IsCancelado => Volatile.Read(ref _isCancelado) != 0;

            public bool TrySumar(int bytes)
            {
                if (Interlocked.Add(ref _total, bytes) <= maxBytes)
                    return true;

                _hasExcedido = true;
                Cancelar();
                return false;
            }

            public void Cancelar()
            {
                if (Interlocked.Exchange(ref _isCancelado, 1) != 0)
                    return;

                try
                {
                    orden.CancelAsync(forceKill: true, millisecondsTimeout: 0);
                }
                catch (Exception)
                {
                    // Ya había terminado, se estaba liberando o la conexión cayó al enviar la señal. Se ejecuta dentro
                    // de callbacks de CancellationToken: nada debe escapar. CancelAsync cierra los flujos igualmente
                    // (SetAsyncComplete en su finally) y los lectores terminan.
                }
            }
        }
    }
}
