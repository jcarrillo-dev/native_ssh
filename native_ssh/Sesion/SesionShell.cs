using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading.Channels;
using NativeSsh.Buffers;
using NativeSsh.Comun;
using NativeSsh.Protocolo;

namespace NativeSsh.Sesion
{
    // Un shell PTY persistente por destino. Bucle async que ejecuta la cola FIFO y un hilo de lectura por canal.
    // Las herramientas de solo lectura usan canales exec sobre la misma conexión TCP, fuera de la cola (EjecutarExecAsync).
    //
    // Conexión compartida: _conexion solo se crea o se sustituye bajo _candadoConexion (ObtenerConexionAsync), tanto
    // desde el bucle como desde los canales exec, así que nunca hay dos TCP a la vez para el destino y ambos pasan por
    // el mismo LimitadorConexiones. Se lee sin candado (volatile) en el camino rápido. Solo se libera una conexión que ya
    // no está conectada (se sustituye) o al cerrar la sesión (DisposeAsync, también bajo el candado); un canal exec que
    // aún la estuviera usando falla con error y no deja nada a medias.
    // El canal del shell queda ligado a la conexión que lo abrió (_conexionDelCanal): al sustituirla se cierra (SSH.NET
    // no siempre lo cierra cuando cae el TCP) y AsegurarShellAsync solo da por bueno un canal de la conexión vigente.
    // Además el adaptador lo cierra en cuanto SSH.NET avisa de la caída (CanalShellSshNet): el comando en curso termina
    // al momento como ShellPerdido y la cola no queda bloqueada hasta su plazo.
    // Tras conectar, SSH.NET no abandona ninguna espera mientras la conexión viva (ConexionSshNet): la apertura del canal
    // del shell se hace fuera del bucle y cada comando la espera solo hasta su propio límite (ObtenerCanalShellAsync).
    // Una escritura al shell que el servidor no acepta (no lee de la conexión y el envío TCP se llena, o la ventana del
    // canal está agotada) no atiende ningún token: solo termina cuando el servidor acepta los datos o cuando se libera la
    // conexión. Liberarla la desbloquea (SSH.NET parcheado en externo/SSH.NET: Session.Dispose no espera más de 1 s al
    // candado de escritura que retiene ese envío y cierra el socket, lo que hace fallar el Send bloqueado), y DisposeAsync
    // lo hace si el bucle no termina al cerrar la sesión. Mientras tanto ssh_input no espera sin límite a ese candado
    // (EnviarEntrada) y el comando en curso avisa de la escritura atascada (SegundosEnEscritura). Si la escritura atascada
    // es la de una entrada, ssh_cancel tampoco espera sin límite: la petición termina como cancelada sin enviar Ctrl+C,
    // diciendo que el proceso sigue (InterrumpirAsync), y la cancelación nunca corre en el hilo de quien la pide
    // (Peticion.SolicitarCancelacion). Ese canal no se vuelve a usar (AbandonarCanal): el candado de escritura, el búfer y
    // la marca de la escritura en curso son de cada canal (ShellAbierto), así que el siguiente comando abre un shell nuevo
    // sin esperar a esa entrada, y el viejo se cierra cuando ella termina.
    // Un canal exec cuenta para el cupo (MaxCanalesExecSimultaneos) hasta que el servidor lo cierra de verdad (o cae la
    // conexión), aunque la operación ya haya fallado por su plazo local, su tope o una cancelación: cerrar un canal espera
    // el CLOSE del servidor (ConexionSshNet), y OpenSSH no lo envía, ni libera la plaza de su MaxSessions, hasta que
    // termina el proceso de la sesión (uno que no escribe vive hasta su plazo remoto, N s y 2 más si ignora TERM).
    public sealed class SesionShell : IAsyncDisposable
    {
        public const int TamanoBloqueLectura = 16 * 1024;
        public const int CodigoCanalCerrado = int.MinValue;

        // Canales exec simultáneos por conexión (más el canal del shell): muy por debajo del MaxSessions 10 de OpenSSH
        // y de SSH.NET. Los que pasen del cupo esperan su turno dentro de su timeout.
        public const int MaxCanalesExecSimultaneos = 4;

        // __NS_CWD viaja en base64 en la línea de inicialización del TTY (máximo 4095 bytes por línea).
        public const int MaxBytesDirectorioTrabajo = 1024;

        // Una escritura al shell que dura más que esto es una que el servidor no acepta (no lee de la conexión o tiene la
        // ventana del canal agotada): el comando en curso lo avisa (ssh_cancel, ssh_output, ssh_input).
        public static readonly TimeSpan UmbralAvisoEscrituraBloqueada = TimeSpan.FromSeconds(2);

        private static readonly TimeSpan MargenLimiteLocalExec = TimeSpan.FromSeconds(3);

        // Base64 del PATH del login: muy por encima de cualquier PATH real.
        private const int MaxBytesCapturaPath = 16 * 1024;

        private static readonly TimeSpan ReenvioInicializacion = TimeSpan.FromSeconds(2);

        // Lo que DisposeAsync espera al bucle antes de liberar la conexión para sacarlo de una escritura que el servidor no
        // acepta (sin ConnectionInfo.Timeout, SSH.NET espera ahí a que responda o caiga la conexión).
        private static readonly TimeSpan EsperaFinBucleAlCerrar = TimeSpan.FromSeconds(2);

        // Lo que quien cierra el canal del shell espera a que el servidor confirme el cierre (lo que hacía SSH.NET con su
        // ChannelCloseTimeout por defecto); después el cierre sigue en segundo plano.
        private static readonly TimeSpan EsperaCierreCanalShell = TimeSpan.FromSeconds(1);

        // Lo que ssh_input espera a que termine la escritura en curso en el shell (otra entrada o el envío de un comando)
        // antes de rechazar la suya, y cada cuánto comprueba su token y el cierre de la sesión mientras tanto.
        private static readonly TimeSpan EsperaMaxCandadoEntrada = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan IntervaloCandadoEntrada = TimeSpan.FromMilliseconds(50);

        private const string DetalleComandoNoEnviado = "el comando no llegó a enviarse.";
        private const string AvisoAperturaShellPendiente = "El servidor aún no ha respondido a la apertura del shell: el siguiente comando esperará a esa misma apertura en lugar de abrir otro canal.";
        private const string DetalleCtrlCEnviado = "se envió Ctrl+C al proceso.";

        private readonly IFabricaConexion _fabrica;
        private readonly LimitadorConexiones _limitador;
        private readonly OpcionesNativeSsh _opciones;
        private readonly ILogger _logger;
        private readonly Channel<Peticion> _cola = Channel.CreateUnbounded<Peticion>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _cierre = new();
        private readonly Lock _candadoProceso = new();
        private readonly EscanerMarcador _escaner = new(TamanoBloqueLectura);
        private readonly Task _bucle;
        private readonly SemaphoreSlim _candadoConexion = new(1, 1);
        private readonly SemaphoreSlim _cupoExec = new(MaxCanalesExecSimultaneos, MaxCanalesExecSimultaneos);

        private ShellAbierto? _canal;
        private IConexionSsh? _conexionDelCanal;
        private BufferSalida? _salidaActual;
        private TaskCompletionSource<int>? _esperaMarcador;
        private bool _isInterrumpiendo;

        // Salida del comando que estaba en curso cuando se cerró el canal del shell: hasta que el bucle lo da por perdido,
        // su entrada se rechaza diciendo por qué.
        private BufferSalida? _salidaConCanalCerrado;

        private volatile IConexionSsh? _conexion;
        private bool _isShellInicializadoAlgunaVez;

        // Apertura del canal del shell aún en curso (la de un comando que venció antes) y su conexión. Solo el bucle (y
        // DisposeAsync cuando el bucle ya terminó).
        private Task<ICanalShell>? _aperturaShell;
        private IConexionSsh? _conexionAperturaShell;

        private volatile Peticion? _peticionActual;
        private volatile bool _isResetSolicitado;
        private int _comandosEnCola;
        private long _comandosEjecutados;
        private int _conexionesAbiertas;
        private int _canalesExecActivos;

        // Canales exec cuya apertura sigue sin confirmar por el servidor (cuentan para el cupo y no tienen plazo conocido).
        private int _aperturasExecSinConfirmar;

        // Canales del shell abandonados (AbandonarCanal) que aún no se han cerrado.
        private int _shellsAbandonados;

        // Canales con una escritura en curso, el vigente o uno anterior (cerrado o abandonado), para AvisoEscrituraBloqueada.
        // Bajo _candadoProceso.
        private readonly List<ShellAbierto> _canalesEscribiendo = [];
        private int _isDispuesto;
        private volatile string? _directorioTrabajo;
        private volatile string? _pathLogin;

