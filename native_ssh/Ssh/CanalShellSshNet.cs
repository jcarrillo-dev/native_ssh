using Renci.SshNet;
using NativeSsh.Sesion;

namespace NativeSsh.Ssh
{
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
