using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NativeSsh.Comun
{
    // Lo de Linux de la resolución por familia (solo se compila para Linux: ver NativeSshSistema en Directory.Build.props):
    // getaddrinfo propio de una sola familia, que distingue «sin direcciones de esa familia» (EAI_NODATA;
    // Dns.GetHostAddresses lo convierte en HostNotFound, igual que un nombre que no existe). Medido con glibc, con
    // 'files dns' y con 'resolve [!UNAVAIL=return]' de systemd-resolved. Cada resolución (cada familia de una entrada y el
    // destino) va en un proceso hijo que se mata al vencer su plazo (ResolutorEnProceso): glibc no tiene plazo en la lectura
    // por TCP del DNS y un getaddrinfo en un hilo propio se quedaría colgado para siempre.
    public static partial class ResolucionPorFamilia
    {
        private static partial IPAddress[]? ResolverFamiliaDelSistema(string nombre, AddressFamily familia) => GetAddrInfoLinux.ResolverFamilia(nombre, familia, out _);

        private static partial Task<DireccionesPorFamilia> ResolverDelSistemaAsync(string nombre, CancellationToken plazo) =>
            ResolverAsync(nombre, ResolutorEnProceso.DelSistema.ResolverAsync, plazo);

        private static partial Task<ResultadoResolucion> ResolverDestinoDelSistemaAsync(string nombre, CancellationToken plazo) =>
            ResolutorEnProceso.DelSistema.ResolverAsync(nombre, AddressFamily.Unspecified, plazo);

        static partial void AnadirSinTerminarDelSistema(List<string> sinTerminar) => ResolutorEnProceso.DelSistema.AnadirSinTerminar(sinTerminar);

        static partial void EjecutarModoInternoDelSistema(string[] args, ref int codigo, ref bool isHecho)
        {
            if (args.Length == 0 || args[0] != ResolutorEnProceso.ModoInterno)
                return;

            isHecho = true;
            codigo = ResolutorEnProceso.EjecutarModoInterno(args);
        }

        public static partial string RutaHostsDelSistema => "/etc/hosts";
    }

    // getaddrinfo de una sola familia (LibraryImport, compatible con AOT).
    internal static partial class GetAddrInfoLinux
    {
        // Valores de glibc en Linux (iguales en todas las arquitecturas).
        public const int SinDireccionesDeLaFamilia = -5; // EAI_NODATA: el nombre existe y no tiene direcciones de esa familia
        public const int FamiliaIpv4 = 2; // AF_INET
        public const int FamiliaIpv6 = 10; // AF_INET6
        public const int FamiliasJuntas = 0; // AF_UNSPEC
        private const int Flujo = 1; // SOCK_STREAM

        // struct addrinfo de Linux: cuatro int, socklen_t (con el relleno de alineación antes del primer puntero) y tres punteros.
        [StructLayout(LayoutKind.Sequential)]
        internal struct InfoDireccion
        {
            public int Opciones;
            public int Familia;
            public int TipoSocket;
            public int Protocolo;
            public uint LongitudDireccion;
            public IntPtr Direccion;
            public IntPtr NombreCanonico;
            public IntPtr Siguiente;
        }

        // getaddrinfo de una familia (AF_INET o AF_INET6): su código y, si es 0, la lista de struct addrinfo.
        internal delegate int ObtenerDirecciones(string nombre, int familia, out IntPtr lista);

        // Las direcciones de 'nombre' de una sola familia (InterNetwork o InterNetworkV6; Unspecified, las dos juntas, como el
        // destino) con getaddrinfo, sin AI_ADDRCONFIG (la máquina no necesita tener esa familia configurada) ni nada mapeado a
        // IPv6. Vacío si getaddrinfo dice que el nombre no tiene direcciones de esa familia (EAI_NODATA); null con el código
        // de getaddrinfo en 'codigo' si falla de cualquier otra forma. Bloquea el hilo hasta que la resolución termina (no se
        // puede cancelar).
        public static IPAddress[]? ResolverFamilia(string nombre, AddressFamily familia, out int codigo) =>
            ResolverFamilia(nombre, familia, ObtenerConLibc, LiberarConLibc, out codigo);

        // Lo mismo con getaddrinfo y freeaddrinfo dados (las pruebas dan los suyos): la lista se libera siempre que se obtuvo.
        internal static unsafe IPAddress[]? ResolverFamilia(string nombre, AddressFamily familia, ObtenerDirecciones obtener, Action<IntPtr> liberar, out int codigo)
        {
            int pedida = familia == AddressFamily.InterNetworkV6 ? FamiliaIpv6 : familia == AddressFamily.Unspecified ? FamiliasJuntas : FamiliaIpv4;
            codigo = obtener(nombre, pedida, out IntPtr lista);
            if (codigo != 0)
                return codigo == SinDireccionesDeLaFamilia ? [] : null;

            try
            {
                List<IPAddress> direcciones = [];
                for (InfoDireccion* actual = (InfoDireccion*)lista; actual != null; actual = (InfoDireccion*)actual->Siguiente)
                    if (Leer(actual) is { } direccion && !direcciones.Contains(direccion))
                        direcciones.Add(direccion);
                return [.. direcciones];
            }
            finally
            {
                liberar(lista);
            }
        }

        private static unsafe int ObtenerConLibc(string nombre, int familia, out IntPtr lista)
        {
            InfoDireccion pistas = default;
            pistas.Familia = familia;
            pistas.TipoSocket = Flujo;
            InfoDireccion* resultado = null;
            int codigo = GetAddrInfo(nombre, null, &pistas, &resultado);
            lista = (IntPtr)resultado;
            return codigo;
        }

        private static unsafe void LiberarConLibc(IntPtr lista) => FreeAddrInfo((InfoDireccion*)lista);

        // sockaddr_in: la dirección en el byte 4 (4 bytes); sockaddr_in6: en el byte 8 (16 bytes). La zona no se lee.
        private static unsafe IPAddress? Leer(InfoDireccion* info)
        {
            byte* direccion = (byte*)info->Direccion;
            bool isIpv4 = info->Familia == FamiliaIpv4 && info->LongitudDireccion >= 8;
            bool isIpv6 = info->Familia == FamiliaIpv6 && info->LongitudDireccion >= 24;
            return direccion == null ? null
                : isIpv4 ? new IPAddress(new ReadOnlySpan<byte>(direccion + 4, 4))
                : isIpv6 ? new IPAddress(new ReadOnlySpan<byte>(direccion + 8, 16))
                : null;
        }

        [LibraryImport("libc", EntryPoint = "getaddrinfo", StringMarshalling = StringMarshalling.Utf8)]
        private static unsafe partial int GetAddrInfo(string nodo, string? servicio, InfoDireccion* pistas, InfoDireccion** resultado);

        [LibraryImport("libc", EntryPoint = "freeaddrinfo")]
        private static unsafe partial void FreeAddrInfo(InfoDireccion* resultado);
    }
}
