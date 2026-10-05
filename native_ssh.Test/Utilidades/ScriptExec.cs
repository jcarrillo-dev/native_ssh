using System.Text;

namespace NativeSsh.Test.Utilidades
{
    // Extrae el script de bash de un comando de canal exec ('bash ... -c '<programa>' ns <base64> <segundos> <path> </dev/null').
    public static class ScriptExec
    {
        public static string Decodificar(string comando)
        {
            string[] partes = comando.Split(' ');
            int indice = Array.IndexOf(partes, "ns");
            return Encoding.UTF8.GetString(Convert.FromBase64String(partes[indice + 1]));
        }

        // PATH del shell de login con el que se ejecuta el script (null = '-', el PATH por defecto del servidor).
        public static string? DecodificarPath(string comando)
        {
            string[] partes = comando.Split(' ');
            string path = partes[Array.IndexOf(partes, "ns") + 3];
            return path == "-" ? null : Encoding.UTF8.GetString(Convert.FromBase64String(path));
        }
    }
}
