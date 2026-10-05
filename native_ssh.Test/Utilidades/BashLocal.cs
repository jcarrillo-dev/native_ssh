using System.Diagnostics;

namespace NativeSsh.Test.Utilidades
{
    // Comandos de bash en la máquina local, que es también el servidor de las pruebas de integración.
    public static class BashLocal
    {
        public static string Ejecutar(string comando)
        {
            ProcessStartInfo inicio = new("bash", ["-c", comando]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using Process proceso = Process.Start(inicio)!;
            Task<string> errores = proceso.StandardError.ReadToEndAsync();
            string salida = proceso.StandardOutput.ReadToEnd();
            proceso.WaitForExit();
            _ = errores.Result;
            return salida.Trim();
        }
    }
}
