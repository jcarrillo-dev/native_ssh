using System.Diagnostics.CodeAnalysis;
using NativeSsh.Buffers;
using NativeSsh.Comun;

namespace NativeSsh.Sesion
{
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El CancellationTokenSource no tiene temporizador: no retiene recursos y nunca se cancela uno ya liberado.")]
    public sealed class Peticion
    {
        private readonly Lock _candado = new();
        private readonly TaskCompletionSource _finalizacion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Sin temporizador asociado: no hace falta Dispose y así nunca se cancela un CTS ya liberado.
        private readonly CancellationTokenSource _interrupcion = new();
        private readonly TimeProvider _reloj;
        private int _estado;
        private long _inicioTimestamp;
        private long _finTimestamp;
        private volatile bool _isCancelacionSolicitada;
        private volatile bool _isComandoEnviado;
        private volatile bool _isRechazadaSoloLectura;
        private volatile bool _isEnvioIniciado;
        private int _codigoSalida = -1;
        private string? _aviso;
        private Func<string?>? _avisoEnCurso;

        public Peticion(DestinoSsh destino, string comando, TimeSpan timeout, int maxBytesSalida, TimeProvider reloj)
        {
            Id = Guid.NewGuid();
            IdTexto = Id.ToString("N");
            Destino = destino;
            Comando = comando;
            Timeout = timeout;
            _reloj = reloj;
            Salida = new BufferSalida(maxBytesSalida, reloj);
            CreadoTimestamp = reloj.GetTimestamp();
        }

        public Guid Id { get; }
        public string IdTexto { get; }
        public DestinoSsh Destino { get; }
        public string Comando { get; }

        // La herramienta de escritura que la lanzó (ssh_exec, ssh_write_file, ssh_replace_file_content), o null si no es una
        // escritura (ssh_set_cwd, lecturas por el shell): antes de enviarla, la sesión comprueba el modo solo lectura con la
        // dirección de la conexión (DestinosSoloLectura.ComprobarConexionAsync).
        public string? HerramientaEscritura { get; init; }

        // Terminó en Error sin enviarse porque la conexión de la sesión es de un destino de solo lectura (el aviso lo dice).
        public bool IsRechazadaSoloLectura => _isRechazadaSoloLectura;
        public TimeSpan Timeout { get; }
        public BufferSalida Salida { get; }
        public long CreadoTimestamp { get; }

        // Se escriben antes que el estado final (Volatile.Write): quien lee primero Estado ve valores coherentes.
        public int CodigoSalida => Volatile.Read(ref _codigoSalida);
        // Mientras no termina, el aviso es el de la fuente fijada con FijarAvisoEnCurso (p. ej. una escritura al shell que el
        // servidor no acepta); al terminar, el del estado final.
        public string? Aviso => IsFinalizada ? Volatile.Read(ref _aviso) : Volatile.Read(ref _avisoEnCurso)?.Invoke();
        public EstadoPeticionEnum Estado => (EstadoPeticionEnum)Volatile.Read(ref _estado);
        public bool IsFinalizada => Estado.IsFinal();
        public bool IsCancelacionSolicitada => _isCancelacionSolicitada;

        // true en cuanto el comando entero se escribió en el canal del shell (SesionShell.EjecutarAsync). Si la petición
        // termina sin él, el comando no llegó al shell: no se ejecutó nada (falló la conexión, la apertura o la
        // inicialización del shell, p. ej. un servidor sin bash, o se canceló antes); si la escritura falló a medias, el
        // estado es ShellPerdido, o Cancelado si la cortó el cierre de la sesión (ssh_disconnect): IsEnvioIniciado.
        public bool IsComandoEnviado => _isComandoEnviado;

        // true desde que la sesión empieza a escribir el comando en el canal del shell (antes de IsComandoEnviado): si la
        // petición termina con esta señal y sin la otra, la escritura se quedó a medias y no se sabe qué llegó al shell.
        public bool IsEnvioIniciado => _isEnvioIniciado;
        public Task Finalizacion => _finalizacion.Task;
        public CancellationToken TokenInterrupcion => _interrupcion.Token;
        public long FinTimestamp => Volatile.Read(ref _finTimestamp);

