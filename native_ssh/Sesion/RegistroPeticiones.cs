using System.Collections.Concurrent;
using JCarrillo.AOT.Core.Extensiones.ValueLINQ;
using NativeSsh.Comun;
using NativeSsh.Protocolo;

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

        public IReadOnlyList<Peticion> ListarRecientes()
        {
            List<Peticion> peticiones = [.. _peticiones.Values];
            peticiones.Sort(static (a, b) => b.CreadoTimestamp.CompareTo(a.CreadoTimestamp));
            return peticiones;
        }

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

            List<Peticion> finalizadas = [];
            foreach (Peticion peticion in _peticiones.Values)
                if (peticion.IsFinalizada)
                    finalizadas.Add(peticion);

            finalizadas.Sort(static (a, b) => a.FinTimestamp.CompareTo(b.FinTimestamp));
            Peticion[] arrayFinalizadas = [.. finalizadas];
            foreach (Peticion peticion in arrayFinalizadas.ToValueQuery().Delay().Take(exceso))
                Quitar(peticion.IdTexto);
        }

        private void Quitar(string id)
        {
            if (_peticiones.TryRemove(id, out Peticion? peticion))
                peticion.Salida.Liberar();
        }
    }
}
