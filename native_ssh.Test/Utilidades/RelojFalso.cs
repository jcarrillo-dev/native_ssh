namespace NativeSsh.Test.Utilidades
{
    /// Reloj manual: 1 tick de timestamp = 1 tick de TimeSpan.
    public sealed class RelojFalso : TimeProvider
    {
        private static readonly DateTimeOffset Origen = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _ticks = 1;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public override DateTimeOffset GetUtcNow() => Origen + TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

        public void Avanzar(TimeSpan intervalo) => Interlocked.Add(ref _ticks, intervalo.Ticks);
    }
}
