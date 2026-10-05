using System.Runtime.Versioning;

namespace NativeSsh.Test.Utilidades
{
    // Directorio con enlaces a todos los programas de /usr/bin salvo los excluidos: un PATH "sin timeout" o "sin rg".
    [SupportedOSPlatform("linux")]
    public static class GranjaBinarios
    {
        public static string Crear(string directorio, params string[] excluidos)
        {
            _ = Directory.CreateDirectory(directorio);
            foreach (string programa in Directory.EnumerateFileSystemEntries("/usr/bin"))
            {
                string nombre = Path.GetFileName(programa);
                if (Array.IndexOf(excluidos, nombre) < 0 && !File.Exists(Path.Combine(directorio, nombre)))
                    _ = File.CreateSymbolicLink(Path.Combine(directorio, nombre), programa);
            }

            return directorio;
        }
    }
}
