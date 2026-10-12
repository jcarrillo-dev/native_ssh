using System.Diagnostics;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // guardarSalida con una ACL por defecto en el directorio que contiene el base: GNU 'ls -ld' la muestra ('+') en el base
    // recién creado y se rechaza sin ejecutar ni escribir nada (el busybox de Alpine no la muestra: ContenedorSudoTests).
    public sealed partial class GuardarSalidaTests
    {
        [Fact]
        public async Task AclPorDefectoEnElPadre_ConLsDeGnu_SeRechazaSinEscribir()
        {
            // Arrange
            using (Process? setfacl = Process.Start("setfacl", ["-d", "-m", "u:nobody:rwx", _raiz]))
            {
                await setfacl!.WaitForExitAsync();
                _ = setfacl.ExitCode.Should().Be(0, "precondición: setfacl en la máquina de pruebas");
            }

            HerramientasSsh h = Herramientas();
            string testigo = Path.Combine(_raiz, "ejecutado");

            // Act
            ResultadoComando r = await h.EjecutarAsync(Destino, $"touch '{testigo}'; echo x", esperarSegundos: 30, guardarSalida: true).ComoComando();

            // Assert
            salida.WriteLine($"{r.Estado} '{r.Aviso}'");
            _ = r.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = r.Aviso.Should().StartWith("El comando NO se ejecutó").And.Contain(Base).And.Contain("ACL");
            _ = File.Exists(testigo).Should().BeFalse();
            _ = Directory.EnumerateFileSystemEntries(Base).Should().BeEmpty();
        }
    }
}
