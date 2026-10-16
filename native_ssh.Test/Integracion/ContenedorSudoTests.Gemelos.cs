using System.Globalization;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // B1 de la revisión del E-octies (EN1): la identidad $$/$PPID colisiona entre contenedores gemelos, así que un Ctrl+C a
    // 'ssh -tt' hacia el gemelo no se detectaba y los comandos siguientes corrían en la otra máquina sin aviso. Con el testigo
    // del canal se detecta. EN2: un exec que cambia de usuario sin cambiar de proceso (setpriv) o con su también se detecta.
    public sealed partial class ContenedorSudoTests
    {
        [TheoryContenedor]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CtrlCAUnSshAUnGemeloConElMismoPidYPadre_CierraElCanalYElSiguienteNoSeEjecutaEnElGemelo(bool isPlazo)
        {
            // Arrange
            using ServidorGemelos gemelos = ServidorGemelos.Iniciar();
            OpcionesNativeSsh opciones = gemelos.Opciones();
            await using GestorSesiones gestor = gemelos.Gestor(opciones);
            HerramientasSsh herramientas = new(gestor, opciones);
            ResultadoComando enA = await herramientas.EjecutarAsync(gemelos.DestinoA, "export NS_GEMELO=si; echo \"$(hostname) $$ $PPID\"", esperarSegundos: 20).ComoComando();
            ResultadoComando ssh = await herramientas.EjecutarAsync(gemelos.DestinoA, gemelos.SshAlGemelo, timeoutSegundos: isPlazo ? 10 : 300, esperarSegundos: 0).ComoComando();
            await EsperarSalidaAsync(herramientas, ssh.Id, "@gemelo-b");
            ResultadoComando dentro = await herramientas.EnviarEntradaAsync(ssh.Id, "echo \"REMOTO $(hostname) $$ $PPID\"", esperarSegundos: 2).ComoComando();

            // Act
            ResultadoComando interrumpido = isPlazo
                ? await herramientas.LeerSalidaAsync(ssh.Id, esperarSegundos: 30).ComoComando()
                : await herramientas.CancelarAsync(ssh.Id).ComoComando();
            ResultadoComando siguiente = await herramientas.EjecutarAsync(gemelos.DestinoA, "echo \"$(hostname) v=$NS_GEMELO\"", esperarSegundos: 20).ComoComando();
            salida.WriteLine($"sesión: {enA.Salida.Trim()}; dentro: {dentro.Salida.Trim()}");
            salida.WriteLine($"{interrumpido.Estado}: {interrumpido.Aviso}");
            salida.WriteLine($"{siguiente.Estado}: '{siguiente.Salida}' {siguiente.Aviso}");

            // Assert: precondición (si no, la prueba no reproduce nada): el bash de B tiene el mismo $$ y $PPID que el de la
            // sesión en A.
            string[] sesion = enA.Salida.Trim().Split(' ');
            _ = sesion[0].Should().Be("gemelo-a");
            _ = dentro.Salida.Should().Contain($"REMOTO gemelo-b {sesion[1]} {sesion[2]}", "los gemelos deben dar el mismo $$ y $PPID");
            _ = interrumpido.Estado.Should().Be(isPlazo ? EstadoPeticionEnum.TiempoAgotado : EstadoPeticionEnum.Cancelado);
            _ = interrumpido.Aviso.Should().Contain("shell interactivo anidado").And.Contain("se cerró el canal del shell");
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Salida.Should().Be("gemelo-a v=\n", "el siguiente se ejecuta en A, en un shell nuevo");
            _ = siguiente.Aviso.Should().Contain("recreado");
        }

        // setpriv con las capacidades de cambiar de usuario (una copia propia con setcap, para no necesitar una sesión de
        // root) deja en el mismo proceso, con el mismo $$ y $PPID, un bash de otro usuario; su forkea (otro $$), pero
        // también es un exec. Los dos se detectan al vencer el plazo o con ssh_cancel.
        [TheoryContenedor]
        [InlineData("setpriv", false)]
        [InlineData("setpriv", true)]
        [InlineData("su", false)]
        public async Task ExecAOtroUsuario_SeDetectaYElSiguienteNoSeEjecutaComoEseUsuario(string via, bool isPlazo)
        {
            // Arrange
            await using Entorno entorno = Preparar(ContenedorSudo.Debian);
            HerramientasSsh herramientas = entorno.Herramientas();
            string programa = $"/usr/local/bin/ns-setpriv-{Guid.NewGuid():N}";
            const string Otro = "nsotro";
            const string ContrasenaOtro = "otro-de-prueba";
            _ = entorno.Root("id \"$2\" >/dev/null 2>&1 || useradd -m \"$2\"; printf '%s:%s\\n' \"$2\" \"$3\" | chpasswd; cp /usr/bin/setpriv \"$1\" && setcap cap_setuid,cap_setgid+ep \"$1\"", programa, Otro, ContrasenaOtro);
            ResultadoComando enLaSesion = await herramientas.EjecutarAsync(entorno.Destino, "export NS_VIVO=si; echo \"$(id -un) $$ $PPID\"", esperarSegundos: 10).ComoComando();
            bool isSetpriv = via == "setpriv";
            string comando = isSetpriv
                ? $"exec {programa} --reuid={Otro} --regid={Otro} --clear-groups env -i PS1='{Otro}$ ' TERM=dumb bash --norc --noprofile -i"
                : $"exec su - {Otro}";
            ResultadoComando conExec = await herramientas.EjecutarAsync(entorno.Destino, comando, timeoutSegundos: isPlazo ? 8 : 300, esperarSegundos: 2).ComoComando();
            if (via == "su")
                _ = await herramientas.EnviarEntradaAsync(conExec.Id, ContrasenaOtro, esperarSegundos: 0).ComoComando();
            await EsperarSalidaAsync(herramientas, conExec.Id, "$ ");
            ResultadoComando dentro = await herramientas.EnviarEntradaAsync(conExec.Id, "echo \"DENTRO $(id -un) $$ $PPID\"", esperarSegundos: 2).ComoComando();

            // Act
            ResultadoComando interrumpido = isPlazo
                ? await herramientas.LeerSalidaAsync(conExec.Id, esperarSegundos: 30).ComoComando()
                : await herramientas.CancelarAsync(conExec.Id).ComoComando();
            ResultadoComando siguiente = await herramientas.EjecutarAsync(entorno.Destino, "echo \"$(id -un) v=$NS_VIVO\"", esperarSegundos: 15).ComoComando();
            _ = entorno.Root("rm -f -- \"$1\"; pkill -KILL -u \"$2\" || true", programa, Otro);
            salida.WriteLine($"sesión: {enLaSesion.Salida.Trim()}; dentro: {dentro.Salida.Trim()}");
            salida.WriteLine($"{interrumpido.Estado}: {interrumpido.Aviso}");
            salida.WriteLine($"{siguiente.Estado}: '{siguiente.Salida}' {siguiente.Aviso}");

            // Assert
            string[] sesion = enLaSesion.Salida.Trim().Split(' ');
            _ = dentro.Salida.Should().Contain($"DENTRO {Otro} ");
            if (isSetpriv)
                _ = dentro.Salida.Should().Contain($"DENTRO {Otro} {sesion[1]} {sesion[2]}", "setpriv no crea otro proceso: mismo $$ y $PPID");
            _ = interrumpido.Estado.Should().Be(isPlazo ? EstadoPeticionEnum.TiempoAgotado : EstadoPeticionEnum.Cancelado);
            _ = interrumpido.Aviso.Should().Contain("sustituyó al de la sesión con exec").And.Contain("se cerró el canal del shell");
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Salida.Should().Be($"{entorno.Servidor.Usuario} v=\n");
            _ = siguiente.Aviso.Should().Contain("recreado");
            _ = int.Parse(sesion[1], CultureInfo.InvariantCulture).Should().BePositive();
        }
    }
}
