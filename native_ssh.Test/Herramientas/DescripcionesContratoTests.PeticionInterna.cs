using FluentAssertions;

namespace NativeSsh.Test.Herramientas
{
    public sealed partial class DescripcionesContratoTests
    {
        // ssh_output rechaza el id de una petición interna (HerramientasSsh.LeerSalidaAsync): las descripciones de las
        // escrituras no pueden mandar a verla con ssh_output; el error de la escritura ya lleva lo que el script escribió.
        [Theory]
        [InlineData("ssh_write_file")]
        [InlineData("ssh_replace_file_content")]
        public void Escrituras_NoRemitenASsh_outputParaLaSalidaDeSuPeticionInterna(string herramienta)
        {
            // Act
            string descripcion = Descripcion(herramienta);

            // Assert
            _ = descripcion.Should().NotContain("para ver su salida con ssh_output")
                .And.Contain("ssh_output no da la salida de una petición interna")
                .And.Contain("sale en ssh_status");
        }
    }
}