        public SesionShell(DestinoSsh destino, IFabricaConexion fabrica, LimitadorConexiones limitador, OpcionesNativeSsh opciones, ILogger logger)
        {
            Destino = destino;
            _fabrica = fabrica;
            _limitador = limitador;
            _opciones = opciones;
            _logger = logger;
            _bucle = Task.Run(BucleComandosAsync);
        }

        // Hasta dónde llegó una lectura cuyo límite local venció: Apertura es el canal pedido sin que el servidor lo haya
        // confirmado (puede que la petición ni siquiera haya salido si la conexión no acepta datos); Canal, confirmado.
        private enum EtapaExecEnum
        {
            Cupo,
            Conexion,
            Apertura,
            Canal
        }

        private enum EscrituraEnum
        {
            Inicializacion,
            ReenvioInicializacion,
            Sincronizacion,
            CapturaPath,
            Comando,
            Interrupcion
        }

        public DestinoSsh Destino { get; }
        public bool IsConectada => _conexion?.IsConectada ?? false;
        public long ComandosEjecutados => Interlocked.Read(ref _comandosEjecutados);
        public int ConexionesAbiertas => Volatile.Read(ref _conexionesAbiertas);
        public int ComandosEnCola => Volatile.Read(ref _comandosEnCola);
        public Peticion? PeticionActual => _peticionActual;
        public int CanalesExecActivos => Volatile.Read(ref _canalesExecActivos);

        // PATH del shell de login, capturado por el shell PTY al (re)inicializarse. Hasta la primera inicialización es
        // null y los canales exec usan el PATH por defecto de sshd (no se abre un PTY solo para averiguarlo).
        public string? PathLogin => _pathLogin;

        // Segundos que lleva la escritura en curso en el shell vigente (null si no hay ninguna). Una escritura que el servidor
        // no acepta solo termina cuando la acepta o cuando se libera la conexión (ssh_disconnect): ver EnviarEntrada. La de un
        // shell abandonado no cuenta (ya no retiene al comando en curso): ver ShellsAbandonados.
        public double? SegundosEnEscritura => Volatile.Read(ref _canal)?.SegundosEnEscritura;

        // Shells abandonados tras una cancelación sin Ctrl+C que siguen abiertos porque el servidor aún no ha aceptado la
        // entrada que tenían dentro: cada uno ocupa una sesión del servidor y una plaza del cupo de canales exec hasta cerrarse.
        public int ShellsAbandonados => Volatile.Read(ref _shellsAbandonados);

        // Fuente de verdad de __NS_CWD (ruta absoluta validada por ssh_set_cwd, o null = $HOME). Se reexporta al
        // (re)inicializar el shell PTY y se pasa explícitamente a cada canal exec.
        public string? DirectorioTrabajo
        {
            get => _directorioTrabajo;
            set => _directorioTrabajo = string.IsNullOrEmpty(value) ? null : value;
        }

        // Un canal cuya conexión ya se perdió no cuenta como shell activo aunque aún no se haya cerrado: ssh_status nunca
        // muestra un shell activo sin conexión (IsConectada se lee antes que este valor).
        public bool IsShellActivo
        {
            get
            {
                lock (_candadoProceso)
                    return _canal is not null && _conexionDelCanal is { IsConectada: true };
            }
        }

        // Cierre en curso o terminado. DisposeAsync lo marca antes de cancelar _cierre: es el único criterio para decidir
        // si un fallo se debe al cierre (un comando en curso termina como Cancelado, nunca como Error).
        private bool IsCerrando => Volatile.Read(ref _isDispuesto) != 0;

        public void Encolar(Peticion peticion)
        {
            _ = Interlocked.Increment(ref _comandosEnCola);
            if (!IsCerrando && _cola.Writer.TryWrite(peticion))
                return;

            _ = Interlocked.Decrement(ref _comandosEnCola);
            _ = peticion.TryFinalizar(EstadoPeticionEnum.Error, -1, $"La sesión con {Destino.Clave} está cerrada; vuelve a lanzar el comando.");
        }

        // Una vez observado el cierre del canal del shell (CerrarCanal) no se escribe nada más en él: el comando que estaba
        // en curso rechaza su entrada con un mensaje propio hasta que el bucle lo da por perdido. La carrera de fondo es de
        // SSH.NET y no se puede cerrar desde aquí: cuando el servidor cierra el canal (exit), su hilo de mensajes contesta
        // con nuestro CLOSE y Channel.SendData comprueba IsOpen fuera de _messagingLock, así que una escritura que ya había
        // pasado esa comprobación puede salir después del CLOSE y OpenSSH corta la conexión ("data packet referred to
        // nonexistent channel"). ShellStream no avisa del EOF del servidor y su evento Closed llega cuando nuestro CLOSE ya
        // salió: lo primero que se puede observar es el propio cierre.
        //
        // La espera al candado de escritura del canal está acotada y atiende el token y el cierre de la sesión: el bucle lo
        // retiene mientras escribe el comando, y esa escritura no termina mientras el servidor no acepte los datos. El canal
        // se elige antes de tomar su candado y se vuelve a comprobar con él tomado: mientras tanto pudo cerrarse o abandonarse
        // (AbandonarCanal), y en un canal que ya no es el vigente no se escribe nada.
        public bool EnviarEntrada(Peticion peticion, ReadOnlySpan<char> texto, bool isEnviarEnter, CancellationToken cancellationToken = default)
        {
            if (peticion.Estado != EstadoPeticionEnum.Ejecutando)
                return false;

            ShellAbierto? canal;
            lock (_candadoProceso)
                canal = _canal;

            if (canal is null)
                return IsCanalCerradoDurante(peticion) ? throw new NativeSshException(DescribirCanalCerradoParaEntrada(peticion)) : false;

            TomarCandadoEscrituraEntrada(canal, peticion, cancellationToken);
            try
            {
                IConexionSsh? conexion;
                bool isCanalCerrado;
                bool isAdmiteEntrada;
                lock (_candadoProceso)
                {
                    // Durante una interrupción (Ctrl+C o el cierre del canal que la sigue) no se admite entrada, como siempre.
                    bool isAdmisible = peticion.Estado == EstadoPeticionEnum.Ejecutando && !_isInterrumpiendo;
                    isCanalCerrado = isAdmisible && ReferenceEquals(_salidaConCanalCerrado, peticion.Salida);
                    isAdmiteEntrada = isAdmisible && ReferenceEquals(_canal, canal) && ReferenceEquals(_salidaActual, peticion.Salida);
                    conexion = _conexionDelCanal;
                }

                if (isCanalCerrado)
                    throw new NativeSshException(DescribirCanalCerradoParaEntrada(peticion));

                if (!isAdmiteEntrada)
                    return false;

                ArrayBufferWriter<byte> escritor = canal.Escritor;
                escritor.ResetWrittenCount();
                Span<byte> destino = escritor.GetSpan(Encoding.UTF8.GetMaxByteCount(texto.Length) + 1);
                int escritos = Encoding.UTF8.GetBytes(texto, destino);
                if (isEnviarEnter)
                    destino[escritos++] = (byte)'\n';
                escritor.Advance(escritos);
                return TryEnviarBajoCandado(canal) ? true : throw new NativeSshException(DescribirEntradaFallida(peticion, conexion));
            }
            finally
            {
                SoltarCandadoEscritura(canal);
            }
        }

        private bool IsCanalCerradoDurante(Peticion peticion)
        {
            lock (_candadoProceso)
                return peticion.Estado == EstadoPeticionEnum.Ejecutando && !_isInterrumpiendo && ReferenceEquals(_salidaConCanalCerrado, peticion.Salida);
        }

        private string DescribirCanalCerradoParaEntrada(Peticion peticion) =>
            $"El canal del shell se cerró (exit, cierre desde el servidor o conexión perdida) mientras se ejecutaba el comando {peticion.IdTexto} en {Destino.Clave}: la entrada no se envió. El comando terminará como shell perdido y el siguiente abrirá un shell nuevo.";

        // La escritura de la entrada falló a mitad o al empezar: no se sabe cuánto llegó (SSH.NET no lo dice). El comando pudo
        // terminar mientras tanto (p. ej. como shell perdido, o cancelado sin Ctrl+C): entonces se dice cómo terminó.
        private string DescribirEntradaFallida(Peticion peticion, IConexionSsh? conexion)
        {
            string parcial = "puede que parte de la entrada llegara al proceso y el resto no";
            string? terminado = DescribirFinal(peticion);
            return IsCerrando
                ? $"La sesión con {Destino.Clave} se cerró (ssh_disconnect) mientras se enviaba la entrada al comando {peticion.IdTexto}: {parcial}. {terminado ?? "El comando termina como cancelado"} y la siguiente operación abrirá una conexión nueva."
                : conexion is { IsConectada: true }
                ? $"El canal del shell de {Destino.Clave} dejó de admitir escrituras mientras se enviaba la entrada al comando {peticion.IdTexto}: {parcial}. {terminado ?? "El comando terminará como shell perdido"} y el siguiente abrirá un shell nuevo."
                : $"Se perdió la conexión SSH con {Destino.Clave} mientras se enviaba la entrada al comando {peticion.IdTexto}: {parcial}. {terminado ?? "El comando terminará como shell perdido"} y el siguiente reconectará y abrirá un shell nuevo.";
        }

