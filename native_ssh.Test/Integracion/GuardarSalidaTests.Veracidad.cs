using FluentAssertions;
using NativeSsh.Herramientas;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // guardarSalida: lo que dicen ssh_disconnect y ssh_reset_shell de lo que borran es cierto aunque se haya borrado algún
    // archivo antes (el flujo que recomienda el aviso del cupo).
    public sealed partial class GuardarSalidaTests
    {
        // ssh_disconnect cuenta en el servidor los archivos que quedan antes de borrarlos: tras borrar uno de dos con rm, dice
        // 1; tras borrar los dos, no dice que borró ninguno.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task Disconnect_TrasRm_CuentaLosQueQuedaban(int borrados)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando uno = await GuardarAsync(h, "echo uno");
            ResultadoComando dos = await GuardarAsync(h, "echo dos");
            string rm = borrados == 1 ? $"rm -- '{uno.Archivo}'" : $"rm -- '{uno.Archivo}' '{dos.Archivo}'";
            _ = await h.EjecutarAsync(Destino, rm, esperarSegundos: 30).ComoComando();

            // Act
            ResultadoAccion desconexion = await h.DesconectarAsync(Destino);

            // Assert
            salida.WriteLine(desconexion.Mensaje);
            _ = File.Exists(dos.Archivo).Should().BeFalse();
            _ = desconexion.Mensaje.Should().NotContain("2 archivo(s)");
            string esperado = borrados == 1 ? "Se borraron del servidor los 1 archivo(s)" : "No quedaba";
            _ = desconexion.Mensaje.Should().Contain(esperado);
        }

        // ssh_reset_shell borra en segundo plano: no afirma cuántos archivos borra, solo hasta cuántos se crearon.
        [Fact]
        public async Task Reset_TrasRm_NoAfirmaUnNumeroQueNoConoce()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando uno = await GuardarAsync(h, "echo uno");
            ResultadoComando dos = await GuardarAsync(h, "echo dos");
            _ = await h.EjecutarAsync(Destino, $"rm -- '{uno.Archivo}'", esperarSegundos: 30).ComoComando();

            // Act
            ResultadoAccion reset = h.ReiniciarShell(Destino);

            // Assert
            salida.WriteLine(reset.Mensaje);
            _ = reset.Mensaje.Should().NotContain("los 2 archivo(s)").And.Contain("hasta 2");
            await EsperasPrueba.EsperarHastaAsync(() => !File.Exists(dos.Archivo), TimeSpan.FromSeconds(15));
        }
    }
}
