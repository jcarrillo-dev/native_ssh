using System.Collections.Concurrent;
using NativeSsh.Comun;

namespace NativeSsh.Sesion
{
    public sealed class RegistroPeticiones(OpcionesNativeSsh opciones, TimeProvider reloj)
    {
        private readonly ConcurrentDictionary<string, Peticion> _peticiones = new(StringComparer.Ordinal);

        public int Cantidad => _peticiones.Count;

        public void Registrar(Peticion peticion)
        {
            _peticiones[peticion.IdTexto] = peticion;
            Podar();
        }

        public Peticion? Buscar(string idTexto) => _peticiones.TryGetValue(idTexto, out Peticion? peticion) ? peticion : null;

        public IReadOnlyList<Peticion> ListarRecientes() => [.. _peticiones.Values.OrderByDescending(p => p.CreadoTimestamp)];

        public void Podar()
        {
            long ahora = reloj.GetTimestamp();
            foreach ((string? id, Peticion? peticion) in _peticiones)
            {
                if (peticion.IsFinalizada && reloj.GetElapsedTime(peticion.FinTimestamp, ahora) > opciones.RetencionPeticionesFinalizadas)
                    Quitar(id);
            }

            int exceso = _peticiones.Count - opciones.MaxPeticionesRetenidas;
            if (exceso <= 0)
                return;

            List<Peticion> sobrantes = [.. _peticiones.Values
                .Where(p => p.IsFinalizada)
                .OrderBy(p => p.FinTimestamp)
                .Take(exceso)];
            foreach (Peticion peticion in sobrantes)
                Quitar(peticion.IdTexto);
        }

        private void Quitar(string id)
        {
            if (_peticiones.TryRemove(id, out Peticion? peticion))
                peticion.Salida.Liberar();
        }
    }
}
