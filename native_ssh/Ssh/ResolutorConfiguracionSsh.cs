using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;

namespace NativeSsh.Ssh
{
    // CI4: el destino se resuelve con la configuración de ssh del usuario mediante 'ssh -G' (la misma lógica de Host, Match e
    // Include que ssh, sin reimplementarla): alias, HostName, User, Port e IdentityFile. Se ejecuta en cada resolución (unos
    // milisegundos: un cambio de ~/.ssh/config vale desde la llamada siguiente, como en ssh), también para IPs y nombres
    // reales, porque 'Host *' y Match se les aplican igual.
    // Decisiones:
    // - Lo explícito del destino manda: usuario@ se pasa con -l y :puerto con -p (así Match user y %r lo ven, como en ssh).
    // - El destino lo escribe el LLM: servidor y usuario solo admiten [A-Za-z0-9._-] (y ':' el servidor, por IPv6), sin
    //   empezar por '-', y el servidor va tras '--'. Sin esto, «ssh://u@h:p» (ssh -G entiende URIs) o «-oX=y» cambiarían la
    //   resolución.
    // - known_hosts y el limitador no cambian: se usan con el HostName y el Port resueltos (los de la conexión real).
    //   StrictHostKeyChecking, UserKnownHostsFile y HostKeyAlias de la configuración se ignoran: siempre known_hosts estricto
    //   en NATIVE_SSH_DIRECTORIO_SSH, buscado por el HostName real.
    // - ProxyJump y ProxyCommand no se admiten: un destino que los usa se rechaza (conectar directamente al HostName se
    //   saltaría el salto que pide la configuración y podría llegar a otra máquina).
    // - IdentityFile: NATIVE_SSH_IDENTIDADES, si está definida, manda (se ignoran los IdentityFile). Si no, los IdentityFile
    //   de la configuración sustituyen a las claves por defecto; si la configuración no pone ninguno, ssh -G lista sus
    //   claves por defecto (~/.ssh/id_*), que se reconocen comparándolas con las de 'ssh -G -F none' (una vez) y se dejan
    //   en las de native_ssh (las de NATIVE_SSH_DIRECTORIO_SSH). ssh -G no expande las rutas: aquí se expanden '~' inicial,
    //   %d, %u, %h, %r, %p, %n, %% y ${VAR}; cualquier otro token, '~usuario', una variable sin definir o una ruta relativa
    //   (relativa al directorio de trabajo de native_ssh, que no es el del usuario) es un error del destino, como en ssh. En
    //   Windows, si el ssh que la lee es el de MSYS (Git for Windows), una ruta de unidad '/c/…' se lee como 'C:\…'.
    // - Plazo (TimeoutConfiguracionSsh, 5 s): un 'Match exec' o una búsqueda DNS de CanonicalizeHostname que se cuelga no
    //   bloquea la llamada: se mata ssh (y lo que lanzó) y el destino da un error que lo dice.
    // - En Linux, ssh se lanza con 'setsid' (si existe) en su propio grupo de procesos: un 'Match exec' que deja algo en
    //   segundo plano con la salida de ssh abierta no retiene la lectura (ssh ya terminó con toda su salida: se mata el
    //   grupo y el destino se resuelve), ni queda huérfano al vencer el plazo. Si lo que deja escribe en la salida de ssh
    //   después de que ssh termine, lo escrito se lee como de ssh; un hostname, user o port repetido (ssh escribe cada uno
    //   una vez) no se toma por bueno: el destino da un error. Una línea añadida de otra opción (identityfile) no se detecta.
    // - Sin el programa ssh: el destino se usa tal cual (lo de antes) y un aviso en el log, una sola vez.
    // - ssh hereda el entorno sin las variables secretas (VariablesSecretas: NATIVE_SSH_PASSWORD, NATIVE_SSH_PASSPHRASE,
    //   NATIVE_SSH_TOKEN_HTTP; un Match exec las vería), y ${VAR} en un IdentityFile las trata como no definidas.
    // - La entrada de ssh es una tubería que se cierra nada más arrancar (fin de fichero, como /dev/null o NUL): nunca la
    //   heredada, que es el canal MCP de native_ssh en modo stdio. Process de .NET no permite darle NUL sin redirigirla.
    // - Lo que cambia por sistema va en ResolutorConfiguracionSsh.<Sistema>.cs, compilado solo para el suyo (NativeSshSistema
    //   en Directory.Build.props): solo Windows tiene (el registro de ssh a un fichero con -E, y -T; ssh buscado solo en
    //   PATH, nunca en el directorio actual).
    public sealed partial class ResolutorConfiguracionSsh(OpcionesNativeSsh opciones, ILogger<ResolutorConfiguracionSsh> logger) : IResolutorDestino
    {
        private const int ErrorNoExiste = 2; // ENOENT
        private const int MaxCaracteresError = 400;
        private static readonly TimeSpan EsperaLectura = TimeSpan.FromMilliseconds(500);
        private static readonly Lazy<string?> ProgramaSetsid = new(() => OperatingSystem.IsLinux() ? BuscarPrograma("setsid") : null);

