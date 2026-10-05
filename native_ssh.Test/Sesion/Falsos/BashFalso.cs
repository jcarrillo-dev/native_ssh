using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Sesion.Falsos
{
    public enum ComportamientoFalsoEnum
    {
        Normal,
        Colgado,
        IgnoraCtrlC,
        PideEntrada,
        Salir
    }

    // Cómo se comporta el shell de login antes de ejecutar el 'exec bash' de la inicialización.
    public enum LoginFalsoEnum
    {
        // Ejecuta cada línea en cuanto llega.
        Instantaneo,

        // Como bash -l con readline: tarda en arrancar y lee la línea del exec con el TTY en modo crudo.
        // Al volver a modo canónico el núcleo junta todo lo pendiente en una sola "línea", así que el bash
        // nuevo lee de una vez todas las líneas recibidas durante el login y las guarda en su búfer interno:
        // el 'read -r __ns_d' de la primera ya no las ve y consume la siguiente línea que llegue.
        LentoConReadline,

        // El login se traga todo lo recibido mientras arranca (la inicialización completa).
        ComeInicializacion
    }

    public sealed record RespuestaFalsa(string Salida, int Codigo = 0, ComportamientoFalsoEnum Comportamiento = ComportamientoFalsoEnum.Normal);

    public sealed class FabricaFalsa(Func<string, RespuestaFalsa> guion, int tamanoTrozo = 7, LoginFalsoEnum login = LoginFalsoEnum.Instantaneo, TimeSpan retrasoLogin = default) : IFabricaConexion
    {
        private int _conexiones;

        public int Conexiones => Volatile.Read(ref _conexiones);
        public ConexionFalsa? Ultima { get; private set; }
        public Exception? ErrorAlConectar { get; set; }

        // Ensancha la ventana de carrera entre conexiones simultáneas.
        public TimeSpan RetrasoConexion { get; set; }

        // Si no es null, cada canal exec espera a que se libere antes de responder (para medir la concurrencia).
        public SemaphoreSlim? BloqueoExec { get; set; }

        public async Task<IConexionSsh> ConectarAsync(DestinoSsh destino, CancellationToken cancellationToken)
        {
            if (ErrorAlConectar is { } error)
                throw error;

            if (RetrasoConexion > TimeSpan.Zero)
                await Task.Delay(RetrasoConexion, cancellationToken);

            _ = Interlocked.Increment(ref _conexiones);
            ConexionFalsa conexion = new(guion, tamanoTrozo, login, retrasoLogin) { BloqueoExec = BloqueoExec };
            Ultima = conexion;
            return conexion;
        }
    }

    public sealed class ConexionFalsa(Func<string, RespuestaFalsa> guion, int tamanoTrozo, LoginFalsoEnum login = LoginFalsoEnum.Instantaneo, TimeSpan retrasoLogin = default) : IConexionSsh
    {
        private int _shellsAbiertos;
        private int _execActivos;
        private int _maxExecSimultaneos;
        private volatile bool _isConectada = true;

        public bool IsConectada => _isConectada;
        public int ShellsAbiertos => Volatile.Read(ref _shellsAbiertos);
        public int SigintsRecibidos => CanalActual?.SigintsRecibidos ?? 0;

        // Comandos ejecutados por el shell PTY y scripts de los canales exec (decodificados).
        public ConcurrentQueue<string> Comandos { get; } = new();

        // Solo los comandos de canal exec, tal cual llegan (sin decodificar).
        public ConcurrentQueue<string> ComandosExec { get; } = new();
        public int ExecActivos => Volatile.Read(ref _execActivos);
        public int MaxExecSimultaneos => Volatile.Read(ref _maxExecSimultaneos);
        public SemaphoreSlim? BloqueoExec { get; init; }
        public CanalFalso? CanalActual { get; private set; }

        // Canal exec: el guion recibe el script decodificado. Las líneas de error ("Error:", "grep:", "rg:") van a stderr.
        public async Task<ResultadoCanalExec> EjecutarExecAsync(string comando, int maxBytesSalida, CancellationToken cancellationToken)
        {
            if (!_isConectada)
                throw new InvalidOperationException("Conexión cerrada.");

            string script = ScriptExec.Decodificar(comando);
            ComandosExec.Enqueue(comando);
            Comandos.Enqueue(script);
            int activos = Interlocked.Increment(ref _execActivos);
            int maximo;
            while (activos > (maximo = Volatile.Read(ref _maxExecSimultaneos)) && Interlocked.CompareExchange(ref _maxExecSimultaneos, activos, maximo) != maximo)
            {
            }

            try
            {
                if (BloqueoExec is { } bloqueo)
                {
                    await bloqueo.WaitAsync(cancellationToken);
                    _ = bloqueo.Release();
                }

                RespuestaFalsa respuesta = guion(script);
                if (respuesta.Comportamiento != ComportamientoFalsoEnum.Normal)
                    await Task.Delay(Timeout.Infinite, cancellationToken);

                StringBuilder salida = new();
                StringBuilder errores = new();
                foreach (string linea in respuesta.Salida.Split('\n'))
                {
                    bool isError = linea.StartsWith("Error:", StringComparison.Ordinal) || linea.StartsWith("grep:", StringComparison.Ordinal) || linea.StartsWith("rg:", StringComparison.Ordinal);
                    _ = (isError ? errores : salida).Append(linea).Append('\n');
                }

                byte[] bytesSalida = Encoding.UTF8.GetBytes(salida.ToString(0, Math.Max(0, salida.Length - 1)));
                byte[] bytesErrores = Encoding.UTF8.GetBytes(errores.ToString());
                return bytesSalida.Length + bytesErrores.Length > maxBytesSalida
                    ? new ResultadoCanalExec(null, null, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, true)
                    : new ResultadoCanalExec(respuesta.Codigo, null, bytesSalida, bytesErrores, false);
            }
            finally
            {
                _ = Interlocked.Decrement(ref _execActivos);
            }
        }

        public ICanalShell AbrirShell()
        {
            if (!_isConectada)
                throw new InvalidOperationException("Conexión cerrada.");

            _ = Interlocked.Increment(ref _shellsAbiertos);
            CanalActual = new CanalFalso(guion, tamanoTrozo, Comandos, login, retrasoLogin);
            return CanalActual;
        }

        // Simula una caída de red: la conexión deja de estar activa y el canal se cierra.
        public void Caer()
        {
            _isConectada = false;
            CanalActual?.Cerrar();
        }

        public void Dispose() => Caer();
    }

    public sealed class CanalFalso : ICanalShell
    {
        private readonly BlockingCollection<byte[]> _salida = [];
        private readonly Lock _candado = new();
        private readonly List<byte> _linea = [];
        private readonly StringBuilder _base64 = new();
        private readonly Func<string, RespuestaFalsa> _guion;
        private readonly int _tamanoTrozo;
        private readonly ConcurrentQueue<string> _comandos;
        private readonly Queue<string> _bufferInterno = new();
        private readonly LoginFalsoEnum _login;
        private readonly Timer? _finLogin;
        private byte[]? _trozoActual;
        private int _desplazamiento;
        private RespuestaFalsa? _enCurso;
        private string? _idEnCurso;
        private int _sigints;
        private int _marcadoresInicializacion;
        private int _sincronizaciones;
        private bool _isEnLogin;
        private bool _isReadPendiente;

        public CanalFalso(Func<string, RespuestaFalsa> guion, int tamanoTrozo, ConcurrentQueue<string> comandos, LoginFalsoEnum login = LoginFalsoEnum.Instantaneo, TimeSpan retrasoLogin = default)
        {
            _guion = guion;
            _tamanoTrozo = tamanoTrozo;
            _comandos = comandos;
            _login = login;
            Emitir("Bienvenido al servidor de pruebas\r\n");
            if (login == LoginFalsoEnum.Instantaneo)
                return;

            _isEnLogin = true;
            _finLogin = new Timer(_ => TerminarLogin(), null, retrasoLogin, Timeout.InfiniteTimeSpan);
        }

        public int MarcadoresInicializacion => Volatile.Read(ref _marcadoresInicializacion);
        public ConcurrentQueue<string> Inicializaciones { get; } = new();
        public int Sincronizaciones => Volatile.Read(ref _sincronizaciones);

        public bool IsCerrado => _salida.IsAddingCompleted;
        public int SigintsRecibidos => Volatile.Read(ref _sigints);

        public int Leer(Span<byte> destino)
        {
            if (_trozoActual is null)
            {
                if (!_salida.TryTake(out byte[]? trozo, Timeout.Infinite))
                    return 0;

                _trozoActual = trozo;
                _desplazamiento = 0;
            }

            int cantidad = Math.Min(destino.Length, _trozoActual.Length - _desplazamiento);
            _trozoActual.AsSpan(_desplazamiento, cantidad).CopyTo(destino);
            _desplazamiento += cantidad;
            if (_desplazamiento == _trozoActual.Length)
                _trozoActual = null;
            return cantidad;
        }

        public void Escribir(ReadOnlySpan<byte> datos)
        {
            lock (_candado)
            {
                if (IsCerrado)
                    throw new InvalidOperationException("Canal cerrado.");

                foreach (byte actual in datos)
                {
                    if (IsCerrado)
                        return;

                    if (actual == 0x03)
                    {
                        _ = Interlocked.Increment(ref _sigints);
                        CtrlC();
                    }
                    else if (actual == (byte)'\n')
                    {
                        ProcesarLinea(Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_linea)));
                        _linea.Clear();
                    }
                    else
                    {
                        _linea.Add(actual);
                    }
                }
            }
        }

        public void Cerrar()
        {
            lock (_candado)
            {
                if (!IsCerrado)
                    _salida.CompleteAdding();
            }
        }

        public void Dispose()
        {
            _finLogin?.Dispose();
            Cerrar();
        }

        private void TerminarLogin()
        {
            lock (_candado)
            {
                if (IsCerrado || !_isEnLogin)
                    return;

                _isEnLogin = false;
                if (_login == LoginFalsoEnum.ComeInicializacion)
                {
                    _bufferInterno.Clear();
                    return;
                }

                // readline del login lee y ejecuta la línea del exec; el resto queda en el búfer del bash nuevo.
                _ = _bufferInterno.TryDequeue(out _);
                Drenar();
            }
        }

        // Ejecuta lo que bash ya tiene en su búfer interno mientras ningún 'read' ni comando espere entrada.
        private void Drenar()
        {
            while (!_isReadPendiente && _enCurso is null && _bufferInterno.TryDequeue(out string? linea))
                EjecutarLinea(linea);
        }

        private void CtrlC()
        {
            if (_enCurso is null || _enCurso.Comportamiento == ComportamientoFalsoEnum.IgnoraCtrlC)
                return;

            _enCurso = null;
            _idEnCurso = null;
        }

        // Línea recibida por el canal (lo que el TTY entrega al shell).
        private void ProcesarLinea(string linea)
        {
            if (_isEnLogin)
            {
                _bufferInterno.Enqueue(linea);
                return;
            }

            if (_enCurso is not null)
            {
                if (_enCurso.Comportamiento == ComportamientoFalsoEnum.PideEntrada)
                {
                    _enCurso = null;
                    EmitirConMarcador($"recibido:{linea}\n", _idEnCurso!, 0);
                    _idEnCurso = null;
                }

                Drenar();
                return;
            }

            // Cada marcador va seguido de 'read -r __ns_d': la siguiente línea es un dato, no un comando.
            if (_isReadPendiente)
            {
                _isReadPendiente = false;
                Drenar();
                return;
            }

            _bufferInterno.Enqueue(linea);
            Drenar();
        }

        private void EjecutarLinea(string linea)
        {
            if (linea.StartsWith("PS1=", StringComparison.Ordinal))
            {
                Inicializaciones.Enqueue(linea);
                _ = Interlocked.Increment(ref _marcadoresInicializacion);
                EmitirConMarcador("", ExtraerId(linea), 0);
            }
            else if (linea.StartsWith("__ns_rc=0;", StringComparison.Ordinal))
            {
                _ = Interlocked.Increment(ref _sincronizaciones);
                EmitirConMarcador("", ExtraerId(linea), 0);
            }
            else if (linea.StartsWith("__ns_b='", StringComparison.Ordinal))
            {
                _ = _base64.Clear();
                _ = _base64.Append(linea, 8, linea.Length - 9);
            }
            else if (linea.StartsWith("__ns_b+='", StringComparison.Ordinal))
            {
                _ = _base64.Append(linea, 9, linea.Length - 10);
            }
            else if (linea.StartsWith("eval ", StringComparison.Ordinal))
            {
                string comando = Encoding.UTF8.GetString(Convert.FromBase64String(_base64.ToString()));
                Ejecutar(comando, ExtraerId(linea));
            }
            else if (linea.StartsWith("__ns_rc=130", StringComparison.Ordinal))
            {
                EmitirConMarcador("", ExtraerId(linea), 130);
            }
        }

        private void Ejecutar(string comando, string id)
        {
            _comandos.Enqueue(comando);
            RespuestaFalsa respuesta = _guion(comando);
            switch (respuesta.Comportamiento)
            {
                case ComportamientoFalsoEnum.Normal:
                    EmitirConMarcador(respuesta.Salida, id, respuesta.Codigo);
                    break;
                case ComportamientoFalsoEnum.Salir:
                    Cerrar();
                    break;
                case ComportamientoFalsoEnum.Colgado:
                case ComportamientoFalsoEnum.IgnoraCtrlC:
                case ComportamientoFalsoEnum.PideEntrada:
                    _enCurso = respuesta;
                    _idEnCurso = id;
                    Emitir(respuesta.Salida);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(comando), respuesta.Comportamiento, null);
            }
        }

        private void EmitirConMarcador(string salida, string id, int codigo)
        {
            _isReadPendiente = true;
            Emitir($"{salida}__NSX_{id}_{codigo}__\n");
        }

        private void Emitir(string texto)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(texto);
            for (int i = 0; i < bytes.Length && !IsCerrado; i += _tamanoTrozo)
                _salida.Add(bytes[i..Math.Min(bytes.Length, i + _tamanoTrozo)]);
        }

        private static string ExtraerId(string linea)
        {
            int inicio = linea.IndexOf("'__NSX' '", StringComparison.Ordinal) + 9;
            return linea.Substring(inicio, 32);
        }
    }
}
