namespace NativeSsh.Ssh
{
    // Lo de Windows de 'ssh -G' (solo se compila para Windows: ver NativeSshSistema en Directory.Build.props).
    public sealed partial class ResolutorConfiguracionSsh
    {
        // -T (no pedir terminal): con la entrada en una tubería, ssh escribe en la salida de errores «Pseudo-terminal will
        // not be allocated because stdin is not a terminal.», y el ssh.exe de OpenSSH para Windows (medido con 9.5p2) se
        // queda bloqueado en esa escritura cuando la salida de errores también es una tubería: 'ssh -G' no terminaba y
        // agotaba el plazo. Con -T el mensaje no se escribe. En los demás sistemas no hace falta.
        static partial void AnadirOpcionesDelSistema(List<string> argumentos) => argumentos.Insert(0, "-T");
    }
}
