using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NativeSsh.Test.Utilidades
{
    public sealed record EntradaLog(LogLevel Nivel, int Evento, string Mensaje);

    // Registra los mensajes. Con EventoBloqueante, el primer Log de ese evento avisa (Bloqueado) y se detiene hasta
    // Liberar (o hasta 10 s, y entonces IsEsperaAgotada): permite ensanchar de forma determinista la ventana que sigue a
    // ese punto del código. Quien llama debe hacerlo desde otro hilo que el de la prueba.
    public sealed class LoggerPrueba : ILogger, IDisposable
    {
        private static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(10);

        private readonly ConcurrentQueue<EntradaLog> _entradas = new();
        private readonly TaskCompletionSource _bloqueado = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _liberado = new(false);
        private int _isBloqueoUsado;
        private volatile bool _isEsperaAgotada;

        public int? EventoBloqueante { get; init; }
        public IReadOnlyCollection<EntradaLog> Entradas => _entradas;
        public Task Bloqueado => _bloqueado.Task;
        public bool IsEsperaAgotada => _isEsperaAgotada;

        public void Liberar() => _liberado.Set();

        public void Dispose() => _liberado.Dispose();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _entradas.Enqueue(new EntradaLog(logLevel, eventId.Id, formatter(state, exception)));
            if (eventId.Id != EventoBloqueante || Interlocked.Exchange(ref _isBloqueoUsado, 1) != 0)
                return;

            _ = _bloqueado.TrySetResult();
            _isEsperaAgotada = !_liberado.Wait(EsperaMaxima);
        }
    }
}
