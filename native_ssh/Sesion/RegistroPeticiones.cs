using System.Collections.Concurrent;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;
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

        public Peticion[] ListarRecientes()
        {
            // Values es una instantánea tomada bajo todos los candados del diccionario (el tipo ICollection lo impone su API).
            // CopyTo y no [.. _peticiones.Values], que se compila a Enumerable.ToArray.
            ICollection<Peticion> valores = _peticiones.Values;
            Peticion[] peticiones = new Peticion[valores.Count];
            valores.CopyTo(peticiones, 0);
            Array.Sort(peticiones, static (a, b) => b.CreadoTimestamp.CompareTo(a.CreadoTimestamp));
            return peticiones;
        }

        public void Podar()
        {
            long ahora = reloj.GetTimestamp();
            foreach ((string? id, Peticion? peticion) in _peticiones)
                if (peticion.IsFinalizada && reloj.GetElapsedTime(peticion.FinTimestamp, ahora) > opciones.RetencionPeticionesFinalizadas)
                    Quitar(id);

            int exceso = _peticiones.Count - opciones.MaxPeticionesRetenidas;
            if (exceso <= 0)
                return;

            using PooledListRef<Peticion> finalizadas = new();
            foreach (Peticion peticion in _peticiones.Values)
                if (peticion.IsFinalizada)
                    finalizadas.Add(peticion);

            finalizadas.Span.Sort(static (a, b) => a.FinTimestamp.CompareTo(b.FinTimestamp));
            int aQuitar = Math.Min(exceso, finalizadas.Tamaño);
            for (int i = 0; i < aQuitar; i++)
                Quitar(finalizadas[i].IdTexto);
        }

        private void Quitar(string id)
        {
            if (_peticiones.TryRemove(id, out Peticion? peticion))
                peticion.Salida.Liberar();
        }
    }
}
