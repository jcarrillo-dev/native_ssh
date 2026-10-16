using System.Diagnostics;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // EO2 (B1 de la revisión del E-septies) con sudo real: Ctrl+C (ssh_cancel o el plazo de ssh_exec) a un shell de root
    // ya autenticado ('sudo -i', 'su -'). El shell de root no muere con Ctrl+C y la línea de recuperación se ejecutaba
    // dentro de él: los comandos siguientes corrían como root y volvían Completado sin aviso. Ahora la línea comprueba que
    // la ejecuta el shell de la sesión ($$ y $PPID) y, si no, se cierra el canal (el shell de root recibe SIGHUP).
    public sealed partial class ContenedorSudoTests
    {
        private const string ContrasenaRoot = "raiz-de-prueba";

        private static async Task EsperarSalidaAsync(HerramientasSsh herramientas, string id, string texto)
        {
            Stopwatch reloj = Stopwatch.StartNew();
            ResultadoComando actual;
            while (!(actual = await herramientas.LeerSalidaAsync(id, esperarSegundos: 0).ComoComando()).Salida.Contains(texto, StringComparison.Ordinal))
            {
                if (reloj.Elapsed > TimeSpan.FromSeconds(15))
                    throw new TimeoutException($"La salida del comando {id} no llegó a contener '{texto}': {actual.Estado} '{actual.Salida}' {actual.Aviso}");
                await Task.Delay(200);
            }
        }

        // 'su -' solo en Debian: el su de busybox (Alpine) no es setuid y no deja cambiar de usuario.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian, "sudo -i", false)]
        [InlineData(ContenedorSudo.Debian, "sudo -i", true)]
        [InlineData(ContenedorSudo.Alpine, "sudo -i", false)]
        [InlineData(ContenedorSudo.Alpine, "sudo -i", true)]
        [InlineData(ContenedorSudo.Debian, "su -", false)]
        public async Task CtrlCAUnShellDeRootAnidado_CierraElCanalYElSiguienteNoSeEjecutaComoRoot(string distro, string comando, bool isPlazo)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            bool isSu = comando == "su -";
            if (isSu)
                _ = entorno.Root("printf 'root:%s\\n' \"$1\" | chpasswd", ContrasenaRoot);
            else
                await entorno.AutenticarSudoAsync(herramientas);
            ResultadoComando enLaSesion = await herramientas.EjecutarAsync(entorno.Destino, "export NS_VIVO=si; echo uid=$(id -u)", esperarSegundos: 10).ComoComando();
            ResultadoComando anidado = await herramientas.EjecutarAsync(entorno.Destino, comando, timeoutSegundos: isPlazo ? 8 : 300, esperarSegundos: 2).ComoComando();
            if (isSu)
                _ = await herramientas.EnviarEntradaAsync(anidado.Id, ContrasenaRoot, esperarSegundos: 0).ComoComando();
            await EsperarSalidaAsync(herramientas, anidado.Id, "# ");
            await EsperarEnContenedorAsync(entorno, "pgrep -f '^[-]' >/dev/null");

            // Act
            ResultadoComando interrumpido = isPlazo
                ? await herramientas.LeerSalidaAsync(anidado.Id, esperarSegundos: 30).ComoComando()
                : await herramientas.CancelarAsync(anidado.Id).ComoComando();
            ResultadoComando siguiente = await herramientas.EjecutarAsync(entorno.Destino, "echo uid=$(id -u) vivo=$NS_VIVO", esperarSegundos: 15).ComoComando();
            salida.WriteLine($"{interrumpido.Estado}: {interrumpido.Aviso}");
            salida.WriteLine($"{siguiente.Estado}: '{siguiente.Salida}' {siguiente.Aviso}");

            // Assert: el siguiente va a un shell nuevo del usuario de prueba y el shell de root (un shell de login, cuyo
            // nombre empieza por '-') ya no existe.
            _ = interrumpido.Estado.Should().Be(isPlazo ? EstadoPeticionEnum.TiempoAgotado : EstadoPeticionEnum.Cancelado);
            _ = interrumpido.Aviso.Should().Contain("shell interactivo anidado").And.Contain("se cerró el canal del shell");
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Salida.Should().Be($"{enLaSesion.Salida.TrimEnd('\n')} vivo=\n");
            _ = siguiente.Aviso.Should().Contain("recreado");
            await EsperarEnContenedorAsync(entorno, "! pgrep -f '^[-]' >/dev/null", 10);
        }

        // El uso normal de un shell de root (sudo -i, ssh_input con órdenes y exit) no se toma por un shell anidado que quedó
        // vivo: la cola del comando la ejecuta el shell de la sesión cuando el de root termina.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task SudoIUsadoConSshInputYCerradoConExit_TerminaCompletadoSinAvisoYConservaElShell(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            await entorno.AutenticarSudoAsync(herramientas);
            ResultadoComando enLaSesion = await herramientas.EjecutarAsync(entorno.Destino, "export NS_VIVO=si; echo uid=$(id -u)", esperarSegundos: 10).ComoComando();
            ResultadoComando anidado = await herramientas.EjecutarAsync(entorno.Destino, "sudo -i; echo salio=$?", timeoutSegundos: 120, esperarSegundos: 2).ComoComando();
            await EsperarSalidaAsync(herramientas, anidado.Id, "# ");

            // Act
            ResultadoComando dentro = await herramientas.EnviarEntradaAsync(anidado.Id, "echo raiz=$(id -u)", esperarSegundos: 2).ComoComando();
            ResultadoComando fuera = await herramientas.EnviarEntradaAsync(anidado.Id, "exit 4", esperarSegundos: 20).ComoComando();
            ResultadoComando siguiente = await herramientas.EjecutarAsync(entorno.Destino, "echo uid=$(id -u) vivo=$NS_VIVO", esperarSegundos: 15).ComoComando();
            salida.WriteLine($"{fuera.Estado}: '{fuera.Salida}' {fuera.Aviso}");

            // Assert
            _ = dentro.Salida.Should().Contain("raiz=0");
            _ = fuera.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = fuera.Aviso.Should().BeNull();
            _ = fuera.Salida.Should().EndWith("salio=4\n");
            _ = siguiente.Salida.Should().Be($"{enLaSesion.Salida.TrimEnd('\n')} vivo=si\n");
            _ = siguiente.Aviso.Should().BeNull();
        }

        // Sin shell anidado no hay falso positivo: sudo con la contraseña en caché, 'sudo -v' respondido con ssh_input y un
        // comando con sudo cancelado conservan el shell de la sesión.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task SudoSinShellAnidado_NoSeTomaPorUnShellAnidado(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            await entorno.AutenticarSudoAsync(herramientas);
            _ = await herramientas.EjecutarAsync(entorno.Destino, "export NS_VIVO=si", esperarSegundos: 10).ComoComando();

            // Act
            ResultadoComando conSudo = await herramientas.EjecutarAsync(entorno.Destino, "sudo id -u; sudo sh -c 'echo $$ > /dev/null; echo hijo'", esperarSegundos: 15).ComoComando();
            ResultadoComando largo = await herramientas.EjecutarAsync(entorno.Destino, "sudo sleep 302", esperarSegundos: 1).ComoComando();
            await EsperarEnContenedorAsync(entorno, "pgrep -f '^[s]leep 302$' >/dev/null");
            ResultadoComando cancelado = await herramientas.CancelarAsync(largo.Id).ComoComando();
            ResultadoComando siguiente = await herramientas.EjecutarAsync(entorno.Destino, "echo vivo=$NS_VIVO uid=$(id -u)", esperarSegundos: 15).ComoComando();

            // Assert
            _ = conSudo.Salida.Should().Be("0\nhijo\n");
            _ = conSudo.Aviso.Should().BeNull();
            _ = cancelado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = cancelado.Aviso.Should().Be("Cancelado: se envió Ctrl+C al proceso.");
            _ = siguiente.Salida.Should().StartWith("vivo=si uid=").And.NotContain("uid=0");
            _ = siguiente.Aviso.Should().BeNull();
        }
    }
}
