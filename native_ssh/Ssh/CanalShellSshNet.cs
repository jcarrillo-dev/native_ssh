using Renci.SshNet;
using NativeSsh.Sesion;

namespace NativeSsh.Ssh
{
    // Cuando cae el TCP, SSH.NET (externo/SSH.NET, rama native-ssh, "Close the ShellStream when the session is lost") libera
    // el ShellStream al avisar del error de la sesión: Read devuelve 0 y la sesión da el comando por terminado (shell perdido)
    // al momento. La conexión ya se ha dado por perdida antes (ConexionSshNet atiende el mismo aviso y se suscribió antes que
    // el ShellStream), así que quien ve el canal cerrado ve también la conexión perdida.
    public sealed class CanalShellSshNet(ShellStream flujo) : ICanalShell
    {
        // ShellStream.Read bloquea hasta que hay datos y devuelve 0 cuando el canal se cierra.
        public int Leer(Span<byte> destino) => flujo.Read(destino);

        // ShellStream acumula las escrituras: sin Flush no sale nada.
        public void Escribir(ReadOnlySpan<byte> datos)
        {
            flujo.Write(datos);
            flujo.Flush();
        }

        public void Dispose() => flujo.Dispose();
    }
}
