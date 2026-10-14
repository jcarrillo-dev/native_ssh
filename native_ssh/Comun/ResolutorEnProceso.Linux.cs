using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;

namespace NativeSsh.Comun
{
    // Resolución de un nombre (una familia de una entrada de NATIVE_SSH_SOLO_LECTURA, o las dos juntas para el destino) en un
    // proceso hijo, solo en Linux: el propio binario relanzado en su modo interno ('--resolver-interno <pid del padre>
    // <4|6|0> <nombre>'), que hace getaddrinfo, escribe el resultado y sale. getaddrinfo no se puede cancelar y glibc no
    // tiene plazo en la lectura por TCP del DNS: con un TCP/53 que no cierra nunca, un hilo propio se quedaba bloqueado
    // para siempre (y, si se reutilizaba, el nombre no volvía a resolverse aunque el DNS se recuperara; si no, los hilos
    // crecían con cada reintento). Con el hijo, al vencer el plazo se le manda SIGKILL (por su pid, el de un hijo propio)
    // y se espera a recogerlo (EsperaRecogida): no queda nada bloqueado y la comprobación siguiente vuelve a lanzar la
    // resolución, así que en cuanto el DNS responde el nombre se resuelve.
    //
    // Si el hijo no muere tras el SIGKILL (p. ej. bloqueado en el núcleo, en estado D), la resolución lo dice (la incidencia
    // llega al rechazo), ssh_status lo muestra mientras siga vivo y se vuelve a mirar si terminó, sin bloquear, en cada
    // resolución y en cada ssh_status (.NET recoge el proceso cuando termina). Como mucho MaxSinTerminar a la vez: con
    // ese tope no se lanzan más resoluciones (falla cerrado, y el rechazo lo dice) hasta que alguno termine.
    //
    // El hijo no hereda nada que no necesite: entrada, salida y errores son tuberías propias (nunca el canal MCP), el
    // entorno se vacía salvo las variables que cambian lo que resuelve glibc (LOCALDOMAIN, RES_OPTIONS, HOSTALIASES) y, solo
    // desde las pruebas, la que necesita el ejecutable sin AOT para encontrar .NET (DOTNET_ROOT); ni el token HTTP ni
    // ninguna NATIVE_SSH_*. Los descriptores de .NET se abren con O_CLOEXEC, así que no pasan al hijo. Si native_ssh muere, el hijo
    // lo ve (su padre cambia: getppid) y sale en menos de VigilanciaPadre, y en ningún caso vive más de MaxVidaHijo.
    // El modo interno no está en el MCP (es un argumento del ejecutable) y a mano solo resuelve un nombre válido y solo si
    // se le da el pid de su padre de verdad; con cualquier otra cosa sale con el código 2 sin hacer nada.
    internal sealed partial class ResolutorEnProceso
    {
        public const string ModoInterno = "--resolver-interno";
        public const int MaxSinTerminar = 8;
        private const int LongitudMaximaNombre = 253;
        private static readonly TimeSpan VigilanciaPadre = TimeSpan.FromMilliseconds(50);
        private static readonly TimeSpan MaxVidaHijo = TimeSpan.FromSeconds(30);
        private static readonly string[] VariablesHeredadas = ["LOCALDOMAIN", "RES_OPTIONS", "HOSTALIASES"];

        public static ResolutorEnProceso DelSistema { get; } = new(LanzarPropio, Matar, RecogerAsync);

        // El binario propio (ElegirPrograma); null si no se sabe cuál es: entonces no se resuelve en un proceso hijo
        // (ResolucionPorFamilia.Linux.cs resuelve en hilos, como antes).
        internal static string? ProgramaPropio { get; } = ElegirPrograma(Environment.ProcessPath, File.Exists("/proc/self/exe"));