        private readonly Lock _candado = new();
        private string[]? _identidadesPorDefecto;
        private volatile bool _isSinPrograma;

        // Repetida: una opción de valor único (hostname, user, port) que salió más de una vez (ssh -G las escribe una sola vez).
        private sealed record ConfiguracionLeida(string? Servidor, string? Usuario, string? Puerto, string[] Identidades, string? Salto, string? Repetida);

        public DestinoSsh Resolver(DestinoSsh destino, bool isUsuarioExplicito, bool isPuertoExplicito)
        {
            if (opciones.ConfiguracionSsh is not { } configuracion || _isSinPrograma)
                return destino;

            string servidor = destino.Servidor;
            if (!IsTextoValido(servidor, isServidor: true) || (isUsuarioExplicito && !IsTextoValido(destino.Usuario, isServidor: false)))
                throw new NativeSshException($"Destino '{(isUsuarioExplicito ? $"{destino.Usuario}@" : "")}{servidor}' no válido para leerlo con la configuración de ssh: el servidor solo admite letras, números, '.', '_', '-' y ':' (sin empezar por '-'), y el usuario lo mismo salvo ':'.");

            if (!TryLeerConfiguracion(configuracion, isUsuarioExplicito ? destino.Usuario : null, servidor, isPuertoExplicito ? destino.Puerto : null, out ConfiguracionLeida? leida))
                return destino;

            if (leida.Repetida is not null)
                throw new NativeSshException($"'ssh -G' escribió '{leida.Repetida}' más de una vez al leer la configuración de ssh de '{servidor}' (ssh escribe cada opción una sola vez: ¿un Match exec que deja en segundo plano algo que escribe en su salida?): no se ha conectado. Revisa la configuración de ssh o usa NATIVE_SSH_CONFIG_SSH=none para no usarla.");
            if (leida.Salto is not null)
                throw new NativeSshException($"La configuración de ssh de '{servidor}' usa {leida.Salto}, que native_ssh no admite: no se conecta directamente para no saltarse el salto que pide. Usa una entrada de la configuración sin ProxyJump ni ProxyCommand, o el destino real si se llega a él sin salto.");

            bool isPuertoValido = int.TryParse(leida.Puerto, NumberStyles.None, CultureInfo.InvariantCulture, out int puerto) && puerto is > 0 and <= 65535;
            if (leida.Servidor is not { Length: > 0 } real || leida.Usuario is not { Length: > 0 } usuario || !isPuertoValido)
                throw new NativeSshException($"'ssh -G' no devolvió el servidor, el usuario y el puerto de '{servidor}' (hostname '{leida.Servidor}', user '{leida.Usuario}', port '{leida.Puerto}'). Revisa la configuración de ssh o usa NATIVE_SSH_CONFIG_SSH=none para no usarla.");

            string[]? identidades = null;
            if (!opciones.IsRutasIdentidadExplicitas && !IsListaPorDefecto(leida.Identidades, servidor))
                identidades = Expandir(leida.Identidades, servidor, real, usuario, puerto, IsSshDeMsys());

            string? alias = string.Equals(servidor, real, StringComparison.OrdinalIgnoreCase) ? null : servidor;
            DestinoSsh resuelto = DestinoSsh.Resuelto(usuario, real, puerto, identidades, alias);
            logger.DestinoResuelto(servidor, resuelto.Clave, identidades is null ? "claves de native_ssh" : string.Join(", ", identidades));
            return resuelto;
        }

