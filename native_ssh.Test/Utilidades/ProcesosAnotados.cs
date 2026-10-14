using System.Globalization;
using System.Linq;

namespace NativeSsh.Test.Utilidades
{
    // Procesos que la propia prueba anota al lanzarlos (su pid o su grupo de procesos, en un fichero propio), para comprobar
    // si siguen vivos y matarlos sin buscarlos por su línea de órdenes: un pkill o un pgrep con patrón alcanzaría también
    // los procesos de otras copias de la suite o del usuario que coincidieran. El servidor de las pruebas es esta misma
    // máquina, así que lo que se anota en el servidor se lee aquí.
    public sealed class ProcesosAnotados(string fichero) : IDisposable
    {
        public ProcesosAnotados()
            : this(Path.Combine(Path.GetTempPath(), $"ns_pids_{Guid.NewGuid():N}"))
        {
        }

        public string Fichero { get; } = fichero;

        // Al principio de un script de bash: anota su grupo de procesos, que heredan los procesos que lance aunque queden
        // huérfanos (en los canales exec, el que crean 'timeout' o el watchdog para el script).
        public string AnotarGrupo => $"echo g $(ps -o pgid= -p $$) >> '{Fichero}'; ";

        // Tras un 'orden &' en bash: anota el pid del proceso en segundo plano.
        public string AnotarUltimo => $"echo p $! >> '{Fichero}'";

        // Ejecuta la orden en una subshell que anota su pid y se sustituye por ella: el proceso de la orden es el anotado y
        // cuelga del shell igual que una orden simple.
        public string Ejecutar(string orden) => $"(echo p $BASHPID >> '{Fichero}'; exec {orden})";

        // El script de 'sh -c' que anota su pid y ejecuta la orden en su lugar, para un shell que no sea bash (sin comillas
        // dobles: va dentro de un ssh_config).
        public string ScriptSh(string orden) => $"echo p $$ >> {Fichero}; exec {orden}";

        // Los procesos vivos (no zombis) anotados o de un grupo anotado, con su línea de órdenes.
        public IReadOnlyList<(int Pid, string Orden)> Vivos()
        {
            (HashSet<int> pids, HashSet<int> grupos) = Leer();
            List<(int, string)> vivos = [];
            if (pids.Count == 0 && grupos.Count == 0)
                return vivos;

            foreach (string linea in BashLocal.Ejecutar("ps -e -o pid=,pgid=,stat=,args=").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] campos = linea.Trim().Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
                if (campos.Length < 3 || campos[2].StartsWith('Z'))
                    continue;
                int pid = int.Parse(campos[0], CultureInfo.InvariantCulture);
                int grupo = int.Parse(campos[1], CultureInfo.InvariantCulture);
                if (pids.Contains(pid) || grupos.Contains(grupo))
                    vivos.Add((pid, campos.Length == 4 ? campos[3].Trim() : string.Empty));
            }

            return vivos;
        }

        // Los vivos cuya línea de órdenes cumple la condición.
        public string[] Vivos(Func<string, bool> isElegido) => [.. Vivos().Where(v => isElegido(v.Orden)).Select(v => v.Pid.ToString(CultureInfo.InvariantCulture))];

        // Manda la señal a cada pid y a cada grupo anotados (los que ya no existen se ignoran).
        public void Matar(string senal = "KILL")
        {
            (HashSet<int> pids, HashSet<int> grupos) = Leer();
            IEnumerable<string> objetivos = pids.Select(p => p.ToString(CultureInfo.InvariantCulture)).Concat(grupos.Select(g => $"-{g.ToString(CultureInfo.InvariantCulture)}"));
            string lista = string.Join(' ', objetivos);
            if (lista.Length > 0)
                _ = BashLocal.Ejecutar($"for o in {lista}; do kill -{senal} -- $o 2>/dev/null; done; true");
        }

        // Manda la señal a los hijos de cada pid anotado (p. ej. el 'sleep' que lanza un 'flock … sleep' anotado).
        public void MatarHijos(string senal = "TERM")
        {
            (HashSet<int> pids, _) = Leer();
            if (pids.Count > 0)
                _ = BashLocal.Ejecutar($"for p in {string.Join(' ', pids)}; do pkill -{senal} -P $p; done; true");
        }

        public void Dispose()
        {
            Matar();
            File.Delete(Fichero);
        }

        private (HashSet<int> Pids, HashSet<int> Grupos) Leer()
        {
            HashSet<int> pids = [];
            HashSet<int> grupos = [];
            if (!File.Exists(Fichero))
                return (pids, grupos);

            foreach (string linea in File.ReadAllLines(Fichero))
            {
                string[] campos = linea.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // Un grupo 0 o 1 sería el del que manda la señal o el de todo el sistema: nunca se usa.
                if (campos.Length != 2 || !int.TryParse(campos[1], NumberStyles.None, CultureInfo.InvariantCulture, out int numero) || numero <= 1)
                    continue;
                _ = (campos[0] == "g" ? grupos : pids).Add(numero);
            }

            // Un script lanzado aquí sin grupo propio anotaría el de la propia suite: ni ella ni su grupo se tocan.
            if (grupos.Count > 0)
                _ = grupos.Remove(int.Parse(BashLocal.Ejecutar($"ps -o pgid= -p {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}"), CultureInfo.InvariantCulture));
            _ = pids.Remove(Environment.ProcessId);
            return (pids, grupos);
        }
    }
}
