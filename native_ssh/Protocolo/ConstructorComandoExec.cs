using System.Text;

namespace NativeSsh.Protocolo
{
    // Comando de un canal exec. Lo interpreta el shell de login del usuario, que puede no ser bash (fish, zsh, tcsh...):
    // solo se usa sintaxis común a todos (palabras simples, un literal entre comillas simples y '</dev/null'). El script
    // viaja en base64 como argumento y lo ejecuta bash, con stdin en /dev/null (nada puede quedarse esperando entrada).
    //
    // El plazo se aplica en el propio servidor, porque cerrar el canal no mata nada (OpenSSH ignora la señal de
    // SshCommand.CancelAsync y el proceso sigue vivo si no escribe):
    // - con 'timeout': TERM al grupo del script al vencer y KILL 2 s después (código 124, o 137 si hizo falta KILL);
    // - sin 'timeout': watchdog en bash. El script corre como trabajo en su propio grupo de procesos (set -m); una
    //   subshell duerme el plazo, avisa al padre (USR1), manda TERM al grupo y KILL 2 s después, y el padre sale con
    //   124. Si el script acaba antes se mata el grupo del watchdog (su 'sleep' incluido). El stderr del padre va a
    //   /dev/null para que los avisos del control de trabajos no se mezclen con el del script (que usa el descriptor 3).
    //   El PID de cada trabajo sale de 'jobs -p %+' y no de '$!': csh/tcsh expanden '!' incluso entre comillas simples.
    // Un proceso que se desligue con setsid escapa de ambos.
    public static class ConstructorComandoExec
    {
        private const string Programa =
            "s=$(printf %s \"$1\" | base64 -d) || exit 125; " +
            "if command -v timeout >/dev/null 2>&1; then exec timeout -k 2 \"$2\" bash --noprofile --norc -c \"$s\"; fi; " +
            "exec 3>&2 2>/dev/null; vencido=0; trap \"vencido=1\" USR1; set -m; " +
            "bash --noprofile --norc -c \"$s\" 2>&3 3>&- & p=$(jobs -p %+); " +
            "( sleep \"$2\" || exit; kill -USR1 $$; kill -TERM -- -$p; sleep 2; kill -KILL -- -$p ) 3>&- & w=$(jobs -p %+); " +
            "while :; do wait $p; rc=$?; kill -0 $p || break; done; " +
            "kill -KILL -- -$w; [ $vencido = 1 ] && exit 124; exit $rc";

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
