using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Herramientas
{
    // Avisos de antipatrón de ssh_exec ampliados (C10): cada uso que tiene herramienta especializada avisa de ella, y cada
    // falso positivo conocido (tuberías, tail -f, redirecciones a /dev/null y 2>&1, --help, transformaciones en el
    // servidor, heredocs y sustituciones) no avisa de nada.
    public sealed class AvisosAntipatronTests
    {
        [Theory]
        [InlineData("cat /etc/nginx/nginx.conf", "'cat'")]
        [InlineData("sudo cat /var/log/syslog", "'cat'")]
        [InlineData("cat app.log 2>&1", "'cat'")]
        [InlineData("cat app.log >&2", "'cat'")]
        [InlineData("cat app.log 1>&2", "'cat'")]
        [InlineData("cat -n src/main.py 2>/dev/null", "'cat'")]
        [InlineData("timeout 10 cat datos.txt", "'cat'")]
        [InlineData("env LANG=C cat datos.txt", "'cat'")]
        [InlineData("head -n 50 app.py", "'head'")]
        [InlineData("tail -n 100 /var/log/nginx/error.log", "'tail'")]
        [InlineData("tail -200 app.log", "'tail'")]
        [InlineData("nice -n 5 tail -n 5 app.log", "'tail'")]
        [InlineData("sed -n '10,20p' app.py", "'sed -n'")]
        [InlineData("sed -n 5p config.ini", "'sed -n'")]
        [InlineData("sed -n '1p;$p' config.ini", "'sed -n'")]
        [InlineData("less README.md", "'less'")]
        [InlineData("more notas.txt", "'more'")]
        public void Lectura_AvisaDeSshReadFile(string comando, string programa)
        {
            // Act
            string? aviso = HerramientasSsh.DetectarAvisoAntipatron(comando);

            // Assert
            _ = aviso.Should().StartWith($"Sugerencia de rendimiento: Has leído un archivo con {programa} vía ssh_exec. Utiliza la herramienta especializada 'ssh_read_file'");
        }

        [Theory]
        [InlineData("rg 'TODO' src/", "rg")]
        [InlineData("ag patron .", "ag")]
        [InlineData("ack foo lib/", "ack")]
        [InlineData("sudo rg -n clave /etc", "rg")]
        public void BusquedaConRgAgAck_AvisaDeSshSearch(string comando, string programa) =>
            _ = HerramientasSsh.DetectarAvisoAntipatron(comando).Should().StartWith($"Sugerencia de rendimiento: Has ejecutado '{programa}' sobre el sistema de archivos vía ssh_exec. Utiliza la herramienta especializada 'ssh_search'");

        [Theory]
        [InlineData("ls -la /etc/nginx", "ls")]
        [InlineData("ls", "ls")]
        [InlineData("tree -L 2", "tree")]
        [InlineData("(cd /srv; ls -R)", "ls")]
        public void Listado_AvisaDeSshListDir(string comando, string programa) =>
            _ = HerramientasSsh.DetectarAvisoAntipatron(comando).Should().Contain($"Sugerencia de rendimiento: Has ejecutado '{programa}' vía ssh_exec. Utiliza la herramienta especializada 'ssh_list_dir'");

        [Theory]
        [InlineData("stat /etc/passwd")]
        [InlineData("stat -c '%s %U' app.db")]
        public void Stat_AvisaDeSshStat(string comando) =>
            _ = HerramientasSsh.DetectarAvisoAntipatron(comando).Should().StartWith("Sugerencia de rendimiento: Has ejecutado 'stat' vía ssh_exec. Utiliza la herramienta especializada 'ssh_stat'");

        [Theory]
        [InlineData("sed -i 's/a/b/' app.conf", "sed -i")]
        [InlineData("sed -Ei 's/a/b/' app.conf", "sed -i")]
        [InlineData("sed -i.bak 's/a/b/' app.conf", "sed -i")]
        [InlineData("sed -e 's/x/y/' -i app.conf", "sed -i")]
        [InlineData("sed --in-place 's/x/y/' app.conf", "sed -i")]
        [InlineData("sudo sed -i 's/x/y/' /etc/app.conf", "sed -i")]
        [InlineData("perl -pi -e 's/a/b/' app.conf", "perl -i")]
        [InlineData("perl -i -pe 's/a/b/' app.conf", "perl -i")]
        [InlineData("perl -i.bak -pe 's/a/b/' app.conf", "perl -i")]
        public void EdicionEnElSitio_AvisaDeSshReplaceFileContent(string comando, string orden) =>
            _ = HerramientasSsh.DetectarAvisoAntipatron(comando).Should().StartWith($"Sugerencia de seguridad: Has editado un archivo en el sitio con '{orden}' vía ssh_exec. Utiliza la herramienta especializada 'ssh_replace_file_content'");

        [Theory]
        [InlineData("echo 'x=1' | tee config.env", "tee")]
        [InlineData("printf 'a\\n' | sudo tee /etc/app.conf > /dev/null", "tee")]
        [InlineData("echo 'linea' | tee -a notas.txt", "tee")]
        [InlineData("cat <<EOF | sudo tee /etc/x.conf\nA=1\nEOF", "tee")]
        [InlineData("cat > /etc/app.conf <<EOF\nclave=1\nEOF", "cat > archivo <<")]
        [InlineData("cat <<'EOF' >> notas.txt\nhola\nEOF", "cat > archivo <<")]
        [InlineData("cat > app.env <<< 'A=1'", "cat > archivo <<")]
        public void Escritura_AvisaDeSshWriteFile(string comando, string orden) =>
            _ = HerramientasSsh.DetectarAvisoAntipatron(comando).Should().Be($"Sugerencia de seguridad: Has escrito un archivo con '{orden}' vía ssh_exec. Utiliza la herramienta especializada 'ssh_write_file' (contenido sin problemas de comillas ni de expansión de variables, escritura atómica con candado, append=true para anexar y sudo=true para archivos de root).");

        // El cuerpo de un heredoc no son órdenes: solo cuenta la escritura, no el 'ls' ni el 'cat' de dentro.
        [Fact]
        public void HeredocConOrdenesDentro_SoloAvisaDeLaEscritura() =>
            _ = HerramientasSsh.DetectarAvisoAntipatron("cat > script.sh <<'FIN'\nls -la\ncat secreto.txt\nrg x\nFIN\nchmod +x script.sh").Should().StartWith("Sugerencia de seguridad: Has escrito un archivo con 'cat > archivo <<'").And.NotContain("ssh_list_dir").And.NotContain("ssh_read_file").And.NotContain("ssh_search");

        // Varios avisos: primero los de antes de C10 (grep, find, cd) y después los demás, sin repetir.
        [Fact]
        public void VariasOrdenes_JuntaLosAvisosSinRepetir()
        {
            // Act
            string? aviso = HerramientasSsh.DetectarAvisoAntipatron("cd /srv && cat a.txt && cat b.txt; ls");

            // Assert
            _ = aviso.Should().StartWith("Sugerencia de rendimiento: Has encadenado 'cd <ruta> &&'");
            _ = aviso.Should().Contain("'ssh_read_file'").And.Contain("'ssh_list_dir'");
            _ = aviso!.Split("ssh_read_file").Should().HaveCount(2, "un solo aviso de lectura");
        }

        [Theory]
        [InlineData("journalctl -u nginx | tail -n 50")]
        [InlineData("journalctl -u nginx | tail")]
        [InlineData("docker logs app 2>&1 | tail -n 20")]
        [InlineData("git log | head -20")]
        [InlineData("head -c 16 /dev/urandom | base64")]
        [InlineData("tail -f /var/log/syslog")]
        [InlineData("tail -F app.log")]
        [InlineData("tail -fn 20 app.log")]
        [InlineData("tail --follow=name app.log")]
        [InlineData("cat app.log | grep error")]
        [InlineData("cat archivo > /dev/null")]
        [InlineData("cat a.txt b.txt > c.txt")]
        [InlineData("cat")]
        [InlineData("ls | wc -l")]
        [InlineData("ls > lista.txt")]
        [InlineData("ls -la >/dev/null 2>&1")]
        [InlineData("stat --help")]
        [InlineData("cat --help")]
        [InlineData("rg --help")]
        [InlineData("ls --version")]
        [InlineData("sed -n -E 's/^([A-Z_]+)=.*/\\1/p' .env")]
        [InlineData("sed -n 's/^\\([A-Z_]*\\)=.*/\\1/p' .env")]
        [InlineData("sed -n '/^\\[/p' config.ini")]
        [InlineData("sed 's/a/b/' archivo")]
        [InlineData("sed -n 5p app.log | wc -c")]
        [InlineData("npm run build | tee build.log")]
        [InlineData("echo 1 | sudo tee /proc/sys/vm/drop_caches")]
        [InlineData("echo hola | tee /dev/null")]
        [InlineData("systemctl status nginx 2>&1")]
        [InlineData("make > /dev/null 2>&1")]
        [InlineData("command -v rg")]
        [InlineData("echo $(cat VERSION)")]
        [InlineData("x=$(ls | wc -l); echo $x")]
        [InlineData("echo `cat VERSION`")]
        [InlineData("diff <(ls a) <(ls b)")]
        [InlineData("cat <<EOF\nls\ncat secreto\nrg x\nEOF")]
        [InlineData("python3 - <<'EOF'\nimport os\nprint(open('x').read())\nEOF")]
        [InlineData("bash -c 'cat /etc/hosts'")]
        [InlineData("# cat archivo\necho hola")]
        [InlineData("echo 'cat file; ls'")]
        [InlineData("echo \"tail -n 5 x\"")]
        [InlineData("sudo -u postgres psql -c 'select 1'")]
        [InlineData("perl -ne 'print if /-i/' app.log")]
        public void FalsoPositivo_NoAvisa(string comando) =>
            _ = HerramientasSsh.DetectarAvisoAntipatron(comando).Should().BeNull();

        // El análisis corre en cada respuesta de ssh_exec, ssh_output y ssh_input: con una entrada malformada no lanza (si
        // lanzara, la salida de ese comando quedaría ilegible) y no inventa avisos.
        [Theory]
        [InlineData("\\")]
        [InlineData("cat 2>")]
        [InlineData("&>")]
        [InlineData("cat <<")]
        [InlineData("cat <<-")]
        [InlineData("echo $(cat x")]
        [InlineData("echo `cat x")]
        [InlineData("echo \"abierta")]
        [InlineData("echo 'abierta")]
        [InlineData("|||")]
        [InlineData("&&&")]
        [InlineData(";;;")]
        [InlineData("\r")]
        [InlineData("cat <<EOF")]
        [InlineData("echo $((1+(2)")]
        [InlineData("diff <(ls")]
        [InlineData("{ ; }")]
        [InlineData("( ) ) (")]
        [InlineData("sudo -u")]
        [InlineData("env -u")]
        [InlineData("nice -n")]
        [InlineData("tail -n")]
        [InlineData("sed -e")]
        [InlineData("x=1 y=2")]
        [InlineData("!")]
        [InlineData("2>&1")]
        [InlineData(">")]
        [InlineData("<<<")]
        public void EntradaMalformada_NoLanzaNiAvisa(string comando) =>
            _ = HerramientasSsh.DetectarAvisoAntipatron(comando).Should().BeNull();
    }
}