        // Para NATIVE_SSH_SOLO_LECTURA: el HostName al que lleva la entrada (como ssh, con su usuario y su puerto si los lleva),
        // o null si la configuración no la cambia, no se usa configuración o no está el programa ssh. Una entrada que no se
        // puede pasar a ssh (caracteres que un destino tampoco admite con la configuración activa) no es ningún alias: null.
        // ProxyJump y ProxyCommand no importan aquí (es la máquina a la que lleva el alias aunque no se conecte a ella).
        // Lanza NativeSshException si ssh -G falla o no da el HostName.
        public string? HostNameDeEntrada(string? usuario, string servidor, int? puerto)
        {
            bool isTextoValido = IsTextoValido(servidor, isServidor: true) && (usuario is null || IsTextoValido(usuario, isServidor: false));
            return opciones.ConfiguracionSsh is not { } configuracion || _isSinPrograma || !isTextoValido || !TryLeerConfiguracion(configuracion, usuario, servidor, puerto, out ConfiguracionLeida? leida) ? null
                : leida.Repetida is not null || leida.Servidor is not { Length: > 0 } real
                    ? throw new NativeSshException($"'ssh -G' no devolvió un único HostName de la entrada '{servidor}' de NATIVE_SSH_SOLO_LECTURA.")
                    : string.Equals(servidor, real, StringComparison.OrdinalIgnoreCase) ? null : real;
        }

        // ssh -G [-F configuración] [-l usuario] [-p puerto] -- servidor; false sin el programa ssh.
        private bool TryLeerConfiguracion(string configuracion, string? usuario, string servidor, int? puerto, [NotNullWhen(true)] out ConfiguracionLeida? leida)
        {
            leida = null;
            using PooledListRef<string> argumentos = new();
            argumentos.Add("-G");
            if (configuracion.Length > 0)
            {
                argumentos.Add("-F");
                argumentos.Add(configuracion);
            }

            if (usuario is not null)
            {
                argumentos.Add("-l");
                argumentos.Add(usuario);
            }

            if (puerto is { } conPuerto)
            {
                argumentos.Add("-p");
                argumentos.Add(conPuerto.ToString(CultureInfo.InvariantCulture));
            }

            argumentos.Add("--");
            argumentos.Add(servidor);
            if (!TryEjecutar(argumentos.Span, servidor, out string? salida))
                return false;

            leida = Leer(salida);
            return true;
        }

        // Letras y números ASCII, '.', '_', '-' (y ':' en el servidor), sin empezar por '-'.
        private static bool IsTextoValido(string texto, bool isServidor)
        {
            if (texto.Length == 0 || texto[0] == '-')
                return false;

            foreach (char c in texto)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' || (isServidor && c == ':')))
                    return false;

            return true;
        }

        private static ConfiguracionLeida Leer(string salida)
        {
            string? servidor = null;
            string? usuario = null;
            string? puerto = null;
            string? salto = null;
            string? repetida = null;
            using PooledListRef<string> identidades = new();
            foreach (string linea in salida.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int espacio = linea.IndexOf(' ', StringComparison.Ordinal);
                if (espacio <= 0)
                    continue;

                string valor = linea[(espacio + 1)..];
                bool isNinguno = string.Equals(valor, "none", StringComparison.OrdinalIgnoreCase);
                switch (linea[..espacio])
                {
                    case "hostname":
                        repetida ??= servidor is null ? null : "hostname";
                        servidor = valor;
                        break;
                    case "user":
                        repetida ??= usuario is null ? null : "user";
                        usuario = valor;
                        break;
                    case "port":
                        repetida ??= puerto is null ? null : "port";
                        puerto = valor;
                        break;
                    case "identityfile":
                        identidades.Add(valor);
                        break;
                    case "proxyjump" when !isNinguno:
                        salto = "ProxyJump";
                        break;
                    case "proxycommand" when !isNinguno:
                        salto ??= "ProxyCommand";
                        break;
                    default:
                        break;
                }
            }

            return new ConfiguracionLeida(servidor, usuario, puerto, identidades.Span.ToArray(), salto, repetida);
        }

        // Las claves por defecto de ssh (las que lista sin ningún IdentityFile), leídas una vez con 'ssh -G -F none'.
        private bool IsListaPorDefecto(string[] identidades, string servidor)
        {
            string[] porDefecto;
            lock (_candado)
            {
                if (_identidadesPorDefecto is null)
                {
                    if (!TryEjecutar(["-G", "-F", "none", "--", servidor], servidor, out string? salida))
                        return true;

                    _identidadesPorDefecto = Leer(salida).Identidades;
                }

                porDefecto = _identidadesPorDefecto;
            }

            if (identidades.Length != porDefecto.Length)
                return false;

            for (int i = 0; i < identidades.Length; i++)
                if (!string.Equals(identidades[i], porDefecto[i], StringComparison.Ordinal))
                    return false;

            return true;
        }

