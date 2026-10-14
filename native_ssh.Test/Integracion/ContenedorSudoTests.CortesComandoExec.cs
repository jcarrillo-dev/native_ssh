using System.Linq;
using NativeSsh.Protocolo;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    public sealed partial class ContenedorSudoTests
    {
        // WS2 con los sh de verdad de los servidores: dash (el /bin/sh de Debian) y el ash de busybox (el de Alpine), además de
        // bash. El comando del canal exec cortado en cada posición no ejecuta nada del script (CortesComandoExec).
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public void ComandoExecTruncado_EnCualquierPosicion_NoEjecutaNadaConElShDeLaDistro(string distro)
        {
            // Arrange
            ServidorContenedor servidor = contenedores.Servidor(distro);
            string efecto = $"/home/{servidor.Usuario}/ns_efecto_{Guid.NewGuid():N}";
            string comando = ConstructorComandoExec.Construir(CortesComandoExec.Script(efecto), null, TimeSpan.FromSeconds(30));
            CortesComandoExec.ShellCortes[] shells = [new("/bin/sh", 1), new("/bin/bash", 1)];
            string sh = servidor.ComoUsuario("readlink -f /bin/sh").Trim();

            // Act
            string resultado = servidor.ComoUsuario($"exec bash -c \"$1\" ns \"$2\" \"$3\" {string.Join(' ', shells.Select(s => s.Argumento))}", CortesComandoExec.Conductor, comando, efecto);

            // Assert
            salida.WriteLine($"{distro}: /bin/sh = {sh}, {comando.Length} caracteres");
            CortesComandoExec.Comprobar(comando, CortesComandoExec.Leer(resultado), shells);
        }
    }
}
