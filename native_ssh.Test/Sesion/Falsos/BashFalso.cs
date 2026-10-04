using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using NativeSsh.Comun;
using NativeSsh.Sesion;

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

    public sealed record RespuestaFalsa(string Salida, int Codigo = 0, ComportamientoFalsoEnum Comportamiento = ComportamientoFalsoEnum.Normal);

    public sealed class FabricaFalsa(Func<string, RespuestaFalsa> guion, int tamanoTrozo = 7) : IFabricaConexion
    {
        private int _conexiones;

        public int Conexiones => Volatile.Read(ref _conexiones);
        public ConexionFalsa? Ultima { get; private set; }
        public Exception? ErrorAlConectar { get; set; }

        public Task<IConexionSsh> ConectarAsync(DestinoSsh destino, CancellationToken cancellationToken)
        {
            if (ErrorAlConectar is { } error)
                return Task.FromException<IConexionSsh>(error);

            _ = Interlocked.Increment(ref _conexiones);
            ConexionFalsa conexion = new(guion, tamanoTrozo);
            Ultima = conexion;
            return Task.FromResult<IConexionSsh>(conexion);
        }
    }

    public sealed class ConexionFalsa(Func<string, RespuestaFalsa> guion, int tamanoTrozo) : IConexionSsh
    {
        private int _shellsAbiertos;
        private volatile bool _isConectada = true;

        public bool IsConectada => _isConectada;
        public int ShellsAbiertos => Volatile.Read(ref _shellsAbiertos);
        public int SigintsRecibidos => CanalActual?.SigintsRecibidos ?? 0;
        public ConcurrentQueue<string> Comandos { get; } = new();
        public CanalFalso? CanalActual { get; private set; }

        public ICanalShell AbrirShell()
        {
            if (!_isConectada)
                throw new InvalidOperationException("Conexión cerrada.");

            _ = Interlocked.Increment(ref _shellsAbiertos);
            CanalActual = new CanalFalso(guion, tamanoTrozo, Comandos);
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
        private byte[]? _trozoActual;
        private int _desplazamiento;
        private RespuestaFalsa? _enCurso;
        private string? _idEnCurso;
        private int _sigints;

        public CanalFalso(Func<string, RespuestaFalsa> guion, int tamanoTrozo, ConcurrentQueue<string> comandos)
        {
            _guion = guion;
            _tamanoTrozo = tamanoTrozo;
            _comandos = comandos;
            Emitir("Bienvenido al servidor de pruebas\r\n");
        }

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

        public void Dispose() => Cerrar();

        private void CtrlC()
        {
            if (_enCurso is null || _enCurso.Comportamiento == ComportamientoFalsoEnum.IgnoraCtrlC)
                return;

            _enCurso = null;
            _idEnCurso = null;
        }

        private void ProcesarLinea(string linea)
        {
            if (_enCurso is not null)
            {
                if (_enCurso.Comportamiento == ComportamientoFalsoEnum.PideEntrada)
                {
                    Emitir($"recibido:{linea}\n" + Marcador(_idEnCurso!, 0));
                    _enCurso = null;
                    _idEnCurso = null;
                }

                return;
            }

            if (linea.StartsWith("PS1=", StringComparison.Ordinal))
            {
                Emitir(Marcador(ExtraerId(linea), 0));
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
                Emitir(Marcador(ExtraerId(linea), 130));
            }
        }

        private void Ejecutar(string comando, string id)
        {
            _comandos.Enqueue(comando);
            RespuestaFalsa respuesta = _guion(comando);
            switch (respuesta.Comportamiento)
            {
                case ComportamientoFalsoEnum.Normal:
                    Emitir(respuesta.Salida + Marcador(id, respuesta.Codigo));
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

        private static string Marcador(string id, int codigo) => $"__NSX_{id}_{codigo}__\n";
    }
}
