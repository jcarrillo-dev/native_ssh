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

        public static void EscribirInicializacion(Guid id, IBufferWriter<byte> escritor)
        {
            escritor.Write("exec bash --noediting --norc --noprofile\n"u8);
            EscribirLineaInicializacion(id, escritor);
        }

        public static void EscribirLineaInicializacion(Guid id, IBufferWriter<byte> escritor)
        {
            escritor.Write("PS1=; PS2=; PS4=; unset HISTFILE MAILCHECK PROMPT_COMMAND; set +o history +m; export TERM=dumb PAGER=cat GIT_PAGER=cat SYSTEMD_PAGER=cat MANPAGER=cat; stty -echo -onlcr; __ns_rc=0; cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; "u8);
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