        // /proc/self/exe si existe (el mismo binario aunque se haya sustituido o borrado el fichero tras arrancar); si no (sin
        // /proc, o tapado), la ruta del proceso; desde las pruebas, que corren dentro de dotnet, el NativeSsh compilado junto
        // a ellas.
        internal static string? ElegirPrograma(string? rutaProceso, bool hasProcSelfExe) =>
            IsDesdeDotnet(rutaProceso) ? Path.Combine(AppContext.BaseDirectory, "NativeSsh")
            : hasProcSelfExe ? "/proc/self/exe"
            : rutaProceso;

        private static bool IsDesdeDotnet(string? rutaProceso) => Path.GetFileNameWithoutExtension(rutaProceso) is "dotnet" or "testhost";

        private readonly Func<string, string, Process?> _lanzar;
        private readonly Action<Process> _matar;
        private readonly Func<Process, TimeSpan, Task<bool>> _recoger;
        private readonly List<SinTerminar> _sinTerminar = [];
        private readonly Lock _candado = new();

        private sealed record SinTerminar(Process Proceso, int Pid, string Nombre, string Familia, long Desde);

        // lanzar: arranca el hijo para (nombre, familia "4"|"6"|"0") con la entrada y la salida estándar redirigidas; matar:
        // le manda SIGKILL; recoger: espera como mucho ese tiempo a que el proceso termine (true si terminó). Las pruebas dan
        // los suyos.
        internal ResolutorEnProceso(Func<string, string, Process?> lanzar, Action<Process> matar, Func<Process, TimeSpan, Task<bool>> recoger)
        {
            _lanzar = lanzar;
            _matar = matar;
            _recoger = recoger;
        }

        // La resolución de 'nombre' en un hijo, que se mata al vencer 'plazo': sus direcciones (vacío: sin direcciones de esa
        // familia), o null si falló, no terminó a tiempo o el nombre no es válido; con la incidencia si el hijo no se pudo
        // terminar o se llegó al tope de los que siguen vivos (entonces no se lanza).
        public async Task<ResultadoResolucion> ResolverAsync(string nombre, AddressFamily familia, CancellationToken plazo)
        {
            if (!IsNombreValido(nombre))
                return default;

            string codigoFamilia = familia == AddressFamily.InterNetwork ? "4" : familia == AddressFamily.InterNetworkV6 ? "6" : "0";
            if (DescribirTope() is { } tope)
                return new ResultadoResolucion(null, tope);

            Process? proceso;
            try
            {
                proceso = _lanzar(nombre, codigoFamilia);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                return default;
            }

            if (proceso is null)
                return default;

            bool isRecogido = true;
            try
            {
                proceso.StandardInput.Close();
                // La salida de errores se lee y se descarta: si se llenara su tubería, el hijo se quedaría bloqueado.
                _ = proceso.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
                string salida = await proceso.StandardOutput.ReadToEndAsync(plazo).ConfigureAwait(false);
                await proceso.WaitForExitAsync(plazo).ConfigureAwait(false);
                return new ResultadoResolucion(Interpretar(salida, proceso.ExitCode));
            }
            catch (OperationCanceledException)
            {
                int pid = proceso.Id;
                _matar(proceso);
                isRecogido = await _recoger(proceso, ResolucionPorFamilia.EsperaRecogida).ConfigureAwait(false);
                if (isRecogido)
                    return default;

                lock (_candado)
                    _sinTerminar.Add(new SinTerminar(proceso, pid, nombre, NombreFamilia(codigoFamilia), Environment.TickCount64));
                return new ResultadoResolucion(null, $"no se pudo terminar el proceso de resolución {pid} ({nombre}); sigue vivo: native_ssh le mandó SIGKILL al vencer el plazo de la resolución y no terminó en {ResolucionPorFamilia.EsperaRecogida.TotalSeconds:0.#} s (p. ej. bloqueado en el núcleo, en estado D)");
            }
            finally
            {
                if (isRecogido)
                    proceso.Dispose();
            }
        }

        // Los hijos que no se pudieron terminar y siguen vivos, para ssh_status (los que ya terminaron se quitan).
        public void AnadirSinTerminar(scoped ref PooledListRef<string> textos)
        {
            lock (_candado)
            {
                QuitarTerminados();
                foreach (SinTerminar hijo in _sinTerminar)
                    textos.Add($"proceso {hijo.Pid} ({hijo.Nombre}, {hijo.Familia}): no terminó tras SIGKILL; vivo desde hace {(Environment.TickCount64 - hijo.Desde) / 1000} s");
            }
        }