        private static string? DescribirFinal(Peticion peticion) => peticion.Estado switch
        {
            EstadoPeticionEnum.Completado => "El comando ya había terminado (completado)",
            EstadoPeticionEnum.Cancelado => "El comando ya había terminado como cancelado",
            EstadoPeticionEnum.TiempoAgotado => "El comando ya había terminado por tiempo agotado",
            EstadoPeticionEnum.ShellPerdido => "El comando ya había terminado como shell perdido",
            EstadoPeticionEnum.Error => "El comando ya había terminado con error",
            EstadoPeticionEnum.Encolado or EstadoPeticionEnum.Ejecutando => null,
            _ => null
        };

        // Espera acotada al candado de escritura del canal para quien no es el bucle de su escritura (la interrupción de
        // ssh_cancel): false si vence o si la sesión se está cerrando. Como en la entrada, quien lo retiene puede estar en un
        // envío que el servidor no acepta y que no termina hasta que lo acepte o se libere la conexión.
        private bool TryTomarCandadoEscritura(ShellAbierto canal)
        {
            long inicio = Stopwatch.GetTimestamp();
            while (!canal.CandadoEscritura.TryEnter(IntervaloCandadoEntrada))
                if (IsCerrando || Stopwatch.GetElapsedTime(inicio) >= EsperaMaxCandadoEntrada)
                    return false;

            return true;
        }

        private void TomarCandadoEscrituraEntrada(ShellAbierto canal, Peticion peticion, CancellationToken cancellationToken)
        {
            long inicio = Stopwatch.GetTimestamp();
            while (!canal.CandadoEscritura.TryEnter(IntervaloCandadoEntrada))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsCerrando)
                    throw new NativeSshException($"La sesión con {Destino.Clave} se cerró (ssh_disconnect) mientras la entrada para el comando {peticion.IdTexto} esperaba turno: la entrada no se envió.");

