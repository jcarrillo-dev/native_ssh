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
    }
}
