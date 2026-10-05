using System.Buffers;
using System.Text;
using System.Threading.Channels;
using NativeSsh.Buffers;
using NativeSsh.Comun;
using NativeSsh.Protocolo;

namespace NativeSsh.Sesion
{
    // Un shell PTY persistente por destino. Bucle async que ejecuta la cola FIFO y un hilo de lectura por canal.
    public sealed class SesionShell : IAsyncDisposable
    {
        public const int TamanoBloqueLectura = 16 * 1024;
        public const int CodigoCanalCerrado = int.MinValue;

        private static readonly TimeSpan ReenvioInicializacion = TimeSpan.FromSeconds(2);

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

        private ICanalShell? _canal;
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
        private int _isDispuesto;

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
            Comando,
            Interrupcion
        }

        public DestinoSsh Destino { get; }
        public bool IsConectada => _conexion?.IsConectada ?? false;
        public long ComandosEjecutados => Interlocked.Read(ref _comandosEjecutados);
        public int ConexionesAbiertas => Volatile.Read(ref _conexionesAbiertas);
        public int ComandosEnCola => Volatile.Read(ref _comandosEnCola);
        public Peticion? PeticionActual => _peticionActual;

        public bool IsShellActivo
        {
            get
            {
                lock (_candadoProceso)
                    return _canal is not null;
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
            _conexion?.Dispose();
            _conexion = null;
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
                FinalizarInterrumpida(peticion, limite.IsCancellationRequested, isComandoEnviado: false, null);
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
                FinalizarInterrumpida(peticion, limite.IsCancellationRequested, isComandoEnviado: false, avisoShell);
                return;
            }

            TaskCompletionSource<int> espera = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ICanalShell? canal;
            bool isEnviado = false;
            lock (_candadoEscritura)
            {
                lock (_candadoProceso)
                {
                    canal = _canal;
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
                _ = peticion.TryFinalizar(EstadoPeticionEnum.ShellPerdido, -1, Unir(avisoShell, "No se pudo enviar el comando: el canal del shell se cerró. El siguiente comando abrirá uno nuevo."));
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
                    string? avisoInterrupcion = await InterrumpirAsync(canal, peticion).ConfigureAwait(false);
                    FinalizarInterrumpida(peticion, limite.IsCancellationRequested, isComandoEnviado: true, Unir(avisoShell, avisoInterrupcion));
                    return;
                }

                codigo = espera.Task.Result;
            }

            _ = codigo == CodigoCanalCerrado
                ? peticion.TryFinalizar(EstadoPeticionEnum.ShellPerdido, -1, Unir(avisoShell, "El canal del shell se cerró durante el comando (exit, caída de la conexión o cierre desde el servidor). El siguiente comando abrirá un shell nuevo y las variables exportadas se perderán."))
                : peticion.TryFinalizar(EstadoPeticionEnum.Completado, codigo, avisoShell);
        }

        private async Task<string?> InterrumpirAsync(ICanalShell canal, Peticion peticion)
        {
            TaskCompletionSource<int> recuperacion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Guid idRecuperacion = Guid.NewGuid();
            bool isEnviado;
            lock (_candadoEscritura)
            {
                bool isCanalVigente;
                lock (_candadoProceso)
                {
                    isCanalVigente = ReferenceEquals(_canal, canal);
                    if (isCanalVigente)
                    {
                        _isInterrumpiendo = true;
                        _escaner.Reiniciar(idRecuperacion, peticion.Id);
                        _salidaActual = peticion.Salida;
                        _esperaMarcador = recuperacion;
                    }
                }

                if (!isCanalVigente)
                    return "El canal del shell ya estaba cerrado; el siguiente comando abrirá uno nuevo.";

                Componer(EscrituraEnum.Interrupcion, idRecuperacion, null);
                isEnviado = TryEnviarBajoCandado(canal);
            }

            if (isEnviado)
            {
                try
                {
                    int codigo = await recuperacion.Task.WaitAsync(_opciones.EsperaTrasCtrlC, _cierre.Token).ConfigureAwait(false);
                    return codigo == CodigoCanalCerrado
                        ? "El shell se cerró al interrumpir; el siguiente comando abrirá uno nuevo y las variables exportadas se perderán."
                        : null;
                }
                catch (TimeoutException)
                {
                    // No respondió a Ctrl+C: se escala al cierre del canal.
                }
            }

            CerrarCanal(canal);
            return $"El proceso no respondió a Ctrl+C en {_opciones.EsperaTrasCtrlC.TotalSeconds:0.#} s: se cerró el canal del shell (la conexión TCP sigue abierta). El siguiente comando abrirá un shell nuevo y las variables exportadas se perderán.";
        }

        private static void FinalizarInterrumpida(Peticion peticion, bool isTiempoAgotado, bool isComandoEnviado, string? aviso)
        {
            string detalle = isComandoEnviado ? "se envió Ctrl+C al proceso." : "el comando no llegó a enviarse.";
            _ = peticion.IsCancelacionSolicitada || !isTiempoAgotado
                ? peticion.TryFinalizar(EstadoPeticionEnum.Cancelado, 130, Unir($"Cancelado: {detalle}", aviso))
                : peticion.TryFinalizar(EstadoPeticionEnum.TiempoAgotado, 130, Unir($"Tiempo agotado ({peticion.Timeout.TotalSeconds:0.#} s): {detalle}", aviso));
        }

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
            bool isConectada = conexion is { IsConectada: true };
            if (isConectada && IsShellActivo)
                return null;

            CerrarCanalActual();
            if (!isConectada)
            {
                conexion?.Dispose();
                _conexion = null;
                _conexion = await ConectarAsync(token).ConfigureAwait(false);
            }

            return await AbrirShellAsync(_conexion!, token).ConfigureAwait(false);
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
            catch (Exception ex)
            {
                throw new NativeSshException($"No se pudo abrir un shell en {Destino.Clave}: {ex.Message}", ex);
            }

            Guid id = Guid.NewGuid();
            TaskCompletionSource<int> espera = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_candadoProceso)
            {
                _canal = canal;
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
                    ConstructorComando.EscribirInicializacion(id, _escritor);
                    break;
                case EscrituraEnum.ReenvioInicializacion:
                    ConstructorComando.EscribirLineaInicializacion(id, _escritor);
                    break;
                case EscrituraEnum.Sincronizacion:
                    ConstructorComando.EscribirSincronizacion(id, _escritor);
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
