using System.Buffers;
using System.Buffers.Text;

namespace NativeSsh.Protocolo
{
    // Identidad del bash de un canal del shell: su $$ y su $PPID, capturados al inicializarlo (ConstructorComando.
    // EscribirCapturaPath). La cola de cada comando y la línea de recuperación tras Ctrl+C la llevan como literal y la
    // comparan con la del shell que las ejecuta: si no coincide, ese shell es otro (uno interactivo anidado que dejó el
    // comando: sudo -i, su -, ssh a otra máquina, bash -i) y el marcador lleva MarcadorFin.CodigoShellAnidado.
    // Ni el usuario ni un proceso hijo pueden cambiarla: $$ y $PPID son de solo lectura en bash y el literal viaja en el
    // texto de cada línea, no en una variable que se pueda heredar o sobrescribir. Los dos se conservan durante toda la
    // vida del proceso, también tras un 'exec' (mismo pid y mismo padre), y en un subshell $$ sigue siendo el del padre.
    public readonly record struct IdentidadShell(int Pid, int PidPadre)
    {
        private const int MaxDigitos = 10;

        // "__NSI_" + id + "_" + pid + "_" + ppid + "__", exactamente una vez en la salida de la captura.
        public static bool TryLeer(ReadOnlySpan<byte> salida, Guid id, out IdentidadShell identidad)
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

            identidad = new IdentidadShell(pid, pidPadre);
            return true;
        }

        // Literal con el que se compara "$$ $PPID" en la cola de cada línea.
        public void Escribir(IBufferWriter<byte> escritor)
        {
            Span<byte> destino = escritor.GetSpan((2 * MaxDigitos) + 1);
            _ = Utf8Formatter.TryFormat(Pid, destino, out int escritosPid);
            destino[escritosPid] = (byte)' ';
            _ = Utf8Formatter.TryFormat(PidPadre, destino[(escritosPid + 1)..], out int escritosPadre);
            escritor.Advance(escritosPid + 1 + escritosPadre);
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
