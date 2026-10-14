using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Protocolo;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // WS1 sin falsos positivos: un servidor Linux cuyo perfil escribe lo mismo que el terminal de Windows con cmd (el prefijo
    // de ConPTY, la cabecera «Microsoft Windows [Version …]» y un indicador «C:\Users\…>») sigue funcionando, con bash, dash y
    // el ash de busybox como shell de login: la detección exige que el servidor se anuncie como OpenSSH for Windows. El
    // perfil y el shell de login del usuario de prueba se restauran al terminar.
    public sealed partial class ContenedorSudoTests
    {
        private const string TextoComoWindows = "\\033[?9001h\\033[?1004hMicrosoft Windows [Version 10.0.26100.1742]\\r\\n(c) Microsoft Corporation.\\r\\nC:\\\\Users\\\\prueba>";

        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian, "/bin/bash", ".bashrc")]
        [InlineData(ContenedorSudo.Debian, "/bin/sh", ".profile")]
        [InlineData(ContenedorSudo.Alpine, "/bin/bash", ".bashrc")]
        [InlineData(ContenedorSudo.Alpine, "/bin/ash", ".profile")]
        public async Task PerfilQueEscribeComoWindows_EnLinux_NoSeTomaPorWindows(string distro, string shellLogin, string archivoPerfil)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            string usuario = entorno.Servidor.Usuario;
            string perfil = $"/home/{usuario}/{archivoPerfil}";
            string shellAntes = entorno.Root("getent passwd \"$1\" | cut -d: -f7", usuario).Trim();
            _ = entorno.Root("touch \"$1\" && cp -p \"$1\" /root/ns_perfil && printf 'printf \"%s\"\\n' \"$2\" >> \"$1\" && sed -i \"s#^\\($3:.*:\\)[^:]*\\$#\\1$4#\" /etc/passwd",
                perfil, TextoComoWindows, usuario, shellLogin);
            try
            {
                OpcionesNativeSsh opciones = entorno.Servidor.Opciones();
                await using GestorSesiones gestor = entorno.Servidor.Gestor(opciones);
                HerramientasSsh h = new(gestor, opciones);

                // Act
                string login = entorno.Root("getent passwd \"$1\" | cut -d: -f7", usuario).Trim();
                ResultadoComando comando = await h.EjecutarAsync(entorno.Destino, "echo hola", esperarSegundos: 30).ComoComando();
                string leido = await h.LeerArchivoAsync(entorno.Destino, perfil, -1);

                // Assert
                salida.WriteLine($"{distro} {login}: {comando.Estado} {comando.Aviso}");
                _ = login.Should().Be(shellLogin);
                _ = (comando.Estado, comando.Salida).Should().Be((EstadoPeticionEnum.Completado, "hola\n"), comando.Aviso);
                _ = RespuestaWindows.IsMensajeWindows(comando.Aviso).Should().BeFalse();
                _ = leido.Should().Contain("Microsoft Windows");
            }
            finally
            {
                _ = entorno.Root("cp -p /root/ns_perfil \"$1\" && rm -f /root/ns_perfil && sed -i \"s#^\\($2:.*:\\)[^:]*\\$#\\1$3#\" /etc/passwd", perfil, usuario, shellAntes);
            }
        }
    }
}
