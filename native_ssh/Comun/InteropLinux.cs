using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NativeSsh.Comun
{
    // Llamadas a libc que .NET no expone: getaddrinfo de una sola familia que distinga «sin direcciones de esa familia»
    // (EAI_NODATA; Dns.GetHostAddressesAsync lo convierte en HostNotFound, igual que un nombre que no existe), abrir sin
    // seguir un enlace ni bloquearse (O_NOFOLLOW | O_NONBLOCK) y leer el tipo, el dueño y los permisos del descriptor ya
    // abierto (statx), para comprobar exactamente lo que se va a leer. statx tiene la misma disposición en todas las
    // arquitecturas de Linux (struct stat no); los valores de O_NOFOLLOW cambian.
    [SupportedOSPlatform("linux")]
    internal static partial class InteropLinux
    {
        public const int ErrorEnlace = 40; // ELOOP
        public const int ErrorNoExiste = 2; // ENOENT

        private const int SoloLectura = 0; // O_RDONLY
        private const int SinBloqueo = 0x800; // O_NONBLOCK
        private const int CerrarAlEjecutar = 0x80000; // O_CLOEXEC
        private const int RutaVacia = 0x1000; // AT_EMPTY_PATH
        private const uint CamposPedidos = 0x1 | 0x2 | 0x8 | 0x200; // STATX_TYPE | STATX_MODE | STATX_UID | STATX_SIZE
        private const uint CamposImprescindibles = 0x1 | 0x2 | 0x8;
        private const int TamanoStatx = 256;

        public const int TipoMascara = 0xF000; // S_IFMT
        public const int TipoFichero = 0x8000; // S_IFREG
        public const int TipoDirectorio = 0x4000; // S_IFDIR
        public const int TipoFifo = 0x1000; // S_IFIFO
        public const int TipoSocket = 0xC000; // S_IFSOCK
        public const int TipoEnlace = 0xA000; // S_IFLNK

        public readonly record struct Estado(int Modo, uint Dueno, long Longitud);

        // O_NOFOLLOW: 0100000 en arm, arm64 y powerpc; 0400000 en las demás (x86, x64, riscv64, s390x, loongarch64).
        private static int NoSeguirEnlace => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm or Architecture.Arm64 or Architecture.Armv6 or Architecture.Ppc64le => 0x8000,
            Architecture.X86 or Architecture.X64 or Architecture.Wasm or Architecture.S390x or Architecture.LoongArch64 or Architecture.RiscV64 => 0x20000,
            _ => 0x20000
        };

        // Abre para leer sin seguir un enlace en el último componente y sin bloquearse (una FIFO sin escritor). Devuelve el
        // descriptor o el errno.
        public static SafeFileHandle AbrirSinSeguirEnlace(string ruta, out int error)
        {
            int descriptor = Open(ruta, SoloLectura | SinBloqueo | CerrarAlEjecutar | NoSeguirEnlace);
            error = descriptor < 0 ? Marshal.GetLastPInvokeError() : 0;
            return new SafeFileHandle(descriptor, ownsHandle: true);
        }

        // Tipo y permisos (st_mode), dueño y tamaño del descriptor abierto; false con el errno si no se pudieron leer.
        public static bool TryLeerEstado(SafeFileHandle descriptor, out Estado estado, out int error)
        {
            Span<byte> buffer = stackalloc byte[TamanoStatx];
            buffer.Clear();
            bool isAnadido = false;
            try
            {
                descriptor.DangerousAddRef(ref isAnadido);
                int resultado = Statx((int)descriptor.DangerousGetHandle(), string.Empty, RutaVacia, CamposPedidos, ref MemoryMarshal.GetReference(buffer));
                error = resultado < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
            finally
            {
                if (isAnadido)
                    descriptor.DangerousRelease();
            }

            // struct statx: stx_mask (u32) en 0, stx_uid (u32) en 20, stx_mode (u16) en 28, stx_size (u64) en 40.
            uint campos = MemoryMarshal.Read<uint>(buffer);
            bool isCompleto = error == 0 && (campos & CamposImprescindibles) == CamposImprescindibles;
            estado = isCompleto ? new Estado(MemoryMarshal.Read<ushort>(buffer[28..]), MemoryMarshal.Read<uint>(buffer[20..]), (long)MemoryMarshal.Read<ulong>(buffer[40..])) : default;
            return isCompleto;
        }

        public static uint UsuarioEfectivo() => GetEuid();

        // SIGKILL a todo un grupo de procesos (kill(-grupo)); false si ya no queda ninguno (ESRCH) u otro error.
        public static bool MatarGrupo(int grupo) => grupo > 1 && Kill(-grupo, SenalMatar) == 0;

        [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int Open(string ruta, int flags);

        [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int Statx(int directorio, string ruta, int flags, uint campos, ref byte buffer);

        private const int SenalMatar = 9; // SIGKILL

        [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static partial int Kill(int proceso, int senal);

        [LibraryImport("libc", EntryPoint = "geteuid")]
        private static partial uint GetEuid();

        // getaddrinfo de una sola familia. Valores de glibc en Linux (iguales en todas las arquitecturas).
        public const int SinDireccionesDeLaFamilia = -5; // EAI_NODATA: el nombre existe y no tiene direcciones de esa familia
        private const int FamiliaIpv4 = 2; // AF_INET
        private const int FamiliaIpv6 = 10; // AF_INET6
        private const int Flujo = 1; // SOCK_STREAM

        // struct addrinfo de Linux: cuatro int, socklen_t (con el relleno de alineación antes del primer puntero) y tres punteros.
        [StructLayout(LayoutKind.Sequential)]
        private struct InfoDireccion
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

        // Las direcciones de 'nombre' de una sola familia (InterNetwork o InterNetworkV6) con getaddrinfo, sin AI_ADDRCONFIG
        // (la máquina no necesita tener esa familia configurada) ni nada mapeado a IPv6. Vacío si getaddrinfo dice que el
        // nombre no tiene direcciones de esa familia (EAI_NODATA); null con el código de getaddrinfo en 'codigo' si falla de
        // cualquier otra forma. Bloquea el hilo hasta que la resolución termina (no se puede cancelar).
        public static unsafe IPAddress[]? ResolverFamilia(string nombre, AddressFamily familia, out int codigo)
        {
            InfoDireccion pistas = default;
            pistas.Familia = familia == AddressFamily.InterNetworkV6 ? FamiliaIpv6 : FamiliaIpv4;
            pistas.TipoSocket = Flujo;
            InfoDireccion* resultado = null;
            codigo = GetAddrInfo(nombre, null, &pistas, &resultado);
            if (codigo != 0)
                return codigo == SinDireccionesDeLaFamilia ? [] : null;

            try
            {
                List<IPAddress> direcciones = [];
                for (InfoDireccion* actual = resultado; actual != null; actual = (InfoDireccion*)actual->Siguiente)
                    if (Leer(actual) is { } direccion && !direcciones.Contains(direccion))
                        direcciones.Add(direccion);
                return [.. direcciones];
            }
            finally
            {
                FreeAddrInfo(resultado);
            }
        }

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
