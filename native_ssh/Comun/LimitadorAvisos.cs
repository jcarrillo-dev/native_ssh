namespace NativeSsh.Comun
{
    // Tope de avisos repetidos en el log (las peticiones HTTP rechazadas: con --http-unsecure, cualquiera de la red puede
    // provocar uno por petición): como mucho 'maximo' por ventana; los demás se cuentan y el recuento sale junto al siguiente
    // aviso que se registre (en una ventana posterior), no al terminar la ráfaga.
    public sealed class LimitadorAvisos
    {
        private readonly Lock _candado = new();
        private readonly long _ticksVentana;
        private readonly TimeProvider _reloj;
        private long _inicioVentana;
        private int _enVentana;
        private int _omitidos;

        public LimitadorAvisos(int maximo, TimeSpan ventana, TimeProvider reloj)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximo, 1);
            Maximo = maximo;
            _reloj = reloj;
            _ticksVentana = (long)(ventana.TotalSeconds * reloj.TimestampFrequency);
            _inicioVentana = reloj.GetTimestamp();
        }

        public int Maximo { get; }

        // true si este aviso se registra; omitidos: los que se callaron desde el último registrado.
        public bool TryRegistrar(out int omitidos)
        {
            lock (_candado)
            {
                long ahora = _reloj.GetTimestamp();
                if (ahora - _inicioVentana >= _ticksVentana)
                {
                    _inicioVentana = ahora;
                    _enVentana = 0;
                }

                if (_enVentana >= Maximo)
                {
                    _omitidos++;
                    omitidos = 0;
                    return false;
                }

                _enVentana++;
                omitidos = _omitidos;
                _omitidos = 0;
                return true;
            }
        }
    }
}
