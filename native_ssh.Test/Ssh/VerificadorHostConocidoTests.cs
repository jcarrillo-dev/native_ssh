using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using System.Security.Cryptography;
using System.Text;
using NativeSsh.Ssh;

namespace NativeSsh.Test.Ssh
{
    public class VerificadorHostConocidoTests
    {
        private static readonly byte[] Clave = [1, 2, 3, 4, 5];
        private static readonly byte[] OtraClave = [9, 9, 9];
        private static readonly string ClaveB64 = Convert.ToBase64String(Clave);
        private static readonly string OtraB64 = Convert.ToBase64String(OtraClave);

        private static ResultadoHostEnum Verificar(string servidor, int puerto, params string[] lineas) =>
            VerificadorHostConocido.Verificar(lineas, servidor, puerto, "ssh-ed25519", Clave);

        [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "Reproduce el formato hasheado de known_hosts.")]
        private static string Hasheado(string nombre)
        {
            byte[] sal = RandomNumberGenerator.GetBytes(20);
            byte[] hash = HMACSHA1.HashData(sal, Encoding.UTF8.GetBytes(nombre));
            return $"|1|{Convert.ToBase64String(sal)}|{Convert.ToBase64String(hash)}";
        }

        [Theory]
        [InlineData("10.0.0.1", 22, "10.0.0.1")]
        [InlineData("10.0.0.1", 22, "otro,10.0.0.1")]
        [InlineData("Servidor.Local", 22, "servidor.local")]
        [InlineData("10.0.0.1", 2222, "[10.0.0.1]:2222")]
        [InlineData("db1.prod", 22, "*.prod")]
        [InlineData("db1.prod", 22, "db?.prod")]
        public void Plano_Conocido(string servidor, int puerto, string patron) => _ = Verificar(servidor, puerto, $"{patron} ssh-ed25519 {ClaveB64} comentario").Should().Be(ResultadoHostEnum.Conocido);

        [Fact]
        public void PuertoDistinto_NoCoincideConLaEntradaSinPuerto() => _ = Verificar("10.0.0.1", 2222, $"10.0.0.1 ssh-ed25519 {ClaveB64}").Should().Be(ResultadoHostEnum.Desconocido);

        [Fact]
        public void Negacion_Excluye() => _ = Verificar("db1.prod", 22, $"*.prod,!db1.prod ssh-ed25519 {ClaveB64}").Should().Be(ResultadoHostEnum.Desconocido);

        [Theory]
        [InlineData("10.0.0.1", 22)]
        [InlineData("10.0.0.1", 2222)]
        public void Hasheado_Conocido(string servidor, int puerto)
        {
            // Arrange
            string nombre = puerto == 22 ? servidor : $"[{servidor}]:{puerto}";

            // Act & Assert
            _ = Verificar(servidor, puerto, $"{Hasheado(nombre)} ssh-ed25519 {ClaveB64}").Should().Be(ResultadoHostEnum.Conocido);
            _ = Verificar("10.0.0.2", puerto, $"{Hasheado(nombre)} ssh-ed25519 {ClaveB64}").Should().Be(ResultadoHostEnum.Desconocido);
        }

        [Fact]
        public void MismoTipoConOtraClave_ClaveDistinta() => _ = Verificar("h", 22, $"h ssh-ed25519 {OtraB64}").Should().Be(ResultadoHostEnum.ClaveDistinta);

        [Fact]
        public void OtroTipoDeClave_Desconocido() => _ = Verificar("h", 22, $"h ecdsa-sha2-nistp256 {OtraB64}").Should().Be(ResultadoHostEnum.Desconocido);

        [Fact]
        public void VariasEntradas_BastaUnaQueCoincida() => _ = Verificar("h", 22, $"h ssh-ed25519 {OtraB64}", $"h ssh-ed25519 {ClaveB64}").Should().Be(ResultadoHostEnum.Conocido);

        [Fact]
        public void Revocada_GanaSobreConocida() => _ = Verificar("h", 22, $"h ssh-ed25519 {ClaveB64}", $"@revoked * ssh-ed25519 {ClaveB64}").Should().Be(ResultadoHostEnum.Revocado);

        [Theory]
        [InlineData("# h ssh-ed25519 AQIDBAU=")]
        [InlineData("")]
        [InlineData("h ssh-ed25519")]
        [InlineData("h ssh-ed25519 no-es-base64!")]
        [InlineData("@cert-authority h ssh-ed25519 AQIDBAU=")]
        [InlineData("@desconocido h ssh-ed25519 AQIDBAU=")]
        [InlineData("@REVOKED h ssh-ed25519 AQIDBAU=")]
        [InlineData("@custom h ssh-ed25519 AQIDBAU=")]
        public void LineasIgnoradas(string linea) => _ = Verificar("h", 22, linea).Should().Be(ResultadoHostEnum.Desconocido);

