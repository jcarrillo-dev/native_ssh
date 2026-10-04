using NativeSsh.Comun;

namespace NativeSsh.Sesion
{
    // Protege el cupo de 'ufw limit' del servidor: cada TCP nuevo (incluso fallido) cuenta.
    public sealed class LimitadorConexiones(OpcionesNativeSsh opciones, TimeProvider reloj)
    {
        private const int MaxSegundosEspera = 60;

        private readonly Lock _candado = new();
        private readonly Dictionary<string, EstadoDestino> _estados = new(StringComparer.Ordinal);

        public bool TryReservar(string clave, out TimeSpan espera)
        {
            lock (_candado)
            {
                long ahora = reloj.GetTimestamp();
                EstadoDestino estado = Obtener(clave);
                if (estado.BloqueadoHasta > ahora)
                {
                    espera = reloj.GetElapsedTime(ahora, estado.BloqueadoHasta);
                    return false;
                }

                while (estado.Intentos.Count > 0 && reloj.GetElapsedTime(estado.Intentos.Peek(), ahora) >= opciones.VentanaConexiones)
                    _ = estado.Intentos.Dequeue();

                if (estado.Intentos.Count >= opciones.MaxConexionesPorVentana)
                {
                    espera = opciones.VentanaConexiones - reloj.GetElapsedTime(estado.Intentos.Peek(), ahora);
                    return false;
                }

                estado.Intentos.Enqueue(ahora);
                espera = TimeSpan.Zero;
                return true;
            }
        }

        public void RegistrarFallo(string clave)
        {
            lock (_candado)
            {
                EstadoDestino estado = Obtener(clave);
                estado.FallosSeguidos++;
                int segundos = Math.Min(MaxSegundosEspera, 1 << Math.Min(estado.FallosSeguidos, 6));
                estado.BloqueadoHasta = reloj.GetTimestamp() + (segundos * reloj.TimestampFrequency);
            }
        }

        public void RegistrarExito(string clave)
        {
            lock (_candado)
            {
                EstadoDestino estado = Obtener(clave);
                estado.FallosSeguidos = 0;
                estado.BloqueadoHasta = 0;
            }
        }

        private EstadoDestino Obtener(string clave)
        {
            if (!_estados.TryGetValue(clave, out EstadoDestino? estado))
            {
                estado = new EstadoDestino();
                _estados[clave] = estado;
            }

            return estado;
        }

        private sealed class EstadoDestino
        {
            public Queue<long> Intentos { get; } = new();
            public int FallosSeguidos { get; set; }
            public long BloqueadoHasta { get; set; }
        }
    }
}
