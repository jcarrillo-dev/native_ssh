using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NativeSsh.Test.Utilidades
{
    // sudo sin contraseña simulado con un espacio de nombres de usuario. Con los rangos subordinados del usuario
    // (/etc/subuid y /etc/subgid, aplicados por newuidmap/newgidmap) el "root" del espacio de nombres manda también sobre
    // otros uid y gid (el 1 del espacio es el primero del rango, hasta el 65533): hay archivos y directorios de OTRO usuario que el
    // de las pruebas no puede tocar y "root" sí, como con sudo real. Sin rangos subordinados se usa 'unshare -r': "root"
    // es el propio usuario y solo están mapeados su uid y su gid primario (cualquier otro aparece como 65534 y no se le
    // puede asignar). Diferencias con sudo real: no es root del sistema (solo manda sobre los uid y gid mapeados), no hay
    // env_reset ni secure_path (el PATH se hereda) ni contraseña. Requiere espacios de nombres de usuario sin privilegios.
    public static class SudoSimulado
    {
        private static readonly Lazy<string[]> Mapeo = new(CalcularMapeo);

        // Argumentos de 'unshare' de la simulación.
        public static IReadOnlyList<string> Argumentos => Mapeo.Value;

        // Hay uid y gid ajenos mapeados: dentro del espacio de nombres, 1, 2, 3... son usuarios y grupos de otro uid.
        public static bool HasOtrosUsuarios => Mapeo.Value.Length > 1;

        // Cuerpo de un 'sudo' falso para el PATH del shell remoto.
        public static string Script => $"exec unshare {string.Join(' ', Mapeo.Value)} \"$@\"";

        // Ejecuta 'script' con bash como el "root" de la simulación ("$1"... = argumentos). Devuelve stdout tal cual.
        public static string ComoRoot(string script, params string[] argumentos)
        {
            (int codigo, string salida, string errores) = Ejecutar("unshare", [.. Mapeo.Value, "bash", "-c", script, "ns", .. argumentos]);
            return codigo != 0
                ? throw new InvalidOperationException($"El script como root simulado falló ({codigo}): {errores}")
                : salida;
        }

        // Rango subordinado de 'usuario' en un archivo con líneas 'nombre_o_uid:inicio:cantidad' (el primero que aparezca),
        // acotado para mapear solo los id 1 .. desbordamiento-1 del espacio de nombres. El id de desbordamiento (65534,
        // /proc/sys/kernel/overflow{u,g}id) es el que muestra el espacio para un uid o gid sin mapear (p. ej. un grupo
        // secundario del usuario): si estuviera mapeado, 'chown' a "65534" asignaría en silencio un id ajeno al real.
        internal static string? BuscarRango(string contenido, string usuario, string uid, long desbordamiento)
        {
            foreach (string linea in contenido.Split('\n'))
            {
                string[] partes = linea.Trim().Split(':');
                bool isSuyo = partes.Length == 3 && (partes[0] == usuario || partes[0] == uid);
                if (isSuyo && long.TryParse(partes[1], NumberStyles.None, CultureInfo.InvariantCulture, out _) && long.TryParse(partes[2], NumberStyles.None, CultureInfo.InvariantCulture, out long cantidad) && cantidad > 0)
                    return $"{partes[1]}:{Math.Min(cantidad, desbordamiento - 1).ToString(CultureInfo.InvariantCulture)}";
            }

            return null;
        }

        private static string[] CalcularMapeo()
        {
            string usuario = Environment.UserName;
            string uid = Ejecutar("id", ["-u"]).Salida.Trim();
            string? rangoUid = BuscarRango(LeerSiExiste("/etc/subuid"), usuario, uid, LeerDesbordamiento("/proc/sys/kernel/overflowuid"));
            string? rangoGid = BuscarRango(LeerSiExiste("/etc/subgid"), usuario, uid, LeerDesbordamiento("/proc/sys/kernel/overflowgid"));
            if (rangoUid is not null && rangoGid is not null)
            {
                string[] conRangos = ["--map-root-user", "--map-users", $"1:{rangoUid}", "--map-groups", $"1:{rangoGid}"];
                if (Ejecutar("unshare", [.. conRangos, "true"]).Codigo == 0)
                    return conRangos;
            }

            return Ejecutar("unshare", ["-r", "true"]).Codigo == 0
                ? ["-r"]
                : throw new InvalidOperationException("La simulación de sudo necesita 'unshare' con espacios de nombres de usuario sin privilegios (kernel.unprivileged_userns_clone=1 y sin restricción de AppArmor); con /etc/subuid y /etc/subgid, además, cubre archivos de otros usuarios.");
        }

        private static string LeerSiExiste(string ruta) => File.Exists(ruta) ? File.ReadAllText(ruta, Encoding.UTF8) : string.Empty;

        private static long LeerDesbordamiento(string ruta) =>
            long.TryParse(LeerSiExiste(ruta).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 1 ? id : 65534;

        private static (int Codigo, string Salida, string Errores) Ejecutar(string programa, string[] argumentos)
        {
            ProcessStartInfo inicio = new(programa, argumentos) { RedirectStandardOutput = true, RedirectStandardError = true };
            using Process proceso = Process.Start(inicio)!;
            Task<string> errores = proceso.StandardError.ReadToEndAsync();
            string salida = proceso.StandardOutput.ReadToEnd();
            proceso.WaitForExit();
            return (proceso.ExitCode, salida, errores.Result);
        }
    }
}
