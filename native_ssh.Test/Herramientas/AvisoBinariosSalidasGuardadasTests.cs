using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Herramientas
{
    // El aviso de salidas binarias omitidas solo sale buscando en el directorio de guardarSalida (o por debajo), nunca con
    // la ruta de un .out concreto ni fuera de ese directorio.
    public sealed class AvisoBinariosSalidasGuardadasTests
    {
        [Theory]
        [InlineData("/tmp/native_ssh-ana/0123456789abcdef0123456789abcdef", true)]
        [InlineData("/tmp/native_ssh-ana", true)]
        [InlineData("/tmp/native_ssh-ana/0123456789abcdef0123456789abcdef/fedcba9876543210fedcba9876543210.out", false)]
        [InlineData("/tmp/otra", false)]
        [InlineData("/tmp/native_ssh", false)]
        [InlineData(".", false)]
        public void SoloEnElDirectorioDeLasSalidasGuardadas(string ruta, bool isConAviso)
        {
            // Act
            string aviso = HerramientasSsh.AvisoBinariosEnSalidasGuardadas(ruta, "/tmp/native_ssh");

            // Assert
            _ = aviso.Should().Be(isConAviso ? HerramientasSsh.AvisoBinariosOmitidos + "\n" : string.Empty);
        }
    }
}