                if (Stopwatch.GetElapsedTime(inicio) >= EsperaMaxCandadoEntrada)
                    throw new NativeSshException($"La entrada no se envió al comando {peticion.IdTexto}: el shell de {Destino.Clave} sigue enviando datos anteriores{DescribirDuracionEscritura(canal)} que el servidor no acepta (no lee de la conexión, la red no responde o el canal tiene agotada su ventana). Reintenta en unos segundos; si el servidor no se recupera, ssh_disconnect cierra la conexión y lo libera (la siguiente operación abrirá una conexión nueva).");
            }
        }

        // Toda escritura en un canal suelta su candado con esto: si el canal se abandonó mientras ella estaba dentro, el último
        // que lo suelta es quien lo cierra (CerrarSiAbandonado).
        private void SoltarCandadoEscritura(ShellAbierto canal)
        {
            canal.CandadoEscritura.Exit();
            CerrarSiAbandonado(canal);
        }

        private static string DescribirDuracionEscritura(ShellAbierto canal) => canal.SegundosEnEscritura is double segundos ? $" (lleva {segundos:0} s)" : "";

        // Aviso del comando en curso mientras el bucle está atascado en una escritura al shell (la suya o la de su entrada).
        // Una escritura atascada en un shell anterior no retiene al comando en curso: no se le atribuye.
        private string? DescribirEscrituraBloqueada(Peticion peticion) => ReferenceEquals(_peticionActual, peticion) ? AvisoEscrituraBloqueadaShellVigente : null;

        // Aviso de una escritura al shell que lleva más que UmbralAvisoEscrituraBloqueada sin que el servidor la acepte (null
        // si no la hay): la del shell vigente o, si no, la de una entrada que sigue dentro de un shell anterior (cerrado o
        // abandonado). Pensado para ssh_status.
        public string? AvisoEscrituraBloqueada => AvisoEscrituraBloqueadaShellVigente ?? AvisoEscrituraBloqueadaShellAnterior;

        private string? AvisoEscrituraBloqueadaShellAnterior
        {
            get
            {
                double maximo = 0;
                lock (_candadoProceso)
                    foreach (ShellAbierto canal in _canalesEscribiendo)
                        if (!ReferenceEquals(canal, _canal) && canal.SegundosEnEscritura is double segundos && segundos > maximo)
                            maximo = segundos;

                return maximo >= UmbralAvisoEscrituraBloqueada.TotalSeconds
                    ? $"Una entrada de ssh_input enviada a un shell anterior de {Destino.Clave} (cerrado o abandonado) lleva {maximo:0} s sin que el servidor la acepte: esa llamada a ssh_input no vuelve hasta entonces y, si el shell está abandonado, sigue ocupando una sesión del servidor. Los comandos nuevos usan otro shell (si el servidor no lee de la conexión, también esperarán). ssh_disconnect cierra la conexión y la libera (la siguiente operación abrirá una conexión nueva)."
                    : null;
            }
        }

        // Aviso de una escritura atascada en el shell vigente: lo muestra el comando en curso (ssh_cancel, ssh_output, ssh_input).
        private string? AvisoEscrituraBloqueadaShellVigente =>
            SegundosEnEscritura is double segundos && segundos >= UmbralAvisoEscrituraBloqueada.TotalSeconds
                ? $"El shell de {Destino.Clave} lleva {segundos:0} s intentando enviar datos que el servidor no acepta (no lee de la conexión, la red no responde o el canal tiene agotada su ventana): mientras tanto ssh_cancel no puede interrumpir el comando (si lo atascado es una entrada de ssh_input, lo da por cancelado sin interrumpir el proceso y abandona ese shell) y ssh_input no envía nada. ssh_disconnect cierra la conexión y lo libera (el comando termina como cancelado y la siguiente operación abrirá una conexión nueva)."
                : null;

        public void SolicitarReset()
        {
            _isResetSolicitado = true;
            _ = (_peticionActual?.SolicitarCancelacion());
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDispuesto, 1) != 0)
                return;

            // Desde aquí cualquier fallo de un comando o de una lectura se atribuye al cierre (IsCerrando), aunque _cierre
            // aún no esté cancelado.
            _logger.CerrandoSesion(Destino.Clave);
            _ = _cola.Writer.TryComplete();
            _cierre.Cancel();

            // Una escritura en el shell que el servidor no acepta (envío TCP lleno, ventana agotada, renegociación de claves
            // sin respuesta) no atiende ningún token: si el bucle sigue dentro, liberar la conexión lo saca (la escritura
            // falla al cerrarse el socket o al terminar la sesión de SSH.NET, y el comando termina como Cancelado).
            if (await Task.WhenAny(_bucle, Task.Delay(EsperaFinBucleAlCerrar)).ConfigureAwait(false) != _bucle)
            {
                _logger.BucleBloqueadoAlCerrar(Destino.Clave);
                await LiberarConexionAsync().ConfigureAwait(false);
            }

            try
            {
                await _bucle.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.BucleTerminadoConError(ex, Destino.Clave);
            }

            CerrarCanalActual();
            DescartarAperturaShell();
            await LiberarConexionAsync().ConfigureAwait(false);
        }

        // Un canal exec que estuviera conectando ya vio la sesión cerrada; tras el candado nadie crea otra conexión.
        private async Task LiberarConexionAsync()
        {
            await _candadoConexion.WaitAsync().ConfigureAwait(false);
            try
            {
                _conexion?.Dispose();
                _conexion = null;
            }
            finally
            {
                _ = _candadoConexion.Release();
            }
        }

        // Canal exec sobre la conexión de la sesión, sin pasar por la cola del shell. 'timeout' cubre la espera de cupo,
        // la conexión (si no la hay) y la ejecución; al agotarse se cierra el canal y se lanza NativeSshException.
        // La operación termina al agotarse el límite local aunque la apertura del canal siga sin respuesta del servidor o
        // su proceso remoto siga vivo: el canal conserva su cupo hasta que el servidor lo cierra de verdad (como mucho al
        // vencer su plazo remoto), para no superar el MaxSessions del servidor con canales que ya nadie usa. Las lecturas
        // que esperan ese cupo fallan al vencer su propio límite y lo dicen.
        public async Task<ResultadoCanalExec> EjecutarExecAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (IsCerrando)
                throw new NativeSshException($"La sesión con {Destino.Clave} está cerrada; vuelve a lanzar la operación.");

            // El script se acota en el servidor con 'timeout'; el límite local, algo mayor, solo actúa si el servidor
            // no responde (o no tiene 'timeout') y cubre la espera de cupo y la conexión.
            string comando = ConstructorComandoExec.Construir(script, _directorioTrabajo, timeout, _pathLogin);
            using CancellationTokenSource limite = new(timeout + MargenLimiteLocalExec);
            using CancellationTokenSource enlazado = CancellationTokenSource.CreateLinkedTokenSource(limite.Token, cancellationToken, _cierre.Token);
            CancellationToken token = enlazado.Token;
            bool isCupoTomado = false;
            bool isCanalContado = false;
            EtapaExecEnum etapa = EtapaExecEnum.Cupo;
            Task<ResultadoCanalExec>? ejecucion = null;
            AperturaExec? apertura = null;
            try
            {
                await _cupoExec.WaitAsync(token).ConfigureAwait(false);
                isCupoTomado = true;
                etapa = EtapaExecEnum.Conexion;
                IConexionSsh conexion = await ObtenerConexionAsync(token).ConfigureAwait(false);
                _ = Interlocked.Increment(ref _canalesExecActivos);
                isCanalContado = true;
                etapa = EtapaExecEnum.Apertura;
                apertura = new AperturaExec(this);
                ejecucion = conexion.EjecutarExecAsync(comando, _opciones.MaxBytesSalidaPorPeticion, apertura.Confirmar, token);
                return await ejecucion.WaitAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsCerrando && !cancellationToken.IsCancellationRequested)
            {
                // Con la sesión cerrándose, el fallo del canal (cancelación, objeto liberado, conexión cerrada) es
                // consecuencia del cierre: no se muestra el error crudo de SSH.NET.
                throw new NativeSshException($"La sesión con {Destino.Clave} se cerró (ssh_disconnect) durante la operación; vuelve a lanzarla.", ex);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new NativeSshException(DescribirTiempoAgotadoExec(etapa == EtapaExecEnum.Apertura && apertura is { IsConfirmada: true } ? EtapaExecEnum.Canal : etapa, timeout));
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or NativeSshException))
            {
                throw new NativeSshException($"Falló el canal exec en {Destino.Clave}: {ex.Message}", ex);
            }
            finally
            {
                if (ejecucion is { IsCompleted: false })
                    _ = LiberarCanalExecAlCerrarseAsync(ejecucion, apertura);
                else
                {
                    apertura?.Terminar();
                    LiberarCanalExec(isCupoTomado, isCanalContado);
                }
            }
        }

        private string DescribirTiempoAgotadoExec(EtapaExecEnum etapa, TimeSpan timeout)
        {
            string plazo = $"La operación no terminó en {timeout.TotalSeconds:0.#} s (tiempo agotado)";
            return etapa switch
            {
                EtapaExecEnum.Cupo => Volatile.Read(ref _aperturasExecSinConfirmar) is var sinConfirmar and > 0
                    ? $"{plazo} sin llegar a obtener canal: los {MaxCanalesExecSimultaneos} canales exec de las lecturas en {Destino.Clave} siguen ocupados, y {sinConfirmar} de ellos con la apertura aún sin confirmar por el servidor (puede que la conexión no acepte datos): esos no se liberan hasta que el servidor responda o caiga la conexión, sin plazo conocido.{DescribirShellsAbandonadosEnElCupo()} Si el servidor no se recupera, ssh_disconnect cierra la conexión y los libera (la siguiente operación abrirá una conexión nueva)."
                    : $"{plazo} sin llegar a obtener canal: los {MaxCanalesExecSimultaneos} canales exec de las lecturas en {Destino.Clave} siguen ocupados, por lecturas en curso o por otras ya abandonadas cuyo proceso remoto aún no ha terminado (el servidor mantiene su sesión hasta entonces, como mucho hasta el plazo de cada una).{DescribirShellsAbandonadosEnElCupo()} Reintenta en unos segundos.",
                EtapaExecEnum.Conexion => $"{plazo} sin llegar a obtener canal: la conexión con {Destino.Clave} no se estableció a tiempo.",
                EtapaExecEnum.Apertura => $"{plazo} sin que el servidor confirmara la apertura de su canal exec (puede que la petición ni siquiera llegara a salir, si la conexión con {Destino.Clave} no acepta datos): no se sabe si el comando llegó a arrancar. Su canal cuenta para el límite de lecturas simultáneas hasta que la apertura termine: si el servidor la confirma más tarde, el canal se cierra entonces y lo que llegara a arrancar queda acotado por su plazo remoto.",
                EtapaExecEnum.Canal => $"{plazo}; se abandona y su canal exec se cierra (el servidor lo libera cuando termina su proceso remoto, como mucho al vencer su plazo).",
                _ => throw new ArgumentOutOfRangeException(nameof(etapa), etapa, null)
            };
        }

        // Un shell abandonado (AbandonarCanal) retiene una plaza del cupo hasta cerrarse, sin plazo conocido.
        private string DescribirShellsAbandonadosEnElCupo() => ShellsAbandonados is var abandonados and > 0
            ? $" Además, {abandonados} plaza(s) las retiene un shell abandonado tras una cancelación sin Ctrl+C, que sigue ocupando su sesión en el servidor hasta que este acepte la entrada que tenía pendiente y se cierre (ssh_disconnect lo libera)."
            : "";

        // La tarea de IConexionSsh.EjecutarExecAsync termina cuando el canal está cerrado (si su apertura seguía en curso,
        // cuando termina y se cierra): hasta entonces sigue contando como canal abierto en la conexión.
        private async Task LiberarCanalExecAlCerrarseAsync(Task<ResultadoCanalExec> ejecucion, AperturaExec? apertura)
        {
            try
            {
                _ = await ejecucion.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.CanalExecCerradoTrasAbandonarlo(ex, Destino.Clave);
            }
            finally
            {
                apertura?.Terminar();
                LiberarCanalExec(isCupoTomado: true, isCanalContado: true);
            }
        }

        private void LiberarCanalExec(bool isCupoTomado, bool isCanalContado)
        {
            if (isCanalContado)
                _ = Interlocked.Decrement(ref _canalesExecActivos);
            if (isCupoTomado)
                _ = _cupoExec.Release();
        }

        private async Task BucleComandosAsync()
        {
            try
            {
                await foreach (Peticion? peticion in _cola.Reader.ReadAllAsync(_cierre.Token).ConfigureAwait(false))
                {
                    _ = Interlocked.Decrement(ref _comandosEnCola);
                    if (!peticion.TryMarcarEjecutando())
                        continue;

                    _peticionActual = peticion;
                    peticion.FijarAvisoEnCurso(() => DescribirEscrituraBloqueada(peticion));
                    try
                    {
                        await EjecutarAsync(peticion).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsCerrando)
                    {
                        _logger.ComandoInterrumpidoPorCierre(ex, Destino.Clave);
                        _ = peticion.TryFinalizar(EstadoPeticionEnum.Cancelado, 130, "La sesión se cerró durante la ejecución.");
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorInesperado(ex, Destino.Clave);
                        _ = peticion.TryFinalizar(EstadoPeticionEnum.Error, -1, $"Error interno: {ex.Message}");
                    }
                    finally
                    {
                        lock (_candadoProceso)
                        {
                            _isInterrumpiendo = false;
                            if (ReferenceEquals(_salidaActual, peticion.Salida))
                                _salidaActual = null;
                        }

                        _peticionActual = null;
                    }
                }
            }
            catch (OperationCanceledException) when (_cierre.IsCancellationRequested)
            {
                // Cierre ordenado de la sesión.
            }
            finally
            {
                while (_cola.Reader.TryRead(out Peticion? pendiente))
                    _ = pendiente.TryFinalizar(EstadoPeticionEnum.Cancelado, 130, "La sesión se cerró antes de ejecutar el comando.");
            }
        }

        private async Task EjecutarAsync(Peticion peticion)
        {
            using CancellationTokenSource limite = new(peticion.Timeout);
            using CancellationTokenSource enlazado = CancellationTokenSource.CreateLinkedTokenSource(limite.Token, peticion.TokenInterrupcion, _cierre.Token);
            CancellationToken token = enlazado.Token;

            string? avisoShell;
            try
            {
                avisoShell = await AsegurarShellAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!IsCerrando)
            {
                FinalizarInterrumpida(peticion, limite.IsCancellationRequested, DetalleComandoNoEnviado, _aperturaShell is { IsCompleted: false } ? AvisoAperturaShellPendiente : null);
                return;
            }
            catch (NativeSshException ex) when (!IsCerrando)
            {
                _ = peticion.TryFinalizar(EstadoPeticionEnum.Error, -1, ex.Message);
                return;
            }

            if (token.IsCancellationRequested)
            {
                if (IsCerrando)
                    throw new OperationCanceledException(token);
                FinalizarInterrumpida(peticion, limite.IsCancellationRequested, DetalleComandoNoEnviado, avisoShell);
                return;
            }

            TaskCompletionSource<int> espera = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ShellAbierto? canal;
            IConexionSsh? conexionDelCanal;
            lock (_candadoProceso)
            {
                canal = _canal;
                conexionDelCanal = _conexionDelCanal;
                _salidaConCanalCerrado = null;
            }

            bool isEnviado = canal is not null && TryEnviarComando(canal, peticion, espera);
            if (!isEnviado || canal is null)
            {
                if (canal is not null)
                    CerrarCanal(canal);

                // DisposeAsync liberó la conexión para sacar al bucle de la escritura.
                if (IsCerrando)
                    throw new OperationCanceledException(token);

                string motivo = conexionDelCanal is { IsConectada: true }
                    ? "No se pudo enviar el comando: el canal del shell se cerró. El siguiente comando abrirá uno nuevo."
                    : $"No se pudo enviar el comando: se perdió la conexión SSH con {Destino.Clave}. El siguiente comando reconectará y abrirá un shell nuevo.";
                _ = peticion.TryFinalizar(EstadoPeticionEnum.ShellPerdido, -1, Unir(avisoShell, motivo));
                return;
            }

            _ = Interlocked.Increment(ref _comandosEjecutados);
            int codigo;
            try
            {
                codigo = await espera.Task.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!IsCerrando)
            {
                if (!espera.Task.IsCompletedSuccessfully)
                {
                    (string detalle, string? avisoInterrupcion) = await InterrumpirAsync(canal, peticion).ConfigureAwait(false);
                    if (IsCerrando)
                        throw new OperationCanceledException(token);
                    FinalizarInterrumpida(peticion, limite.IsCancellationRequested, detalle, Unir(avisoShell, avisoInterrupcion));
                    return;
                }

                codigo = espera.Task.Result;
            }

            _ = codigo == CodigoCanalCerrado
                ? peticion.TryFinalizar(EstadoPeticionEnum.ShellPerdido, -1, Unir(avisoShell, DescribirShellPerdido(conexionDelCanal)))
                : peticion.TryFinalizar(EstadoPeticionEnum.Completado, codigo, avisoShell);
        }

        // Con el candado de escritura del canal tomado, y solo si sigue siendo el vigente: false si no lo es o si la escritura
        // falla.
        private bool TryEnviarComando(ShellAbierto canal, Peticion peticion, TaskCompletionSource<int> espera)
        {
            canal.CandadoEscritura.Enter();
            try
            {
                lock (_candadoProceso)
                {
                    if (!ReferenceEquals(_canal, canal))
                        return false;

                    _escaner.Reiniciar(peticion.Id);
                    _salidaActual = peticion.Salida;
                    _esperaMarcador = espera;
                }

                Componer(canal, EscrituraEnum.Comando, peticion.Id, peticion.Comando);
                return TryEnviarBajoCandado(canal);
            }
            finally
            {
                SoltarCandadoEscritura(canal);
            }
        }

        // Con la conexión perdida (el adaptador la marca así antes de cerrar el canal) se dice explícitamente. No se afirma
        // que el comando llegara a ejecutarse: el canal pudo cerrarse antes de que el shell lo leyera.
        private string DescribirShellPerdido(IConexionSsh? conexion) => conexion is { IsConectada: true }
            ? "El canal del shell se cerró antes de que terminara el comando (exit o cierre desde el servidor), así que puede que no llegara a ejecutarse o que se interrumpiera a medias. El siguiente comando abrirá un shell nuevo y las variables exportadas se perderán."
            : $"Se perdió la conexión SSH con {Destino.Clave} antes de que terminara el comando, así que puede que no llegara a ejecutarse o que se interrumpiera a medias: el shell remoto se cerró y las variables exportadas se perdieron. El siguiente comando reconectará y abrirá un shell nuevo.";

        // Devuelve qué se hizo con el proceso (para el estado final) y el aviso sobre el shell. Solo afirma que se envió
        // Ctrl+C, que no respondió o que la conexión sigue abierta cuando es cierto.
        private async Task<(string Detalle, string? Aviso)> InterrumpirAsync(ShellAbierto canal, Peticion peticion)
        {
            TaskCompletionSource<int> recuperacion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Guid idRecuperacion = Guid.NewGuid();
            IConexionSsh? conexion;
            bool isEnviado;

            // La escritura en curso puede ser la de una entrada atascada (ssh_input con el envío TCP lleno o la ventana del
            // canal agotada): no se espera sin límite. Sin el candado no se escribe Ctrl+C ni se cierra el canal, porque
            // cerrarlo con esa escritura dentro de SSH.NET podría sacar datos tras el CLOSE y OpenSSH cortaría la conexión: se
            // abandona (el siguiente comando no puede escribir en la entrada del proceso que sigue vivo).
            if (!TryTomarCandadoEscritura(canal))
            {
                _logger.InterrupcionSinCandadoEscritura(Destino.Clave);
                string duracion = DescribirDuracionEscritura(canal);
                if (!AbandonarCanal(canal))
                    return ("no se envió Ctrl+C porque el canal del shell ya estaba cerrado.", "El siguiente comando abrirá un shell nuevo.");

                return ($"no se envió Ctrl+C: el shell sigue enviando datos{duracion} que el servidor no acepta (no lee de la conexión, la red no responde o el canal tiene agotada su ventana), así que el proceso sigue en ejecución en el servidor.",
                    "Ese shell queda abandonado: no se le enviará nada más (ni comandos ni otra entrada), aunque la entrada pendiente aún puede llegar al proceso, y se cerrará en cuanto el servidor acepte esos datos (al cerrarse su terminal, el proceso normalmente termina por SIGHUP); hasta entonces ocupa una sesión en el servidor y una de las plazas de las lecturas. El siguiente comando abrirá un shell nuevo sobre la misma conexión: las variables exportadas y el resto del estado del shell (funciones, alias, opciones) se perderán; el directorio fijado con ssh_set_cwd se conserva. Si el servidor no lee de la conexión, el shell nuevo tampoco podrá abrirse hasta que lea. ssh_disconnect cierra la conexión y lo libera todo (la siguiente operación abrirá una conexión nueva).");
            }

            try
            {
                bool isCanalVigente;
                lock (_candadoProceso)
                {
                    isCanalVigente = ReferenceEquals(_canal, canal);
                    conexion = _conexionDelCanal;
                    if (isCanalVigente)
                    {
                        _isInterrumpiendo = true;
                        _escaner.Reiniciar(idRecuperacion, peticion.Id);
                        _salidaActual = peticion.Salida;
                        _esperaMarcador = recuperacion;
                    }
                }

                if (!isCanalVigente)
                    return ("no se envió Ctrl+C porque el canal del shell ya estaba cerrado.", "El siguiente comando abrirá un shell nuevo.");

                Componer(canal, EscrituraEnum.Interrupcion, idRecuperacion, null);
                isEnviado = TryEnviarBajoCandado(canal);
            }
            finally
            {
                SoltarCandadoEscritura(canal);
            }

            if (isEnviado)
            {
                try
                {
                    int codigo = await recuperacion.Task.WaitAsync(_opciones.EsperaTrasCtrlC, _cierre.Token).ConfigureAwait(false);
                    if (codigo != CodigoCanalCerrado)
                        return (DetalleCtrlCEnviado, null);

                    return (DetalleCtrlCEnviado, conexion is { IsConectada: true }
                        ? "El shell se cerró al interrumpir; el siguiente comando abrirá uno nuevo y las variables exportadas se perderán."
                        : "El shell se cerró al interrumpir porque se perdió la conexión SSH; el siguiente comando reconectará y abrirá uno nuevo, y las variables exportadas se perderán.");
                }
                catch (TimeoutException)
                {
                    // No respondió a Ctrl+C: se escala al cierre del canal.
                }
            }

            bool isCierreConfirmado = TryCerrarCanal(canal);
            bool isConectada = conexion is { IsConectada: true };
            string siguiente = isConectada
                ? "El siguiente comando abrirá un shell nuevo sobre la misma conexión y las variables exportadas se perderán."
                : "El siguiente comando reconectará y abrirá un shell nuevo, y las variables exportadas se perderán.";
            if (!isEnviado)
                return ("no se pudo enviar Ctrl+C al proceso.", isConectada
                    ? $"El canal del shell ya no admitía escrituras: se cerró. {siguiente}"
                    : $"El canal del shell ya no admitía escrituras porque se perdió la conexión SSH: se cerró. {siguiente}");

            // Que la conexión sigue abierta solo se afirma si el servidor confirmó el cierre del canal: congelado (o con un
            // bash que no muere al perder el terminal) no lo confirma, y entonces no se sabe si responde.
            string espera = $"{_opciones.EsperaTrasCtrlC.TotalSeconds:0.#} s";
            if (!isConectada)
                return (DetalleCtrlCEnviado, $"El proceso no respondió a Ctrl+C en {espera} y se perdió la conexión SSH: se cerró el canal del shell. {siguiente}");

            return (DetalleCtrlCEnviado, isCierreConfirmado
                ? $"El proceso no respondió a Ctrl+C en {espera}: se cerró el canal del shell (la conexión TCP sigue abierta). {siguiente}"
                : $"El proceso no respondió a Ctrl+C en {espera}: se cerró el canal del shell, pero el servidor no confirmó el cierre en {EsperaCierreCanalShell.TotalSeconds:0.#} s (puede que no responda, o que el proceso siga vivo sin terminal). {siguiente} Si las operaciones siguientes tampoco responden, ssh_disconnect cierra la conexión.");
        }

        private static void FinalizarInterrumpida(Peticion peticion, bool isTiempoAgotado, string detalle, string? aviso) =>
            _ = peticion.IsCancelacionSolicitada || !isTiempoAgotado
                ? peticion.TryFinalizar(EstadoPeticionEnum.Cancelado, 130, Unir($"Cancelado: {detalle}", aviso))
                : peticion.TryFinalizar(EstadoPeticionEnum.TiempoAgotado, 130, Unir($"Tiempo agotado ({peticion.Timeout.TotalSeconds:0.#} s): {detalle}", aviso));

        private async Task<string?> AsegurarShellAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_isResetSolicitado)
            {
                _isResetSolicitado = false;
                CerrarCanalActual();
                _isShellInicializadoAlgunaVez = false;
            }

            IConexionSsh? conexion = _conexion;
            if (conexion is { IsConectada: true } && IsShellDe(conexion))
                return null;

            CerrarCanalActual();
            IConexionSsh vigente = await ObtenerConexionAsync(token).ConfigureAwait(false);
            return await AbrirShellAsync(vigente, token).ConfigureAwait(false);
        }

        private bool IsShellDe(IConexionSsh conexion)
        {
            lock (_candadoProceso)
                return _canal is not null && ReferenceEquals(_conexionDelCanal, conexion);
        }

        // Única vía para crear o sustituir _conexion (ver el comentario de la clase).
        private async Task<IConexionSsh> ObtenerConexionAsync(CancellationToken token)
        {
            IConexionSsh? actual = _conexion;
            if (actual is { IsConectada: true })
                return actual;

            IConexionSsh? caida = null;
            await _candadoConexion.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (IsCerrando)
                    throw new NativeSshException($"La sesión con {Destino.Clave} está cerrada; vuelve a lanzar la operación.");

                actual = _conexion;
                if (actual is { IsConectada: true })
                    return actual;

                caida = actual;
                _conexion = null;
                IConexionSsh nueva = await ConectarAsync(token).ConfigureAwait(false);
                _conexion = nueva;
                return nueva;
            }
            finally
            {
                _ = _candadoConexion.Release();
                // Fuera del candado: liberar una conexión muerta (y su canal) puede tardar.
                if (caida is not null)
                    DescartarConexion(caida);
            }
        }

        // Una conexión sustituida se lleva su canal de shell: si siguiera en _canal, el siguiente comando se escribiría
        // en un canal muerto (ShellPerdido sin ejecutarse) en lugar de abrir un shell nuevo sobre la conexión vigente.
        // Mientras tanto (ya fuera del candado) _conexion es la nueva y _canal puede ser aún el de la caída:
        // AsegurarShellAsync no lo da por bueno porque no es de la conexión vigente (IsShellDe).
        private void DescartarConexion(IConexionSsh conexion)
        {
            _logger.ConexionDescartada(Destino.Clave);
            ShellAbierto? canal;
            lock (_candadoProceso)
                canal = ReferenceEquals(_conexionDelCanal, conexion) ? _canal : null;

            if (canal is not null)
                CerrarCanal(canal);

            try
            {
                conexion.Dispose();
            }
            catch (Exception ex)
            {
                _logger.ErrorLiberandoConexion(ex, Destino.Clave);
            }
        }

        private async Task<IConexionSsh> ConectarAsync(CancellationToken token)
        {
            if (!_limitador.TryReservar(Destino.ClaveServidor, out TimeSpan espera))
                throw new NativeSshException($"Conexión a {Destino.Clave} retenida localmente (máximo {_opciones.MaxConexionesPorVentana} cada {_opciones.VentanaConexiones.TotalSeconds:0} s, con espera creciente tras fallos) para no disparar el 'ufw limit' del servidor. Reintenta en {Math.Ceiling(espera.TotalSeconds):0} s.");

            using CancellationTokenSource limite = CancellationTokenSource.CreateLinkedTokenSource(token);
            limite.CancelAfter(_opciones.TimeoutConexion);
            try
            {
                IConexionSsh conexion = await _fabrica.ConectarAsync(Destino, limite.Token).ConfigureAwait(false);
                _limitador.RegistrarExito(Destino.ClaveServidor);
                _ = Interlocked.Increment(ref _conexionesAbiertas);
                return conexion;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                _limitador.RegistrarFallo(Destino.ClaveServidor);
                throw new NativeSshException($"Tiempo agotado conectando a {Destino.Clave} ({_opciones.TimeoutConexion.TotalSeconds:0} s).");
            }
            catch (NativeSshException)
            {
                _limitador.RegistrarFallo(Destino.ClaveServidor);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _limitador.RegistrarFallo(Destino.ClaveServidor);
                throw new NativeSshException($"No se pudo conectar a {Destino.Clave}: {ex.Message}", ex);
            }
        }

        private async Task<string?> AbrirShellAsync(IConexionSsh conexion, CancellationToken token)
        {
            ShellAbierto canal;
            try
            {
                canal = new ShellAbierto(await ObtenerCanalShellAsync(conexion, token).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not (NativeSshException or OperationCanceledException))
            {
                // Con la conexión caída, el texto de SSH.NET ("Client not connected.", un handle nulo...) no dice nada útil.
                throw new NativeSshException(conexion.IsConectada
                    ? $"No se pudo abrir un shell en {Destino.Clave}: {ex.Message}"
                    : $"Se perdió la conexión SSH con {Destino.Clave} mientras se abría el shell: el comando no llegó a enviarse. El siguiente comando reconectará y abrirá un shell nuevo.", ex);
            }

            Guid id = Guid.NewGuid();
            TaskCompletionSource<int> espera = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_candadoProceso)
            {
                _canal = canal;
                _conexionDelCanal = conexion;
                _escaner.Reiniciar(id);
                _salidaActual = null;
                _esperaMarcador = espera;
            }

            IniciarBomba(canal);

            using CancellationTokenSource limite = CancellationTokenSource.CreateLinkedTokenSource(token);
            limite.CancelAfter(_opciones.TimeoutInicializacionShell);
            int codigo;
            try
            {
                if (!TryEscribir(canal, EscrituraEnum.Inicializacion, id))
                    throw new NativeSshException($"No se pudo escribir en el shell de {Destino.Clave}.");

                try
                {
                    codigo = await espera.Task.WaitAsync(ReenvioInicializacion, limite.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Si el shell de login consumió la línea antes del exec, se reenvía una vez. Si no la consumió,
                    // pueden ejecutarse las dos: la sincronización posterior deja el shell en un estado conocido.
                    _ = TryEscribir(canal, EscrituraEnum.ReenvioInicializacion, id);
                    codigo = await espera.Task.WaitAsync(limite.Token).ConfigureAwait(false);
                    if (codigo != CodigoCanalCerrado)
                        codigo = await SincronizarAsync(canal, id, limite.Token).ConfigureAwait(false);
                }

                if (codigo != CodigoCanalCerrado)
                    codigo = await CapturarPathAsync(canal, limite.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                CerrarCanal(canal);
                throw new NativeSshException($"El shell de {Destino.Clave} no completó la inicialización en {_opciones.TimeoutInicializacionShell.TotalSeconds:0} s: el login tardó más que ese plazo, el servidor no respondió o al shell le falta bash, stty o base64 (los necesita el shell de ssh_exec).");
            }
            catch
            {
                CerrarCanal(canal);
                throw;
            }

            if (codigo == CodigoCanalCerrado)
                throw new NativeSshException($"El shell de {Destino.Clave} se cerró durante la inicialización.");

            bool isRecreado = _isShellInicializadoAlgunaVez;
            _isShellInicializadoAlgunaVez = true;
            return isRecreado ? "Shell remoto recreado: las variables exportadas en comandos anteriores se han perdido." : null;
        }

        // La apertura de SSH.NET es síncrona, no atiende ningún token y, sin ConnectionInfo.Timeout, espera a que el servidor
        // responda o a que caiga la conexión: se hace fuera del bucle y el comando la espera hasta su propio límite. Si vence
        // antes, la apertura sigue en curso y el siguiente comando la espera (sobre la misma conexión) en lugar de abrir otro
        // canal, que dejaría en el servidor uno sin usar; si ya no es de la conexión vigente, su canal se cierra al abrirse.
        private async Task<ICanalShell> ObtenerCanalShellAsync(IConexionSsh conexion, CancellationToken token)
        {
            Task<ICanalShell>? apertura = _aperturaShell;
            if (apertura is null || apertura.IsFaulted || apertura.IsCanceled || !ReferenceEquals(_conexionAperturaShell, conexion))
            {
                DescartarAperturaShell();
                apertura = Task.Factory.StartNew(conexion.AbrirShell, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                _aperturaShell = apertura;
                _conexionAperturaShell = conexion;
            }

            try
            {
                ICanalShell canal = await apertura.WaitAsync(token).ConfigureAwait(false);
                _aperturaShell = null;
                _conexionAperturaShell = null;
                return canal;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // Falló la propia apertura (no venció el comando): no se reutiliza.
                _aperturaShell = null;
                _conexionAperturaShell = null;
                throw;
            }
        }

        // Solo el bucle (o DisposeAsync cuando el bucle ya terminó).
        private void DescartarAperturaShell()
        {
            Task<ICanalShell>? apertura = _aperturaShell;
            _aperturaShell = null;
            _conexionAperturaShell = null;
            if (apertura is not null)
                _ = CerrarAperturaDescartadaAsync(apertura);
        }

        private async Task CerrarAperturaDescartadaAsync(Task<ICanalShell> apertura)
        {
            ICanalShell canal;
            try
            {
                canal = await apertura.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.AperturaShellAbandonada(ex, Destino.Clave);
                return;
            }

            _logger.AperturaShellAbandonada(null, Destino.Clave);
            _ = Task.Factory.StartNew(LiberarCanal, canal, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        // Un login lento con readline (bash -l) lee la línea del exec en modo crudo; al volver a modo canónico el
        // núcleo junta en una sola "línea" lo pendiente (la inicialización y su reenvío) y el bash nuevo lo lee de
        // una vez. La copia queda en su búfer, fuera del alcance del 'read', y se ejecutaría con el primer comando
        // (repite el marcador viejo y su 'read' se come la primera línea del comando). La línea de sincronización,
        // con un id nuevo, consume esa copia; su salida y los marcadores repetidos de la inicialización se descartan.
        private async Task<int> SincronizarAsync(ShellAbierto canal, Guid idInicializacion, CancellationToken token)
        {
            Guid id = Guid.NewGuid();
            TaskCompletionSource<int> espera = new(TaskCreationOptions.RunContinuationsAsynchronously);
            canal.CandadoEscritura.Enter();
            try
            {
                lock (_candadoProceso)
                {
                    if (!ReferenceEquals(_canal, canal))
                        return CodigoCanalCerrado;

                    _escaner.Reiniciar(id, idInicializacion);
                    _salidaActual = null;
                    _esperaMarcador = espera;
                }

                Componer(canal, EscrituraEnum.Sincronizacion, id, null);
                if (!TryEnviarBajoCandado(canal))
                    throw new NativeSshException($"No se pudo escribir en el shell de {Destino.Clave}.");
            }
            finally
            {
                SoltarCandadoEscritura(canal);
            }

            return await espera.Task.WaitAsync(token).ConfigureAwait(false);
        }

        // Con el shell ya en un estado conocido. Si la captura no da un PATH válido (LectorCapturaPath) se descarta: los
        // canales exec vuelven al PATH por defecto de sshd y se registra el motivo. Nunca hace fallar la inicialización
        // (salvo que el canal se cierre o no responda, como cualquier otra línea).
        private async Task<int> CapturarPathAsync(ShellAbierto canal, CancellationToken token)
        {
            Guid id = Guid.NewGuid();
            BufferSalida captura = new(MaxBytesCapturaPath, TimeProvider.System);
            TaskCompletionSource<int> espera = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                canal.CandadoEscritura.Enter();
                try
                {
                    lock (_candadoProceso)
                    {
                        if (!ReferenceEquals(_canal, canal))
                            return CodigoCanalCerrado;

                        _escaner.Reiniciar(id);
                        _salidaActual = captura;
                        _esperaMarcador = espera;
                    }

                    Componer(canal, EscrituraEnum.CapturaPath, id, null);
                    if (!TryEnviarBajoCandado(canal))
                        throw new NativeSshException($"No se pudo escribir en el shell de {Destino.Clave}.");
                }
                finally
                {
                    SoltarCandadoEscritura(canal);
                }

                int codigo = await espera.Task.WaitAsync(token).ConfigureAwait(false);
                if (codigo != 0)
                    return codigo;

                if (!TryLeerPath(captura, id, out string? path, out string? motivo))
                    _logger.PathLoginDescartado(Destino.Clave, motivo);
                _pathLogin = path;
                return codigo;
            }
            finally
            {
                captura.Liberar();
            }
        }

        private static bool TryLeerPath(BufferSalida captura, Guid id, [NotNullWhen(true)] out string? path, [NotNullWhen(false)] out string? motivo)
        {
            byte[] bytes = new byte[(int)Math.Min(captura.Longitud, MaxBytesCapturaPath)];
            LecturaSalida lectura = captura.Leer(0, bytes);
            if (!lectura.HasDatosDescartados && lectura.Fin <= bytes.Length)
                return LectorCapturaPath.TryLeer(bytes.AsSpan(0, lectura.Copiados), id, out path, out motivo);

            path = null;
            motivo = $"la salida de la captura superó {MaxBytesCapturaPath} bytes";
            return false;
        }

        private void IniciarBomba(ShellAbierto canal)
        {
            Thread hilo = new(Bombear) { IsBackground = true, Name = "native-ssh-bomba" };
            hilo.Start(canal);
        }

        // Hilo dedicado: ICanalShell.Leer es bloqueante. Un bloque del pool por canal, reutilizado.
        private void Bombear(object? estado)
        {
            ShellAbierto canal = (ShellAbierto)estado!;
            byte[] bloque = ArrayPool<byte>.Shared.Rent(TamanoBloqueLectura);
            try
            {
                while (true)
                {
                    int leidos = canal.Canal.Leer(bloque.AsSpan(0, TamanoBloqueLectura));
                    if (leidos <= 0)
                        break;

                    Procesar(canal, bloque.AsSpan(0, leidos));
                }
            }
            catch (Exception ex)
            {
                _logger.LecturaTerminadaConError(ex, Destino.Clave);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bloque);
                CerrarCanal(canal);
            }
        }

        private void Procesar(ShellAbierto canal, ReadOnlySpan<byte> datos)
        {
            TaskCompletionSource<int>? completada;
            int codigo;
            lock (_candadoProceso)
            {
                if (!ReferenceEquals(_canal, canal) || !_escaner.Procesar(datos, _salidaActual, out codigo))
                    return;

                completada = _esperaMarcador;
                _esperaMarcador = null;
                _salidaActual = null;
            }

            _ = (completada?.TrySetResult(codigo));
        }

        private void CerrarCanalActual()
        {
            ShellAbierto? canal;
            lock (_candadoProceso)
                canal = _canal;

            if (canal is not null)
                CerrarCanal(canal);
        }

        private void CerrarCanal(ShellAbierto canal) => _ = TryCerrarCanal(canal);

        // true si el servidor confirmó el cierre en EsperaCierreCanalShell (o la conexión ya no lo exigía); false si sigue
        // pendiente o si el canal ya no era el vigente (lo cerró otro).
        private bool TryCerrarCanal(ShellAbierto canal)
        {
            TaskCompletionSource<int>? espera;
            lock (_candadoProceso)
            {
                if (!ReferenceEquals(_canal, canal))
                    return false;

                _canal = null;
                _conexionDelCanal = null;
                espera = _esperaMarcador;
                _esperaMarcador = null;
                _salidaConCanalCerrado = _salidaActual;
                _salidaActual = null;
                _escaner.Desactivar();
            }

            _logger.CanalShellCerrado(Destino.Clave);
            _ = (espera?.TrySetResult(CodigoCanalCerrado));
            // SSH.NET espera sin plazo el CLOSE del servidor, que no llega mientras el bash remoto no termine: uno que no
            // muere al perder el terminal (trap '' HUP con un trabajo en primer plano) no puede bloquear a quien cierra.
            Task cierre = Task.Factory.StartNew(LiberarCanal, canal.Canal, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return cierre.Wait(EsperaCierreCanalShell);
        }

        // Deja de usar el canal del shell sin cerrarlo, porque una escritura (la entrada de ssh_input) sigue dentro de un envío
        // que el servidor no acepta: cerrarlo con ella dentro podría sacar sus datos después del CLOSE y OpenSSH cortaría la
        // conexión (EQ4). Desde aquí ya no es el vigente, así que nadie escribe más en él (toda escritura lo comprueba con
        // el candado del canal tomado) y el siguiente comando abre un shell nuevo sin esperar a esa entrada. Se cierra en
        // cuanto la escritura termina (CerrarSiAbandonado) y, como sigue ocupando una sesión del MaxSessions del servidor,
        // retiene hasta entonces una plaza del cupo de canales exec. false si ya no era el vigente (lo cerró otro).
        private bool AbandonarCanal(ShellAbierto canal)
        {
            lock (_candadoProceso)
            {
                if (!ReferenceEquals(_canal, canal))
                    return false;

                _canal = null;
                _conexionDelCanal = null;
                _esperaMarcador = null;
                _salidaActual = null;
                _escaner.Desactivar();
            }

            _logger.CanalShellAbandonado(Destino.Clave);
            _ = Interlocked.Increment(ref _shellsAbandonados);
            canal.MarcarAbandonado();
            _ = RetenerCupoMientrasSigaAbiertoAsync(canal);
            CerrarSiAbandonado(canal);
            return true;
        }

        // Lo llama quien suelta el candado de escritura del canal y quien lo abandona: el primero que lo encuentre abandonado
        // y libre lo cierra, en segundo plano (SSH.NET espera el CLOSE del servidor). Nunca con el candado ya tomado por el
        // mismo hilo (Lock admite reentrada y el cierre se colaría dentro de esa escritura).
        private void CerrarSiAbandonado(ShellAbierto canal)
        {
            if (!canal.IsAbandonado || !canal.CandadoEscritura.TryEnter())
                return;

            bool isPrimero;
            try
            {
                isPrimero = canal.TryIniciarCierre();
            }
            finally
            {
                canal.CandadoEscritura.Exit();
            }

            if (isPrimero)
                _ = Task.Factory.StartNew(LiberarCanalAbandonado, canal, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void LiberarCanalAbandonado(object? estado)
        {
            ShellAbierto canal = (ShellAbierto)estado!;
            _logger.CanalShellAbandonadoCerrado(Destino.Clave);
            LiberarCanal(canal.Canal);
            _ = Interlocked.Decrement(ref _shellsAbandonados);
            canal.MarcarCerrado();
        }

        // La plaza se toma en cuanto haya una libre (las lecturas en curso no se interrumpen) y se devuelve al cerrarse el
        // canal; si se cierra antes, se devuelve al momento.
        private async Task RetenerCupoMientrasSigaAbiertoAsync(ShellAbierto canal)
        {
            try
            {
                await _cupoExec.WaitAsync(_cierre.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await canal.Cerrado.ConfigureAwait(false);
            }
            finally
            {
                _ = _cupoExec.Release();
            }
        }

        private void LiberarCanal(object? estado)
        {
            try
            {
                ((ICanalShell)estado!).Dispose();
            }
            catch (Exception ex)
            {
                _logger.ErrorCerrandoCanal(ex, Destino.Clave);
            }
        }

        private bool TryEscribir(ShellAbierto canal, EscrituraEnum tipo, Guid id)
        {
            canal.CandadoEscritura.Enter();
            try
            {
                Componer(canal, tipo, id, null);
                return TryEnviarBajoCandado(canal);
            }
            finally
            {
                SoltarCandadoEscritura(canal);
            }
        }

        // Requiere el candado de escritura del canal.
        private void Componer(ShellAbierto canal, EscrituraEnum tipo, Guid id, string? comando)
        {
            ArrayBufferWriter<byte> escritor = canal.Escritor;
            escritor.ResetWrittenCount();
            switch (tipo)
            {
                case EscrituraEnum.Inicializacion:
                    ConstructorComando.EscribirInicializacion(id, escritor, _directorioTrabajo);
                    break;
                case EscrituraEnum.ReenvioInicializacion:
                    ConstructorComando.EscribirLineaInicializacion(id, escritor, _directorioTrabajo);
                    break;
                case EscrituraEnum.Sincronizacion:
                    ConstructorComando.EscribirSincronizacion(id, escritor);
                    break;
                case EscrituraEnum.CapturaPath:
                    ConstructorComando.EscribirCapturaPath(id, escritor);
                    break;
                case EscrituraEnum.Comando:
                    ConstructorComando.EscribirComando(comando, id, escritor);
                    break;
                case EscrituraEnum.Interrupcion:
                    ConstructorComando.EscribirInterrupcion(id, escritor);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(tipo), tipo, null);
            }
        }

        // Requiere el candado de escritura del canal.
        private bool TryEnviarBajoCandado(ShellAbierto canal)
        {
            canal.MarcarInicioEscritura();
            lock (_candadoProceso)
                _canalesEscribiendo.Add(canal);
            try
            {
                canal.Canal.Escribir(canal.Escritor.WrittenSpan);
                return true;
            }
            catch (Exception ex)
            {
                _logger.EscrituraFallida(ex, Destino.Clave);
                return false;
            }
            finally
            {
                lock (_candadoProceso)
                    _ = _canalesEscribiendo.Remove(canal);
                canal.MarcarFinEscritura();
            }
        }

        private static string? Unir(string? primero, string? segundo) =>
            primero is null ? segundo : segundo is null ? primero : $"{primero} {segundo}";

        // Canal del shell con lo que hace falta para escribir en él: su candado de escritura, su búfer y la marca de la
        // escritura en curso. Son de cada canal y no de la sesión: una escritura que el servidor no acepta solo retiene el
        // suyo, y el búfer que SSH.NET aún está enviando (ShellStream.Write lo copia por trozos mientras envía) no lo pisa
        // la escritura de otro canal.
        private sealed class ShellAbierto(ICanalShell canal)
        {
            private readonly TaskCompletionSource _cerrado = new(TaskCreationOptions.RunContinuationsAsynchronously);

            // Marca de tiempo (Stopwatch) del inicio de la escritura en curso; 0 si no hay ninguna.
            private long _inicioEscritura;
            private int _isAbandonado;
            private int _isCierreIniciado;

            public ICanalShell Canal { get; } = canal;
            public Lock CandadoEscritura { get; } = new();
            public ArrayBufferWriter<byte> Escritor { get; } = new(4096);

            // Se completa cuando un canal abandonado termina de cerrarse.
            public Task Cerrado => _cerrado.Task;

            public double? SegundosEnEscritura
            {
                get
                {
                    long inicio = Interlocked.Read(ref _inicioEscritura);
                    return inicio == 0 ? null : Stopwatch.GetElapsedTime(inicio).TotalSeconds;
                }
            }

            // Lectura con barrera completa: quien acaba de soltar el candado y comprueba esto no puede adelantar la lectura
            // a su propia liberación, así que él o quien abandona el canal ven siempre al otro (CerrarSiAbandonado).
            public bool IsAbandonado => Interlocked.CompareExchange(ref _isAbandonado, 0, 0) != 0;

            public void MarcarInicioEscritura() => _ = Interlocked.Exchange(ref _inicioEscritura, Stopwatch.GetTimestamp());

            public void MarcarFinEscritura() => _ = Interlocked.Exchange(ref _inicioEscritura, 0);

            public void MarcarAbandonado() => _ = Interlocked.Exchange(ref _isAbandonado, 1);

            // true solo para el primero.
            public bool TryIniciarCierre() => Interlocked.Exchange(ref _isCierreIniciado, 1) == 0;

            public void MarcarCerrado() => _cerrado.TrySetResult();
        }

        // Apertura de un canal exec: sin confirmar cuenta en _aperturasExecSinConfirmar hasta que el servidor la confirma
        // (Confirmar, desde el adaptador) o la operación termina sin confirmación (Terminar); solo la primera cuenta.
        private sealed class AperturaExec
        {
            private readonly SesionShell _sesion;
            private int _estado;

            public AperturaExec(SesionShell sesion)
            {
                _sesion = sesion;
                _ = Interlocked.Increment(ref sesion._aperturasExecSinConfirmar);
            }

            public bool IsConfirmada => Volatile.Read(ref _estado) == 1;

            public void Confirmar() => Finalizar(1);

            public void Terminar() => Finalizar(2);

            private void Finalizar(int estado)
            {
                if (Interlocked.CompareExchange(ref _estado, estado, 0) == 0)
                    _ = Interlocked.Decrement(ref _sesion._aperturasExecSinConfirmar);
            }
        }
    }
}
