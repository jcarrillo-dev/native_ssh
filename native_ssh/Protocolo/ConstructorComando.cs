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

        // Línea centinela: '#' y 32 hexadecimales (LongitudLineaCentinela bytes). Ver EscribirCola.
        public const int LongitudLineaCentinela = 1 + MarcadorFin.LongitudId;

        // EOF seguidos (Ctrl+D al principio de una línea) que la cola admite antes de abandonar la espera del centinela: un
        // terminal colgado da EOF sin fin (EscribirCola).
        public const int MaxEofSeguidos = 1000;

        // identidad: la del shell que debe ejecutar la línea (null mientras no se conoce, solo durante la inicialización).
        // centinela: el del canal (EscribirCola).
        public static void EscribirComando(ReadOnlySpan<char> comando, Guid id, Guid centinela, IBufferWriter<byte> escritor, IdentidadShell? identidad = null)
        {
            using BufferAlquilado utf8 = new(Encoding.UTF8.GetMaxByteCount(comando.Length));
            int longitud = Encoding.UTF8.GetBytes(comando, utf8.Span);
            ReadOnlySpan<byte> resto = utf8.Span[..longitud];

            EscribirLineaCentinela(centinela, escritor);
            escritor.Write("__ns_b='"u8);
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
            EscribirCola(id, centinela, escritor, identidad);
        }

        // Sin centinela delante: la primera línea la lee el shell de login, que puede no ser bash (en zsh o csh interactivos
        // '#' no empieza un comentario), y antes no hay ninguna cola esperándolo.
        public static void EscribirInicializacion(Guid id, Guid centinela, IBufferWriter<byte> escritor, string? directorioTrabajo = null)
        {
            escritor.Write("exec bash --noediting --norc --noprofile\n"u8);
            EscribirLineaInicializacion(id, centinela, escritor, directorioTrabajo);
        }

        // El reenvío de la línea de inicialización (SesionShell.AbrirShellAsync) sí lleva el centinela delante: si la primera
        // llegó a ejecutarse, su cola consume esa línea; si no, bash la toma como un comentario.
        public static void EscribirReenvioInicializacion(Guid id, Guid centinela, IBufferWriter<byte> escritor, string? directorioTrabajo = null)
        {
            EscribirLineaCentinela(centinela, escritor);
            EscribirLineaInicializacion(id, centinela, escritor, directorioTrabajo);
        }

        // directorioTrabajo (fuente de verdad en C#) se reexporta como __NS_CWD; viaja en base64 para que ninguna ruta
        // rompa la línea. La llamada lo acota a SesionShell.MaxBytesDirectorioTrabajo.
        public static void EscribirLineaInicializacion(Guid id, Guid centinela, IBufferWriter<byte> escritor, string? directorioTrabajo = null)
        {
            // 'mesg n': otro usuario local del servidor no puede escribir en el terminal (write, wall) y falsificar líneas de la
            // salida, como los anuncios de las escrituras. Sin mesg (Debian slim) se quita a mano la escritura del grupo, que
            // es lo que hace mesg; sin terminal o sin permiso, nada, en silencio. Repetirlo (reenvío de la línea) no cambia nada.
            escritor.Write("PS1=; PS2=; PS4=; unset HISTFILE MAILCHECK PROMPT_COMMAND; set +o history +m; export TERM=dumb PAGER=cat GIT_PAGER=cat SYSTEMD_PAGER=cat MANPAGER=cat; stty -echo -onlcr; { mesg n || chmod g-w -- \"$(tty)\"; } 2>/dev/null; __ns_rc=0; "u8);
            if (!string.IsNullOrEmpty(directorioTrabajo))
            {
                escritor.Write("export __NS_CWD=\"$(printf '%s' '"u8);
                EscribirBase64(directorioTrabajo, escritor);
                escritor.Write("' | base64 -d)\"; "u8);
            }

            escritor.Write("cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; "u8);
            EscribirCola(id, centinela, escritor, isEsperarCentinela: false);
        }

        // "#<32 hexadecimales>\n". Para bash, un comentario: si no lo espera ninguna cola (tras un Ctrl+C que abortó la suya,
        // o en el reenvío de la inicialización), no hace nada.
        public static void EscribirLineaCentinela(Guid centinela, IBufferWriter<byte> escritor)
        {
            escritor.Write("#"u8);
            EscribirTestigo(centinela, escritor);
            escritor.Write("\n"u8);
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

        // Tras un reenvío de la inicialización. El primer centinela lo consume la cola de la línea que ya respondió; el segundo,
        // la de la copia que bash pueda tener aún en su búfer (se ejecuta después y repite su marcador). Si no hay ninguna cola
        // esperándolos, son comentarios inocuos.
        public static void EscribirSincronizacion(Guid id, Guid centinela, IBufferWriter<byte> escritor)
        {
            EscribirLineaCentinela(centinela, escritor);
            EscribirLineaCentinela(centinela, escritor);
            escritor.Write("__ns_rc=0; "u8);
            EscribirCola(id, centinela, escritor);
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
        // línea sigue y da su marcador sin la identidad. El centinela del principio termina la espera de la cola anterior.
        public static void EscribirCapturaPath(Guid id, Guid testigo, Guid centinela, IBufferWriter<byte> escritor)
        {
            EscribirLineaCentinela(centinela, escritor);
            escritor.Write("__ns_rc=0; eval \"declare -r -a "u8);
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
            EscribirCola(id, centinela, escritor);
        }

        // Ctrl+C va solo en su escritura y la línea de recuperación después, en otra (EscribirRecuperacion): un 'sudo <cmd>'
        // con use_pty (o pidiendo la contraseña) se tragaría lo que llega junto al Ctrl+C, o lo tomaría como contraseñas.
        public static void EscribirCtrlC(IBufferWriter<byte> escritor) => escritor.Write("\u0003"u8);

        // Línea que devuelve el shell a un estado conocido tras Ctrl+C, con el centinela delante: si Ctrl+C abortó la línea
        // en curso (el comando o la espera de una cola), bash lo toma como un comentario; si una cola sigue esperándolo (el
        // terminal no convirtió Ctrl+C en señal), la termina. Borra también __ns_d: una cola abortada a mitad pudo dejar en
        // ella la última línea tecleada que leyó.
        // Si la ejecuta otro shell que el Ctrl+C no terminó (uno interactivo anidado: sudo -i, su -, ssh a otra máquina,
        // bash -i; o uno que sustituyó al de la sesión con exec), su marcador lleva MarcadorFin.CodigoShellAnidado
        // (EscribirCola).
        public static void EscribirRecuperacion(Guid id, Guid centinela, IBufferWriter<byte> escritor, IdentidadShell? identidad = null)
        {
            EscribirLineaCentinela(centinela, escritor);
            escritor.Write("__ns_rc=130; unset __ns_b __ns_d; cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; "u8);
            EscribirCola(id, centinela, escritor, identidad);
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
        // Tras el marcador, la cola espera el centinela del canal (EscribirLineaCentinela; aleatorio, de 128 bits, nuevo en cada
        // canal), que es la primera línea de todo envío salvo la inicialización: lee líneas del terminal hasta la que termina
        // en él, y lo anterior es entrada tecleada con ssh_input que ningún comando leyó (una contraseña enviada antes de que
        // la pidiera o cuando ya no la leía, varias líneas, un ssh_input con '\n' o '\r' dentro, Ctrl+D). Lo tecleado y el envío
        // siguiente viajan en orden por el mismo canal, y C# no escribe el envío siguiente hasta recibir este marcador (y
        // ninguna entrada después), así que todo lo tecleado llega antes que el centinela: nada se ejecuta como orden, sin
        // ventana ni plazo que dependan de la red, la carga o el volumen. Cada línea se cuenta y se borra (__ns_d se vacía en
        // cada vuelta, como __ns_x, y el 'unset' final las quita); una línea a medias (enter=false) se junta con el centinela y cuenta como
        // una. El recuento solo se sabe cuando llega el centinela, así que viaja en el marcador del envío SIGUIENTE
        // ("${__ns_n:+_$__ns_n}", MarcadorFin): el aviso va en la respuesta de ese comando. __ns_n solo se fija si el centinela
        // llegó; si Ctrl+C aborta la espera (el centinela se lo comió otro proceso que leía del terminal y la línea que la
        // terminaría era la del comando del LLM), queda vacío y nada se da por descartado. El recuento se queda en
        // MarcadorFin.MaxLineasDescartadas (más cifras romperían el marcador), pero el bucle descarta todas.
        // Un Ctrl+C (interrupción) aborta la espera o el comando en curso, y el núcleo vacía la entrada pendiente del terminal
        // (sin 'stty noflsh'): lo tecleado tampoco se ejecuta. Con 'noflsh' (si el comando lo dejó así) no la vacía y bash
        // ejecutaría como órdenes las líneas pendientes antes de la línea de recuperación: no se puede evitar desde aquí.
        // Ctrl+D al principio de una línea es un EOF del terminal: 'read' devuelve 1 sin leer nada; se cuenta como una línea y
        // se sigue esperando, hasta MaxEofSeguidos seguidos (un terminal colgado da EOF sin fin).
        // 'command read' y 'command [', con IFS y TMOUT vacíos para 'read': una función o un alias 'read', '[' o 'test' no los
        // rompen ('command' no busca funciones y tras él no se expanden alias), el contenido no pierde los espacios de los
        // extremos y un TMOUT del usuario no corta la espera. 'command' también se puede redefinir como función y entonces el
        // comando siguiente agota su plazo; como un alias de una palabra reservada de las que usa (while, do, done, '{', '}'),
        // que rompe la sintaxis de la línea: no se protege más.
        // Sin 'break' (también se puede redefinir) ni 'case' (un alias lo rompe, como en la comprobación de identidad): termina
        // por la condición del 'while' y el centinela se reconoce con longitudes ("${__ns_d%'<centinela>'}" es más corto solo si
        // la línea termina en él). Sin '!' (con la expansión del historial activa, '!__ns_f' daría «event not found»). Con
        // 'set -x' la traza del bucle va a /dev/null con su stderr. Válida con 'set -u', 'set -e', 'set -f', 'set -a', un IFS
        // raro y las opciones de patrones (nocasematch, extglob, nullglob, failglob).
        // Todos los envíos salvo la inicialización empiezan por el centinela, y todas las colas lo esperan salvo la de la
        // línea de inicialización (isEsperarCentinela=false), que consume una sola línea: la siguiente que llega siempre es
        // una línea centinela (la del reenvío, la primera de la sincronización o la de la captura), y mientras se inicializa
        // ssh_input se rechaza, así que no hay nada tecleado que descartar. Esa línea debe ser corta: con un login lento con
        // readline (bash -l), el bash nuevo lee de una vez la inicialización, el centinela del reenvío y su copia
        // (SesionShell.SincronizarAsync) con una sola lectura de su búfer de entrada; medido con bash 5.3 en un terminal, si
        // las dos copias y el centinela pasan de unos 1024 bytes, la lectura corta la segunda copia y la inicialización se
        // cuelga (con la espera del centinela en esta línea ocupaban unos 1250).
        // Solo construcciones POSIX: si la cola la ejecuta otro shell (uno anidado que dejó el comando, p. ej. el ash de busybox
        // de root tras 'sudo -i' en Alpine, o dash), el marcador sale con MarcadorFin.CodigoShellAnidado y ese shell también
        // descarta lo tecleado hasta el centinela, en vez de ejecutarlo, mientras el canal se cierra (medido con dash 0.5 y el
        // ash de busybox en un terminal: mismos recuentos que bash, Ctrl+D incluido).
        private static void EscribirCola(Guid id, Guid centinela, IBufferWriter<byte> escritor, IdentidadShell? identidad = null, bool isEsperarCentinela = true)
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

            escritor.Write("printf '%s_%s_%d%s__\\n' '__NSX' '"u8);
            MarcadorFin.EscribirId(id, escritor);
            if (!isEsperarCentinela)
            {
                escritor.Write("' \"$__ns_rc\" \"${__ns_n:+_$__ns_n}\"; command read -r __ns_d; unset __ns_d\n"u8);
                return;
            }

            // 1000 = MaxEofSeguidos; 999999 = MarcadorFin.MaxLineasDescartadas.
            escritor.Write("' \"$__ns_rc\" \"${__ns_n:+_$__ns_n}\"; __ns_n=; __ns_c=0; __ns_e=0; __ns_f=0; while command [ \"$__ns_f\" -eq 0 ] && { { TMOUT= IFS= command read -r __ns_d && __ns_e=0; } || command [ \"$((__ns_e+=1))\" -lt 1000 ]; }; do __ns_x=${__ns_d%'#"u8);
            EscribirTestigo(centinela, escritor);
            escritor.Write("'}; __ns_f=$(( ${#__ns_x} < ${#__ns_d} )); __ns_c=$(( __ns_c + ((__ns_f == 0 || ${#__ns_x} > 0) && __ns_c < 999999) )); __ns_d=; __ns_x=; done 2>/dev/null; __ns_n=${__ns_c#0}; unset __ns_d __ns_x __ns_c __ns_e __ns_f\n"u8);
        }
    }
}
