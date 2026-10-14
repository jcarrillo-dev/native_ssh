using System.Text;

namespace NativeSsh.Protocolo
{
    // Comando de un canal exec. Lo interpreta el shell de login del usuario, que puede no ser bash (fish, zsh, tcsh...):
    // solo se usa sintaxis común a todos (palabras simples, un literal entre comillas simples y '</dev/null'). El script
    // viaja en base64 como argumento y lo ejecuta bash, con stdin en /dev/null (nada puede quedarse esperando entrada).
    // Con el PATH del shell de login capturado por el shell PTY (en base64, '-' si aún no se conoce), para que el
    // script vea los mismos programas que ssh_exec (p. ej. rg en ~/.cargo/bin); un canal exec solo recibe el PATH por
    // defecto de sshd porque no pasa por los archivos de perfil. 'timeout' y 'bash' se buscan ya con ese PATH.
    //
    // El plazo se aplica en el propio servidor, porque cerrar el canal no mata un proceso que no escribe y no se envían
    // señales (CanalExecSshNet explica por qué):
    // - con 'timeout': TERM al grupo del script al vencer y KILL 2 s después (código 124, o 137 si hizo falta KILL);
    // - sin 'timeout': watchdog en bash. El script corre como trabajo en su propio grupo de procesos (set -m); una
    //   subshell duerme el plazo, avisa al padre (USR1), manda TERM al grupo y KILL 2 s después, y el padre sale con
    //   124. Si el script acaba antes se mata el grupo del watchdog (su 'sleep' incluido). El stderr del padre va a
    //   /dev/null para que los avisos del control de trabajos no se mezclen con el del script (que usa el descriptor 3).
    //   El PID de cada trabajo sale de 'jobs -p %+' y no de '$!': csh/tcsh expanden '!' incluso entre comillas simples.
    // Un proceso que se desligue con setsid escapa de ambos.
    //
    // Integridad (WS2): un servidor puede truncar la línea de órdenes y ejecutar el principio (medido: el sshd de Windows la
    // corta hacia los 8191 caracteres, sin avisar). Por eso el último argumento es uno de control (ArgumentoControl) que el
    // programa comprueba ANTES de nada: si no llegó entero, faltan también los de detrás del corte, y el programa sale con
    // CodigoComandoTruncado y MarcaTruncado en stderr sin decodificar ni ejecutar el script. Un corte dentro del programa
    // deja una comilla sin cerrar (error de sintaxis del shell de login) y uno en la redirección final, un archivo que no
    // existe: tampoco ejecutan nada. Si llegó entero, escribe MarcaInicio en stderr antes que nada del script: quien lee el
    // resultado (SesionShell.EjecutarExecAsync) la quita y, sin ella, sabe que el programa no llegó a arrancar (lo que haya
    // respondido es del shell de login: sin bash, cmd o PowerShell...). La entrada se redirige también dentro del programa:
    // un corte que solo pierda parte de la redirección final ya lleva todos los argumentos. La redirección sigue al final de
    // la línea: cmd y PowerShell rechazan la línea entera al analizarla (cmd por '3>&-', PowerShell por '<'), antes de
    // ejecutar nada.
    public static class ConstructorComandoExec
    {
        public const string ArgumentoControl = "ns_fin";

        // Lo que devuelve el programa si no le llegó el argumento de control (el script no se ejecutó), junto con MarcaTruncado.
        public const int CodigoComandoTruncado = 97;
        public const string MarcaTruncado = "__NS_TRUNCADO__";

        // La primera línea de stderr del programa cuando le llegaron todos los argumentos.
        public const string MarcaInicio = "__NS_EXEC__";

        // Las dos marcas van partidas en el programa ('echo __NS_EX""EC__'): un shell de login que cite la línea en su error de
        // sintaxis (fish lo hace con una comilla sin cerrar) no las escribe enteras.

        private const string Programa =
            "exec </dev/null; [ \"$4\" = " + ArgumentoControl + " ] || { echo __NS_TRUN\"\"CADO__ >&2; exit 97; }; echo __NS_EX\"\"EC__ >&2; " +
            "s=$(printf %s \"$1\" | base64 -d) || exit 125; " +
            "[ \"$3\" = - ] || PATH=$(printf %s \"$3\" | base64 -d) || exit 125; export PATH; " +
            "if command -v timeout >/dev/null 2>&1; then exec timeout -k 2 \"$2\" bash --noprofile --norc -c \"$s\"; fi; " +
            "exec 3>&2 2>/dev/null; vencido=0; trap \"vencido=1\" USR1; set -m; " +
            "bash --noprofile --norc -c \"$s\" 2>&3 3>&- & p=$(jobs -p %+); " +
            "( sleep \"$2\" || exit; kill -USR1 $$; kill -TERM -- -$p; sleep 2; kill -KILL -- -$p ) 3>&- & w=$(jobs -p %+); " +
            "while :; do wait $p; rc=$?; kill -0 $p || break; done; " +
            "kill -KILL -- -$w; [ $vencido = 1 ] && exit 124; exit $rc";

        public static string Construir(string script, string? directorioTrabajo, TimeSpan timeout, string? pathLogin = null)
        {
            StringBuilder completo = new(script.Length + 128);
            if (!string.IsNullOrEmpty(directorioTrabajo))
                _ = completo.Append("export __NS_CWD='").Append(directorioTrabajo.Replace("'", "'\\''", StringComparison.Ordinal)).Append("'\n");
            _ = completo.Append("cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~\n").Append(script);

            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(completo.ToString()));
            long segundos = Math.Max(1, (long)Math.Ceiling(timeout.TotalSeconds));
            string path = string.IsNullOrEmpty(pathLogin) ? "-" : Convert.ToBase64String(Encoding.UTF8.GetBytes(pathLogin));
            return $"bash --noprofile --norc -c '{Programa}' ns {base64} {segundos} {path} {ArgumentoControl} </dev/null";
        }

        private static ReadOnlySpan<byte> LineaMarcaInicio => "__NS_EXEC__\n"u8;

        private static ReadOnlySpan<byte> LineaMarcaTruncado => "__NS_TRUNCADO__\n"u8;

        // Los errores sin la línea de MarcaInicio (la primera vez que aparece; lo que el shell de login escribiera antes se
        // conserva); false si no está: el programa no arrancó con todos sus argumentos.
        public static bool TryQuitarMarcaInicio(ReadOnlyMemory<byte> errores, out ReadOnlyMemory<byte> sinMarca)
        {
            int inicio = errores.Span.IndexOf(LineaMarcaInicio);
            if (inicio < 0)
            {
                sinMarca = errores;
                return false;
            }

            int fin = inicio + LineaMarcaInicio.Length;
            if (inicio == 0)
            {
                sinMarca = errores[fin..];
                return true;
            }

            byte[] unidos = [.. errores.Span[..inicio], .. errores.Span[fin..]];
            sinMarca = unidos;
            return true;
        }

        // El programa no recibió todos sus argumentos: el servidor truncó la línea de órdenes y no se ejecutó nada.
        public static bool IsTruncado(int? codigoSalida, ReadOnlySpan<byte> salida, ReadOnlySpan<byte> errores) =>
            codigoSalida == CodigoComandoTruncado && salida.IsEmpty && errores.IndexOf(LineaMarcaTruncado) >= 0;
    }
}
