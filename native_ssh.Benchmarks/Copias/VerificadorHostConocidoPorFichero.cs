using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;
using NativeSsh.Ssh;

namespace NativeSsh.Benchmarks.Copias
{
    // Solo para medir (GeneracionesKnownHostsBenchmarks): el producto (VerificadorHostConocido.Verificar sobre una lista rentada
    // de líneas), pero fichero a fichero: una sola lista rentada, vaciada entre ficheros, para separar el efecto de cargar
    // todos los known_hosts a la vez del tipo de recorrido. Se combina como el producto: un @revoked de la clave en
    // cualquier fichero gana (el producto devuelve en cuanto lo ve, y lo anterior solo marca), después Conocido, después
    // ClaveDistinta. ObtenerTiposClave no se reparte así (un @revoked de un fichero anula claves de otro): no se mide.
    public sealed class VerificadorHostConocidoPorFichero(string[] rutas)
    {
        public string[] Rutas { get; } = rutas;

        public ResultadoHostEnum Verificar(string servidor, int puerto, string tipoClave, ReadOnlySpan<byte> clave)
        {
            ResultadoHostEnum total = ResultadoHostEnum.Desconocido;
            // Un 'using' no se puede pasar por ref: se devuelve en el finally.
            PooledListRef<string> lineas = new();
            try
            {
                foreach (string ruta in Rutas)
                {
                    if (!File.Exists(ruta))
                        continue;

                    lineas.Clear();
                    foreach (string linea in File.ReadLines(ruta))
                        lineas.Add(linea);
                    ResultadoHostEnum resultado = VerificadorHostConocido.Verificar(lineas.Span, servidor, puerto, tipoClave, clave);
                    if (resultado == ResultadoHostEnum.Revocado)
                        return resultado;
                    if (resultado == ResultadoHostEnum.Conocido || (resultado == ResultadoHostEnum.ClaveDistinta && total == ResultadoHostEnum.Desconocido))
                        total = resultado;
                }
            }
            finally
            {
                lineas.Dispose();
            }

            return total;
        }
    }
}
