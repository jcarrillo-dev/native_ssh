using System.Net;
using System.Net.Sockets;

namespace NativeSsh.Comun
{
    // Lo de macOS y los demás Unix de la resolución por familia (solo se compila para ellos: ver NativeSshSistema en
    // Directory.Build.props): cada familia con el DNS de .NET (Dns.GetHostAddresses(nombre, familia)). .NET junta «sin
    // direcciones de esa familia» (EAI_NODATA) con «el nombre no existe», así que no se distingue: cualquier error sale y
    // hace fallar esa familia (falla cerrado; un host del DNS solo IPv4 o solo IPv6 bloquea salvo que esté en /etc/hosts).
    public static partial class ResolucionPorFamilia
    {
        // Las familias que el resolver del sistema aún no ha terminado de resolver (ver FamiliasEnCurso): aquí la resolución
        // va en hilos del grupo y se cuenta con que el resolver del sistema se rinda solo (no medido en estos sistemas), así
        // que una familia que sigue en curso se espera en lugar de lanzarla otra vez.
        private static readonly FamiliasEnCurso EnCursoDelSistema = new();

        private static partial Task<DireccionesPorFamilia> ResolverDelSistemaAsync(string nombre, CancellationToken plazo) =>
            ResolverAsync(nombre, ResolverFamiliaDelSistema, EnCursoDelSistema, plazo);

        private static partial Task<ResultadoResolucion> ResolverDestinoDelSistemaAsync(string nombre, CancellationToken plazo) => ResolverDestinoConNetAsync(nombre, plazo);

        private static partial IPAddress[]? ResolverFamiliaDelSistema(string nombre, AddressFamily familia) => ResolverFamiliaUnix(nombre, familia, Dns.GetHostAddresses);

        public static partial string RutaHostsDelSistema => "/etc/hosts";

        // dns: el resolver de una familia (las pruebas dan el suyo).
        internal static IPAddress[] ResolverFamiliaUnix(string nombre, AddressFamily familia, Func<string, AddressFamily, IPAddress[]> dns) =>
            DeLaFamilia(dns(nombre, familia), familia);
    }
}
