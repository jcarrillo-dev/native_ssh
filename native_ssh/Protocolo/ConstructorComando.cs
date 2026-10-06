using System.Buffers;
using System.Buffers.Text;
using System.Text;
using NativeSsh.Buffers;

namespace NativeSsh.Protocolo
{
    public static class ConstructorComando
    {
        private static ReadOnlySpan<byte> VariableTestigoUtf8 => "__NS_TESTIGO"u8;

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

        private static void EscribirTestigo(Guid testigo, IBufferWriter<byte> escritor)
        {
            _ = testigo.TryFormat(escritor.GetSpan(MarcadorFin.LongitudId), out int escritos, "N");
            escritor.Advance(escritos);
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

        // Tras inicializar: guarda el testigo del canal en el bash (IdentidadShell: un array de solo lectura, que bash no
        // exporta) e imprime su identidad ($$ y $PPID) en "__NSI_<id>_<pid>_<ppid>__\n" solo si el testigo quedó guardado
        // (si no, la cabecera sale con más texto tras "__NSI" y la inicialización falla: fallo cerrado), y el PATH que dejó el
        // shell de login (lo hereda el bash del PTY), para los canales exec (que solo reciben el PATH por defecto de sshd). El
        // PATH va en base64, en una sola línea, entre "__NSP_<id>_\n" y "\n__NSQ_<id>_\n" (LectorCapturaPath): otro proceso
        // que escriba en el terminal a la vez no se confunde con él. Los marcadores se imprimen con printf '%s_%s', así que el
        // texto de la línea no los contiene. Se envía una sola vez por canal (la inicialización puede llegar dos veces, y
        // asignar de nuevo una variable de solo lectura daría error). La asignación va dentro de un eval: si falla (ya hay
        // una variable de solo lectura con ese nombre), bash abandona el resto de la orden, que así es solo el eval, y la
        // línea sigue y da su marcador sin la identidad. El primer '\n' satisface el 'read'.
        public static void EscribirCapturaPath(Guid id, Guid testigo, IBufferWriter<byte> escritor)
        {
            escritor.Write("\n__ns_rc=0; eval \"declare -r -a "u8);
            escritor.Write(VariableTestigoUtf8);
            escritor.Write("=('"u8);
            EscribirTestigo(testigo, escritor);
            escritor.Write("')\"; __ns_t=${"u8);
            escritor.Write(VariableTestigoUtf8);
            escritor.Write("-x}; __ns_t=${__ns_t#'"u8);
            EscribirTestigo(testigo, escritor);
            escritor.Write("'}; printf '%s%s_%s_%d_%d__\\n' '__NSI' \"$__ns_t\" '"u8);
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
        // Si la ejecuta otro shell que el Ctrl+C no terminó (uno interactivo anidado: sudo -i, su -, ssh a otra máquina,
        // bash -i; o uno que sustituyó al de la sesión con exec), su marcador lleva MarcadorFin.CodigoShellAnidado
        // (EscribirCola).
        public static void EscribirRecuperacion(Guid id, IBufferWriter<byte> escritor, IdentidadShell? identidad = null)
        {
            escritor.Write("\n__ns_rc=130; unset __ns_b; cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; "u8);
            EscribirCola(id, escritor, identidad);
        }

        // Con identidad, la línea comprueba antes del marcador que la ejecuta el shell de la sesión ("$$ $PPID" y el testigo
        // del canal, IdentidadShell); si no, el marcador lleva MarcadorFin.CodigoShellAnidado. Solo con asignaciones y
        // expansiones POSIX, sin ninguna orden: nada que se pueda redefinir como función ('[', 'test') ni como alias (ni
        // siquiera 'case', que un alias rompe), sin salida propia (con 'set -x' bash traza esta línea como todas las del
        // protocolo), sin división de palabras (IFS no influye en una asignación) y válida con 'set -u' y 'set -e'.
        // "${__ns_t#'<literal>'}" queda vacío solo si el valor es exactamente el literal (si empieza por él y sigue, queda
        // el resto; si no empieza por él, queda entero, y nunca está vacío porque lleva espacios): vacío = mismo shell.
        // Cualquier shell POSIX la entiende (dash, el ash de busybox): en uno ajeno el testigo falta y el marcador sale con
        // el código.
        private static void EscribirCola(Guid id, IBufferWriter<byte> escritor, IdentidadShell? identidad = null)
        {
            if (identidad is { } esperada)
            {
                escritor.Write("__ns_t=\"$$ $PPID ${"u8);
                escritor.Write(VariableTestigoUtf8);
                escritor.Write("-}\"; __ns_t=${__ns_t#'"u8);
                esperada.Escribir(escritor);
                // 999 = MarcadorFin.CodigoShellAnidado.
                escritor.Write("'}; __ns_t=${__ns_t:+999}; __ns_rc=${__ns_t:-$__ns_rc}; "u8);
            }

            escritor.Write("printf '%s_%s_%d__\\n' '__NSX' '"u8);
            MarcadorFin.EscribirId(id, escritor);
            escritor.Write("' \"$__ns_rc\"; read -r __ns_d\n"u8);
        }
    }
}
