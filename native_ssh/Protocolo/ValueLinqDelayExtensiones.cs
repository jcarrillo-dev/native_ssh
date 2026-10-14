using System.Runtime.CompilerServices;
using JCarrillo.AOT.Core.ValueLINQ.Delay;
using JCarrillo.AOT.Core.ValueLINQ.Interfaces;

namespace NativeSsh.Protocolo
{
    public ref struct ValueTakeDelay<T, TEnumerator>(TEnumerator enumerator, int count) : IValueLINQEnumerator<T>
        where T : allows ref struct
        where TEnumerator : IValueLINQEnumerator<T>, allows ref struct
    {
        private TEnumerator _enumerator = enumerator;
        private int _remaining = count > 0 ? count : 0;

        public ref readonly T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _enumerator.Current;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (_remaining > 0 && _enumerator.MoveNext())
            {
                _remaining--;
                return true;
            }

            Dispose();
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            if (_remaining < 0)
                return;

            _remaining = -1;
            _enumerator.Dispose();
            _enumerator = default!;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ValueTakeDelay<T, TEnumerator> GetEnumerator() => this;
    }

    public static class ValueLinqDelayExtensiones
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ValueTakeDelay<T, TEnumerator> Take<T, TEnumerator>(
            this ValueLINQDelayStruct<T, TEnumerator> query,
            int count)
            where T : allows ref struct
            where TEnumerator : IValueLINQEnumerator<T>, allows ref struct =>
            new(query.GetEnumerator(), count);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Any<T, TEnumerator>(
            this ValueTakeDelay<T, TEnumerator> enumerator)
            where T : allows ref struct
            where TEnumerator : IValueLINQEnumerator<T>, allows ref struct
        {
            bool has = enumerator.MoveNext();
            enumerator.Dispose();
            return has;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Any<T, TEnumerator>(
            this ValueLINQDelayStruct<T, TEnumerator> query)
            where T : allows ref struct
            where TEnumerator : IValueLINQEnumerator<T>, allows ref struct
        {
            TEnumerator enumerator = query.GetEnumerator();
            bool has = enumerator.MoveNext();
            enumerator.Dispose();
            return has;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Any<T, TEnumerator, TPredicate, TState>(
            this ValueLINQDelayStruct<T, TEnumerator> query,
            TState state,
            scoped ref TPredicate predicate)
            where T : allows ref struct
            where TEnumerator : IValueLINQEnumerator<T>, allows ref struct
            where TPredicate : struct, IWhereDelegado<T, TState>, allows ref struct
            where TState : allows ref struct
        {
            TEnumerator enumerator = query.GetEnumerator();
            while (enumerator.MoveNext())
                if (predicate.Ejecutar(enumerator.Current, state))
                {
                    enumerator.Dispose();
                    return true;
                }

            enumerator.Dispose();
            return false;
        }
    }
}
