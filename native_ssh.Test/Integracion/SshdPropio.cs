using System.Globalization;
using System.Runtime.Versioning;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // sshd de una sola prueba: puerto libre del rango manual, directorio de estado temporal propio y líneas extra de
    // sshd_config (p. ej. "LogLevel DEBUG" para contar sesiones o "MaxSessions 5"). Dispose lo para y borra el directorio.
    [SupportedOSPlatform("linux")]
    public sealed class SshdPropio : IDisposable
    {
        // Otra prueba en paralelo puede elegir el mismo puerto libre justo antes: sshd no arranca y se prueba con otro.
        private const int Intentos = 3;

        // forceCommand: el shell interactivo forzado (por defecto el de sshd-local.sh, /bin/bash -l).
        public SshdPropio(string configuracionExtra, string? forceCommand = null)
        {
            Directorio = Path.Combine(Path.GetTempPath(), $"ns_sshd_{Guid.NewGuid():N}");
            for (int intento = 1; ; intento++)
            {
                try
                {
                    Sshd = new SshdLocal(PuertosPruebas.Libre().ToString(CultureInfo.InvariantCulture), Directorio, forceCommand, configuracionExtra);
                    return;
                }
                catch (InvalidOperationException) when (intento < Intentos)
                {
                }
                catch
                {
                    BorrarDirectorio();
                    throw;
                }
            }
        }

        public SshdLocal Sshd { get; }
        public string Directorio { get; }

        // Canales de sesión (shell y exec) abiertos ahora en el servidor por la última conexión aceptada, según su log
        // (necesita LogLevel DEBUG): los creados menos los liberados.
        public int SesionesAbiertas()
        {
            int abiertas = 0;
            foreach (string linea in File.ReadLines(Sshd.RutaLog))
            {
                if (linea.Contains("Accepted publickey", StringComparison.Ordinal))
                    abiertas = 0;
                else if (linea.Contains("new session [server-session]", StringComparison.Ordinal))
                    abiertas++;
                else if (linea.Contains("free: server-session", StringComparison.Ordinal))
                    abiertas--;
            }

            return abiertas;
        }

        public void Dispose()
        {
            Sshd.Dispose();
            BorrarDirectorio();
        }

        private void BorrarDirectorio()
        {
            if (Directory.Exists(Directorio))
                Directory.Delete(Directorio, recursive: true);
        }
    }
}
