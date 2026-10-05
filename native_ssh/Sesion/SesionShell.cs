using System.Buffers;
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
    public sealed class SesionShell : IAsyncDisposable
    {
        public const int TamanoBloqueLectura = 16 * 1024;
        public const int CodigoCanalCerrado = int.MinValue;

        // Canales exec simultáneos por conexión (más el canal del shell): muy por debajo del MaxSessions 10 de OpenSSH
        // y de SSH.NET. Los que pasen del cupo esperan su turno dentro de su timeout.
        public const int MaxCanalesExecSimultaneos = 4;

        // __NS_CWD viaja en base64 en la línea de inicialización del TTY (máximo 4095 bytes por línea).
        public const int MaxBytesDirectorioTrabajo = 1024;

        private static readonly TimeSpan MargenLimiteLocalExec = TimeSpan.FromSeconds(3);

        // Base64 del PATH del login: muy por encima de cualquier PATH real.
        private const int MaxBytesCapturaPath = 16 * 1024;

        private static readonly TimeSpan ReenvioInicializacion = TimeSpan.FromSeconds(2);

        private const string DetalleComandoNoEnviado = "el comando no llegó a enviarse.";
        private const string DetalleCtrlCEnviado = "se envió Ctrl+C al proceso.";

        private readonly IFabricaConexion _fabrica;
        private readonly LimitadorConexiones _limitador;
        private readonly OpcionesNativeSsh _opciones;
        private readonly ILogger _logger;
        private readonly Channel<Peticion> _cola = Channel.CreateUnbounded<Peticion>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _cierre = new();
        private readonly Lock _candadoProceso = new();
        private readonly Lock _candadoEscritura = new();
        private readonly ArrayBufferWriter<byte> _escritor = new(4096);
        private readonly EscanerMarcador _escaner = new(TamanoBloqueLectura);
        private readonly Task _bucle;
        private readonly SemaphoreSlim _candadoConexion = new(1, 1);
        private readonly SemaphoreSlim _cupoExec = new(MaxCanalesExecSimultaneos, MaxCanalesExecSimultaneos);

        private ICanalShell? _canal;
        private IConexionSsh? _conexionDelCanal;
        private BufferSalida? _salidaActual;
        private TaskCompletionSource<int>? _esperaMarcador;
        private bool _isInterrumpiendo;

        private volatile IConexionSsh? _conexion;
        private bool _isShellInicializadoAlgunaVez;

        private volatile Peticion? _peticionActual;
        private volatile bool _isResetSolicitado;
        private int _comandosEnCola;
        private long _comandosEjecutados;
        private int _conexionesAbiertas;
        private int _canalesExecActivos;
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

        // Fuente de verdad de __NS_CWD (ruta absoluta validada por ssh_set_cwd, o null = $HOME). Se reexporta al
        // (re)inicializar el shell PTY y se pasa explícitamente a cada canal exec.
        public string? DirectorioTrabajo
        {
            get => _directorioTrabajo;
            set => _directorioTrabajo = string.IsNullOrEmpty(value) ? null : value;
        }

        // Un canal cuya conexión ya se perdió no cuenta como shell activo aunque aún no se haya cerrado: ssh_status nunca
        // muestra un shell activo sin conexión (IsConectada se lee antes que este valor).
        // Cierre en curso o terminado (DisposeAsync lo marca antes que nada).
        private bool IsCerrando => Volatile.Read(ref _isDispuesto) != 0;

        public bool IsShellActivo
        {
            get
            {
                lock (_candadoProceso)
                    return _canal is not null && _conexionDelCanal is { IsConectada: true };
            }
        }

        public void Encolar(Peticion peticion)
        {
            _ = Interlocked.Increment(ref _comandosEnCola);
            if (Volatile.Read(ref _isDispuesto) == 0 && _cola.Writer.TryWrite(peticion))
                return;

            _ = Interlocked.Decrement(ref _comandosEnCola);
            _ = peticion.TryFinalizar(EstadoPeticionEnum.Error, -1, $"La sesión con {Destino.Clave} está cerrada; vuelve a lanzar el comando.");
        }

        public bool EnviarEntrada(Peticion peticion, ReadOnlySpan<char> texto, bool isEnviarEnter)
        {
            lock (_candadoEscritura)
            {
                ICanalShell? canal;
                lock (_candadoProceso)
                {
                    bool isAdmiteEntrada = peticion.Estado == EstadoPeticionEnum.Ejecutando && !_isInterrumpiendo && ReferenceEquals(_salidaActual, peticion.Salida);
                    canal = isAdmiteEntrada ? _canal : null;
                }

                if (canal is null)
                    return false;

                _escritor.ResetWrittenCount();
                Span<byte> destino = _escritor.GetSpan(Encoding.UTF8.GetMaxByteCount(texto.Length) + 1);
                int escritos = Encoding.UTF8.GetBytes(texto, destino);
                if (isEnviarEnter)
                    destino[escritos++] = (byte)'\n';
                _escritor.Advance(escritos);
                return TryEnviarBajoCandado(canal);
            }
        }

        public void SolicitarReset()
        {
            _isResetSolicitado = true;
            _ = (_peticionActual?.SolicitarCancelacion());
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDispuesto, 1) != 0)
                return;

            _ = _cola.Writer.TryComplete();
            _cierre.Cancel();
            try
            {
                await _bucle.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.BucleTerminadoConError(ex, Destino.Clave);
            }

            CerrarCanalActual();
            // Un canal exec que estuviera conectando ya vio _cierre cancelado; tras el candado nadie crea otra conexión.
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
        // La operación termina al agotarse el límite local aunque la apertura del canal siga colgada (servidor que no
        // responde): el canal conserva su cupo hasta que se cierra de verdad, para no superar el MaxSessions del servidor.
        public async Task<ResultadoCanalExec> EjecutarExecAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _isDispuesto) != 0)
                throw new NativeSshException($"La sesión con {Destino.Clave} está cerrada; vuelve a lanzar la operación.");

            // El script se acota en el servidor con 'timeout'; el límite local, algo mayor, solo actúa si el servidor
            // no responde (o no tiene 'timeout') y cubre la espera de cupo y la conexión.
            string comando = ConstructorComandoExec.Construir(script, _directorioTrabajo, timeout, _pathLogin);
            using CancellationTokenSource limite = new(timeout + MargenLimiteLocalExec);
            using CancellationTokenSource enlazado = CancellationTokenSource.CreateLinkedTokenSource(limite.Token, cancellationToken, _cierre.Token);
            CancellationToken token = enlazado.Token;
            bool isCupoTomado = false;
            bool isCanalContado = false;
            Task<ResultadoCanalExec>? ejecucion = null;
            try
            {
                await _cupoExec.WaitAsync(token).ConfigureAwait(false);
                isCupoTomado = true;
                IConexionSsh conexion = await ObtenerConexionAsync(token).ConfigureAwait(false);
                _ = Interlocked.Increment(ref _canalesExecActivos);
                isCanalContado = true;
                ejecucion = conexion.EjecutarExecAsync(comando, _opciones.MaxBytesSalidaPorPeticion, token);
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
                throw new NativeSshException($"La operación no terminó en {timeout.TotalSeconds:0.#} s (tiempo agotado); se abandona y su canal exec se cierra.");
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or NativeSshException))
            {
                throw new NativeSshException($"Falló el canal exec en {Destino.Clave}: {ex.Message}", ex);
            }
            finally
            {
                if (ejecucion is { IsCompleted: false })
                    _ = LiberarCanalExecAlCerrarseAsync(ejecucion);
                else
                    LiberarCanalExec(isCupoTomado, isCanalContado);
            }
        }

        // La tarea de IConexionSsh.EjecutarExecAsync termina cuando el canal está cerrado (si su apertura seguía en curso,
        // cuando termina y se cierra): hasta entonces sigue contando como canal abierto en la conexión.
        private async Task LiberarCanalExecAlCerrarseAsync(Task<ResultadoCanalExec> ejecucion)
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
                    try
                    {
                        await EjecutarAsync(peticion).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (_cierre.IsCancellationRequested)
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
            catch (OperationCanceledException) when (!_cierre.IsCancellationRequested)
            {
                FinalizarInterrumpida(peticion, limite.IsCancellationRequested, DetalleComandoNoEnviado, null);
                return;
            }
            catch (NativeSshException ex)
            {
                _ = peticion.TryFinalizar(EstadoPeticionEnum.Error, -1, ex.Message);
                return;
            }

            if (token.IsCancellationRequested)
            {
                _cierre.Token.ThrowIfCancellationRequested();
                FinalizarInterrumpida(peticion, limite.IsCancellationRequested, DetalleComandoNoEnviado, avisoShell);
                return;
            }

            TaskCompletionSource<int> espera = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ICanalShell? canal;
            IConexionSsh? conexionDelCanal;
            bool isEnviado = false;
            lock (_candadoEscritura)
            {
                lock (_candadoProceso)
                {
                    canal = _canal;
                    conexionDelCanal = _conexionDelCanal;
                    if (canal is not null)
                    {
                        _escaner.Reiniciar(peticion.Id);
                        _salidaActual = peticion.Salida;
                        _esperaMarcador = espera;
                    }
                }

                if (canal is not null)
                {
                    Componer(EscrituraEnum.Comando, peticion.Id, peticion.Comando);
                    isEnviado = TryEnviarBajoCandado(canal);
                }
            }

            if (!isEnviado || canal is null)
            {
                if (canal is not null)
                    CerrarCanal(canal);
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
            catch (OperationCanceledException) when (!_cierre.IsCancellationRequested)
            {
                if (!espera.Task.IsCompletedSuccessfully)
                {
                    (string detalle, string? avisoInterrupcion) = await InterrumpirAsync(canal, peticion).ConfigureAwait(false);
                    FinalizarInterrumpida(peticion, limite.IsCancellationRequested, detalle, Unir(avisoShell, avisoInterrupcion));
                    return;
                }

                codigo = espera.Task.Result;
            }

            _ = codigo == CodigoCanalCerrado
                ? peticion.TryFinalizar(EstadoPeticionEnum.ShellPerdido, -1, Unir(avisoShell, DescribirShellPerdido(conexionDelCanal)))
                : peticion.TryFinalizar(EstadoPeticionEnum.Completado, codigo, avisoShell);
        }

        // Con la conexión perdida (el adaptador la marca así antes de cerrar el canal) se dice explícitamente.
        private string DescribirShellPerdido(IConexionSsh? conexion) => conexion is { IsConectada: true }
            ? "El canal del shell se cerró durante el comando (exit o cierre desde el servidor). El siguiente comando abrirá un shell nuevo y las variables exportadas se perderán."
            : $"Se perdió la conexión SSH con {Destino.Clave} durante el comando: el shell remoto se cerró y las variables exportadas se perdieron. El siguiente comando reconectará y abrirá un shell nuevo.";

        // Devuelve qué se hizo con el proceso (para el estado final) y el aviso sobre el shell. Solo afirma que se envió
        // Ctrl+C, que no respondió o que la conexión sigue abierta cuando es cierto.
        private async Task<(string Detalle, string? Aviso)> InterrumpirAsync(ICanalShell canal, Peticion peticion)
        {
            TaskCompletionSource<int> recuperacion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Guid idRecuperacion = Guid.NewGuid();
            IConexionSsh? conexion;
            bool isEnviado;
            lock (_candadoEscritura)
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

                Componer(EscrituraEnum.Interrupcion, idRecuperacion, null);
                isEnviado = TryEnviarBajoCandado(canal);
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

            CerrarCanal(canal);
            bool isConectada = conexion is { IsConectada: true };
            string siguiente = isConectada
                ? "El siguiente comando abrirá un shell nuevo sobre la misma conexión y las variables exportadas se perderán."
                : "El siguiente comando reconectará y abrirá un shell nuevo, y las variables exportadas se perderán.";
            if (!isEnviado)
                return ("no se pudo enviar Ctrl+C al proceso.", isConectada
                    ? $"El canal del shell ya no admitía escrituras: se cerró. {siguiente}"
                    : $"El canal del shell ya no admitía escrituras porque se perdió la conexión SSH: se cerró. {siguiente}");

            string espera = $"{_opciones.EsperaTrasCtrlC.TotalSeconds:0.#} s";
            return (DetalleCtrlCEnviado, isConectada
                ? $"El proceso no respondió a Ctrl+C en {espera}: se cerró el canal del shell (la conexión TCP sigue abierta). {siguiente}"
                : $"El proceso no respondió a Ctrl+C en {espera} y se perdió la conexión SSH: se cerró el canal del shell. {siguiente}");
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
                if (Volatile.Read(ref _isDispuesto) != 0)
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
            ICanalShell? canal;
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
            ICanalShell canal;
            try
            {
                canal = conexion.AbrirShell();
            }
            catch (Exception ex) when (ex is not NativeSshException)
            {
                throw new NativeSshException($"No se pudo abrir un shell en {Destino.Clave}: {ex.Message}", ex);
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
                throw new NativeSshException($"El shell de {Destino.Clave} no completó la inicialización en {_opciones.TimeoutInicializacionShell.TotalSeconds:0} s. El servidor necesita bash, stty y base64.");
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

        // Un login lento con readline (bash -l) lee la línea del exec en modo crudo; al volver a modo canónico el
        // núcleo junta en una sola "línea" lo pendiente (la inicialización y su reenvío) y el bash nuevo lo lee de
        // una vez. La copia queda en su búfer, fuera del alcance del 'read', y se ejecutaría con el primer comando
        // (repite el marcador viejo y su 'read' se come la primera línea del comando). La línea de sincronización,
        // con un id nuevo, consume esa copia; su salida y los marcadores repetidos de la inicialización se descartan.
        private async Task<int> SincronizarAsync(ICanalShell canal, Guid idInicializacion, CancellationToken token)
        {
            Guid id = Guid.NewGuid();
            TaskCompletionSource<int> espera = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_candadoEscritura)
            {
                lock (_candadoProceso)
                {
                    if (!ReferenceEquals(_canal, canal))
                        return CodigoCanalCerrado;

                    _escaner.Reiniciar(id, idInicializacion);
                    _salidaActual = null;
                    _esperaMarcador = espera;
                }

                Componer(EscrituraEnum.Sincronizacion, id, null);
                if (!TryEnviarBajoCandado(canal))
                    throw new NativeSshException($"No se pudo escribir en el shell de {Destino.Clave}.");
            }

            return await espera.Task.WaitAsync(token).ConfigureAwait(false);
        }

        // Con el shell ya en un estado conocido. Si la captura no da un PATH válido (LectorCapturaPath) se descarta: los
        // canales exec vuelven al PATH por defecto de sshd y se registra el motivo. Nunca hace fallar la inicialización
        // (salvo que el canal se cierre o no responda, como cualquier otra línea).
        private async Task<int> CapturarPathAsync(ICanalShell canal, CancellationToken token)
        {
            Guid id = Guid.NewGuid();
            BufferSalida captura = new(MaxBytesCapturaPath, TimeProvider.System);
            TaskCompletionSource<int> espera = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                lock (_candadoEscritura)
                {
                    lock (_candadoProceso)
                    {
                        if (!ReferenceEquals(_canal, canal))
                            return CodigoCanalCerrado;

                        _escaner.Reiniciar(id);
                        _salidaActual = captura;
                        _esperaMarcador = espera;
                    }

                    Componer(EscrituraEnum.CapturaPath, id, null);
                    if (!TryEnviarBajoCandado(canal))
                        throw new NativeSshException($"No se pudo escribir en el shell de {Destino.Clave}.");
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

        private void IniciarBomba(ICanalShell canal)
        {
            Thread hilo = new(Bombear) { IsBackground = true, Name = "native-ssh-bomba" };
            hilo.Start(canal);
        }

        // Hilo dedicado: ICanalShell.Leer es bloqueante. Un bloque del pool por canal, reutilizado.
        private void Bombear(object? estado)
        {
            ICanalShell canal = (ICanalShell)estado!;
            byte[] bloque = ArrayPool<byte>.Shared.Rent(TamanoBloqueLectura);
            try
            {
                while (true)
                {
                    int leidos = canal.Leer(bloque.AsSpan(0, TamanoBloqueLectura));
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

        private void Procesar(ICanalShell canal, ReadOnlySpan<byte> datos)
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
            ICanalShell? canal;
            lock (_candadoProceso)
                canal = _canal;

            if (canal is not null)
                CerrarCanal(canal);
        }

        private void CerrarCanal(ICanalShell canal)
        {
            TaskCompletionSource<int>? espera;
            lock (_candadoProceso)
            {
                if (!ReferenceEquals(_canal, canal))
                    return;

                _canal = null;
                _conexionDelCanal = null;
                espera = _esperaMarcador;
                _esperaMarcador = null;
                _salidaActual = null;
                _escaner.Desactivar();
            }

            _ = (espera?.TrySetResult(CodigoCanalCerrado));
            try
            {
                canal.Dispose();
            }
            catch (Exception ex)
            {
                _logger.ErrorCerrandoCanal(ex, Destino.Clave);
            }
        }

        private bool TryEscribir(ICanalShell canal, EscrituraEnum tipo, Guid id)
        {
            lock (_candadoEscritura)
            {
                Componer(tipo, id, null);
                return TryEnviarBajoCandado(canal);
            }
        }

        // Requiere _candadoEscritura.
        private void Componer(EscrituraEnum tipo, Guid id, string? comando)
        {
            _escritor.ResetWrittenCount();
            switch (tipo)
            {
                case EscrituraEnum.Inicializacion:
                    ConstructorComando.EscribirInicializacion(id, _escritor, _directorioTrabajo);
                    break;
                case EscrituraEnum.ReenvioInicializacion:
                    ConstructorComando.EscribirLineaInicializacion(id, _escritor, _directorioTrabajo);
                    break;
                case EscrituraEnum.Sincronizacion:
                    ConstructorComando.EscribirSincronizacion(id, _escritor);
                    break;
                case EscrituraEnum.CapturaPath:
                    ConstructorComando.EscribirCapturaPath(id, _escritor);
                    break;
                case EscrituraEnum.Comando:
                    ConstructorComando.EscribirComando(comando, id, _escritor);
                    break;
                case EscrituraEnum.Interrupcion:
                    ConstructorComando.EscribirInterrupcion(id, _escritor);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(tipo), tipo, null);
            }
        }

        // Requiere _candadoEscritura.
        private bool TryEnviarBajoCandado(ICanalShell canal)
        {
            try
            {
                canal.Escribir(_escritor.WrittenSpan);
                return true;
            }
            catch (Exception ex)
            {
                _logger.EscrituraFallida(ex, Destino.Clave);
                return false;
            }
        }

        private static string? Unir(string? primero, string? segundo) =>
            primero is null ? segundo : segundo is null ? primero : $"{primero} {segundo}";
    }
}
