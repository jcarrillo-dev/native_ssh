using System.Runtime.Versioning;
using FluentAssertions;
using NativeSsh.Comun;

namespace NativeSsh.Test.Comun
{
    // Token obligatorio del modo HTTP (CI2/CT5): de NATIVE_SSH_TOKEN_HTTP o de un fichero (NATIVE_SSH_TOKEN_HTTP_FICHERO)
    // que solo pueda leer su dueño; sin token, o con uno inválido, el modo HTTP no arranca y el error dice qué falta.
    [SupportedOSPlatform("linux")]
    public sealed class TokenHttpTests : IDisposable
    {
        private const string TokenValido = "token-de-prueba-0123456789";
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_token_{Guid.NewGuid():N}")).FullName;

        public void Dispose() => Directory.Delete(_directorio, recursive: true);

        private static Func<string, string?> Entorno(string? token = null, string? fichero = null) => nombre => nombre switch
        {
            TokenHttp.VariableToken => token,
            TokenHttp.VariableFichero => fichero,
            _ => null
        };

        private string Fichero(string contenido, UnixFileMode modo)
        {
            string ruta = Path.Combine(_directorio, $"{Guid.NewGuid():N}.token");
            File.WriteAllText(ruta, contenido);
            File.SetUnixFileMode(ruta, modo);
            return ruta;
        }

        [Theory]
        [InlineData(TokenValido)]
        [InlineData(TokenValido + "\n")]
        [InlineData(TokenValido + "\r\n")]
        public void DesdeFichero0600_LoCargaSinElSaltoFinal(string contenido)
        {
            // Arrange
            string ruta = Fichero(contenido, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            // Act
            bool isCargado = TokenHttp.TryCargar(Entorno(fichero: ruta), out TokenHttp? token, out string? error);

            // Assert
            _ = isCargado.Should().BeTrue(error);
            _ = token!.IsValido($"Bearer {TokenValido}").Should().BeTrue();
            _ = token.IsValido($"Bearer {TokenValido}\n").Should().BeFalse();
        }

        [Fact]
        public void DesdeFichero0400_LoCarga()
        {
            // Arrange
            string ruta = Fichero(TokenValido, UnixFileMode.UserRead);

            // Act & Assert
            _ = TokenHttp.TryCargar(Entorno(fichero: ruta), out _, out string? error).Should().BeTrue(error);
        }

        [Theory]
        [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, "0640")]
        [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, "0644")]
        [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite, "0602")]
        [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupExecute, "0610")]
        public void FicheroConPermisosParaElGrupoUOtros_SeRechaza(UnixFileMode modo, string octal)
        {
            // Arrange
            string ruta = Fichero(TokenValido, modo);

            // Act
            bool isCargado = TokenHttp.TryCargar(Entorno(fichero: ruta), out TokenHttp? token, out string? error);

            // Assert
            _ = isCargado.Should().BeFalse();
            _ = token.Should().BeNull();
            _ = error.Should().Contain(ruta).And.Contain(octal).And.Contain("chmod 600").And.NotContain(TokenValido);
        }

        // Un enlace simbólico no se sigue: sus permisos (0777) no dicen nada del fichero al que apunta.
        [Fact]
        public void FicheroQueEsUnEnlace_SeRechaza()
        {
            // Arrange
            string destino = Fichero(TokenValido, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string enlace = Path.Combine(_directorio, "enlace.token");
            _ = File.CreateSymbolicLink(enlace, destino);

            // Act
            bool isCargado = TokenHttp.TryCargar(Entorno(fichero: enlace), out _, out string? error);

            // Assert
            _ = isCargado.Should().BeFalse();
            _ = error.Should().Contain("enlace");
        }

        [Fact]
        public void FicheroInexistenteOVacio_SeRechazaConLaRuta()
        {
            // Arrange
            string inexistente = Path.Combine(_directorio, "no_existe");
            string vacio = Fichero("\n", UnixFileMode.UserRead | UnixFileMode.UserWrite);

            // Act
            bool isInexistente = TokenHttp.TryCargar(Entorno(fichero: inexistente), out _, out string? errorInexistente);
            bool isVacio = TokenHttp.TryCargar(Entorno(fichero: vacio), out _, out string? errorVacio);

            // Assert
            _ = isInexistente.Should().BeFalse();
            _ = errorInexistente.Should().Contain(inexistente);
            _ = isVacio.Should().BeFalse();
            _ = errorVacio.Should().Contain(vacio).And.Contain("vacío");
        }

        [Fact]
        public void DesdeLaVariable_LoCarga()
        {
            // Act
            bool isCargado = TokenHttp.TryCargar(Entorno(token: TokenValido), out TokenHttp? token, out string? error);

            // Assert
            _ = isCargado.Should().BeTrue(error);
            _ = token!.IsValido($"Bearer {TokenValido}").Should().BeTrue();
        }

        [Theory]
        [InlineData(null, null, "NATIVE_SSH_TOKEN_HTTP")]
        [InlineData("", "", "NATIVE_SSH_TOKEN_HTTP")]
        [InlineData("abc123xyz", null, "16")]
        [InlineData(TokenValido, "/algo", "solo una")]
        public void SinTokenCortoOConLasDosFuentes_NoArrancaYDiceQueFalta(string? variable, string? fichero, string fragmento)
        {
            // Act
            bool isCargado = TokenHttp.TryCargar(Entorno(variable, fichero), out TokenHttp? token, out string? error);

            // Assert
            _ = isCargado.Should().BeFalse();
            _ = token.Should().BeNull();
            _ = error.Should().Contain(fragmento);
            if (variable is { Length: > 0 })
                _ = error.Should().NotContain(variable);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(TokenValido)]
        [InlineData("Basic " + TokenValido)]
        [InlineData("Bearer")]
        [InlineData("Bearer ")]
        [InlineData("Bearer otro-token-0123456789x")]
        [InlineData("Bearer " + TokenValido + "x")]
        [InlineData("Bearer " + "token-de-prueba-012345678")]
        public void Cabeceras_SoloElTokenExactoConBearerEsValido(string? cabecera)
        {
            // Arrange
            _ = TokenHttp.TryCargar(Entorno(token: TokenValido), out TokenHttp? token, out _).Should().BeTrue();

            // Act & Assert
            _ = token!.IsValido(cabecera).Should().BeFalse();
            _ = token.IsValido($"Bearer {TokenValido}").Should().BeTrue();
            _ = token.IsValido($"bearer {TokenValido}").Should().BeTrue("el esquema no distingue mayúsculas (RFC 7235)");
        }
    }
}
