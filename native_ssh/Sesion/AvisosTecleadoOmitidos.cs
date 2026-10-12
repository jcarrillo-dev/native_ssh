using NativeSsh.Protocolo;

namespace NativeSsh.Sesion
{
    // Avisos de líneas tecleadas descartadas que la compactación de la cadena soltó por el tope
    // (Peticion.CompactarAvisosTecleadoPendientes, más de Peticion.MaxAvisosTecleadoEnlazados pendientes): uno por sesión. Los
    // suma el bucle de la sesión y los da, resumidos y una sola vez, la primera respuesta de ssh_exec, ssh_input u ssh_output
    // de una petición de esa sesión (HerramientasSsh.DescribirAvisosOmitidos): así la respuesta sigue diciendo exactamente
    // cuántas líneas se descartaron, aunque no las detalle comando a comando.
    public sealed class AvisosTecleadoOmitidos
    {
        private readonly Lock _candado = new();
        private int _peticiones;
        private int _lineas;
        private bool _isAlMenos;

        public void Sumar(int lineas)
        {
            lock (_candado)
            {
                _peticiones = _peticiones == int.MaxValue ? int.MaxValue : _peticiones + 1;
                _lineas = (int)Math.Min((long)_lineas + lineas, MarcadorFin.MaxLineasDescartadas);
                // Una petición con el recuento en el tope del marcador (999999 o más) o una suma que lo alcanza: «o más».
                _isAlMenos |= lineas >= MarcadorFin.MaxLineasDescartadas || _lineas == MarcadorFin.MaxLineasDescartadas;
            }
        }

        public bool TryTomar(out int peticiones, out int lineas, out bool isAlMenos)
        {
            lock (_candado)
            {
                peticiones = _peticiones;
                lineas = _lineas;
                isAlMenos = _isAlMenos;
                _peticiones = 0;
                _lineas = 0;
                _isAlMenos = false;
            }

            return peticiones > 0;
        }
    }
}
