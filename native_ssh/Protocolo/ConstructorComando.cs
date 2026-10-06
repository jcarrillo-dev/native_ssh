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

        // identidad: la del shell que debe ejecutar la línea (null mientras no se conoce, solo durante la inicialización).
        public static void EscribirComando(ReadOnlySpan<char> comando, Guid id, IBufferWriter<byte> escritor, IdentidadShell? identidad = null)
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
            EscribirCola(id, escritor, identidad);
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

        // Tras inicializar: la identidad del bash ($$ y $PPID) en "__NSI_<id>_<pid>_<ppid>__\n" (IdentidadShell) y el PATH que
        // dejó el shell de login (lo hereda el bash del PTY), para los canales exec (que solo reciben el PATH por defecto de
        // sshd). El PATH va en base64, en una sola línea, entre "__NSP_<id>_\n" y "\n__NSQ_<id>_\n" (LectorCapturaPath): otro
        // proceso que escriba en el terminal a la vez no se confunde con él. Los marcadores se imprimen con printf '%s_%s',
        // así que el texto de la línea no los contiene. El primer '\n' satisface el 'read'.
        public static void EscribirCapturaPath(Guid id, IBufferWriter<byte> escritor)
        {
            escritor.Write("\n__ns_rc=0; printf '%s_%s_%d_%d__\\n' '__NSI' '"u8);
            MarcadorFin.EscribirId(id, escritor);
            escritor.Write("' \"$$\" \"$PPID\"; printf '%s_%s_\\n' '__NSP' '"u8);
            MarcadorFin.EscribirId(id, escritor);
            escritor.Write("'; printf '%s' \"$PATH\" | base64 | tr -d '\\n'; printf '\\n%s_%s_\\n' '__NSQ' '"u8);
            MarcadorFin.EscribirId(id, escritor);
            escritor.Write("'; "u8);
            EscribirCola(id, escritor);
        }

        // Ctrl+C va solo en su escritura y la línea de recuperación después, en otra (EscribirRecuperacion): un 'sudo <cmd>'
        // con use_pty (o pidiendo la contraseña) se tragaría lo que llega junto al Ctrl+C, o lo tomaría como contraseñas.
        public static void EscribirCtrlC(IBufferWriter<byte> escritor) => escritor.Write("\u0003"u8);

        // Línea que devuelve el shell a un estado conocido tras Ctrl+C. El primer '\n' es una línea vacía para bash (que
        // descartó lo que tenía a medias al recibir Ctrl+C).
        // Si la ejecuta un shell interactivo anidado que el Ctrl+C no terminó (sudo -i, su -, ssh a otra máquina, bash -i),
        // su marcador lleva MarcadorFin.CodigoShellAnidado (EscribirCola).
        public static void EscribirRecuperacion(Guid id, IBufferWriter<byte> escritor, IdentidadShell? identidad = null)
        {
            escritor.Write("\n__ns_rc=130; unset __ns_b; cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; "u8);
            EscribirCola(id, escritor, identidad);
        }

        // Con identidad, la línea comprueba antes del marcador que la ejecuta el shell de la sesión; si no, el marcador lleva
        // MarcadorFin.CodigoShellAnidado. Con 'case' y no con '[': '[' (y 'test') se pueden redefinir como función (en el
        // shell de la sesión daría falsos positivos; en el rc de un shell anidado, que no se detectara), y 'case' es una
        // palabra reservada; un alias con su nombre solo rompe la línea, que entonces no da marcador y se trata como un
        // proceso que no responde. Cualquier shell POSIX la entiende.
        private static void EscribirCola(Guid id, IBufferWriter<byte> escritor, IdentidadShell? identidad = null)
        {
            if (identidad is { } esperada)
            {
                escritor.Write("case \"$$ $PPID\" in '"u8);
                esperada.Escribir(escritor);
                // 999 = MarcadorFin.CodigoShellAnidado.
                escritor.Write("') ;; *) __ns_rc=999 ;; esac; "u8);
            }

            escritor.Write("printf '%s_%s_%d__\\n' '__NSX' '"u8);
            MarcadorFin.EscribirId(id, escritor);
            escritor.Write("' \"$__ns_rc\"; read -r __ns_d\n"u8);
        }
    }
}
