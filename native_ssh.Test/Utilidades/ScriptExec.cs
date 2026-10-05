using System.Text;

namespace NativeSsh.Test.Utilidades
{
    // Extrae el script de bash de un comando de canal exec ('bash ... -c '<programa>' ns <base64> <segundos> </dev/null').
    public static class ScriptExec
    {
        public static string Decodificar(string comando)
        {
            string[] partes = comando.Split(' ');
            int indice = Array.IndexOf(partes, "ns");
            return Encoding.UTF8.GetString(Convert.FromBase64String(partes[indice + 1]));
        }
    }
}
