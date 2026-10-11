using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NativeSsh.Comun
{
    // Llamadas a libc que .NET no expone: abrir sin seguir un enlace ni bloquearse (O_NOFOLLOW | O_NONBLOCK) y leer el tipo,
    // el dueño y los permisos del descriptor ya abierto (statx), para comprobar exactamente lo que se va a leer. statx tiene
    // la misma disposición en todas las arquitecturas de Linux (struct stat no); los valores de O_NOFOLLOW cambian.
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

        [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int Open(string ruta, int flags);

        [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int Statx(int directorio, string ruta, int flags, uint campos, ref byte buffer);

        [LibraryImport("libc", EntryPoint = "geteuid")]
        private static partial uint GetEuid();
    }
}
