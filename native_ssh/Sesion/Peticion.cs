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
        private volatile bool _isEnvioIniciado;
        private int _isAvisoTecleadoTomado;
        private int _lineasTecleadoDescartadas;
        private volatile string? _idComandoQueNoLeyo;
        private volatile Peticion? _anteriorConTecleadoDescartado;
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

        // La de una herramienta de archivo o de ssh_set_cwd por el shell: su id solo lo ve el LLM si la herramienta falla, así
        // que los avisos de otras respuestas no lo citan (HerramientasSsh.Resultado, SesionShell.EjecutarAsync).
        public bool IsHerramienta { get; init; }

        // Con ssh_exec y guardarSalida=true: el nonce de las marcas que el envoltorio deja en el terminal (SalidaGuardada). La
        // sesión envuelve el comando al enviarlo; Comando sigue siendo el que pidió el LLM (ssh_status, avisos de antipatrón).
        public Guid? MarcaGuardado { get; init; }
        public string IdTexto { get; }
        public DestinoSsh Destino { get; }
        public string Comando { get; }
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
        // Líneas tecleadas con ssh_input que ningún comando leyó y que la cola anterior descartó al llegar el envío de este
        // comando (ConstructorComando.EscribirCola): las que no leyó el comando anterior del mismo shell. Se fija antes del
        // estado final, que lo avisa.
        public int LineasTecleadoDescartadas => Volatile.Read(ref _lineasTecleadoDescartadas);

        // Id del comando anterior del mismo shell (el que no leyó esas líneas), si se conoce.
        public string? IdComandoQueNoLeyo => _idComandoQueNoLeyo;

        // La última petición del mismo shell que trajo líneas tecleadas descartadas y cuyo aviso no había dado aún ninguna
        // respuesta cuando empezó esta (su final no se ha leído, o es la de una herramienta, cuya respuesta no da avisos): si
        // sigue sin darlo, lo da la de esta (HerramientasSsh.Resultado), junto con el de las anteriores enlazadas igual.
        public Peticion? AnteriorConTecleadoDescartado => _anteriorConTecleadoDescartado;

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

        // true una sola vez, y solo si hay líneas descartadas: quien lo recibe da el aviso (el final de esta petición o la
        // respuesta del comando siguiente, lo que llegue antes); nadie más lo repite.
        public bool IsAvisoTecleadoTomado => Volatile.Read(ref _isAvisoTecleadoTomado) == 1;

        public bool TryTomarAvisoTecleado() => LineasTecleadoDescartadas > 0 && Interlocked.Exchange(ref _isAvisoTecleadoTomado, 1) == 0;

        public void FijarLineasTecleadoDescartadas(int lineas, string? idComandoQueNoLeyo)
        {
            _idComandoQueNoLeyo = idComandoQueNoLeyo;
            Volatile.Write(ref _lineasTecleadoDescartadas, lineas);
        }

        // Lo fija el bucle de la sesión antes de enviar el comando, y lo rehace al compactar la cadena
        // (CompactarAvisosTecleadoPendientes); lo lee quien construye las respuestas, en otro hilo.
        public void FijarAnteriorConTecleadoDescartado(Peticion? anterior) => _anteriorConTecleadoDescartado = anterior;

        // Lo llama la respuesta que ya recorrió la cadena (HerramientasSsh.Resultado): suelta el enlace solo si sigue siendo el
        // que recorrió, para no pisar el que el bucle fije después.
        public void SoltarAnteriorConTecleadoDescartado(Peticion recorrida) => _ = Interlocked.CompareExchange(ref _anteriorConTecleadoDescartado, null, recorrida);

        // true si trajo líneas tecleadas descartadas y ninguna respuesta ha dado aún su aviso.
        public bool IsAvisoTecleadoPendiente => LineasTecleadoDescartadas > 0 && !IsAvisoTecleadoTomado;

        // Tope de peticiones con el aviso pendiente que se enlazan (CompactarAvisosTecleadoPendientes) y que recorre una
        // respuesta (HerramientasSsh.AvisosTecleadoDeAnteriores). Cada respuesta de ssh_exec, ssh_input y ssh_output da los
        // avisos de las enlazadas, así que con un uso normal quedan una o dos; si se pasara, los avisos más antiguos no se
        // detallan, pero se cuentan (AvisosTecleadoOmitidos) y la respuesta siguiente dice cuántos comandos y cuántas líneas
        // fueron (antes la cadena crecía sin límite y su recorrido recursivo desbordaba la pila del servidor).
        public const int MaxAvisosTecleadoEnlazados = 16;

        // Los avisos omitidos de la sesión de esta petición (los fija el bucle de la sesión al enlazarla): los da la primera
        // respuesta que llega (HerramientasSsh.Resultado).
        public AvisosTecleadoOmitidos? AvisosTecleadoOmitidos { get; private set; }

        public void FijarAvisosTecleadoOmitidos(AvisosTecleadoOmitidos omitidos) => AvisosTecleadoOmitidos = omitidos;

        // Solo el bucle de la sesión, antes de enlazar la petición siguiente a 'ultima'. Deja la cadena que empieza en 'ultima'
        // solo con las peticiones cuyo aviso sigue pendiente, en el mismo orden y como mucho MaxAvisosTecleadoEnlazados, y
        // devuelve su cabeza (null si no queda ninguna): las ya avisadas no se retienen ni se recorren otra vez. Una dada puede
        // tener detrás una pendiente (el error de una herramienta da el aviso suyo sin recorrer los anteriores), por eso no basta
        // con mirar la cabeza. Las pendientes que no caben (las más antiguas) se dan por tomadas y se suman a 'omitidos', que
        // la respuesta siguiente resume: nadie las repite ni se pierden en silencio. Entre dos llamadas solo se añade una
        // cabeza, así que el recorrido es corto (como mucho una de más); el tope de visitas es defensa. Quien recorre a la vez
        // la cadena ve el enlace viejo o el nuevo: los dos llevan a pendientes, y la que se toma aquí ya no la da.
        internal static Peticion? CompactarAvisosTecleadoPendientes(Peticion? ultima, AvisosTecleadoOmitidos? omitidos = null)
        {
            Peticion? cabeza = null;
            Peticion? previa = null;
            int enlazadas = 0;
            for (int visitadas = 0; ultima is not null && visitadas < 4 * MaxAvisosTecleadoEnlazados; visitadas++)
            {
                Peticion actual = ultima;
                ultima = actual.AnteriorConTecleadoDescartado;
                if (!actual.IsAvisoTecleadoPendiente)
                    continue;

                if (enlazadas == MaxAvisosTecleadoEnlazados)
                {
                    if (omitidos is not null && actual.TryTomarAvisoTecleado())
                        omitidos.Sumar(actual.LineasTecleadoDescartadas);
                    continue;
                }

                if (previa is null)
                    cabeza = actual;
                else
                    previa.FijarAnteriorConTecleadoDescartado(actual);
                previa = actual;
                enlazadas++;
            }

            previa?.FijarAnteriorConTecleadoDescartado(null);
            return cabeza;
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
