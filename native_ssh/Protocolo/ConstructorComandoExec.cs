using System.Text;

namespace NativeSsh.Protocolo
{
    // Comando de un canal exec. Lo interpreta el shell de login del usuario, que puede no ser bash (fish, zsh, tcsh...):
    // solo se usa sintaxis común a todos (palabras simples, un literal entre comillas simples y '</dev/null'). El script
    // viaja en base64 como argumento y lo ejecuta bash, con stdin en /dev/null (nada puede quedarse esperando entrada)
    // y, si el servidor tiene 'timeout', limitado en el propio servidor para no dejar procesos huérfanos.
    public static class ConstructorComandoExec
    {
        private const string Programa = "s=$(printf %s \"$1\" | base64 -d) || exit 125; if command -v timeout >/dev/null 2>&1; then exec timeout -k 2 \"$2\" bash --noprofile --norc -c \"$s\"; fi; eval \"$s\"";

        public static string Construir(string script, string? directorioTrabajo, TimeSpan timeout)
        {
            StringBuilder completo = new(script.Length + 128);
            if (!string.IsNullOrEmpty(directorioTrabajo))
                _ = completo.Append("export __NS_CWD='").Append(directorioTrabajo.Replace("'", "'\\''", StringComparison.Ordinal)).Append("'\n");
            _ = completo.Append("cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~\n").Append(script);

            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(completo.ToString()));
            long segundos = Math.Max(1, (long)Math.Ceiling(timeout.TotalSeconds));
            return $"bash --noprofile --norc -c '{Programa}' ns {base64} {segundos} </dev/null";
        }
    }
}