        // Si el ssh que lee la configuración es de MSYS: sus rutas de unidad ('/c/…') se leen como 'C:\…' (RutaDelSistema).
        private bool IsSshDeMsys()
        {
            bool isDeMsys = false;
            SshDeMsysDelSistema(opciones.BinarioSsh, ref isDeMsys);
            return isDeMsys;
        }

        // Sin repetidas, en su orden. Se guardan en el destino (DestinoSsh.Identidades): array del tamaño justo.
        private static string[] Expandir(string[] identidades, string alias, string servidor, string usuario, int puerto, bool isSshDeMsys)
        {
            string casa = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            using PooledListRef<string> rutas = new();
            foreach (string identidad in identidades)
            {
                if (!TryExpandir(identidad, casa, alias, servidor, usuario, puerto, isSshDeMsys, out string? ruta, out string? motivo))
                    throw new NativeSshException($"El IdentityFile '{identidad}' de la configuración de ssh de '{alias}' {motivo}. Corrígelo, o define NATIVE_SSH_IDENTIDADES (manda sobre los IdentityFile).");

                if (rutas.IndexOf(ruta) < 0)
                    rutas.Add(ruta);
            }

            return rutas.Span.ToArray();
        }

        internal static bool TryExpandir(string identidad, string casa, string alias, string servidor, string usuario, int puerto, bool isSshDeMsys, [NotNullWhen(true)] out string? ruta, [NotNullWhen(false)] out string? motivo)
        {
            ruta = null;
            StringBuilder texto = new(identidad.Length + casa.Length);
            int i = 0;
            bool isConTilde = identidad.StartsWith('~');
            if (isConTilde && identidad.Length > 1 && identidad[1] != '/')
            {
                motivo = "usa '~usuario', que native_ssh no expande (solo '~' para tu directorio personal)";
                return false;
            }

            if (isConTilde)
            {
                _ = texto.Append(casa);
                i = 1;
            }

            for (; i < identidad.Length; i++)
            {
                char c = identidad[i];
                bool isVariable = c == '$' && i + 1 < identidad.Length && identidad[i + 1] == '{';
                if (c != '%' && !isVariable)
                {
                    _ = texto.Append(c);
                    continue;
                }

                if (isVariable)
                {
                    int cierre = identidad.IndexOf('}', i + 2);
                    string? nombre = cierre < 0 ? null : identidad[(i + 2)..cierre];
                    // Las variables secretas de native_ssh cuentan como no definidas: ssh no las tiene y su valor no debe salir.
                    bool hasNombre = nombre is { Length: > 0 };
                    bool isSecreta = hasNombre && VariablesSecretas.IsSecreta(nombre!);
                    string? valor = hasNombre && !isSecreta ? Environment.GetEnvironmentVariable(nombre!) : null;
                    if (valor is null)
                    {
                        string aclaracion = isSecreta ? " para la configuración de ssh (es un secreto de native_ssh: ssh tampoco la recibe)" : "";
                        motivo = hasNombre ? $"usa la variable de entorno ${{{nombre}}}, que no está definida{aclaracion}" : "tiene un '${' sin cerrar";
                        return false;
                    }

                    _ = texto.Append(valor);
                    i = cierre;
                    continue;
                }

                char token = i + 1 < identidad.Length ? identidad[++i] : '\0';
                string? sustituto = token switch
                {
                    '%' => "%",
                    'd' => casa,
                    'u' => Environment.UserName,
                    'h' => servidor,
                    'r' => usuario,
                    'p' => puerto.ToString(CultureInfo.InvariantCulture),
                    'n' => alias,
                    _ => null
                };
                if (sustituto is null)
                {
                    motivo = token == '\0' ? "termina en un '%' suelto" : $"usa '%{token}', que native_ssh no expande (admite %d, %u, %h, %r, %p, %n, %%, '~' y ${{VARIABLE}})";
                    return false;
                }

                _ = texto.Append(sustituto);
            }

            string expandida = texto.ToString();
            RutaDelSistema(ref expandida, isSshDeMsys);
            if (!Path.IsPathRooted(expandida))
            {
                motivo = $"es una ruta relativa ('{expandida}'): usa una absoluta o que empiece por '~/'";
                return false;
            }

            ruta = expandida;
            motivo = null;
            return true;
        }

