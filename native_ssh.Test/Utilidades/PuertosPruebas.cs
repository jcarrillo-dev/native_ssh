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
    public static class PuertosPruebas
    {
        public const int PuertoPruebasPorDefecto = 22022;

        // Los sshd de las colecciones con login lento, restringido y en español, si no se fijan con su variable, escuchan en
        // X222, X322 y X072 del bloque de la ejecución: fuera del rango manual y del X122 del HTTP de los humos y de las
        // sondas. El de español estuvo en X422, dentro del rango manual: chocaba con el puerto que Libre() daba a otra prueba.
        public const int DesplazamientoLento = 200;
        public const int DesplazamientoRestringido = 300;
        public const int DesplazamientoIdioma = 50;

        private const int InicioManuales = 400;
        private const int FinManuales = 999;
        private const int UltimoPuerto = 65535;
        private const int Intentos = 50;

        public static int PuertoPruebas =>
            int.TryParse(Environment.GetEnvironmentVariable("NATIVE_SSH_PUERTO_PRUEBAS"), NumberStyles.None, CultureInfo.InvariantCulture, out int puerto)
                ? puerto
                : PuertoPruebasPorDefecto;

        // El puerto de un sshd de colección: el de su variable si está definida y no vacía (vacía equivale a no definida,
        // como en las variables del servidor); si no, NATIVE_SSH_PUERTO_PRUEBAS más su desplazamiento, para que dos
        // ejecuciones en paralelo con bloques distintos no se pisen.
        public static string PuertoColeccion(string variable, int desplazamiento) =>
            PuertoColeccion(Environment.GetEnvironmentVariable(variable), PuertoPruebas, desplazamiento);

        internal static string PuertoColeccion(string? valorVariable, int puertoPruebas, int desplazamiento)
        {
            if (valorVariable is { Length: > 0 })
                return valorVariable;

            ComprobarBloque(puertoPruebas);
            return (puertoPruebas + desplazamiento).ToString(CultureInfo.InvariantCulture);
        }

        public static (int Inicio, int Fin) RangoManual => RangoManualDe(PuertoPruebas);

        internal static (int Inicio, int Fin) RangoManualDe(int puertoPruebas)
        {
            ComprobarBloque(puertoPruebas);
            int bloque = puertoPruebas / 1000 * 1000;
            return (bloque + InicioManuales, bloque + FinManuales);
        }

        // El puerto de pruebas y sus derivados (X+50 español, X+100 HTTP de los humos, X+200 lento, X+300 restringido) deben
        // quedar por debajo del rango manual X400–X999 de su bloque: si no, un sshd de colección y uno manual pueden caer en el
        // mismo puerto. Solo se cumple con las tres últimas cifras entre 000 y 099 (X0nn). Además, el bloque entero (hasta X999)
        // tiene que caber por debajo de 65536: con 65000 o más, el rango manual pasaría del último puerto.
        private static void ComprobarBloque(int puertoPruebas)
        {
            int finBloque = (puertoPruebas / 1000 * 1000) + FinManuales;
            if (finBloque > UltimoPuerto)
                throw new InvalidOperationException($"NATIVE_SSH_PUERTO_PRUEBAS={puertoPruebas} no es válido: el rango {InicioManuales}–{FinManuales} de su bloque de mil, del que se eligen los sshd manuales, llegaría a {finBloque} y el último puerto es {UltimoPuerto}. Usa un valor por debajo de 65000 con las tres últimas cifras entre 000 y 099 (convención X0nn, p. ej. 64022).");
            if ((puertoPruebas % 1000) + DesplazamientoRestringido < InicioManuales)
                return;
            throw new InvalidOperationException($"NATIVE_SSH_PUERTO_PRUEBAS={puertoPruebas} no es válido: sus puertos derivados (+{DesplazamientoIdioma} español, +100 HTTP, +{DesplazamientoLento} lento, +{DesplazamientoRestringido} restringido) caerían en el rango {InicioManuales}–{FinManuales} de su bloque de mil, del que se eligen los sshd manuales. Usa un valor con las tres últimas cifras entre 000 y 099 (convención X0nn, p. ej. {(puertoPruebas / 1000 * 1000) + 22}).");
        }

        // Un puerto del rango manual en el que ahora no escucha nadie, elegido al azar para que dos pruebas seguidas no
        // repitan el mismo (el sshd anterior puede estar aún liberándolo).
        [SupportedOSPlatform("linux")]
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

        [SupportedOSPlatform("linux")]
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

        [SupportedOSPlatform("linux")]
        private static (int Inicio, int Fin) RangoEfimero()
        {
            string[] limites = File.ReadAllText("/proc/sys/net/ipv4/ip_local_port_range").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return (int.Parse(limites[0], CultureInfo.InvariantCulture), int.Parse(limites[1], CultureInfo.InvariantCulture));
        }
    }
}
