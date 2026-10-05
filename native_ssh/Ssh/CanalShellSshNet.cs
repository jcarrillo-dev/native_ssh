using Renci.SshNet;
using Renci.SshNet.Common;
using NativeSsh.Sesion;

namespace NativeSsh.Ssh
{
    public sealed class CanalShellSshNet : ICanalShell
    {
        private readonly ShellStream _flujo;
        private readonly Action _alPerderseLaConexion;

        // SSH.NET no cierra el ShellStream cuando cae el TCP: solo lo avisa con ErrorOccurred (Disconnected solo llega si
        // el servidor manda SSH_MSG_DISCONNECT), y Read seguiría bloqueado, así que el comando en curso no terminaría
        // hasta su plazo. Al recibir el aviso se marca la conexión como perdida y se libera el flujo: Read devuelve 0 y
        // la sesión da el comando por terminado (shell perdido) al momento.
        public CanalShellSshNet(ShellStream flujo, Action alPerderseLaConexion)
        {
            _flujo = flujo;
            _alPerderseLaConexion = alPerderseLaConexion;
            flujo.ErrorOccurred += OnConexionPerdidaEvent;
        }

        // ShellStream.Read bloquea hasta que hay datos y devuelve 0 cuando el canal se cierra.
        public int Leer(Span<byte> destino) => _flujo.Read(destino);

        // ShellStream acumula las escrituras: sin Flush no sale nada.
        public void Escribir(ReadOnlySpan<byte> datos)
        {
            _flujo.Write(datos);
            _flujo.Flush();
        }

        public void Dispose()
        {
            _flujo.ErrorOccurred -= OnConexionPerdidaEvent;
            _flujo.Dispose();
        }

        // Corre en el hilo de mensajes de SSH.NET, con la sesión ya dada por fallida (sus esperas no bloquean).
        private void OnConexionPerdidaEvent(object? sender, ExceptionEventArgs e)
        {
            _alPerderseLaConexion();
            _flujo.Dispose();
        }
    }
}
