using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace NativeSsh.Protocolo
{
    // Identidad del bash de un canal del shell: su $$ y su $PPID, capturados al inicializarlo (ConstructorComando.
    // EscribirCapturaPath), y un testigo aleatorio del canal que la misma línea de captura guarda en ese bash. La cola de
    // cada comando y la línea de recuperación tras Ctrl+C llevan la identidad como literal y la comparan con la del shell
    // que las ejecuta: si no coincide, ese shell es otro y el marcador lleva MarcadorFin.CodigoShellAnidado. Otro shell es
    // uno interactivo anidado que dejó el comando (sudo -i, su -, ssh a otra máquina, bash -i) o uno que sustituyó al de la
    // sesión con exec (exec bash, exec su, exec setpriv … bash, chroot, nsenter).
    // $$ y $PPID solos no bastan: dos máquinas arrancadas igual (contenedores gemelos de la misma imagen) dan el mismo $$ y
    // $PPID al bash de una sesión, y un exec conserva los dos. El testigo sí distingue: es aleatorio, nuevo en cada canal
    // (también tras ssh_reset_shell o al recrear el shell) y vive en un array de solo lectura de ese bash (no se puede
    // reasignar ni borrar, y bash no exporta arrays ni con 'export' ni con 'set -a'), así que ningún otro proceso lo hereda:
    // ni un shell anidado ni el programa que deja un exec. Quien lea el texto de una línea ve el literal, pero lo compara
    // con su propia variable, que no tiene. Su único rastro está en el texto de las líneas que se escriben al terminal.
    public readonly record struct IdentidadShell(int Pid, int PidPadre, Guid Testigo)
    {
        private const int MaxDigitos = 10;

        // 32 hexadecimales en minúsculas ("N"): sin caracteres especiales para el shell ni para un patrón.
        public static Guid NuevoTestigo()
        {
            Span<byte> bytes = stackalloc byte[16];
            RandomNumberGenerator.Fill(bytes);
            return new Guid(bytes);
        }

        // "__NSI_" + id + "_" + pid + "_" + ppid + "__", exactamente una vez en la salida de la captura. La captura solo lo
        // imprime así si el bash guardó el testigo del canal (si no, la cabecera no casa).
        public static bool TryLeer(ReadOnlySpan<byte> salida, Guid id, Guid testigo, out IdentidadShell identidad)
        {
            identidad = default;
            Span<byte> cabecera = stackalloc byte[6 + MarcadorFin.LongitudId + 1];
            "__NSI_"u8.CopyTo(cabecera);
            _ = id.TryFormat(cabecera[6..], out int escritos, "N");
            cabecera[6 + escritos] = (byte)'_';

            int posicion = salida.IndexOf(cabecera);
            if (posicion < 0 || salida.LastIndexOf(cabecera) != posicion)
                return false;

            ReadOnlySpan<byte> resto = salida[(posicion + cabecera.Length)..];
            if (!TryLeerNumero(ref resto, out int pid) || pid == 0 || resto.IsEmpty || resto[0] != (byte)'_')
                return false;

            resto = resto[1..];
            if (!TryLeerNumero(ref resto, out int pidPadre) || !resto.StartsWith("__"u8))
                return false;

            identidad = new IdentidadShell(pid, pidPadre, testigo);
            return true;
        }

        // Literal con el que se compara "$$ $PPID ${__NS_TESTIGO-}" en la cola de cada línea.
        public void Escribir(IBufferWriter<byte> escritor)
        {
            Span<byte> destino = escritor.GetSpan((2 * MaxDigitos) + 2 + MarcadorFin.LongitudId);
            _ = Utf8Formatter.TryFormat(Pid, destino, out int escritosPid);
            destino[escritosPid] = (byte)' ';
            int posicion = escritosPid + 1;
            _ = Utf8Formatter.TryFormat(PidPadre, destino[posicion..], out int escritosPadre);
            posicion += escritosPadre;
            destino[posicion++] = (byte)' ';
            _ = Testigo.TryFormat(destino[posicion..], out int escritosTestigo, "N");
            escritor.Advance(posicion + escritosTestigo);
        }

        // Un pid es positivo; el $PPID de un proceso puede ser 0 (huérfano de un espacio de nombres).
        private static bool TryLeerNumero(ref ReadOnlySpan<byte> datos, out int numero)
        {
            int digitos = datos.IndexOfAnyExceptInRange((byte)'0', (byte)'9');
            if (digitos < 0)
                digitos = datos.Length;

            numero = 0;
            if (digitos == 0 || digitos > MaxDigitos || !Utf8Parser.TryParse(datos[..digitos], out numero, out int consumidos) || consumidos != digitos || numero < 0)
                return false;

            datos = datos[digitos..];
            return true;
        }
    }
}
