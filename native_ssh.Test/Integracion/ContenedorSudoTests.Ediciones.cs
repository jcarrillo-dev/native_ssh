using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // Varias ediciones con sudo REAL sobre un archivo de root (CI5): una sola escritura con las garantías de siempre
    // (propietario, grupo y modo conservados; atómica en Debian, en el sitio en Alpine) y, si una edición falla, el archivo
    // sigue intacto.
    public sealed partial class ContenedorSudoTests
    {
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task ReemplazarConSudo_VariasEdiciones_TodasEnUnaEscrituraONinguna(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            await entorno.AutenticarSudoAsync(herramientas);
            string archivo = $"{entorno.DirectorioRoot}/app.conf";
            _ = entorno.Root("printf 'puerto=80\\nmodo=lento\\nnivel=1\\n' > \"$1\" && chown root:adm \"$1\" && chmod 0640 \"$1\"", archivo);

            // Act
            NativeSshException fallo = (await FluentActions.Awaiting(() => herramientas.ReemplazarContenidoArchivoAsync(entorno.Destino, archivo, sudo: true, ediciones:
                [new EdicionTexto("puerto=80", "puerto=8080"), new EdicionTexto("no=existe", "x")])).Should().ThrowAsync<NativeSshException>()).Which;
            string trasFallo = entorno.Root("cat \"$1\"", archivo);
            ResultadoEscritura resultado = await herramientas.ReemplazarContenidoArchivoAsync(entorno.Destino, archivo, sudo: true, ediciones:
                [new EdicionTexto("puerto=80", "puerto=8080"), new EdicionTexto("modo=lento", "modo=rapido"), new EdicionTexto("nivel=1\n", "nivel=2\nextra=si\n")]);
            salida.WriteLine(resultado.Mensaje);

            // Assert
            _ = fallo.Message.Should().StartWith("Edición 2 de 2: no se encontró");
            _ = trasFallo.Should().Be("puerto=80\nmodo=lento\nnivel=1\n");
            _ = entorno.Root("cat \"$1\"", archivo).Should().Be("puerto=8080\nmodo=rapido\nnivel=2\nextra=si\n");
            string atributos = Atributos(entorno, archivo);
            _ = atributos[..atributos.LastIndexOf(' ')].Should().Be("root:adm 640");
            _ = resultado.Reemplazos.Should().Be(3);
            _ = resultado.IsAtomica.Should().Be(entorno.Servidor.IsDebian, resultado.Mensaje);
            _ = entorno.Root("ls -A \"$1\"", entorno.DirectorioRoot).Should().Be("app.conf\n");
        }
    }
}
