using FluentAssertions;
using System.Text;
using NativeSsh.Protocolo;

namespace NativeSsh.Test.Protocolo
{
    public class LectorCapturaPathTests
    {
        private static readonly Guid Id = Guid.Parse("0123456789abcdef0123456789abcdef");

        private static string Base64(string texto) => Convert.ToBase64String(Encoding.UTF8.GetBytes(texto));

        private static string Captura(string base64, Guid? id = null) => $"__NSP_{id ?? Id:N}_\n{base64}\n__NSQ_{id ?? Id:N}_\n";

        private static (bool IsValido, string? Path, string? Motivo) Leer(string salida) =>
            (LectorCapturaPath.TryLeer(Encoding.UTF8.GetBytes(salida), Id, out string? path, out string? motivo), path, motivo);

        [Theory]
        [InlineData("/usr/local/bin:/usr/bin:/bin")]
        [InlineData("/opt/mi dir/bin:/home/ana/.cargo/bin:/usr/bin")]
        [InlineData("/ruta/con'comilla:/ruta/$(x):/ñandú")]
        public void PathValido_SeLeeTalCual(string path)
        {
            // Act
            (bool isValido, string? leido, _) = Leer(Captura(Base64(path)));

            // Assert
            _ = isValido.Should().BeTrue();
            _ = leido.Should().Be(path);
        }

        [Fact]
        public void RuidoFueraDeLosMarcadores_SeIgnora()
        {
            // Arrange: ruido que también es base64 válido, antes y después.
            string salida = $"QUJD\nMensaje: reinicio\n{Captura(Base64("/usr/bin:/bin"))}QUJD\n[1]+ Hecho\n";

            // Act
            (bool isValido, string? path, _) = Leer(salida);

            // Assert
            _ = isValido.Should().BeTrue();
            _ = path.Should().Be("/usr/bin:/bin");
        }

        [Theory]
        [InlineData("")]
        [InlineData("L3Vzci9iaW4=\n")]
        [InlineData("__NSP_0123456789abcdef0123456789abcdef_\nL3Vzci9iaW4=\n")]
        [InlineData("\n__NSQ_0123456789abcdef0123456789abcdef_\n__NSP_0123456789abcdef0123456789abcdef_\nL3Vzci9iaW4=\n")]
        [InlineData("__NSP_0123456789abcdef0123456789abcdef_\nL3Vzci9iaW4=\n__NSQ_0123456789abcdef0123456789abcdef_\n__NSP_0123456789abcdef0123456789abcdef_\n")]
        [InlineData("__NSP_ffffffffffffffffffffffffffffffff_\nL3Vzci9iaW4=\n__NSQ_ffffffffffffffffffffffffffffffff_\n")]
        public void MarcadoresAusentesRepetidosDesordenadosODeOtraCaptura_SeRechaza(string salida)
        {
            // Act
            (bool isValido, string? path, string? motivo) = Leer(salida);

            // Assert
            _ = isValido.Should().BeFalse();
            _ = path.Should().BeNull();
            _ = motivo.Should().Contain("marcadores");
        }

        [Theory]
        [InlineData("L3Vzci9i aW4=")]
        [InlineData("L3Vzci9iaW4")]
        [InlineData("L3Vz=ci9iaW4")]
        [InlineData("ruido\nL3Vzci9iaW4=")]
        public void ContenidoQueNoEsBase64_SeRechaza(string contenido)
        {
            // Act
            (bool isValido, _, string? motivo) = Leer(Captura(contenido));

            // Assert
            _ = isValido.Should().BeFalse();
            _ = motivo.Should().Contain("base64");
        }

        [Theory]
        [InlineData("", "PATH vacío")]
        [InlineData("bin:/usr/bin", "entrada relativa ('bin')")]
        [InlineData("/usr/bin:.", "entrada relativa ('.')")]
        [InlineData("/usr/bin::/bin", "entrada vacía")]
        [InlineData(":/usr/bin", "entrada vacía")]
        [InlineData("/usr/bin:", "entrada vacía")]
        [InlineData("/usr/bin:/a\tb", "caracteres de control")]
        [InlineData("/usr/bin:/a\u001bb", "caracteres de control")]
        [InlineData("/usr/bin:/a\u0085b", "caracteres de control")]
        [InlineData("/usr/bin\n:/opt", "caracteres de control")]
        public void PathNoValido_SeRechazaConSuMotivo(string path, string motivo)
        {
            // Act
            (bool isValido, string? leido, string? motivoLeido) = Leer(Captura(Base64(path)));

            // Assert
            _ = isValido.Should().BeFalse();
            _ = leido.Should().BeNull();
            _ = motivoLeido.Should().Contain(motivo);
        }

        [Fact]
        public void PathQueNoEsUtf8_SeRechaza()
        {
            // Arrange
            string base64 = Convert.ToBase64String([.. "/usr/bin:/opt/"u8, 0xC3, .. "x"u8]);

            // Act
            (bool isValido, _, string? motivo) = Leer(Captura(base64));

            // Assert
            _ = isValido.Should().BeFalse();
            _ = motivo.Should().Contain("UTF-8");
        }
    }
}
