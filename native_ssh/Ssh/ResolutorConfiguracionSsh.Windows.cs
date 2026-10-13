namespace NativeSsh.Ssh
{
    // Lo de Windows de 'ssh -G' (solo se compila para Windows: ver NativeSshSistema en Directory.Build.props).
    // El ssh.exe de OpenSSH para Windows (medido con 9.5p2) se queda bloqueado en la segunda escritura en su salida de
    // errores cuando esta es una tubería, sea cual sea su entrada: un 'ssh -G' que escribía algún mensaje (una configuración
    // con errores, un -F que no existe o, con la entrada en una tubería, «Pseudo-terminal will not be allocated because
    // stdin is not a terminal.») no terminaba y agotaba el plazo. Por eso sus mensajes van a un fichero propio de cada
    // ejecución (-E), que se lee como su salida de errores y se borra; y -T (no pedir terminal), para que el mensaje del
    // terminal ni siquiera se escriba. En los demás sistemas no hace falta.
    public sealed partial class ResolutorConfiguracionSsh
    {
        static partial void AnadirOpcionesDelSistema(List<string> argumentos, ref string? registro)
        {
            registro = Path.Combine(Path.GetTempPath(), $"native_ssh_g_{Guid.NewGuid():N}.log");
            argumentos.InsertRange(0, ["-T", "-E", registro]);
        }

        static partial void LeerRegistroDelSistema(string? registro, ref string errores)
        {
            try
            {
                if (registro is not null && File.Exists(registro))
                    errores = File.ReadAllText(registro) + errores;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sin el registro, el error lleva solo lo que ssh escribió en su salida de errores.
            }
        }

        static partial void BorrarRegistroDelSistema(string? registro)
        {
            try
            {
                if (registro is not null)
                    File.Delete(registro);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Queda en la carpeta temporal del usuario.
            }
        }
    }
}
