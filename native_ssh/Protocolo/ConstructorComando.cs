using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
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

        // EOF seguidos (Ctrl+D al principio de una línea) que la cola admite esperando el centinela: al llegar al tope, el shell
        // termina con CodigoSalidaTopeEof (EscribirCola). Un terminal colgado da EOF o error sin fin.
        public const int MaxEofSeguidos = 1000;
        public const int CodigoSalidaTopeEof = 129;

        // identidad: la del shell que debe ejecutar la línea (null mientras no se conoce, solo durante la inicialización).
        // centinela: el del canal (EscribirCola). El envío empieza por un salto de línea y la línea centinela
        // (EscribirPrefijoCentinela), sigue el comando en base64, en líneas, y termina con una línea que lo ejecuta solo si
        // llegó entero: si otro proceso que lee del terminal (uno en segundo plano) se come alguna de las líneas del base64,
        // lo que queda es el final del comando, o nada, y ejecutarlo sería ejecutar otra cosa; entonces no se ejecuta y el
        // marcador lleva MarcadorFin.CodigoEnvioIncompleto (medido: un lector de dos líneas se comía el salto y la primera
        // línea del base64, y el comando se daba por completado con código 0 sin haberse ejecutado).
        // Dos comprobaciones: (1) la longitud del base64, para la línea que falta; (2) un nonce aleatorio por envío al final de
        // lo codificado, que se comprueba tras decodificar y se quita antes del eval, para cuando la longitud cuadra con otro
        // contenido: si el lector se come el salto que termina una línea del base64, bash junta la siguiente al valor, y si se
        // come justo los caracteres que lo compensan, la longitud coincide. Un lector solo quita bytes y lo que bash añade solo
        // puede venir de detrás, así que el final de lo decodificado cambia (o base64 -d falla con los caracteres ajenos al
        // base64) y el nonce no está. No es una suma de comprobación: un cambio del mismo tamaño en medio que dejara el final
        // intacto pasaría, pero eso no lo puede producir otro lector del terminal (y cksum puede faltar en el servidor).
        // Entre el comando y el nonce va un salto de línea, y se quitan juntos ("${__ns_b%$'\n<nonce>'}"): bash compara por
        // caracteres del locale del shell, y en uno multibyte heredado (GBK, Big5, GB18030) el último byte del comando y el
        // primero del nonce podían formar un carácter (medido en zh_CN.GBK: 'echo x€' seguido de a–f no se ejecutaba). 0x0A no
        // es byte de cola en ninguna de esas codificaciones ni en UTF-8, y lo evaluado es byte a byte el comando pedido, también
        // sus saltos de línea finales (la sustitución de órdenes de la decodificación quita los del final, y el final es el
        // nonce). Shift-JIS no está cubierto: con un shell en ja_JP.SJIS el primer comando se cuelga, también sin este
        // separador (medido en la auditoría del tramo 1-septies; causa no verificada).
        public static void EscribirComando(ReadOnlySpan<char> comando, Guid id, Guid centinela, IBufferWriter<byte> escritor, IdentidadShell? identidad = null)
        {
            Guid nonce = IdentidadShell.NuevoTestigo();
            using BufferAlquilado utf8 = new(Encoding.UTF8.GetMaxByteCount(comando.Length) + 1 + MarcadorFin.LongitudId);
            int longitud = Encoding.UTF8.GetBytes(comando, utf8.Span);
            utf8.Span[longitud++] = (byte)'\n';
            _ = nonce.TryFormat(utf8.Span[longitud..], out int escritosNonce, "N");
            ReadOnlySpan<byte> resto = utf8.Span[..(longitud + escritosNonce)];

            EscribirPrefijoCentinela(centinela, escritor);
            escritor.Write("__ns_b='"u8);
            int longitudBase64 = 0;
            while (true)
            {
                int trozo = Math.Min(resto.Length, BytesPorLinea);
                Span<byte> destino = escritor.GetSpan(Base64.GetMaxEncodedToUtf8Length(trozo));
                _ = Base64.EncodeToUtf8(resto[..trozo], destino, out _, out int escritos);
                escritor.Advance(escritos);
                longitudBase64 += escritos;
                resto = resto[trozo..];
                if (resto.IsEmpty)
                    break;

                escritor.Write("'\n__ns_b+='"u8);
            }

            // 997 = MarcadorFin.CodigoEnvioIncompleto. La decodificación va en un grupo con su stderr a /dev/null: lo que avisa
            // base64 -d de un valor roto y bash de un byte nulo no es salida del comando. Con 'set -e' del usuario, un fallo
            // dentro de la lista con '&&' no termina el shell.
            escritor.Write("'\n__ns_rc=997; __ns_b=${__ns_b-}; command [ \"${#__ns_b}\" = '"u8);
            Span<byte> cifras = escritor.GetSpan(11);
            _ = longitudBase64.TryFormat(cifras, out int escritasCifras, provider: CultureInfo.InvariantCulture);
            escritor.Advance(escritasCifras);
            escritor.Write("' ] && { __ns_b=$(printf '%s' \"$__ns_b\" | base64 -d); } 2>/dev/null && __ns_k=${__ns_b%$'\\n"u8);
            EscribirTestigo(nonce, escritor);
            escritor.Write("'} && command [ \"${#__ns_k}\" -lt \"${#__ns_b}\" ] && { eval \"$__ns_k\"; __ns_rc=$?; }; unset __ns_b __ns_k; cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; "u8);
            EscribirCola(id, centinela, escritor, identidad);
            escritor.Write("\n"u8);
        }

        // Sin centinela delante: la primera línea la lee el shell de login, que puede no ser bash (en zsh o csh interactivos
        // '#' no empieza un comentario), y antes no hay ninguna cola esperándolo.
        public static void EscribirInicializacion(Guid id, IBufferWriter<byte> escritor)
        {
            escritor.Write("exec bash --noediting --norc --noprofile\n"u8);
            EscribirLineaInicializacion(id, escritor);
        }

        // El reenvío de la línea de inicialización (SesionShell.AbrirShellAsync) sí lleva el centinela delante: si la primera
        // llegó a ejecutarse, su cola consume esa línea; si no, bash la toma como un comentario.
        public static void EscribirReenvioInicializacion(Guid id, Guid centinela, IBufferWriter<byte> escritor)
        {
            EscribirLineaCentinela(centinela, escritor);
            EscribirLineaInicializacion(id, escritor);
        }

        // Lo mínimo para que el protocolo funcione (sin prompt, sin historial, sin eco), el marcador y una cola que consume una
        // sola línea, sin esperar el centinela: la siguiente que llega siempre es una línea centinela (la del reenvío, la
        // primera de la sincronización o la de la captura), y mientras se inicializa ssh_input se rechaza, así que no hay nada
        // tecleado que descartar. Debe ser corta y de longitud fija: con un login lento con readline (bash -l), el bash nuevo
        // recibe de una vez esta línea, el centinela del reenvío y su copia (SesionShell.SincronizarAsync) y los lee con una
        // sola lectura de su búfer de entrada; medido con bash 5.3 en un terminal, si pasan de unos 1024 bytes la lectura
        // corta la copia y, si el corte cae dentro de unas comillas, bash se come las líneas siguientes y la inicialización se
        // cuelga. Por eso el resto de la preparación (variables, mesg, __NS_CWD y el cd) va en la línea de captura, que nunca
        // llega junto con otras (EscribirCapturaPath); con el directorio de trabajo aquí, una ruta larga bastaba para colgarla.
        // '-ixon': sin él, un Ctrl+S tecleado con ssh_input detiene la salida del terminal (medido: el marcador no llega y el
        // comando espera hasta su plazo) y Ctrl+Q desaparece de lo tecleado; con él, los dos son caracteres normales.
        public static void EscribirLineaInicializacion(Guid id, IBufferWriter<byte> escritor)
        {
            escritor.Write("PS1=; PS2=; PS4=; unset HISTFILE MAILCHECK PROMPT_COMMAND; set +o history +m; stty -echo -onlcr -ixon; __ns_rc=0; printf '%s_%s_%d%s__\\n' '__NSX' '"u8);
            MarcadorFin.EscribirId(id, escritor);
            escritor.Write("' \"${__ns_rc-997}\" \"${__ns_n:+_$__ns_n}\"; unset __ns_rc; command read -r __ns_d; unset __ns_d\n"u8);
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
            escritor.Write("\n"u8);
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
        // Antes, el resto de la preparación del shell (que no cabe en la línea de inicialización, EscribirLineaInicializacion):
        // variables para que nada pagine ni use el terminal; 'mesg n', para que otro usuario local del servidor no pueda
        // escribir en el terminal (write, wall) y falsificar líneas de la salida, como los anuncios de las escrituras (sin mesg,
        // como en Debian slim, se quita a mano la escritura del grupo, que es lo que hace mesg; sin terminal o sin permiso,
        // nada, en silencio); y directorioTrabajo (fuente de verdad en C#), que se reexporta como __NS_CWD en base64 para que
        // ninguna ruta rompa la línea (la llamada lo acota a SesionShell.MaxBytesDirectorioTrabajo), con su cd.
        public static void EscribirCapturaPath(Guid id, Guid testigo, Guid centinela, IBufferWriter<byte> escritor, string? directorioTrabajo = null)
        {
            EscribirLineaCentinela(centinela, escritor);
            escritor.Write("__ns_rc=0; export TERM=dumb PAGER=cat GIT_PAGER=cat SYSTEMD_PAGER=cat MANPAGER=cat; { mesg n || chmod g-w -- \"$(tty)\"; } 2>/dev/null; "u8);
            if (!string.IsNullOrEmpty(directorioTrabajo))
            {
                escritor.Write("export __NS_CWD=\"$(printf '%s' '"u8);
                EscribirBase64(directorioTrabajo, escritor);
                escritor.Write("' | base64 -d)\"; "u8);
            }

            escritor.Write("cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; eval \"declare -r -a "u8);
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
            escritor.Write("\n"u8);
        }

        // Ctrl+C va solo en su escritura y la línea de recuperación después, en otra (EscribirRecuperacion): un 'sudo <cmd>'
        // con use_pty (o pidiendo la contraseña) se tragaría lo que llega junto al Ctrl+C, o lo tomaría como contraseñas.
        public static void EscribirCtrlC(IBufferWriter<byte> escritor) => escritor.Write("\u0003"u8);

        // Línea que devuelve el shell a un estado conocido tras Ctrl+C, con el salto y el centinela delante
        // (EscribirPrefijoCentinela): si Ctrl+C abortó la línea en curso (el comando o la espera de una cola), bash los toma
        // como una línea vacía y un comentario; si una cola sigue esperándolos (un programa que atrapa Ctrl+C dejó seguir la
        // línea, o el terminal no convirtió Ctrl+C en señal), la terminan. Termina a su vez en el centinela, como comentario:
        // si ese programa lee una línea, se come la vacía; si lee dos, también el centinela, y la cola lee esta línea en su
        // lugar: la reconoce por el final y termina sin contarla como tecleada (EscribirCola) y sin ejecutarla; la ejecuta el
        // reenvío (SesionShell.InterrumpirAsync). Antes, la cola contaba la línea de recuperación como tecleada con ssh_input.
        // Borra también __ns_d: una cola abortada a mitad pudo dejar en ella la última línea tecleada que leyó.
        // Si la ejecuta otro shell que el Ctrl+C no terminó (uno interactivo anidado: sudo -i, su -, ssh a otra máquina,
        // bash -i; o uno que sustituyó al de la sesión con exec), su marcador lleva MarcadorFin.CodigoShellAnidado
        // (EscribirCola).
        public static void EscribirRecuperacion(Guid id, Guid centinela, IBufferWriter<byte> escritor, IdentidadShell? identidad = null)
        {
            EscribirPrefijoCentinela(centinela, escritor);
            escritor.Write("__ns_rc=130; unset __ns_b __ns_k __ns_d; cd \"${__NS_CWD:-$HOME}\" 2>/dev/null || cd ~; "u8);
            EscribirCola(id, centinela, escritor, identidad);
            escritor.Write(" #"u8);
            EscribirTestigo(centinela, escritor);
            escritor.Write("\n"u8);
        }

        // "\n#<centinela>\n": el salto termina una línea a medias que se tecleara con enter=false, por larga que sea (el núcleo
        // acepta el fin de línea aunque el búfer de la línea, de 4095 bytes, esté lleno, y tira lo que no cabe antes de él), y
        // el centinela llega siempre solo en su línea. Lo que deja ese salto (la línea a medias, o una línea vacía si no la
        // había) la cola lo lee antes del centinela: la línea vacía justo antes del centinela no se cuenta (EscribirCola).
        // Medido sin el salto: con 4094 bytes o más a medias el núcleo tiraba el centinela, la cola se comía el comando
        // siguiente y este agotaba su plazo sin ejecutarse.
        private static void EscribirPrefijoCentinela(Guid centinela, IBufferWriter<byte> escritor)
        {
            escritor.Write("\n"u8);
            EscribirLineaCentinela(centinela, escritor);
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
        // canal), que es la primera línea de todo envío salvo la inicialización (en los de comando y recuperación, tras un salto:
        // EscribirPrefijoCentinela): lee líneas del terminal hasta la que termina en él, y lo anterior es entrada tecleada con
        // ssh_input que ningún comando leyó (una contraseña enviada antes de que la pidiera o cuando ya no la leía, varias
        // líneas, un ssh_input con '\n' o '\r' dentro, Ctrl+D). Lo tecleado y el envío siguiente viajan en orden por el mismo
        // canal, y C# no escribe el envío siguiente hasta recibir este marcador (y ninguna entrada después), así que todo lo
        // tecleado llega antes que el centinela: nada se ejecuta como orden, sin ventana ni plazo que dependan de la red, la
        // carga o el volumen. Cada línea se borra (__ns_d se vacía en cada vuelta, como __ns_x, y el 'unset' final las quita)
        // y se cuenta, salvo:
        // - la línea vacía justo antes del centinela: la deja el salto que lo precede cuando no había una línea a medias (si la
        //   había, ese salto la termina y es ella la que se cuenta, así que entonces no hay línea vacía). Una línea vacía se
        //   cuenta solo cuando llega otra línea que no es el centinela (__ns_v la deja pendiente); así el recuento es exacto
        //   también con líneas vacías tecleadas;
        // - la línea que termina en el centinela: es el centinela o una línea del protocolo que llegó tras un centinela que se
        //   comió otro lector (la de recuperación, EscribirRecuperacion, termina en él): termina la espera y no se cuenta;
        // - un 'read' interrumpido por una señal que el usuario atrapa en el shell ('trap ... USR1', 'trap ... CHLD' y un hijo
        //   que termina mientras se espera), si devuelve más de 128: no es una línea. Defensa: medido, bash 5.2 y 5.3
        //   ejecutan la trampa y siguen leyendo (no hay vuelta que contar), y dash 0.5 y el ash de busybox 1.37 devuelven 1,
        //   que se cuenta como un EOF (solo pasa si una de ellos ejecuta la cola, en un shell anidado).
        // El marcador lleva "${__ns_rc-997}" y __ns_rc se borra justo después: cada envío lo fija antes de su cola, así que si
        // la línea que lo fija no llega a ejecutarse como tal (otro lector se comió el salto que la separa del base64 y bash
        // la juntó al valor de __ns_b), el marcador lleva MarcadorFin.CodigoEnvioIncompleto y no el código del comando
        // anterior (medido antes: «Completado, código 7» de un comando que no se ejecutó).
        // El recuento solo se sabe cuando llega el centinela, así que viaja en el marcador del envío SIGUIENTE
        // ("${__ns_n:+_$__ns_n}", MarcadorFin): el aviso va en la respuesta de ese comando. Se queda en
        // MarcadorFin.MaxLineasDescartadas (más cifras romperían el marcador), pero el bucle descarta todas.
        // Un Ctrl+C (interrupción: ssh_cancel o el plazo; ssh_input no lo envía) aborta la espera o el comando en curso, y el
        // núcleo vacía la entrada pendiente del terminal (sin 'stty noflsh'): lo tecleado tampoco se ejecuta. __ns_n solo se
        // fija si el centinela llegó; si Ctrl+C aborta la espera (el centinela se lo comió otro proceso que leía del terminal
        // y la línea que la terminaría era la del comando del LLM), queda vacío y nada se da por descartado. Con 'noflsh' (si
        // el comando lo dejó así) el núcleo no la vacía y bash ejecutaría como órdenes las líneas pendientes antes de la línea
        // de recuperación: no se puede evitar desde aquí.
        // Ctrl+D al principio de una línea es un EOF del terminal: 'read' devuelve 1; se cuenta como una línea y se sigue
        // esperando. MaxEofSeguidos EOF seguidos solo los da un terminal que ya no existe (al colgarse el terminal bash muere
        // por SIGHUP; si el comando lo ignoró, 'read' devuelve error sin fin): entonces el shell termina ('exit'), en lugar de
        // abandonar la espera y leer como orden lo que llegue después.
        // 'command read', 'command [' y 'command exit', con IFS y TMOUT vacíos para 'read': una función o un alias 'read',
        // '[', 'test' o 'exit' no los rompen ('command' no busca funciones y tras él no se expanden alias), el
        // contenido no pierde los espacios de los extremos y un TMOUT del usuario no corta la espera. 'command' también se
        // puede redefinir como función y entonces el comando siguiente agota su plazo; como un alias de una palabra reservada
        // de las que usa (while, do, done, '{', '}'), que rompe la sintaxis de la línea: no se protege más.
        // Sin 'break' (también se puede redefinir) ni 'case' (un alias lo rompe, como en la comprobación de identidad): termina
        // por la condición del 'while' y el centinela se reconoce con longitudes ("${__ns_d%'<centinela>'}" es más corto solo si
        // la línea termina en él). Sin '!' (con la expansión del historial activa, '!__ns_f' daría «event not found»). Con
        // 'set -x' la traza del bucle va a /dev/null con su stderr. Válida con 'set -u', 'set -e', 'set -f', 'set -a', un IFS
        // raro y las opciones de patrones (nocasematch, extglob, nullglob, failglob).
        // Todos los envíos salvo la inicialización empiezan por el centinela, y todas las colas lo esperan salvo la de la
        // línea de inicialización, que consume una sola línea (EscribirLineaInicializacion explica por qué).
        // Solo construcciones POSIX: si la cola la ejecuta otro shell (uno anidado que dejó el comando, p. ej. el ash de busybox
        // de root tras 'sudo -i' en Alpine, o dash), el marcador sale con MarcadorFin.CodigoShellAnidado y ese shell también
        // descarta lo tecleado hasta el centinela, en vez de ejecutarlo, mientras el canal se cierra.
        // Sin '\n' final: lo pone quien la escribe (la de recuperación termina antes en el centinela).
        private static void EscribirCola(Guid id, Guid centinela, IBufferWriter<byte> escritor, IdentidadShell? identidad = null)
        {
            if (identidad is { } esperada)
            {
                escritor.Write("__ns_t=\"$$ $PPID ${"u8);
                escritor.Write(VariableTestigoUtf8);
                escritor.Write("-}\"; __ns_t=${__ns_t#'"u8);
                esperada.Escribir(escritor);
                // 999 = MarcadorFin.CodigoShellAnidado.
                escritor.Write("'}; __ns_t=${__ns_t:+999}; __ns_rc=${__ns_t:-${__ns_rc-997}}; "u8);
            }

            escritor.Write("printf '%s_%s_%d%s__\\n' '__NSX' '"u8);
            MarcadorFin.EscribirId(id, escritor);
            // 1000 = MaxEofSeguidos; 129 = CodigoSalidaTopeEof; 999999 = MarcadorFin.MaxLineasDescartadas.
            escritor.Write("' \"${__ns_rc-997}\" \"${__ns_n:+_$__ns_n}\"; unset __ns_rc; __ns_n=; __ns_c=0; __ns_v=0; __ns_e=0; __ns_f=0; while command [ \"$__ns_f\" -eq 0 ] && { TMOUT= IFS= command read -r __ns_d; __ns_s=$?; { command [ \"$__ns_s\" -ne 1 ] && __ns_e=0; } || command [ \"$((__ns_e+=1))\" -lt 1000 ] || command exit 129; }; do __ns_x=${__ns_d%'#"u8);
            EscribirTestigo(centinela, escritor);
            escritor.Write("'}; __ns_f=$(( ${#__ns_x} < ${#__ns_d} )); __ns_g=$(( __ns_f == 0 && __ns_s <= 128 )); __ns_c=$(( __ns_c + __ns_g * (__ns_v + (${#__ns_d} > 0)) )); __ns_c=$(( __ns_c < 999999 ? __ns_c : 999999 )); __ns_v=$(( __ns_g ? ${#__ns_d} == 0 : __ns_v * (__ns_s > 128) )); __ns_d=; __ns_x=; done 2>/dev/null; __ns_n=${__ns_c#0}; unset __ns_d __ns_x __ns_c __ns_e __ns_f __ns_g __ns_s __ns_v"u8);
        }
    }
}