        // Lo propio del sistema para el que se compila (sin fichero de ese sistema, nada): las opciones de ssh, delante de las
        // demás, y el fichero adonde ssh escribe sus mensajes en lugar de su salida de errores (registro; null si no hay); los
        // mensajes de ese fichero, detrás de los de la salida de errores; y borrar el fichero.
        static partial void AnadirOpcionesDelSistema(scoped ref PooledListRef<string> argumentos, ref string? registro);

        static partial void LeerRegistroDelSistema(string? registro, ref string errores);

        static partial void BorrarRegistroDelSistema(string? registro);

        // El nombre del programa como lo busca el sistema (en Windows, con .exe si no lleva extensión, como CreateProcess).
        static partial void NombreDelSistema(ref string programa);

        // Una entrada de PATH donde buscar el programa, o null para saltarla.
        static partial void DirectorioDePathDelSistema(ref string? directorio);

        // Si el ssh que lee la configuración es de MSYS (en Windows, el de Git for Windows).
        static partial void SshDeMsysDelSistema(string binario, ref bool isDeMsys);

        // Un IdentityFile ya expandido, como ruta del sistema (en Windows, con el ssh de MSYS, '/c/…' como 'C:\…').
        static partial void RutaDelSistema(ref string ruta, bool isSshDeMsys);

        // false: no existe el programa ssh (desde entonces, los destinos se usan tal cual). Lanza si ssh falla o no termina.
        // Las opciones del sistema van delante de 'argumentos', en una lista rentada con try/finally: AnadirOpcionesDelSistema
        // la recibe por ref, y una variable de un 'using' no se puede pasar así.
        private bool TryEjecutar(ReadOnlySpan<string> argumentos, string servidor, [NotNullWhen(true)] out string? salida)
        {
            string? registro = null;
            PooledListRef<string> completos = new();
            try
            {
                AnadirOpcionesDelSistema(ref completos, ref registro);
                completos.AddRange(argumentos);
                return TryEjecutar(completos.Span, servidor, registro, out salida);
            }
            finally
            {
                completos.Dispose();
                BorrarRegistroDelSistema(registro);
            }
        }

        private bool TryEjecutar(ReadOnlySpan<string> argumentos, string servidor, string? registro, [NotNullWhen(true)] out string? salida)
        {
            salida = null;
            // La ruta se busca aquí: lanzado por setsid, un ssh que no existe no daría ENOENT al arrancar, sino un error de setsid;
            // y en Windows, CreateProcess buscaría un nombre sin ruta también en el directorio actual (BuscarPrograma).
            string? programa = BuscarPrograma(opciones.BinarioSsh);
            if (programa is null)
            {
                MarcarSinPrograma();
                return false;
            }

            string? setsid = ProgramaSetsid.Value;
            bool isGrupoPropio = setsid is not null;
            // ProcessStartInfo pide un IEnumerable<string>: un array de tamaño justo (con setsid, el programa delante).
            string[] argumentosProceso = isGrupoPropio ? [programa, .. argumentos] : argumentos.ToArray();
            ProcessStartInfo inicio = new(setsid ?? programa, argumentosProceso)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string variable in VariablesSecretas.Nombres)
                _ = inicio.Environment.Remove(variable);

            Process? proceso;
            try
            {
                proceso = Process.Start(inicio);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorNoExiste)
            {
                MarcarSinPrograma();
                return false;
            }
            catch (Win32Exception ex)
            {
                throw new NativeSshException($"No se pudo ejecutar '{opciones.BinarioSsh}' para leer la configuración de ssh de '{servidor}': {ex.Message}", ex);
            }

            using Process ejecucion = proceso ?? throw new NativeSshException($"No se pudo ejecutar '{opciones.BinarioSsh}' para leer la configuración de ssh de '{servidor}'.");
            ejecucion.StandardInput.Close();
            Task<string> lectura = Leer(ejecucion.StandardOutput);
            Task<string> errores = Leer(ejecucion.StandardError);
            bool isTerminado = ejecucion.WaitForExit(opciones.TimeoutConfiguracionSsh);
            if (!isTerminado)
                Matar(ejecucion, isGrupoPropio);

