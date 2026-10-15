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
    }
}
