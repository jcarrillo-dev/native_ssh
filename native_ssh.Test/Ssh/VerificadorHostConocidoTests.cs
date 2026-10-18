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

        // Una clave revocada en un fichero responde sin abrir los siguientes, como cuando se leían con un iterador: un
        // known_hosts2 que existe pero no se puede leer no convierte la revocación en una excepción.
        [Fact]
        [Trait("Categoria", "SoloLinux")]
        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        public void Revocada_EnUnFichero_NoLeeLosSiguientes()
        {
            // Arrange
            string ruta = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");
            string ilegible = ruta + "2";
            File.WriteAllText(ruta, $"@revoked h ssh-ed25519 {ClaveB64}\n");
            File.WriteAllText(ilegible, $"h ssh-ed25519 {ClaveB64}\n");
            File.SetUnixFileMode(ilegible, UnixFileMode.None);
            try
            {
                VerificadorHostConocido verificador = new([ruta, ilegible]);

                // Act & Assert
                _ = verificador.Verificar("h", 22, "ssh-ed25519", Clave).Should().Be(ResultadoHostEnum.Revocado);
            }
            finally
            {
                File.Delete(ruta);
                File.Delete(ilegible);
            }
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

        // Bordes de la lectura de un fichero (el búfer inicial es de 4096 caracteres y crece doblando): ficheros de esas
        // longitudes y alrededor, la línea de la clave al final, con o sin salto final y con cada tipo de salto.
        [Theory]
        [InlineData(4095, "\n", true)]
        [InlineData(4096, "\n", true)]
        [InlineData(4096, "\n", false)]
        [InlineData(4097, "\r\n", true)]
        [InlineData(8192, "\r", true)]
        [InlineData(8193, "\r", false)]
        [InlineData(20000, "\r\n", false)]
        public void Fichero_LongitudesDelBuffer_LeeHastaLaUltimaLinea(int longitud, string salto, bool hasSaltoFinal)
        {
            // Arrange
            string ultima = $"h ssh-ed25519 {ClaveB64}";
            string texto = Relleno(longitud - ultima.Length - (hasSaltoFinal ? salto.Length : 0), salto) + ultima + (hasSaltoFinal ? salto : "");
            _ = texto.Length.Should().Be(longitud);

            // Act & Assert
            ConFichero(texto, verificador =>
            {
                _ = verificador.Verificar("h", 22, "ssh-ed25519", Clave).Should().Be(ResultadoHostEnum.Conocido);
                _ = verificador.ObtenerTiposClave("h", 22).Should().ContainSingle().Which.Should().Be("ssh-ed25519");
            });
        }

        // La línea de la clave (y la que la revoca) empieza antes del carácter 4096 y termina después: cruza el final del búfer
        // inicial y el de un bloque de lectura.
        [Theory]
        [InlineData(4096 - 10)]
        [InlineData(4096 - 1)]
        [InlineData(8192 - 20)]
        public void Linea_QueCruzaElLimiteDeLectura(int inicio)
        {
            // Arrange
            string conocida = $"h ssh-ed25519 {ClaveB64}\n";
            string revocada = $"@revoked h ssh-ed25519 {ClaveB64}\n";

            // Act & Assert
            ConFichero(Relleno(inicio, "\n") + conocida, verificador =>
                _ = verificador.Verificar("h", 22, "ssh-ed25519", Clave).Should().Be(ResultadoHostEnum.Conocido));
            ConFichero(Relleno(inicio, "\n") + revocada + conocida, verificador =>
            {
                _ = verificador.Verificar("h", 22, "ssh-ed25519", Clave).Should().Be(ResultadoHostEnum.Revocado);
                _ = verificador.ObtenerTiposClave("h", 22).Should().BeEmpty();
            });
        }

        // Un carácter de dos bytes en UTF-8 que cae a caballo del byte 4096 (y una clave después) se decodifica entero.
        [Fact]
        public void Fichero_ConCaracteresMultibyteEnElLimite()
        {
            // Arrange
            string texto = "#" + new string('a', 4094) + "ñ\n" + $"h ssh-ed25519 {ClaveB64}";

            // Act & Assert
            ConFichero(texto, verificador => _ = verificador.Verificar("h", 22, "ssh-ed25519", Clave).Should().Be(ResultadoHostEnum.Conocido));
        }

        [Theory]
        [InlineData("")]
        [InlineData("﻿")]
        [InlineData("\n")]
        [InlineData("\r\n\r\n")]
        public void Fichero_VacioOSinLineasUtiles(string texto) =>
            ConFichero(texto, verificador =>
            {
                _ = verificador.Verificar("h", 22, "ssh-ed25519", Clave).Should().Be(ResultadoHostEnum.Desconocido);
                _ = verificador.ObtenerTiposClave("h", 22).Should().BeEmpty();
            });

        [Theory]
        [InlineData("")]
        [InlineData("﻿")]
        public void Fichero_SinSaltoFinal(string prefijo) =>
            ConFichero($"{prefijo}otro ssh-rsa {OtraB64}\nh ssh-ed25519 {ClaveB64}", verificador =>
            {
                _ = verificador.Verificar("h", 22, "ssh-ed25519", Clave).Should().Be(ResultadoHostEnum.Conocido);
                _ = verificador.ObtenerTiposClave("h", 22).Should().ContainSingle().Which.Should().Be("ssh-ed25519");
            });

        // Líneas de comentario hasta la longitud pedida, cada una terminada en el salto dado.
        private static string Relleno(int longitud, string salto)
        {
            StringBuilder texto = new();
            while (texto.Length < longitud)
            {
                int resto = longitud - texto.Length;
                int cuerpo = Math.Min(resto - salto.Length, 70);
                if (cuerpo < 1)
                {
                    // No cabe un comentario y su salto: se completa con saltos solos (líneas vacías).
                    _ = texto.Append('\n', resto);
                    break;
                }

                _ = texto.Append('#').Append('x', cuerpo - 1).Append(salto);
            }

            return texto.ToString();
        }

        private static void ConFichero(string texto, Action<VerificadorHostConocido> comprobar)
        {
            string ruta = Path.Combine(Path.GetTempPath(), $"kh-{Guid.NewGuid():N}");
            File.WriteAllText(ruta, texto, new UTF8Encoding(false));
            try
            {
                comprobar(new VerificadorHostConocido([ruta]));
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

        // Un known_hosts que no cabe en el búfer máximo (2^30 caracteres en el producto; aquí 8192 para no escribir 1 GiB) da un
        // mensaje que lo dice, en lugar del ArgumentOutOfRangeException de rentar un búfer de tamaño negativo.
        [Theory]
        [InlineData(8191, false)]
        [InlineData(8192, true)]
        [InlineData(20000, true)]
        public void LeerTexto_FicheroMayorQueElMaximo_DaUnMensajeClaro(int caracteres, bool isDemasiadoGrande)
        {
            // Arrange
            string ruta = Path.Combine(Path.GetTempPath(), $"ns_kh_grande_{Guid.NewGuid():N}");
            File.WriteAllText(ruta, new string('a', caracteres));
            try
            {
                // Act
                Func<int> leer = () =>
                {
                    char[] texto = VerificadorHostConocido.LeerTexto(ruta, 8192, out int longitud);
                    System.Buffers.ArrayPool<char>.Shared.Return(texto);
                    return longitud;
                };

                // Assert
                if (isDemasiadoGrande)
                    _ = leer.Should().Throw<NativeSsh.Comun.NativeSshException>().WithMessage($"*'{ruta}'*8192 caracteres o más*");
                else
                    _ = leer().Should().Be(caracteres);
            }
            finally
            {
                File.Delete(ruta);
            }
        }

        // Las sobrecargas sobre líneas ya leídas: si juntas suman el máximo o más, el mismo mensaje.
        [Fact]
        public void LongitudJuntada_LineasQueSumanElMaximo_DaUnMensajeClaro()
        {
            // Act
            Func<int> juntar = () => VerificadorHostConocido.LongitudJuntada([new string('a', 5000), new string('b', 5000)], 8192);

            // Assert
            _ = juntar.Should().Throw<NativeSsh.Comun.NativeSshException>().WithMessage("*líneas de known_hosts*8192 caracteres o más*");
            _ = VerificadorHostConocido.LongitudJuntada(["abc", "de"], 8192).Should().Be(5);
        }

        // Host desconocido: el error pide al usuario que compruebe la huella y la guarde él, y dice al cliente que no la añada
        // nunca (tampoco si el usuario dice que ya la comprobó). Con varios known_hosts, el texto habla de «uno de ellos».
        [Fact]
        public void RechazoHostDesconocido_UnFichero_LaGuardaElUsuarioYNoElModelo()
        {
            // Arrange
            _ = NativeSsh.Comun.DestinoSsh.TryParse("ana@srv:2222", "ana", out NativeSsh.Comun.DestinoSsh destino).Should().BeTrue();

            // Act
            string texto = FabricaConexionSsh.DescribirRechazo(destino, ResultadoHostEnum.Desconocido, "ssh-ed25519", "abc", ["/h/.ssh/known_hosts"]);

            // Assert
            _ = texto.Should().Contain("srv:2222 no está en /h/.ssh/known_hosts (clave ssh-ed25519 SHA256:abc)")
                .And.Contain("Pide al usuario que compruebe esa huella y la guarde él en /h/.ssh/known_hosts")
                .And.Contain("si no es ese fichero, hay que copiarla")
                .And.Contain("No la añadas tú, ni aunque el usuario diga que ya la comprobó: la guarda el usuario")
                .And.Contain("no desactives la comprobación de la clave del host")
                .And.NotContain("sin que el usuario haya comprobado").And.NotContain("ssh-keyscan");
        }

        // Una línea @cert-authority no cuenta (native_ssh no admite certificados de host): el error de host desconocido lo
        // dice, para que quien confía en una autoridad de certificación sepa por qué no basta.
        [Fact]
        public void RechazoHostDesconocido_DiceQueLosCertificadosDeHostNoSeAdmiten()
        {
            // Arrange
            _ = NativeSsh.Comun.DestinoSsh.TryParse("ana@srv", "ana", out NativeSsh.Comun.DestinoSsh destino).Should().BeTrue();

            // Act
            string texto = FabricaConexionSsh.DescribirRechazo(destino, ResultadoHostEnum.Desconocido, "ssh-ed25519", "abc", ["/h/.ssh/known_hosts"]);

            // Assert
            _ = texto.Should().Contain("ni certificados de host: una línea @cert-authority no cuenta");
        }

        // Con un servidor IPv6, el host:puerto del error va entre corchetes, como un destino que se puede volver a usar.
        [Fact]
        public void RechazoHostDesconocido_Ipv6_EntreCorchetes()
        {
            // Arrange
            _ = NativeSsh.Comun.DestinoSsh.TryParse("ana@[::1]:2222", "ana", out NativeSsh.Comun.DestinoSsh destino).Should().BeTrue();

            // Act
            string texto = FabricaConexionSsh.DescribirRechazo(destino, ResultadoHostEnum.Desconocido, "ssh-ed25519", "abc", ["/h/.ssh/known_hosts"]);

            // Assert
            _ = texto.Should().StartWith("[::1]:2222 no está en /h/.ssh/known_hosts");
        }

        [Fact]
        public void RechazoHostDesconocido_VariosFicheros_SeLeeComoLista()
        {
            // Arrange
            _ = NativeSsh.Comun.DestinoSsh.TryParse("ana@srv", "ana", out NativeSsh.Comun.DestinoSsh destino).Should().BeTrue();

            // Act
            string texto = FabricaConexionSsh.DescribirRechazo(destino, ResultadoHostEnum.Desconocido, "ssh-ed25519", "abc", ["/a/known_hosts", "/b/known_hosts"]);

            // Assert
            _ = texto.Should().Contain("srv:22 no está en ninguno de estos known_hosts: /a/known_hosts, /b/known_hosts (clave")
                .And.Contain("la guarde él en uno de ellos")
                .And.Contain("si no es ninguno de ellos, hay que copiarla")
                .And.Contain("No la añadas tú");
        }

        // Clave distinta: también la actualiza el usuario, no el cliente.
        [Fact]
        public void RechazoClaveDistinta_LaActualizaElUsuario()
        {
            // Arrange
            _ = NativeSsh.Comun.DestinoSsh.TryParse("ana@srv", "ana", out NativeSsh.Comun.DestinoSsh destino).Should().BeTrue();

            // Act
            string texto = FabricaConexionSsh.DescribirRechazo(destino, ResultadoHostEnum.ClaveDistinta, "ssh-ed25519", "abc", ["/a/known_hosts"]);

            // Assert
            _ = texto.Should().Contain("es él quien debe comprobar la huella nueva y actualizar known_hosts (no lo actualices tú)");
        }
    }
}
