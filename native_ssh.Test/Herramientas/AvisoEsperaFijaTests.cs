using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Herramientas
{
    // Aviso de espera fija: un 'sleep' largo (10 s o más) seguido de otra orden con ';', '&&' o un salto de línea espera un
    // tiempo a ciegas en lugar de comprobar la condición. Un 'sleep' suelto, uno corto o uno dentro de un bucle until/while
    // (el patrón bueno) no avisan. No bloquea: solo añade el aviso.
    public sealed class AvisoEsperaFijaTests
    {
        private const string AvisoEsperado = "Sugerencia de rendimiento: Has esperado un tiempo fijo con 'sleep' antes de otra orden vía ssh_exec. Para esperar a una condición, usa un bucle que la compruebe: 'until <condición>; do sleep 2; done' para esperar a que algo empiece a cumplirse (un puerto que escucha, un archivo que aparece) y 'while <condición>; do sleep 2; done' para esperar a que algo deje de estar activo (un proceso, un archivo de bloqueo), los dos dentro de timeout <s> bash -c '…' para que la espera tenga límite. Para una orden larga ya lanzada, usa 'ssh_output' en lugar de esperar con 'sleep'.";

        [Theory]
        [InlineData("sleep 60; tail -n 5 /var/log/app.log | wc -l")]
        [InlineData("sleep 30 && ps aux | wc -l")]
        [InlineData("sleep 10; systemctl is-active nginx")]
        [InlineData("sleep 10s; curl -s localhost:8080/health")]
        [InlineData("sleep 1m && docker ps")]
        [InlineData("sleep 0.5m; uptime")]
        [InlineData("sleep 1h; uptime")]
        [InlineData("sleep 5 5; uptime")]
        [InlineData("sleep 20\nsystemctl status app")]
        [InlineData("sleep 20\r\nsystemctl status app")]
        [InlineData("systemctl restart app; sleep 15; systemctl is-active app")]
        [InlineData("timeout 100 sleep 60; uptime")]
        [InlineData("sudo sleep 60; uptime")]
        [InlineData("( sleep 60 ); uptime")]
        [InlineData("until curl -s x; do sleep 2; done; sleep 30; uptime")]
        [InlineData("while pgrep -x apt >/dev/null; do sleep 2; done\nsleep 30\nuptime")]
        [InlineData("done; sleep 60; uptime")]
        public void SleepLargoSeguidoDeOtraOrden_Avisa(string comando) =>
            _ = HerramientasSsh.DetectarAvisoAntipatron(comando).Should().Be(AvisoEsperado);

        [Theory]
        [InlineData("sleep 60")]
        [InlineData("sleep 60;")]
        [InlineData("sleep 60 # espera\n")]
        [InlineData("echo empieza; sleep 60")]
        [InlineData("sleep 9; uptime")]
        [InlineData("sleep 9.9; uptime")]
        [InlineData("sleep 2 && systemctl status app")]
        [InlineData("sleep 0.5; uptime")]
        [InlineData("sleep 60 & uptime")]
        [InlineData("sleep 60 | cat")]
        [InlineData("sleep 60 || uptime")]
        [InlineData("sleep $ESPERA; uptime")]
        [InlineData("sleep abc; uptime")]
        [InlineData("sleep; uptime")]
        [InlineData("sleep --help; uptime")]
        [InlineData("until curl -s localhost:8080; do sleep 10; done")]
        [InlineData("until curl -s localhost:8080; do sleep 30; echo esperando; done; echo listo")]
        [InlineData("while pgrep -x apt >/dev/null; do\n  sleep 15\n  echo esperando\ndone")]
        [InlineData("for i in 1 2 3; do sleep 30; curl -s x; done")]
        [InlineData("timeout 120 bash -c 'until curl -s x; do sleep 2; done'; echo listo")]
        [InlineData("bash -c 'sleep 60; uptime'")]
        [InlineData("echo 'sleep 60; uptime'")]
        [InlineData("cat <<EOF\nsleep 60\nuptime\nEOF")]
        [InlineData("{ sleep 60; }")]
        public void SleepSueltoCortoOEnBucle_NoAvisa(string comando) =>
            _ = HerramientasSsh.DetectarAvisoAntipatron(comando).Should().BeNull();

        // Junto a otros avisos: sin repetir y en el orden en que aparecen, tras los de grep, find y cd.
        [Fact]
        public void ConOtrosAvisos_SeJuntaSinRepetir()
        {
            // Act
            string? aviso = HerramientasSsh.DetectarAvisoAntipatron("cd /srv && sleep 30; cat app.log; sleep 20; ls");

            // Assert
            _ = aviso.Should().StartWith("Sugerencia de rendimiento: Has encadenado 'cd <ruta> &&'");
            _ = aviso!.Split("Has esperado un tiempo fijo").Should().HaveCount(2, "un solo aviso de espera");
            _ = aviso.IndexOf(AvisoEsperado, StringComparison.Ordinal).Should().BeLessThan(aviso.IndexOf("'ssh_read_file'", StringComparison.Ordinal));
        }

        // El texto nombra los dos bucles, cada uno con su uso, y no aconseja 'while !'.
        [Fact]
        public void Texto_NombraLosDosBuclesSinWhileNegado() =>
            _ = AvisoEsperado.Should().Contain("'until <condición>; do sleep 2; done' para esperar a que algo empiece").And.Contain("'while <condición>; do sleep 2; done' para esperar a que algo deje de estar activo").And.Contain("ssh_output").And.NotContain("while !");

        // Casos de la auditoría del tramo (95): subshells y grupos pegados, palabras reservadas (then, else, case, do), bucles
        // anidados en una línea, comentarios, prefijos y rutas, duraciones que acepta sleep de GNU coreutils, separadores y
        // heredocs. true: avisa; false: no avisa de la espera (puede avisar de otra cosa).
        public static TheoryData<string, bool, string> CasosAuditoria => new()
        {
            // Subshells, grupos y llaves pegadas
            { "(sleep 60); uptime", true, "subshell sin espacio antes de ')'" },
            { "(sleep 60) && uptime", true, "subshell sin espacio, &&" },
            { "( sleep 60 ); uptime", true, "subshell con espacios (prueba de la rama)" },
            { "{ sleep 60;}; uptime", true, "grupo con ';}' pegado" },
            { "{ sleep 60; } && uptime", true, "grupo" },
            { "(sleep 60; uptime)", true, "dentro de la subshell" },
            { "f() { sleep 60; uptime; }; f", true, "función" },
            { "f() { sleep 60; }; f; uptime", true, "función definida y llamada: espera fija" },
            // $( ) y comillas: opacos
            { "echo $(sleep 60); uptime", false, "dentro de $( ): opaco (informativo)" },
            { "x=$(sleep 60; date); echo $x", false, "dentro de $( ) (informativo)" },
            { "bash -c 'sleep 60; uptime'", false, "bash -c (opaco)" },
            { "timeout 120 bash -c 'until curl -s x; do sleep 2; done'; uptime", false, "patrón aconsejado" },
            { "timeout 120 bash -c 'while pgrep -x apt; do sleep 2; done' && uptime", false, "patrón aconsejado con while" },
            // if / case / palabras reservadas
            { "if true; then sleep 60; fi; uptime", true, "then sleep en una línea" },
            { "if true; then\n  sleep 60\n  uptime\nfi", true, "if en varias líneas" },
            { "if x; then y; else sleep 60; fi; uptime", true, "else sleep" },
            { "case a in a) sleep 60;; esac; uptime", true, "case" },
            { "case a in\n a)\n  sleep 60\n  uptime\n  ;;\nesac", true, "case en varias líneas" },
            { "! sleep 60; uptime", true, "con !" },
            { "time sleep 60; uptime", true, "time" },
            // Bucles
            { "until x; do sleep 2; done; sleep 60; uptime", true, "tras un until en una línea" },
            { "(until x; do sleep 2; done); sleep 60; uptime", true, "until en subshell con 'done)' pegado" },
            { "{ until x; do sleep 2; done;}; sleep 60; uptime", true, "until en grupo" },
            { "for ((i=0;i<3;i++)); do sleep 1; done; sleep 60; uptime", true, "for aritmético" },
            { "select x in a b; do sleep 30; echo $x; done", false, "select" },
            { "for i in 1 2; do until x; do sleep 2; done; sleep 60; y; done", false, "until anidado en una línea dentro de for" },
            { "for i in 1 2; do while x; do sleep 2; done; sleep 60; y; done", false, "while anidado en una línea dentro de for" },
            { "while true; do\n  until x; do sleep 2; done\n  sleep 30\n  y\ndone", false, "anidado en varias líneas" },
            { "while true; do sleep 60; y; done", false, "sleep tras do" },
            { "until x; do echo w; sleep 30; done; y", false, "sleep en el cuerpo tras otra orden" },
            { "while read -r l; do echo $l; done < <(cat f); sleep 30; uptime", true, "tras while con sustitución de procesos" },
            { "while x; do y; done | tee log; sleep 30; uptime", true, "tras while en tubería" },
            { "echo until; sleep 60; uptime", true, "until como argumento" },
            { "echo while; sleep 60; uptime", true, "while como argumento" },
            // Comentarios
            { "sleep 60 # espera\nuptime", true, "comentario y salto" },
            { "# sleep 60; uptime\necho hola", false, "todo comentario" },
            { "echo a # sleep 60; uptime", false, "comentario al final" },
            { "echo a#b; sleep 60; uptime", true, "# dentro de palabra" },
            // Argumento de otra orden
            { "echo sleep 60; ls", false, "argumento de echo" },
            { "timeout 70 sleep 60; ls", true, "timeout N sleep (espera fija igualmente)" },
            { "timeout -s KILL 70 sleep 60; ls", true, "timeout con -s" },
            { "git commit -m 'sleep 60'; ls", false, "en una cadena" },
            { "watch -n 60 ls; uptime", false, "watch" },
            { "xargs sleep < f; uptime", false, "xargs sleep" },
            // Rutas y prefijos
            { "/bin/sleep 60; uptime", true, "/bin/sleep" },
            { "/usr/bin/sleep 60 && uptime", true, "/usr/bin/sleep" },
            { "command sleep 60; uptime", true, "command sleep" },
            { "builtin sleep 60; uptime", true, "builtin sleep (bash no tiene sleep interno: falla; informativo)" },
            { "env sleep 60; uptime", true, "env" },
            { "nice -n 5 sleep 60; uptime", true, "nice -n" },
            { "sudo -u root sleep 60; uptime", true, "sudo -u" },
            { "nohup sleep 60; uptime", true, "nohup" },
            { "\\sleep 60; uptime", true, "\\sleep" },
            { "'sleep' 60; uptime", true, "'sleep' entre comillas" },
            { "s\"leep\" 60; uptime", true, "s\"leep\"" },
            // Duraciones
            { "sleep infinity; uptime", true, "infinity" },
            { "sleep Infinity; uptime", true, "Infinity (GNU lo acepta)" },
            { "sleep INF; uptime", true, "INF (GNU lo acepta)" },
            { "sleep 1e2; uptime", true, "1e2 (GNU lo acepta)" },
            { "sleep +60; uptime", true, "+60 (GNU lo acepta)" },
            { "sleep 60.; uptime", true, "60." },
            { "sleep .5m; uptime", true, ".5m = 30 s" },
            { "sleep 0x3c; uptime", true, "0x3c (GNU lo acepta: 60 s)" },
            { "sleep 9 1; uptime", true, "suma = 10" },
            { "sleep NaN; uptime", false, "NaN (GNU lo rechaza)" },
            { "sleep NaN 60; uptime", true, "NaN + 60 (informativo)" },
            { "sleep -- 60; uptime", true, "-- (GNU lo acepta)" },
            { "sleep 60s 1m; uptime", true, "suma con sufijos" },
            { "sleep 1m30s; uptime", false, "1m30s (GNU lo rechaza)" },
            { "sleep $((60*2)); uptime", false, "aritmética: duración desconocida" },
            { "sleep 10; uptime", true, "umbral exacto" },
            { "sleep 9.99; uptime", false, "por debajo del umbral" },
            { "sleep 1" + new string('0', 400) + "; uptime", true, "número enorme" },
            // Separadores, redirecciones, &
            { "sleep 60;uptime", true, "sin espacios" },
            { "sleep 60&&uptime", true, "&& sin espacios" },
            { "sleep 60 2>/dev/null; uptime", true, "redirección" },
            { "sleep 60 </dev/null && uptime", true, "redirección de entrada" },
            { "sleep 60 &\nwait; uptime", false, "& + wait (informativo)" },
            { "nohup app & sleep 30; tail log", true, "tras lanzar en segundo plano" },
            { "sleep 60 | cat; uptime", false, "en tubería (informativo)" },
            { "sleep 60 || uptime", false, "||" },
            { "sleep 60 \\\n && uptime", true, "&& tras continuación de línea" },
            { "sleep \\\n 60; uptime", true, "duración tras continuación" },
            { "sle\\\nep 60; uptime", true, "nombre partido con \\ (bash lo junta)" },
            { "sleep 60 \\\n; uptime", true, "; tras continuación" },
            { "sleep 60\n\n\nuptime", true, "líneas en blanco" },
            { "sleep 60\n# fin\n", false, "solo comentario detrás" },
            { "sleep 60;;", false, "';;' sin orden detrás" },
            { "sleep 60; ;", false, "';' vacío detrás" },
            { "sleep 60 && \n", false, "&& colgando" },
            // Heredocs
            { "cat <<EOF\nsleep 60\nuptime\nEOF\nsleep 60; uptime", true, "heredoc y después la espera" },
            { "cat <<'EOF' > s.sh\nsleep 60\nuptime\nEOF", false, "heredoc con la espera dentro" },
            { "cat <<-EOF\n\tsleep 60\n\tuptime\n\tEOF", false, "<<- con tabuladores" },
            { "sleep 60 <<EOF\nx\nEOF\nuptime", true, "sleep con heredoc" },
            { "cat <<EOF; sleep 60; uptime\nx\nEOF", true, "heredoc y espera en la misma línea" },
            // Un for con el cuerpo entre llaves no lleva done: no deja la profundidad colgada (lo de después sigue contando).
            { "for i in 1 2; { echo $i; }; sleep 60; uptime", true, "for … { …; } sin done" },
            { "for i in 1 2; { echo $i; }\nsleep 60\nuptime", true, "for … { …; } sin done, en varias líneas" },
        };

        [Theory]
        [MemberData(nameof(CasosAuditoria))]
        public void CasosDeLaAuditoria_AvisanComoSeEspera(string comando, bool isAvisa, string nota)
        {
            // Act
            string? aviso = HerramientasSsh.DetectarAvisoAntipatron(comando);

            // Assert
            bool isAvisoDeEspera = aviso is not null && aviso.Contains("Has esperado un tiempo fijo con 'sleep'", StringComparison.Ordinal);
            _ = isAvisoDeEspera.Should().Be(isAvisa, nota);
        }

        // Casos de la auditoría de la fusión (71): 'do' fuera de un bucle (patrón de case, entre comillas), esperas sin nada
        // detrás salvo cierres, sleep como condición, bucles con llaves, prefijos con opciones, duraciones y comillas.
        public static TheoryData<string, bool, string> CasosAuditoriaFusion => new()
        {
            // Palabra 'do' fuera de un bucle
            { "case x in do) echo a;; esac; sleep 60; uptime", true, "'do' como patrón de case: ¿profundidad colgada?" },
            { "case x in\n  do) echo a ;;\nesac\nsleep 60\nuptime", true, "'do' como patrón de case en varias líneas" },
            { "echo do; sleep 60; uptime", true, "do como argumento" },
            { "d\\o=1; sleep 60; uptime", true, "asignación rara" },
            { "'do' 2>/dev/null; sleep 60; uptime", true, "'do' entre comillas es una orden (bash: do: orden no encontrada)" },
            // Espera sin nada detrás salvo cierres
            { "if x; then sleep 60; fi", false, "then sleep sin nada detrás del fi" },
            { "if x; then\n  sleep 60\nfi", false, "then sleep en varias líneas sin nada detrás" },
            { "if x; then y; else sleep 60; fi", false, "else sleep sin nada detrás" },
            { "{ sleep 60; }", false, "grupo solo" },
            { "(sleep 60)", false, "subshell sola" },
            { "case a in a) sleep 60;; esac", false, "case solo" },
            { "f() { sleep 60; }", false, "solo definir la función" },
            // Condición del if/elif/while
            { "if sleep 60; then uptime; fi", true, "sleep como condición del if" },
            { "if x; then y; elif sleep 60; then uptime; fi", true, "sleep como condición del elif" },
            { "sleep 60; if x; then y; fi", true, "espera antes de un if" },
            // Bucles con llaves / anidados / redirecciones
            { "for i in 1 2; { sleep 60; uptime; }", false, "for con cuerpo entre llaves (cuerpo de bucle)" },
            { "for i in 1 2; { sleep 2; }; sleep 60; uptime", true, "tras un for con llaves" },
            { "while x; do sleep 15; done</dev/null; sleep 60; uptime", true, "done con redirección pegada" },
            { "while x; do sleep 15; done 2>&1 | tee l; sleep 60; uptime", true, "done en tubería" },
            { "while x; do sleep 15; done & sleep 60; uptime", true, "bucle en segundo plano" },
            { "while x\ndo\n  sleep 60\n  y\ndone", false, "do solo en su línea" },
            { "for i in 1 2 3\ndo sleep 60; y; done", false, "for en dos líneas" },
            { "while x; do if y; then sleep 60; fi; z; done", false, "then dentro de un bucle" },
            { "while x; do case $a in b) sleep 60;; esac; z; done", false, "case dentro de un bucle" },
            { "if x; then\n  while y; do sleep 60; z; done\nfi", false, "bucle dentro de then" },
            { "while x; do y; done; done; sleep 60; z", true, "done de más" },
            { "time while x; do sleep 2; done; sleep 60; y", true, "time delante del bucle" },
            { "x=$(for i in 1; do echo; done); sleep 60; uptime", true, "bucle dentro de $( )" },
            { "select x in a; do break; done; sleep 60; y", true, "tras select" },
            { "until x; do sleep 2; done && sleep 30 && curl x", true, "tras until con &&" },
            // Prefijos con opciones
            { "time -p sleep 60; uptime", true, "time -p" },
            { "sudo -- sleep 60; uptime", true, "sudo --" },
            { "env -i sleep 60; uptime", true, "env -i" },
            { "env FOO=1 sleep 60; uptime", true, "env con asignación" },
            { "timeout --foreground 70 sleep 60; uptime", true, "timeout --foreground" },
            { "timeout 1m sleep 60; uptime", true, "timeout con sufijo" },
            { "nice -10 sleep 60; uptime", true, "nice -10" },
            { "stdbuf -oL sleep 60; uptime", true, "stdbuf" },
            { "builtin command sleep 60; uptime", true, "builtin command" },
            { "coproc sleep 60; uptime", false, "coproc (asíncrono)" },
            // Duraciones
            { "sleep 1E2; uptime", true, "1E2" },
            { "sleep 1e+2; uptime", true, "1e+2" },
            { "sleep 1.5e1; uptime", true, "1.5e1 = 15" },
            { "sleep .1e2; uptime", true, ".1e2 = 10" },
            { "sleep 1e-1; uptime", false, "1e-1" },
            { "sleep 0X3C; uptime", true, "0X3C" },
            { "sleep 0x3cs; uptime", true, "0x3cs" },
            { "sleep 0x3c.8; uptime", true, "0x3c.8 (strtod: 60,5)" },
            { "sleep 0x1p6; uptime", true, "0x1p6 (strtod: 64)" },
            { "sleep infinitys; uptime", true, "infinitys" },
            { "sleep infinit; uptime", false, "infinit (GNU lo rechaza)" },
            { "sleep ' 60'; uptime", true, "' 60' (strtod salta el espacio)" },
            { "sleep '60 '; uptime", false, "'60 ' (GNU lo rechaza)" },
            { "sleep 60e; uptime", false, "60e (GNU lo rechaza)" },
            { "sleep 1e400; uptime", true, "1e400" },
            { "sleep 0.0000000000000000000001e23; uptime", true, "exponente que compensa" },
            { "sleep 5 5; uptime", true, "5+5" },
            { "sleep -- -- 60; uptime", false, "segundo -- (GNU: argumento inválido)" },
            { "sleep $T; uptime", false, "variable" },
            // Comillas y escapes
            { "s'le'ep 60; uptime", true, "comillas partidas" },
            { "sleep 6\"0\"; uptime", true, "duración partida" },
            { "sleep 60 \\; uptime", false, "';' escapado: es un argumento" },
            { "sleep 60 ';' uptime", false, "';' entre comillas" },
            { "echo \"$(sleep 1 ')' )\"; sleep 60; uptime", true, "comillas desalineadas por $( ')' )" },
            { "echo \"$(echo '\"')\"; sleep 60; uptime", true, "$( '\"' ) dentro de comillas dobles" },
            { "echo `echo \\`date\\``; sleep 60; uptime", true, "comillas invertidas anidadas" },
            { "echo $'a\\'b'; sleep 60; uptime", true, "$'…' con comilla escapada" },
            { "echo ${x:-}}; sleep 60; uptime", true, "llave de cierre suelta" },
            { "arr=(a b); sleep 60; uptime", true, "asignación de array" },
            { "[[ $a == b ]]; sleep 60; uptime", true, "[[ ]]" },
            { "(( i++ )); sleep 60; uptime", true, "(( ))" },
        };

        [Theory]
        [MemberData(nameof(CasosAuditoriaFusion))]
        public void CasosDeLaAuditoriaDeLaFusion_AvisanComoSeEspera(string comando, bool isAvisa, string nota) =>
            _ = IsAvisoDeEspera(comando).Should().Be(isAvisa, nota);

        // Lo que se ejecuta después de la espera: los cierres (fi, esac, done) no son una comprobación; tras el final de una rama
        // (else, elif, la siguiente cláusula de un case) lo siguiente es lo que va detrás del cierre; las palabras reservadas
        // entre comillas son órdenes (bash: «orden no encontrada») y no abren ni cierran nada.
        public static TheoryData<string, bool, string> CasosBloques => new()
        {
            { "if x; then sleep 60; else y; fi", false, "then que termina en la espera: el else no se ejecuta" },
            { "if x; then sleep 60; else y; fi; uptime", true, "y después del fi, una orden" },
            { "if x; then sleep 60; elif y; then z; fi", false, "el elif no se ejecuta tras la rama then" },
            { "if x; then sleep 60; elif y; then z; fi; uptime", true, "tras el fi" },
            { "if x; then\n  sleep 60\nelse\n  y\nfi", false, "else en su línea" },
            { "if x; then if y; then sleep 60; fi; else z; fi", false, "if anidado: el else es del de fuera" },
            { "if x; then if y; then sleep 60; fi; uptime; fi", true, "if anidado con una orden detrás del fi de dentro" },
            { "if x; then sleep 60; fi && uptime", true, "fi && orden" },
            { "if x; then sleep 60; 'fi'; fi", true, "'fi' entre comillas es una orden" },
            { "case a in a) sleep 60;; b) uptime;; esac", false, "la cláusula siguiente no se ejecuta" },
            { "case a in a) sleep 60;; b) y;; esac; uptime", true, "tras el esac" },
            { "case a in\n  a) sleep 60\n  ;;\n  b) uptime\n  ;;\nesac", false, "';;' en su línea" },
            { "case a in a) sleep 60;& b) uptime;; esac", true, "';&' sigue en la cláusula siguiente" },
            { "case a in a) sleep 60;;& b) uptime;; esac", true, "';;&' prueba los patrones siguientes: la cláusula siguiente puede ejecutarse" },
            { "case x in x) sleep 60;;& *) uptime;; esac", true, "';;&' con '*' detrás: uptime se ejecuta" },
            { "case x in (do) echo a;; esac\nsleep 60\nuptime", true, "patrón con '(' delante" },
            { "case x in a|do) echo a;; esac; sleep 60; uptime", true, "do como alternativa de un patrón" },
            { "case x in\n  do|a) echo a ;;\nesac\nsleep 60\nuptime", true, "do como primera alternativa, en varias líneas" },
            { "case x in esac; sleep 60; uptime", true, "case vacío" },
            { "case x in a) case y in do) z;; esac;; esac; sleep 60; uptime", true, "case anidado" },
            { "while x; do 'done'; sleep 60; y; done", false, "'done' entre comillas no cierra el bucle" },
            { "\"do\"; sleep 60; uptime", true, "\"do\" entre comillas dobles" },
            { "d\\o; sleep 60; uptime", true, "d\\o es una orden" },
            { "while sleep 60; do y; done", false, "sleep como condición de while: es el bucle" },
            { "until sleep 60; do y; done", false, "sleep como condición de until" },
            { "if ! sleep 60; then y; fi", true, "if ! sleep" },
            { "if sleep 60\nthen\n  uptime\nfi", true, "then en su línea" },
            { "sleep 60; done", false, "done suelto: no es una comprobación" },
            // for y select con el cuerpo entre llaves (bash los acepta: el cuerpo se ejecuta en cada vuelta, como con do … done)
            { "select x in a; { sleep 60; y; }", false, "select con llaves" },
            { "for i in 1 2\n{\n  sleep 60\n  y\n}", false, "for con llaves en varias líneas" },
            { "for i in 1 2; { for j in 3; { sleep 2; }; }; sleep 60; z", true, "llaves anidadas: la profundidad vuelve a 0" },
            { "for i in 1 2; { { sleep 60; y; }; }", false, "grupo dentro del cuerpo" },
            { "f() { for i in 1; { sleep 2; }; }; sleep 60; z", true, "for con llaves dentro de una función" },
            // Duraciones
            { "sleep 0x.4p6; uptime", true, "0x.4p6 = 16" },
            { "sleep 0x1P-3; uptime", false, "0x1P-3 = 0,125" },
            { "sleep 0x1p; uptime", false, "0x1p (GNU lo rechaza)" },
            { "sleep 0x.p6; uptime", false, "0x.p6 (GNU lo rechaza)" },
            { "sleep '\t60'; uptime", true, "tabulador delante (strtod lo salta)" },
            { "sleep -- 60 --; uptime", false, "'--' detrás de la duración (GNU: argumento inválido)" },
            // Comillas
            { "echo \"$(echo ')')\"; sleep 60; uptime", true, "$( ')' ) dentro de comillas dobles" },
            { "echo \"`echo '\"'`\"; sleep 60; uptime", true, "`…` con '\"' dentro de comillas dobles" },
            { "echo $'\\''; sleep 60; uptime", true, "$'\\''" },
            { "echo $'do'; sleep 60; uptime", true, "$'do' como argumento" },
            // $'…' decodifica los escapes de bash: la duración es la que recibe sleep.
            { "sleep $'\\t60'; uptime", true, "$'\\t60' = tabulador + 60" },
            { "sleep $'\\n60'; uptime", true, "$'\\n60' = salto de línea + 60" },
            { "sleep $'6\\x30'; uptime", true, "$'6\\x30' = 60" },
            { "sleep $'\\0660'; uptime", true, "$'\\0660' = 60 (octal)" },
            { "sleep $'6\\q0'; uptime", false, "$'6\\q0' = 6\\q0 (GNU lo rechaza)" },
        };

        [Theory]
        [MemberData(nameof(CasosBloques))]
        public void CierresRamasYPalabrasEntreComillas_AvisanComoBash(string comando, bool isAvisa, string nota) =>
            _ = IsAvisoDeEspera(comando).Should().Be(isAvisa, nota);

        private static bool IsAvisoDeEspera(string comando) =>
            HerramientasSsh.DetectarAvisoAntipatron(comando) is { } aviso && aviso.Contains("Has esperado un tiempo fijo con 'sleep'", StringComparison.Ordinal);

        // Los demás avisos dentro de bucles y ramas (then, else y do abren una lista de órdenes): los mismos que antes, salvo
        // 'cd <ruta> &&' dentro de un bucle, que no tiene alternativa (ssh_set_cwd no sirve para un directorio que cambia en
        // cada vuelta). Las condiciones de if, elif, while y until no avisan de nada salvo de la espera.
        public static TheoryData<string, string[]> CasosOtrosAvisos => new()
        {
            { "for f in *.log; do cat $f; done", ["Has leído un archivo con 'cat' vía ssh_exec"] },
            { "for d in a b; do cd $d && make; cd ..; done", [] },
            { "if [ -d x ]; then cd x && make; fi", ["Has encadenado 'cd <ruta> &&' en ssh_exec"] },
            { "for h in a b; do ls /srv/$h; done", ["Has ejecutado 'ls' vía ssh_exec"] },
            { "for f in $(ls); do grep -q x $f && echo $f; done", ["Has ejecutado 'grep' sobre el sistema de archivos vía ssh_exec"] },
            { "while read f; do stat $f; done < lista", ["Has ejecutado 'stat' vía ssh_exec"] },
            { "for f in a b; do sed -i 's/a/b/' $f; done", ["Has editado un archivo en el sitio con 'sed -i' vía ssh_exec"] },
            { "for i in 1 2; do echo x > f$i; done", [] },
            { "if [ -f log ]; then tail -n 20 log; fi", ["Has leído un archivo con 'tail' vía ssh_exec"] },
            { "if x; then head -5 f; else cat g; fi", ["Has leído un archivo con 'head' vía ssh_exec", "Has leído un archivo con 'cat' vía ssh_exec"] },
            { "for f in *.conf; do sed -n '1,5p' $f; done", ["Has leído un archivo con 'sed -n' vía ssh_exec"] },
            { "while true; do tail -f log; done", [] },
            { "for p in a b; do find /srv/$p -name '*.log'; done", ["Has ejecutado 'find' vía ssh_exec"] },
            { "until curl -s x; do sleep 2; done", [] },
            { "for f in a b; do cat > $f <<EOF\nx\nEOF\ndone", ["Has escrito un archivo con 'cat > archivo <<' vía ssh_exec"] },
            { "for f in a b; do echo $f; done | grep a", [] },
            { "if grep -q x f; then echo si; fi", [] },
            { "for f in *.c; do grep -rn TODO $f; done", ["Has ejecutado 'grep' sobre el sistema de archivos vía ssh_exec"] },
            { "for s in a b; do systemctl status $s; done", [] },
            { "do cat f", ["Has leído un archivo con 'cat' vía ssh_exec"] },
            { "then cat f", ["Has leído un archivo con 'cat' vía ssh_exec"] },
            { "else ls", ["Has ejecutado 'ls' vía ssh_exec"] },
            { "echo do cat f", [] },
            { "for i in 1 2; do tree /srv; done", ["Has ejecutado 'tree' vía ssh_exec"] },
            { "for f in a b; do printf x >> $f; done", [] },
            { "for f in a b; do tee $f < x; done", [] },
            { "for f in a b; do rg x $f; done", ["Has ejecutado 'rg' sobre el sistema de archivos vía ssh_exec"] },
            { "if true; then cat f | wc -l; fi", [] },
            { "for d in a b\ndo\n  cd $d && make\ndone", [] },
            { "for d in a b; { cd $d && make; }", [] },
            { "while x; do y; done; cd /srv && make", ["Has encadenado 'cd <ruta> &&' en ssh_exec"] },
            { "if cat f; then y; fi", [] },
            { "while cat f; do y; done", [] },
            { "if x; then y; elif ls; then z; fi", [] },
            { "case $c in\n  ls) echo listar;;\nesac", [] },
        };

        [Theory]
        [MemberData(nameof(CasosOtrosAvisos))]
        public void OtrosAvisosEnBuclesYRamas_SonLosEsperados(string comando, string[] esperados)
        {
            // Act
            string? aviso = HerramientasSsh.DetectarAvisoAntipatron(comando);

            // Assert
            if (esperados.Length == 0)
            {
                _ = aviso.Should().BeNull();
                return;
            }

            _ = aviso.Should().NotBeNull();
            _ = aviso!.Split("Sugerencia de ").Length.Should().Be(esperados.Length + 1);
            foreach (string esperado in esperados)
                _ = aviso.Should().Contain(esperado);
        }

        // Tiempo lineal con una orden que empieza por muchas palabras reservadas y sigue con 'case x in' repetido: la cabecera
        // de case se reconoce sin volver a recorrer las palabras de delante (con 200 000 caracteres, un recorrido cuadrático
        // tardaba unos 12 s).
        [Theory]
        [InlineData("! ")]
        [InlineData("if ")]
        [InlineData("then ")]
        public void PalabrasReservadasYCaseRepetido_TardaPoco(string prefijo)
        {
            // Arrange
            System.Text.StringBuilder texto = new(200_100);
            while (texto.Length < 100_000)
                _ = texto.Append(prefijo);
            while (texto.Length < 200_000)
                _ = texto.Append("case x in ");
            string comando = texto.ToString();
            System.Diagnostics.Stopwatch reloj = System.Diagnostics.Stopwatch.StartNew();

            // Act
            _ = HerramientasSsh.DetectarAvisoAntipatron(comando);

            // Assert
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        }

        // Tiempo lineal: 5 MB de 'done)' repetido (y de otras construcciones que abren o cierran bloques) en mucho menos de lo
        // que tardaría un recorrido cuadrático.
        [Theory]
        [InlineData("done)")]
        [InlineData("if x; then sleep 60; else ")]
        [InlineData("case a in a) sleep 60;; ")]
        [InlineData("for i in 1; { sleep 60; ")]
        [InlineData("sleep 60; fi; ")]
        public void TextoDe5MB_TardaPoco(string trozo)
        {
            // Arrange
            string comando = new System.Text.StringBuilder(5_000_000 + trozo.Length).Insert(0, trozo, 5_000_000 / trozo.Length).ToString();
            System.Diagnostics.Stopwatch reloj = System.Diagnostics.Stopwatch.StartNew();

            // Act
            _ = HerramientasSsh.DetectarAvisoAntipatron(comando);

            // Assert
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        }
    }
}
