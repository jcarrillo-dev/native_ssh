using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;

namespace NativeSsh.Ssh
{
    // Lo de Windows de 'ssh -G' (solo se compila para Windows: ver NativeSshSistema en Directory.Build.props).
    // El ssh.exe de OpenSSH para Windows (medido con 9.5p2) se queda bloqueado en la segunda escritura en su salida de
    // errores cuando esta es una tubería, sea cual sea su entrada: un 'ssh -G' que escribía algún mensaje (una configuración
    // con errores, un -F que no existe o, con la entrada en una tubería, «Pseudo-terminal will not be allocated because
    // stdin is not a terminal.») no terminaba y agotaba el plazo. Por eso sus mensajes van a un fichero propio de cada
    // ejecución (-E), que se lee como su salida de errores y se borra; y -T (no pedir terminal), para que el mensaje del
    // terminal ni siquiera se escriba. En los demás sistemas no hace falta. Lo que lanza un Match exec no escribe en ese
    // fichero, sino en la salida de errores de ssh (la tubería), y ssh lo ejecuta mientras lee la configuración, antes de
    // terminar con su error: en el error, la salida de errores va delante y los mensajes de ssh del fichero detrás, en el
    // orden en que ocurren en el caso normal (si ssh escribe algo antes del Match exec, como sus mensajes de depuración,
    // ese orden entre los dos no se puede saber y sale detrás).
    public sealed partial class ResolutorConfiguracionSsh
    {
        // Se llama con la lista vacía: lo que añade va delante de las demás opciones.
        static partial void AnadirOpcionesDelSistema(scoped ref PooledListRef<string> argumentos, ref string? registro)
        {
            registro = Path.Combine(Path.GetTempPath(), $"native_ssh_g_{Guid.NewGuid():N}.log");
            argumentos.Add("-T");
            argumentos.Add("-E");
            argumentos.Add(registro);
        }

        static partial void LeerRegistroDelSistema(string? registro, ref string errores)
        {
            try
            {
                if (registro is null || !File.Exists(registro))
                    return;

                string mensajes = File.ReadAllText(registro);
                errores = errores.Length == 0 || errores.EndsWith('\n') ? errores + mensajes : errores + Environment.NewLine + mensajes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sin el registro, el error lleva solo lo que ssh escribió en su salida de errores.
            }
        }

        // CreateProcess, con un nombre sin ruta, busca el programa en el directorio de native_ssh y en el directorio actual
        // antes que en PATH: un ssh.exe en la carpeta desde la que se lanza native_ssh (la del proyecto que abre el cliente
        // MCP, que puede venir de un repositorio ajeno) se ejecutaría al leer la configuración. Por eso el programa se busca
        // aquí, solo en PATH, y se lanza por su ruta completa; como CreateProcess, con .exe si el nombre no lleva extensión.
        static partial void NombreDelSistema(ref string programa)
        {
            if (!Path.HasExtension(programa))
                programa += ".exe";
        }

        // Una entrada de PATH entre comillas se usa sin ellas; una que no es absoluta (vacía, '.', una relativa) se salta:
        // sería relativa al directorio actual, justo lo que se evita.
        static partial void DirectorioDePathDelSistema(ref string? directorio)
        {
            string sinComillas = directorio!.Trim().Trim('"');
            directorio = Path.IsPathFullyQualified(sinComillas) ? sinComillas : null;
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
