using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace NativeSsh.Test.Utilidades
{
    // Puertos de las pruebas de integración. Cada ejecución (por ejemplo, otra copia del repositorio probándose en
    // paralelo) usa su bloque de mil puertos, fijado por NATIVE_SSH_PUERTO_PRUEBAS (X022, el del sshd de la colección
    // por defecto): los sshd que una prueba arranca por su cuenta escuchan en X400–X999. Nunca en el rango efímero del
    // núcleo, donde chocarían con el puerto de origen de cualquier conexión saliente de la máquina.
    [SupportedOSPlatform("linux")]
    public static class PuertosPruebas
    {
        public const int PuertoPruebasPorDefecto = 22022;

        private const int InicioManuales = 400;
        private const int FinManuales = 999;
        private const int Intentos = 50;

        public static int PuertoPruebas =>
            int.TryParse(Environment.GetEnvironmentVariable("NATIVE_SSH_PUERTO_PRUEBAS"), NumberStyles.None, CultureInfo.InvariantCulture, out int puerto)
                ? puerto
                : PuertoPruebasPorDefecto;

        public static (int Inicio, int Fin) RangoManual
        {
            get
            {
                int bloque = PuertoPruebas / 1000 * 1000;
                return (bloque + InicioManuales, bloque + FinManuales);
            }
        }

        // Un puerto del rango manual en el que ahora no escucha nadie, elegido al azar para que dos pruebas seguidas no
        // repitan el mismo (el sshd anterior puede estar aún liberándolo).
        public static int Libre()
        {
            (int inicio, int fin) = RangoManual;
            (int inicioEfimero, int finEfimero) = RangoEfimero();
            if (fin > IPEndPoint.MaxPort || (inicio <= finEfimero && fin >= inicioEfimero))
                throw new InvalidOperationException($"El rango de puertos manuales de las pruebas ({inicio}–{fin}, derivado de NATIVE_SSH_PUERTO_PRUEBAS={PuertoPruebas}) no es válido o se solapa con el rango efímero del sistema ({inicioEfimero}–{finEfimero}).");

            for (int i = 0; i < Intentos; i++)
            {
                int puerto = Random.Shared.Next(inicio, fin + 1);
                if (IsLibre(puerto))
                    return puerto;
            }

            throw new InvalidOperationException($"No hay un puerto libre en {inicio}–{fin} tras {Intentos} intentos.");
        }

        private static bool IsLibre(int puerto)
        {
            try
            {
                using TcpListener sonda = new(IPAddress.Loopback, puerto);
                sonda.Start();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private static (int Inicio, int Fin) RangoEfimero()
        {
            string[] limites = File.ReadAllText("/proc/sys/net/ipv4/ip_local_port_range").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return (int.Parse(limites[0], CultureInfo.InvariantCulture), int.Parse(limites[1], CultureInfo.InvariantCulture));
        }
    }
}
