using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // ssh_find y los montajes (C13) en Debian (GNU find) y Alpine (busybox find): desde '/' no se entra en /proc (donde
    // /proc/<pid>/cwd del shell de login lleva otra vez al directorio del usuario) y, con cruzarMontajes=false, no se entra
    // en /etc/ns, que el contenedor monta desde el anfitrión (otro sistema de ficheros, comprobado con stat).
    public sealed partial class ContenedorSudoTests
    {
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task BuscarPorNombre_DesdeLaRaizYSinCruzarMontajes_IgualEnLasDosDistros(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            string objetivo = $"objetivo_{Guid.NewGuid():N}";
            _ = entorno.Servidor.ComoUsuario("mkdir -- \"$1/$2\"", entorno.DirectorioUsuario, objetivo);
            string[] dispositivos = entorno.Root("stat -c %d /etc /etc/ns").Split('\n');
            // El shell del usuario tiene su directorio de trabajo en su casa: /proc/<pid>/cwd lleva al objetivo.
            _ = await herramientas.EjecutarAsync(entorno.Destino, "true");

            // Act
            string desdeRaiz = await herramientas.BuscarPorNombreAsync(entorno.Destino, objetivo, "/", "dir", profundidadMax: 7);
            string cruzando = await herramientas.BuscarPorNombreAsync(entorno.Destino, "authorized_keys", "/etc");
            string sinCruzar = await herramientas.BuscarPorNombreAsync(entorno.Destino, "authorized_keys", "/etc", cruzarMontajes: false);

            // Assert
            salida.WriteLine($"{distro}: {desdeRaiz}\n{sinCruzar}");
            string[] lineas = desdeRaiz.Split('\n');
            _ = lineas[0].Should().Be($"{entorno.DirectorioUsuario[1..]}/{objetivo}");
            _ = lineas[1].Should().BeEmpty();
            _ = lineas[2].Should().StartWith("[montajes_no_explorados: ").And.Contain("/proc").And.Contain("/sys");
            _ = lineas.Should().HaveCount(3);
            _ = dispositivos[0].Should().NotBe(dispositivos[1], "/etc/ns debe ser otro sistema de ficheros");
            _ = cruzando.Should().Be("ns/authorized_keys");
            _ = sinCruzar.Should().StartWith("No se encontraron elementos que coincidan con 'authorized_keys' en '/etc'.\n\n[montajes_no_explorados: ")
                .And.Contain("/etc/ns");
        }
    }
}
