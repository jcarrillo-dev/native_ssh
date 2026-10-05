using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // Garantías de seguridad de datos de las escrituras: tipo del destino, atributos que se conservan, escritura en el sitio
    // con copia de seguridad y comprobación de que el archivo no cambió entre la lectura y la escritura.
    public sealed partial class HerramientasArchivoIntegracionTests
    {
        public static TheoryData<string, string, bool> DestinosNoRegulares
        {
            get
            {
                TheoryData<string, string, bool> datos = [];
                foreach (string destino in new[] { "fifo", "enlace_fifo", "enlace_dev_null" })
                    foreach (string operacion in new[] { "sobrescribir", "reemplazar", "anexar" })
                        foreach (bool isSudo in new[] { false, true })
                            datos.Add(destino, operacion, isSudo);
                return datos;
            }
        }

        // Una FIFO, un socket o un dispositivo no se sustituyen por un archivo regular ni se abren para escribir o leer: una
        // FIFO sin lector bloquearía hasta el timeout y /dev/null se "escribiría" sin guardar nada. Error sin tocar nada.
        [Theory]
        [MemberData(nameof(DestinosNoRegulares))]
        public async Task Escrituras_DestinoQueNoEsUnArchivoRegular_FallanSinTocarlo(string destino, string operacion, bool isSudo)
        {
            // Arrange
            string carpeta = Ruta("datos");
            _ = Directory.CreateDirectory(carpeta);
            string fifo = Path.Combine(carpeta, "fifo");
            _ = Bash($"mkfifo '{fifo}'");
            string ruta = Path.Combine(carpeta, destino);
            if (destino == "enlace_fifo")
                _ = File.CreateSymbolicLink(ruta, "fifo");
            else if (destino == "enlace_dev_null")
                _ = File.CreateSymbolicLink(ruta, "/dev/null");
            string[] entradas = Directory.GetFileSystemEntries(carpeta);
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(10));
            if (isSudo)
                await SimularSudoAsync(herramientas);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => operacion switch
                {
                    "sobrescribir" => herramientas.EscribirArchivoAsync(Destino, ruta, "x\n", overwrite: true, sudo: isSudo),
                    "reemplazar" => herramientas.ReemplazarContenidoArchivoAsync(Destino, ruta, "x", "y", sudo: isSudo),
                    _ => herramientas.EscribirArchivoAsync(Destino, ruta, "x\n", append: true, sudo: isSudo)
                })
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("no es un archivo regular").And.Contain("No se ha modificado");
            _ = Bash($"[ -p '{fifo}' ] && echo fifo").Should().Be("fifo");
            _ = Directory.GetFileSystemEntries(carpeta).Should().BeEquivalentTo(entradas);
            if (destino != "fifo")
                _ = new FileInfo(ruta).LinkTarget.Should().Be(destino == "enlace_fifo" ? "fifo" : "/dev/null");
        }
    }
}