        [Fact]
        public void Hasheado_Mayusculas_Coincide()
        {
            // Arrange
            string entrada = Hasheado("servidor.local");

            // Act & Assert
            _ = Verificar("Servidor.LOCAL", 22, $"{entrada} ssh-ed25519 {ClaveB64}").Should().Be(ResultadoHostEnum.Conocido);
        }

        [Fact]
        public void ObtenerTiposClave_RetornaTiposConocidosNoRevocados()
        {
            // Arrange
            string[] lineas =
            [
                $"h ssh-ed25519 {ClaveB64}",
                $"h ssh-rsa {OtraB64}",
                $"otro ssh-dss {ClaveB64}",
                $"@revoked h ssh-ed25519 {ClaveB64}",
                "@custom h ecdsa-sha2-nistp256 AQIDBAU="
            ];

            // Act
            IReadOnlySet<string> tipos = VerificadorHostConocido.ObtenerTiposClave(lineas, "h", 22);

            // Assert
            _ = tipos.Should().ContainSingle().Which.Should().Be("ssh-rsa");
        }

        [Fact]
        public void Fichero_InexistenteSeIgnora()
        {
            // Arrange
            string ruta = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");
            File.WriteAllText(ruta, $"h ssh-ed25519 {ClaveB64}\n");
            try
            {
                VerificadorHostConocido verificador = new([ruta + ".no-existe", ruta]);

                // Act & Assert
                _ = verificador.Verificar("h", 22, "ssh-ed25519", Clave).Should().Be(ResultadoHostEnum.Conocido);
            }
            finally
            {
                File.Delete(ruta);
            }
        }

        [Fact]
        public void LeerTipo_DelBlob()
        {
            // Arrange
            byte[] blob = [0, 0, 0, 7, .. "ssh-rsa"u8, 0, 0, 0, 1, 3];

            // Act & Assert
            _ = VerificadorHostConocido.LeerTipo(blob).Should().Be("ssh-rsa");
            _ = VerificadorHostConocido.LeerTipo([0, 0, 0, 9, 1]).Should().BeNull();
            _ = VerificadorHostConocido.LeerTipo([]).Should().BeNull();
        }

        [Fact]
        public void FiltrarAlgoritmosHost_FiltraSegunTiposConocidos()
        {
            // Arrange
            Renci.SshNet.ConnectionInfo infoEd = new("host", "user", new Renci.SshNet.PasswordAuthenticationMethod("user", "pass"));
            Renci.SshNet.ConnectionInfo infoRsa = new("host", "user", new Renci.SshNet.PasswordAuthenticationMethod("user", "pass"));
            Renci.SshNet.ConnectionInfo infoEcdsa = new("host", "user", new Renci.SshNet.PasswordAuthenticationMethod("user", "pass"));
            Renci.SshNet.ConnectionInfo infoVacio = new("host", "user", new Renci.SshNet.PasswordAuthenticationMethod("user", "pass"));
            int totalInicial = infoVacio.HostKeyAlgorithms.Count;

            // Act
            FabricaConexionSsh.FiltrarAlgoritmosHost(infoEd, ["ssh-ed25519"]);
            FabricaConexionSsh.FiltrarAlgoritmosHost(infoRsa, ["ssh-rsa"]);
            FabricaConexionSsh.FiltrarAlgoritmosHost(infoEcdsa, ["ecdsa-sha2-nistp256"]);
            FabricaConexionSsh.FiltrarAlgoritmosHost(infoVacio, []);

            // Assert
            _ = infoEd.HostKeyAlgorithms.Keys.Should().Equal(["ssh-ed25519"]);
            _ = infoRsa.HostKeyAlgorithms.Keys.Should().BeSubsetOf(["rsa-sha2-512", "rsa-sha2-256", "ssh-rsa"]);
            _ = infoRsa.HostKeyAlgorithms.Keys.Should().Contain("ssh-rsa");
            _ = infoEcdsa.HostKeyAlgorithms.Keys.Should().AllSatisfy(k => k.Should().StartWith("ecdsa-sha2-"));
            _ = infoVacio.HostKeyAlgorithms.Count.Should().Be(totalInicial);
        }
    }
}