            // Un proceso que lanzó ssh (Match exec) puede seguir con la salida abierta: la espera de la lectura tiene plazo, y
            // al vencer se mata su grupo, que cierra la salida (lo que ssh escribió antes de terminar ya está en ella).
            bool isLeido = Task.WaitAll([lectura, errores], EsperaLectura);
            if (!isLeido)
            {
                // Con ssh ya terminado, su pid solo sigue reservado mientras su grupo tenga algún miembro: si ya existe
                // otro proceso con ese pid, el grupo quedó vacío y el número es de otro, al que no se toca.
                Matar(ejecucion, isGrupoPropio && (!isTerminado || !Directory.Exists($"/proc/{ejecucion.Id}")));
                isLeido = Task.WaitAll([lectura, errores], EsperaLectura);
            }

            if (!isTerminado)
                throw new NativeSshException($"'ssh -G' no terminó en {opciones.TimeoutConfiguracionSsh.TotalSeconds:0.#} s al leer la configuración de ssh de '{servidor}' (¿un Match exec o una búsqueda DNS de CanonicalizeHostname que no responde?): no se ha conectado. Revisa la configuración de ssh o usa NATIVE_SSH_CONFIG_SSH=none para no usarla.");
            if (!isLeido)
                throw new NativeSshException($"'ssh -G' terminó al leer la configuración de ssh de '{servidor}', pero algo que lanzó (¿un Match exec que deja un proceso en segundo plano?) mantiene abierta su salida y no se pudo leer entera: no se ha conectado. Revisa la configuración de ssh o usa NATIVE_SSH_CONFIG_SSH=none para no usarla.");

            if (ejecucion.ExitCode != 0)
            {
                string error = errores.Result;
                LeerRegistroDelSistema(registro, ref error);
                error = error.Trim();
                throw new NativeSshException($"'ssh -G' no pudo leer la configuración de ssh de '{servidor}' (código {ejecucion.ExitCode}): {(error.Length > MaxCaracteresError ? error[..MaxCaracteresError] + "…" : error)}");
            }

            salida = lectura.Result;
            return true;
        }

        // Cada salida se lee en un hilo propio, no en el grupo de hilos: quien llama espera bloqueado (WaitForExit y WaitAll),
        // y con ReadToEndAsync la lectura dependía de que el grupo le diera turno. En Windows las tuberías de Process son
        // síncronas y cada lectura es un trabajo del grupo; en una suite cargada, la de un ssh ya terminado no se hacía hasta
        // que el hilo que esperaba (del grupo) dejaba de esperar, y la resolución fallaba con «no se pudo leer entera» aunque
        // nada retuviera la salida. Con el grupo ocupado pasa lo mismo en Linux. Si algo que lanzó ssh retiene la salida más
        // allá de Matar, el hilo sigue bloqueado hasta que la cierre (antes, en Windows, era un hilo del grupo).
        private static Task<string> Leer(StreamReader lector) =>
            Task.Factory.StartNew(lector.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        private void MarcarSinPrograma()
        {
            if (!_isSinPrograma)
                logger.SshNoEncontrado(opciones.BinarioSsh);
            _isSinPrograma = true;
        }

        // Como execvp: una ruta (con un separador de directorios) se usa tal cual; si no, el primer fichero ejecutable con ese
        // nombre en PATH. Lo propio del sistema (en Windows, la extensión .exe y las entradas de PATH que no son absolutas) en
        // NombreDelSistema y DirectorioDePathDelSistema.
        private static string? BuscarPrograma(string programa)
        {
            NombreDelSistema(ref programa);
            if (programa.Contains(Path.DirectorySeparatorChar) || programa.Contains(Path.AltDirectorySeparatorChar))
                return IsEjecutable(programa) ? programa : null;

            foreach (string entrada in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string? directorio = entrada;
                DirectorioDePathDelSistema(ref directorio);
                if (directorio is null)
                    continue;

                string ruta = Path.Combine(directorio, programa);
                if (IsEjecutable(ruta))
                    return ruta;
            }

            return null;
        }

        private static bool IsEjecutable(string ruta)
        {
            const UnixFileMode Ejecucion = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            try
            {
                return File.Exists(ruta) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(ruta) & Ejecucion) != 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // isGrupoPropio: ssh se lanzó con setsid y es el líder de su grupo (su pid es el del grupo): se mata el grupo entero,
        // también lo que ya quedó huérfano al terminar ssh. Después, el árbol (lo que siga colgando de ssh si salió del grupo).
        private static void Matar(Process proceso, bool isGrupoPropio)
        {
            if (isGrupoPropio && OperatingSystem.IsLinux())
                _ = InteropLinux.MatarGrupo(proceso.Id);

            try
            {
                proceso.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Ya había terminado.
            }
        }
    }
}
