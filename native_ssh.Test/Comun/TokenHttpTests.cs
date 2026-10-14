using System.Runtime.Versioning;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Test.Utilidades;

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
        [Trait("Categoria", "SoloLinux")]
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
        [Trait("Categoria", "SoloLinux")]
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
        [Trait("Categoria", "SoloLinux")]
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
        [Trait("Categoria", "SoloLinux")]
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
        [Trait("Categoria", "SoloLinux")]
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

        // Cargar con un plazo: una apertura que se bloquea (una FIFO sin escritor) haría fallar la prueba en vez de colgarla.
        private static async Task<(bool IsCargado, string? Error)> CargarConPlazoAsync(Func<string, string?> entorno) =>
            await Task.Run(() => (TokenHttp.TryCargar(entorno, out _, out string? error), error)).WaitAsync(TimeSpan.FromSeconds(10));

        // Una FIFO no cuelga el arranque (antes, la apertura esperaba sin fin a un escritor) y se rechaza por no ser un fichero
        // normal; un directorio dice que lo es (antes, «No existe»).
        [Theory]
        [InlineData("fifo", "no es un fichero normal")]
        [InlineData("directorio", "es un directorio")]
        [Trait("Categoria", "SoloLinux")]
        public async Task FifoODirectorio_SeRechazaSinColgarseYDiceQueEs(string tipo, string fragmento)
        {
            // Arrange
            string ruta = Path.Combine(_directorio, tipo);
            bool isFifo = tipo == "fifo";
            _ = isFifo ? BashLocal.Ejecutar($"mkfifo -m 600 '{ruta}'") : Directory.CreateDirectory(ruta).FullName;
            File.SetUnixFileMode(ruta, UnixFileMode.UserRead | UnixFileMode.UserWrite | (isFifo ? UnixFileMode.None : UnixFileMode.UserExecute));

            // Act
            (bool isCargado, string? error) = await CargarConPlazoAsync(Entorno(fichero: ruta));

            // Assert
            _ = isCargado.Should().BeFalse();
            _ = error.Should().Contain(ruta).And.Contain(fragmento);
        }

        // Con O_NOFOLLOW solo se rechaza el enlace en el último componente; un directorio intermedio que es un enlace se acepta
        // a propósito (p. ej. /home -> /var/home): el dueño y los permisos se comprueban en el descriptor abierto, así que un
        // enlace ajeno solo puede llevar a un fichero propio y 0600.
        [Fact]
        [Trait("Categoria", "SoloLinux")]
        public void EnlaceEnUnDirectorioIntermedio_SeAceptaSiElFicheroEsPropioY0600()
        {
            // Arrange
            string real = Directory.CreateDirectory(Path.Combine(_directorio, "real")).FullName;
            string ruta = Path.Combine(real, "token");
            File.WriteAllText(ruta, TokenValido);
            File.SetUnixFileMode(ruta, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string enlace = Path.Combine(_directorio, "enlace_dir");
            _ = Directory.CreateSymbolicLink(enlace, real);

            // Act
            bool isCargado = TokenHttp.TryCargar(Entorno(fichero: Path.Combine(enlace, "token")), out TokenHttp? token, out string? error);

            // Assert
            _ = isCargado.Should().BeTrue(error);
            _ = token!.IsValido($"Bearer {TokenValido}").Should().BeTrue();
        }

        // Un fichero de otro usuario (aunque sea 0600) no vale: otro lo controla. Se simula pidiendo otro uid.
        [Fact]
        [Trait("Categoria", "SoloLinux")]
        public void FicheroDeOtroUsuario_SeRechaza()
        {
            // Arrange
            string ruta = Fichero(TokenValido, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            uint uid = uint.Parse(BashLocal.Ejecutar("id -u"), CultureInfo.InvariantCulture);

            // Act
            bool isPropio = TokenHttp.TryCargar(Entorno(fichero: ruta), uid, out _, out string? errorPropio);
            bool isAjeno = TokenHttp.TryCargar(Entorno(fichero: ruta), uid + 1, out TokenHttp? token, out string? error);

            // Assert
            _ = isPropio.Should().BeTrue(errorPropio);
            _ = isAjeno.Should().BeFalse();
            _ = token.Should().BeNull();
            _ = error.Should().Contain(ruta).And.Contain("dueño").And.NotContain(TokenValido);
        }

        // Los espacios al principio o al final nunca coincidirían (el servidor HTTP los recorta de la cabecera): se rechazan
        // con un mensaje claro en lugar de arrancar con un token inservible. Del fichero solo se quitan los saltos de línea.
        // Las filas del fichero solo en Linux (modos Unix del fichero; fuera de Linux el fichero no se admite); las de la
        // variable, en todos los sistemas.
        public static TheoryData<string, string> TokensConEspacios
        {
            get
            {
                TheoryData<string, string> filas = new()
                {
                    { "variable", TokenValido + " " },
                    { "variable", " " + TokenValido },
                    { "variable", TokenValido + "\t" }
                };
                if (OperatingSystem.IsLinux())
                {
                    filas.Add("fichero", TokenValido + " \n");
                    filas.Add("fichero", " " + TokenValido + "\n");
                }

                return filas;
            }
        }

        [Theory]
        [MemberData(nameof(TokensConEspacios))]
        public void TokenConEspaciosAlPrincipioOAlFinal_SeRechaza(string fuente, string valor)
        {
            // Arrange
            Func<string, string?> entorno = fuente == "variable" ? Entorno(token: valor) : Entorno(fichero: Fichero(valor, UnixFileMode.UserRead | UnixFileMode.UserWrite));

            // Act
            bool isCargado = TokenHttp.TryCargar(entorno, out _, out string? error);

            // Assert
            _ = isCargado.Should().BeFalse();
            _ = error.Should().Contain("espacios").And.NotContain(TokenValido);
        }

        // Frontera del mínimo de 16 caracteres (mutación A04 de la auditoría del tramo 1).
        [Theory]
        [InlineData(15, false)]
        [InlineData(16, true)]
        public void Minimo16Caracteres_LaFronteraExacta(int caracteres, bool isValido)
        {
            // Arrange
            string token = new('k', caracteres);

            // Act
            bool isCargado = TokenHttp.TryCargar(Entorno(token: token), out _, out string? error);

            // Assert
            _ = isCargado.Should().Be(isValido, error);
            if (!isValido)
                _ = error.Should().Contain("16");
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

        // Un token con un salto de línea dentro nunca coincidiría con una cabecera HTTP (no puede llevarlo): un fichero con
        // más de una línea no vacía (o la variable con un salto) se rechaza con un mensaje claro, sin el token.
        [Theory]
        [InlineData("fichero", TokenValido + "\n" + "token-dos-0123456789\n")]
        [InlineData("fichero", TokenValido + "\r\n\r\n" + "token-dos-0123456789")]
        [InlineData("fichero", "\n" + TokenValido + "\n")]
        [InlineData("variable", TokenValido + "\nsegunda")]
        [InlineData("variable", TokenValido + "\rsegunda")]
        [Trait("Categoria", "SoloLinux")]
        public void TokenDeVariasLineas_SeRechaza(string fuente, string valor)
        {
            // Arrange
            Func<string, string?> entorno = fuente == "fichero"
                ? Entorno(fichero: Fichero(valor, UnixFileMode.UserRead | UnixFileMode.UserWrite))
                : Entorno(token: valor);

            // Act
            bool isCargado = TokenHttp.TryCargar(entorno, out TokenHttp? token, out string? error);

            // Assert
            _ = isCargado.Should().BeFalse();
            _ = token.Should().BeNull();
            _ = error.Should().Contain("más de una línea").And.NotContain(TokenValido);
        }

        // Lo mismo con la variable, también fuera de Linux: la anterior es SoloLinux porque sus ficheros llevan modos Unix (y
        // NATIVE_SSH_TOKEN_HTTP_FICHERO solo se admite en Linux), pero la variable es la fuente del token en los demás sistemas.
        [Theory]
        [InlineData(TokenValido + "\nsegunda")]
        [InlineData(TokenValido + "\rsegunda")]
        public void TokenDeVariasLineasEnLaVariable_SeRechazaEnCualquierSistema(string valor)
        {
            // Act
            bool isCargado = TokenHttp.TryCargar(Entorno(token: valor), out TokenHttp? token, out string? error);

            // Assert
            _ = isCargado.Should().BeFalse();
            _ = token.Should().BeNull();
            _ = error.Should().Contain("más de una línea").And.NotContain(TokenValido);
        }

        // Un fichero con solo saltos de línea está vacío; uno enorme (disperso: no ocupa disco) se rechaza por su tamaño sin
        // leerlo.
        [Fact]
        [Trait("Categoria", "SoloLinux")]
        public void SoloSaltosDeLineaOFicheroEnorme_SeRechazan()
        {
            // Arrange
            string saltos = Fichero("\n\r\n\n", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string enorme = Fichero("", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using (FileStream f = new(enorme, FileMode.Open, FileAccess.Write))
                f.SetLength(50_000_000);

            // Act
            bool isSaltos = TokenHttp.TryCargar(Entorno(fichero: saltos), out _, out string? errorSaltos);
            bool isEnorme = TokenHttp.TryCargar(Entorno(fichero: enorme), out _, out string? errorEnorme);

            // Assert
            _ = isSaltos.Should().BeFalse();
            _ = errorSaltos.Should().Contain("vacío");
            _ = isEnorme.Should().BeFalse();
            _ = errorEnorme.Should().Contain("50000000 bytes");
        }

        // El dueño es stx_uid (desplazamiento 20 de struct statx), no stx_gid (24): un statx simulado con uid y gid distintos.
        [Fact]
        public void Statx_DuenoEsElUidNoElGid()
        {
            // Arrange
            byte[] statx = new byte[256];
            _ = BitConverter.TryWriteBytes(statx.AsSpan(20), 1234u);
            _ = BitConverter.TryWriteBytes(statx.AsSpan(24), 5678u);
            _ = BitConverter.TryWriteBytes(statx.AsSpan(28), (ushort)0x8180);
            _ = BitConverter.TryWriteBytes(statx.AsSpan(40), 99ul);

            // Act
            InteropLinux.Estado estado = InteropLinux.InterpretarStatx(statx);

            // Assert
            _ = estado.Should().Be(new InteropLinux.Estado(0x8180, 1234u, 99));
        }

        // Y con el statx real: un fichero propio cuyo grupo es otro (un grupo suplementario del usuario) se acepta, y se
        // rechaza si el uid pedido es ese gid. Sin grupos suplementarios no hay cómo crearlo sin root: lo cubre la anterior.
        [Fact]
        [Trait("Categoria", "SoloLinux")]
        public void FicheroPropioDeOtroGrupo_ElDuenoEsElUid()
        {
            // Arrange
            uint uid = uint.Parse(BashLocal.Ejecutar("id -u"), CultureInfo.InvariantCulture);
            string? otroGrupo = BashLocal.Ejecutar("id -G").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(g => g != uid.ToString(CultureInfo.InvariantCulture));
            if (otroGrupo is null)
                return;
            string ruta = Fichero(TokenValido, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _ = BashLocal.Ejecutar($"chgrp {otroGrupo} '{ruta}' && stat -c %g '{ruta}'").Should().Be(otroGrupo);

            // Act
            bool isPropio = TokenHttp.TryCargar(Entorno(fichero: ruta), uid, out _, out string? errorPropio);
            bool isConElGid = TokenHttp.TryCargar(Entorno(fichero: ruta), uint.Parse(otroGrupo, CultureInfo.InvariantCulture), out _, out string? errorGid);

            // Assert
            _ = isPropio.Should().BeTrue(errorPropio);
            _ = isConElGid.Should().BeFalse();
            _ = errorGid.Should().Contain($"uid {uid}");
        }
    }
}