        // Con MaxSinTerminar hijos sin terminar, el texto del rechazo (no se lanza otro); si no, null.
        private string? DescribirTope()
        {
            lock (_candado)
            {
                QuitarTerminados();
                if (_sinTerminar.Count < MaxSinTerminar)
                    return null;

                StringBuilder lista = new();
                foreach (SinTerminar hijo in _sinTerminar)
                    _ = lista.Append(lista.Length == 0 ? string.Empty : ", ").Append(CultureInfo.InvariantCulture, $"{hijo.Pid} ({hijo.Nombre})");
                return $"hay {_sinTerminar.Count} procesos de resolución que no se pudieron terminar con SIGKILL y siguen vivos ({lista}), el máximo: no se lanzan más resoluciones hasta que alguno termine (se vuelve a mirar en cada comprobación y en ssh_status)";
            }
        }

        // Con el candado tomado: sin bloquear (HasExited no espera).
        private void QuitarTerminados()
        {
            for (int i = _sinTerminar.Count - 1; i >= 0; i--)
            {
                Process proceso = _sinTerminar[i].Proceso;
                if (!IsTerminado(proceso))
                    continue;

                proceso.Dispose();
                _sinTerminar.RemoveAt(i);
            }
        }

        private static bool IsTerminado(Process proceso)
        {
            try
            {
                return proceso.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        internal static void Matar(Process proceso)
        {
            try
            {
                proceso.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Ya terminó (o no se puede señalar): lo dirá la espera.
            }
        }

        private static string NombreFamilia(string codigo) => codigo switch { "4" => "A", "6" => "AAAA", _ => "A y AAAA" };

        // Lo que escribe el hijo: "ok" y una dirección por línea, "nodata" (sin direcciones de esa familia) o "fallo <código>".
        // Cualquier otra cosa, o un código de salida distinto de 0, es un fallo. Cada dirección, solo escrita como la escribe
        // el hijo (IPAddress.ToString: IPv4 con sus 4 números, IPv6 en su forma canónica, sin zona): una salida cortada o
        // ajena ('10.0.0', que se leería como 10.0.0.0) es un fallo.
        internal static IPAddress[]? Interpretar(string salida, int codigoSalida)
        {
            string[] lineas = salida.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (codigoSalida != 0 || lineas.Length == 0)
                return null;

            if (lineas is ["nodata"])
                return [];

            if (lineas[0] != "ok" || lineas.Length == 1)
                return null;

            IPAddress[] direcciones = new IPAddress[lineas.Length - 1];
            for (int i = 1; i < lineas.Length; i++)
            {
                bool isCanonica = IPAddress.TryParse(lineas[i], out IPAddress? direccion) && direccion.ToString() == lineas[i] && !lineas[i].Contains('%', StringComparison.Ordinal);
                if (!isCanonica)
                    return null;
                direcciones[i - 1] = direccion!;
            }

            return direcciones;
        }

        // Un nombre que se puede resolver: como los de NATIVE_SSH_SOLO_LECTURA (letras, dígitos, '.', '-' y '_', con alguna
        // letra o dígito), sin empezar por '-' y de 253 caracteres como mucho.
        internal static bool IsNombreValido(string nombre)
        {
            if (nombre.Length is 0 or > LongitudMaximaNombre || nombre[0] == '-')
                return false;

            bool hasLetraODigito = false;
            foreach (char c in nombre)
            {
                bool isLetraODigito = char.IsLetterOrDigit(c);
                if (!isLetraODigito && c is not ('.' or '-' or '_'))
                    return false;

                hasLetraODigito |= isLetraODigito;
            }

            return hasLetraODigito;
        }

        internal static Process? LanzarPropio(string nombre, string familia)
        {
            ProcessStartInfo inicio = new(ProgramaPropio!)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = "/"
            };
            inicio.ArgumentList.Add(ModoInterno);
            inicio.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            inicio.ArgumentList.Add(familia);
            inicio.ArgumentList.Add(nombre);
            inicio.Environment.Clear();
            foreach (string variable in VariablesHeredadas)
                if (Environment.GetEnvironmentVariable(variable) is { } valor)
                    inicio.Environment[variable] = valor;
            // El NativeSsh de las pruebas no es AOT: necesita encontrar .NET.
            if (IsDesdeDotnet(Environment.ProcessPath) && Environment.GetEnvironmentVariable("DOTNET_ROOT") is { } raiz)
                inicio.Environment["DOTNET_ROOT"] = raiz;
            return Process.Start(inicio);
        }

        internal static async Task<bool> RecogerAsync(Process proceso, TimeSpan espera)
        {
            try
            {
                await proceso.WaitForExitAsync().WaitAsync(espera).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        // El modo interno (args[0] == ModoInterno): '<pid del padre> <4|6|0> <nombre>'. Escribe "ok" y las direcciones,
        // "nodata" o "fallo <código de getaddrinfo>" y sale con 0; con argumentos que no son los de native_ssh, 2 sin hacer
        // nada. Un hilo vigila al padre mientras getaddrinfo bloquea el principal.
        public static int EjecutarModoInterno(string[] args)
        {
            // El pid tal como lo escribe native_ssh (sin ceros a la izquierda) y el del padre de verdad: también 1, si native_ssh
            // es el pid 1 de su espacio de pids (un contenedor sin init).
            bool isPadre = args.Length == 4 && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int padre)
                && args[1] == padre.ToString(CultureInfo.InvariantCulture) && padre == ProcesoPadre();
            AddressFamily? familia = args.Length == 4 ? args[2] switch { "4" => AddressFamily.InterNetwork, "6" => AddressFamily.InterNetworkV6, "0" => AddressFamily.Unspecified, _ => null } : null;
            if (!isPadre || familia is null || !IsNombreValido(args[3]))
            {
                Console.Error.WriteLine("Modo interno de native_ssh (resuelve un nombre para NATIVE_SSH_SOLO_LECTURA en un proceso aparte): solo lo usa el propio native_ssh.");
                return 2;
            }

            int pidPadre = int.Parse(args[1], NumberStyles.None, CultureInfo.InvariantCulture);
            Thread vigia = new(() => VigilarPadre(pidPadre)) { IsBackground = true };
            vigia.Start();
            IPAddress[]? direcciones = GetAddrInfoLinux.ResolverFamilia(args[3], familia.Value, out int codigo);
            StringBuilder texto = new();
            if (direcciones is null)
                _ = texto.Append(CultureInfo.InvariantCulture, $"fallo {codigo}\n");
            else if (direcciones.Length == 0)
                _ = texto.Append("nodata\n");
            else
            {
                _ = texto.Append("ok\n");
                foreach (IPAddress direccion in direcciones)
                    _ = texto.Append(direccion.ToString()).Append('\n');
            }

            Console.Out.Write(texto.ToString());
            Console.Out.Flush();
            return 0;
        }

        // Sale en cuanto el padre deja de serlo (native_ssh murió) o, en todo caso, al cumplir MaxVidaHijo.
        private static void VigilarPadre(int padre)
        {
            long limite = Environment.TickCount64 + (long)MaxVidaHijo.TotalMilliseconds;
            while (true)
            {
                if (ProcesoPadre() != padre || Environment.TickCount64 > limite)
                    SalirSinLimpieza(3);
                Thread.Sleep(VigilanciaPadre);
            }
        }

        // getppid: el padre cambia si muere (el huérfano pasa a init o a un subreaper). _exit: termina ya, sin la limpieza de
        // .NET, aunque el hilo principal esté bloqueado en getaddrinfo.
        [LibraryImport("libc", EntryPoint = "getppid")]
        private static partial int ProcesoPadre();

        [LibraryImport("libc", EntryPoint = "_exit")]
        private static partial void SalirSinLimpieza(int codigo);
    }
}
