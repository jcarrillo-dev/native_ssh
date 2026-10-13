using System.Text;

namespace NativeSsh
{
    // Lo de Windows del arranque (solo se compila para Windows: ver NativeSshSistema en Directory.Build.props).
    // En Windows, .NET escribe la salida de errores en la página de códigos de la consola (850 en un Windows en español; 65001
    // si otro programa, p. ej. un ssh.exe al que se mató, la dejó así) aunque sea una tubería: quien la lee (el cliente MCP,
    // que la guarda en su log) no puede saber cuál. Cuando es una tubería o un fichero, se escribe siempre en UTF-8 (sin
    // marca de orden de bytes), como en Linux. En una consola de verdad se deja como está: .NET ya escribe bien en ella.
    public static partial class Arranque
    {
        static partial void PrepararSalidaDeErroresDelSistema()
        {
            if (Console.IsErrorRedirected)
                Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true });
        }
    }
}
