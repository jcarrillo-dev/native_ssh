using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // RC2 y RC4, con el perfil real de Debian (login → ~/.profile → ~/.bashrc): un ~/.bashrc roto al final ('sleep', 'exit')
    // deja sin shell a ssh_exec, ssh_write_file y ssh_replace_file_content; el error dice que las lecturas siguen funcionando
    // y que ssh_reset_shell con sinPerfil=true abre un shell sin perfil, con el que se repara; un reset normal vuelve al
    // login (que exporta NS_PERFIL desde ~/.bashrc; el shell sin perfil no). El ~/.bashrc del usuario de prueba se
    // restaura al terminar.
    public sealed partial class ContenedorSudoTests
    {
        [TheoryContenedor]
        [InlineData("sleep 20")]
        [InlineData("exit")]
        [InlineData("read -r ns_linea; sleep 20")]
        public async Task PerfilRoto_ErrorConLaSalidaYSinPerfilLoRepara(string linea)
        {
            // Arrange
            await using Entorno entorno = Preparar(ContenedorSudo.Debian);
            string bashrc = $"/home/{entorno.Servidor.Usuario}/.bashrc";
            _ = entorno.Root("cp -p \"$1\" /root/ns_bashrc && printf 'export NS_PERFIL=si\\n%s\\n' \"$2\" >> \"$1\"", bashrc, linea);
            try
            {
                OpcionesNativeSsh opciones = entorno.Servidor.Opciones() with { TimeoutInicializacionShell = TimeSpan.FromSeconds(6) };
                await using GestorSesiones gestor = entorno.Servidor.Gestor(opciones);
                HerramientasSsh h = new(gestor, opciones);

                // Act
                ResultadoComando roto = await h.EjecutarAsync(entorno.Destino, "echo hola", esperarSegundos: 60).ComoComando();
                NativeSshException escritura = (await FluentActions.Awaiting(() => h.EscribirArchivoAsync(entorno.Destino, $"{entorno.DirectorioUsuario}/x.txt", "x")).Should().ThrowAsync<NativeSshException>()).Which;
                string leido = await h.LeerArchivoAsync(entorno.Destino, bashrc, -1);
                _ = h.ReiniciarShell(entorno.Destino, sinPerfil: true);
                ResultadoEscritura reparado = await h.ReemplazarContenidoArchivoAsync(entorno.Destino, bashrc, linea + "\n", "");
                ResultadoComando sinPerfil = await h.EjecutarAsync(entorno.Destino, "echo \"perfil=${NS_PERFIL:-no}\"", esperarSegundos: 30).ComoComando();
                _ = h.ReiniciarShell(entorno.Destino);
                ResultadoComando login = await h.EjecutarAsync(entorno.Destino, "echo \"perfil=${NS_PERFIL:-no}\"", esperarSegundos: 60).ComoComando();

                // Assert
                salida.WriteLine($"{roto.Estado}: {roto.Aviso}\n{escritura.Message}");
                _ = roto.Estado.Should().Be(EstadoPeticionEnum.Error);
                _ = roto.Aviso.Should().Contain("ssh_exec, ssh_write_file y ssh_replace_file_content").And.Contain("siguen funcionando").And.Contain("sinPerfil=true");
                _ = escritura.Message.Should().Contain("ssh_exec, ssh_write_file y ssh_replace_file_content").And.Contain("sinPerfil=true").And.Contain("no se ha modificado");
                _ = leido.Should().EndWith(linea + "\n");
                _ = reparado.Reemplazos.Should().Be(1);
                _ = (sinPerfil.Estado, sinPerfil.Salida).Should().Be((EstadoPeticionEnum.Completado, "perfil=no\n"));
                _ = (login.Estado, login.Salida).Should().Be((EstadoPeticionEnum.Completado, "perfil=si\n"));
            }
            finally
            {
                _ = entorno.Root("cp -p /root/ns_bashrc \"$1\" && rm -f /root/ns_bashrc", bashrc);
            }
        }
    }
}
