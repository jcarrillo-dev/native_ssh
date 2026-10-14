using System.Text;
using System.Text.RegularExpressions;

namespace NativeSsh.Protocolo
{
    // Reconoce la respuesta de un servidor sin bash: lo que escribe el shell de login (o, sin perfil, el del usuario con -c)
    // al fallar el 'exec bash' de la inicialización del shell de ssh_exec (busybox ash: "-ash: exec: line 1: bash: not
    // found"; dash: "sh: 1: exec: bash: not found"; zsh: "zsh: command not found: bash") o el stderr de un canal exec cuyo
    // 'bash -c' no se encuentra ("ash: bash: not found", o el 'timeout' de coreutils: "timeout: failed to run command
    // 'bash': No such file or directory").
    // No se guarda en ninguna caché: cada fallo se reconoce por lo que respondió el servidor esa vez.
    public static partial class RespuestaSinBash
    {
        private const int MaxCaracteresLinea = 300;

        [GeneratedRegex(@"\bbash['’]?: (?:command )?not found|\bbash['’]?: no such file|command not found: bash\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex PatronFaltaBash();

        // La primera línea de 'texto' que dice que falta bash, sin caracteres de control y acotada; null si ninguna lo dice.
        public static string? BuscarLinea(string texto)
        {
            foreach (string linea in texto.Split('\n'))
                if (PatronFaltaBash().IsMatch(linea))
                    return Limpiar(linea);

            return null;
        }

        // Sin caracteres de control (un '\r' del terminal, secuencias de escape) y como mucho MaxCaracteresLinea caracteres.
        public static string Limpiar(string texto)
        {
            StringBuilder limpio = new(Math.Min(texto.Length, MaxCaracteresLinea));
            foreach (char caracter in texto)
            {
                if (limpio.Length == MaxCaracteresLinea)
                    break;
                if (!char.IsControl(caracter))
                    _ = limpio.Append(caracter);
            }

            return limpio.ToString().Trim();
        }

        // Texto fijo del mensaje (Describir) para reconocerlo donde solo llega el texto: el aviso de una petición en Error o
        // el de la NativeSshException de una lectura.
        public const string MarcaMensaje = "no tiene bash (el shell ";

        public static bool IsMensajeSinBash(string? mensaje) => mensaje?.Contains(MarcaMensaje, StringComparison.Ordinal) == true;

        // isLogin: respondió el shell de login (petición 'shell' del shell de ssh_exec: "-ash: exec: …"); si no, el shell del
        // usuario con -c, sin login (canal exec de las lecturas y shell sin perfil, que es una petición 'exec': "ash: …").
        public static string Describir(string destino, string linea, bool isLogin) =>
            $"El servidor {destino} {MarcaMensaje}{(isLogin ? "de login" : "del usuario, con -c y sin login,")} respondió: «{linea}»); native_ssh lo necesita para el shell persistente de ssh_exec y para los scripts de todas sus herramientas, también las de lectura, así que ninguna funcionará en este servidor hasta que se instale bash. No se ejecutó nada.";
    }
}