        public TimeSpan TiempoEjecucion
        {
            get
            {
                long inicio = Volatile.Read(ref _inicioTimestamp);
                if (inicio == 0)
                    return TimeSpan.Zero;

                long fin = FinTimestamp;
                return _reloj.GetElapsedTime(inicio, fin == 0 ? _reloj.GetTimestamp() : fin);
            }
        }

        public TimeSpan? TiempoSinSalida
        {
            get
            {
                if (Estado != EstadoPeticionEnum.Ejecutando)
                    return null;

                long referencia = Math.Max(Salida.UltimaEscritura, Volatile.Read(ref _inicioTimestamp));
                return _reloj.GetElapsedTime(referencia);
            }
        }

        public bool TryMarcarEjecutando()
        {
            lock (_candado)
            {
                if (Estado != EstadoPeticionEnum.Encolado)
                    return false;

                Volatile.Write(ref _inicioTimestamp, _reloj.GetTimestamp());
                Volatile.Write(ref _estado, (int)EstadoPeticionEnum.Ejecutando);
                return true;
            }
        }

        public void FijarAvisoEnCurso(Func<string?> fuente) => Volatile.Write(ref _avisoEnCurso, fuente);

        public void MarcarComandoEnviado() => _isComandoEnviado = true;

        public void MarcarEnvioIniciado() => _isEnvioIniciado = true;

        public bool TryFinalizar(EstadoPeticionEnum estado, int codigo, string? aviso)
        {
            if (!estado.IsFinal())
                throw new ArgumentException($"{estado} no es un estado final.", nameof(estado));

            lock (_candado)
            {
                if (IsFinalizada)
                    return false;

                FinalizarBajoCandado(estado, codigo, aviso);
            }

            _ = _finalizacion.TrySetResult();
            return true;
        }

        // Sin enviarla: la sesión vio que su conexión va a un destino de solo lectura.
        public bool TryRechazarSoloLectura(string? mensaje)
        {
            _isRechazadaSoloLectura = true;
            return TryFinalizar(EstadoPeticionEnum.Error, -1, mensaje);
        }

        public bool SolicitarCancelacion()
        {
            lock (_candado)
            {
                if (IsFinalizada)
                    return false;

                if (Estado == EstadoPeticionEnum.Encolado)
                {
                    FinalizarBajoCandado(EstadoPeticionEnum.Cancelado, 130, "Cancelada antes de empezar.");
                    _ = _finalizacion.TrySetResult();
                    return true;
                }

                _isCancelacionSolicitada = true;
            }

            // Fuera del candado y en el pool: Cancel ejecuta de forma síncrona los callbacks registrados, y con ellos la
            // continuación del bucle que interrumpe el comando, que espera (acotado) al candado de escritura del shell si lo
            // retiene una entrada atascada en un envío que el servidor no acepta: quien cancela (ssh_cancel, ssh_reset_shell)
            // no debe quedarse dentro.
            _ = ThreadPool.UnsafeQueueUserWorkItem(static interrupcion => interrupcion.Cancel(), _interrupcion, preferLocal: false);
            return true;
        }

        // La salida se cierra antes de publicar el estado final: quien ve el estado final lee ya la salida sin nada retenido
        // por si era el principio de un secreto (BufferSalida.Cerrar).
        private void FinalizarBajoCandado(EstadoPeticionEnum estado, int codigo, string? aviso)
        {
            Salida.Cerrar();
            Volatile.Write(ref _codigoSalida, codigo);
            Volatile.Write(ref _aviso, aviso);
            Volatile.Write(ref _avisoEnCurso, null);
            Volatile.Write(ref _finTimestamp, _reloj.GetTimestamp());
            Volatile.Write(ref _estado, (int)estado);
        }
    }
}
