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
        private int _codigoSalida = -1;
        private string? _aviso;

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
        public TimeSpan Timeout { get; }
        public BufferSalida Salida { get; }
        public long CreadoTimestamp { get; }

        // Se escriben antes que el estado final (Volatile.Write): quien lee primero Estado ve valores coherentes.
        public int CodigoSalida => Volatile.Read(ref _codigoSalida);
        public string? Aviso => Volatile.Read(ref _aviso);
        public EstadoPeticionEnum Estado => (EstadoPeticionEnum)Volatile.Read(ref _estado);
        public bool IsFinalizada => Estado.IsFinal();
        public bool IsCancelacionSolicitada => _isCancelacionSolicitada;
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

            // Fuera del candado: Cancel ejecuta los callbacks registrados de forma síncrona.
            _interrupcion.Cancel();
            return true;
        }

        private void FinalizarBajoCandado(EstadoPeticionEnum estado, int codigo, string? aviso)
        {
            Volatile.Write(ref _codigoSalida, codigo);
            Volatile.Write(ref _aviso, aviso);
            Volatile.Write(ref _finTimestamp, _reloj.GetTimestamp());
            Volatile.Write(ref _estado, (int)estado);
        }
    }
}
