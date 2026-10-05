using System.Buffers;
using System.Buffers.Text;
using System.Text;
using NativeSsh.Buffers;

namespace NativeSsh.Protocolo
{
    public static class ConstructorComando
    {
        // Múltiplo de 4 y muy por debajo de los 4095 bytes por línea del TTY en modo canónico.
        public const int MaxBase64PorLinea = 3000;
        public const int BytesPorLinea = MaxBase64PorLinea / 4 * 3;

        public static void EscribirComando(ReadOnlySpan<char> comando, Guid id, IBufferWriter<byte> escritor)
        {
            using BufferAlquilado utf8 = new(Encoding.UTF8.GetMaxByteCount(comando.Length));
            int longitud = Encoding.UTF8.GetBytes(comando, utf8.Span);
            ReadOnlySpan<byte> resto = utf8.Span[..longitud];

            escritor.Write("\n__ns_b='"u8);
            while (true)
            {
                int trozo = Math.Min(resto.Length, BytesPorLinea);
                Span<byte> destino = escritor.GetSpan(Base64.GetMaxEncodedToUtf8Length(trozo));
                _ = Base64.EncodeToUtf8(resto[..trozo], destino, out _, out int escritos);
                escritor.Advance(escritos);
                resto = resto[trozo..];
                if (resto.IsEmpty)
                    break;

                escritor.Write("'\n__ns_b+='"u8);
            }

            escritor.Write("'\neval \"$(printf '%s' \"$__ns_b\" | base64 -d)\"; __ns_rc=$?; unset __ns_b; cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; "u8);
            EscribirCola(id, escritor);
        }

        public static void EscribirInicializacion(Guid id, IBufferWriter<byte> escritor, string? directorioTrabajo = null)
        {
            escritor.Write("exec bash --noediting --norc --noprofile\n"u8);
            EscribirLineaInicializacion(id, escritor, directorioTrabajo);
        }

        // directorioTrabajo (fuente de verdad en C#) se reexporta como __NS_CWD; viaja en base64 para que ninguna ruta
        // rompa la línea. La llamada lo acota a SesionShell.MaxBytesDirectorioTrabajo.
        public static void EscribirLineaInicializacion(Guid id, IBufferWriter<byte> escritor, string? directorioTrabajo = null)
        {
            escritor.Write("PS1=; PS2=; PS4=; unset HISTFILE MAILCHECK PROMPT_COMMAND; set +o history +m; export TERM=dumb PAGER=cat GIT_PAGER=cat SYSTEMD_PAGER=cat MANPAGER=cat; stty -echo -onlcr; __ns_rc=0; "u8);
            if (!string.IsNullOrEmpty(directorioTrabajo))
            {
                escritor.Write("export __NS_CWD=\"$(printf '%s' '"u8);
                EscribirBase64(directorioTrabajo, escritor);
                escritor.Write("' | base64 -d)\"; "u8);
            }

            escritor.Write("cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; "u8);
            EscribirCola(id, escritor);
        }

        private static void EscribirBase64(string texto, IBufferWriter<byte> escritor)
        {
            using BufferAlquilado utf8 = new(Encoding.UTF8.GetMaxByteCount(texto.Length));
            int longitud = Encoding.UTF8.GetBytes(texto, utf8.Span);
            Span<byte> destino = escritor.GetSpan(Base64.GetMaxEncodedToUtf8Length(longitud));
            _ = Base64.EncodeToUtf8(utf8.Span[..longitud], destino, out _, out int escritos);
            escritor.Advance(escritos);
        }

        // Tras un reenvío de la inicialización. El primer '\n' satisface el 'read' de la línea que ya respondió;
        // el segundo, el de la copia que bash pueda tener aún en su búfer (se ejecuta después y repite su marcador).
        // Si no hay ningún 'read' pendiente, son líneas vacías inocuas.
        public static void EscribirSincronizacion(Guid id, IBufferWriter<byte> escritor)
        {
            escritor.Write("\n\n__ns_rc=0; "u8);
            EscribirCola(id, escritor);
        }

        public static void EscribirInterrupcion(Guid id, IBufferWriter<byte> escritor)
        {
            escritor.Write("\u0003\n__ns_rc=130; unset __ns_b; cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; "u8);
            EscribirCola(id, escritor);
        }

        private static void EscribirCola(Guid id, IBufferWriter<byte> escritor)
        {
            escritor.Write("printf '%s_%s_%d__\\n' '__NSX' '"u8);
            MarcadorFin.EscribirId(id, escritor);
            escritor.Write("' \"$__ns_rc\"; read -r __ns_d\n"u8);
        }
    }
}
