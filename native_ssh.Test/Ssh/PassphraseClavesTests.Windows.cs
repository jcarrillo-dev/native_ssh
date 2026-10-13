using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Ssh;
using NativeSsh.Test.Utilidades;
using Renci.SshNet;

namespace NativeSsh.Test.Ssh
{
    // Lo de PassphraseClavesTests (Linux, con bash) en Windows (solo se compila para Windows): claves generadas con el
    // ssh-keygen.exe del sistema (OpenSSH para Windows), lanzado sin shell, en un directorio temporal propio. La passphrase de
    // NATIVE_SSH_PASSPHRASE abre las claves; sin ella o con otra, el motivo lo dice sin mostrarla; el fichero de la
    // passphrase no se admite fuera de Linux y el motivo lo dice.
    public sealed class PassphraseClavesWindowsTests : IDisposable
    {
        private const string Frase = "Canario-passphrase-7Qx";
        private const string FraseIncorrecta = "Canario-incorrecta-3Kz";
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_frasew_{Guid.NewGuid():N}")).FullName;
        private readonly LoggerPrueba _log = new();

        public void Dispose()
        {
            _log.Dispose();
            Directory.Delete(_directorio, recursive: true);
        }

        // Los mismos formatos que en Linux: OpenSSH, PEM (PKCS#1) y PKCS#8.
        public static TheoryData<string, string> Formatos => new()
        {
            { "ed25519", "OpenSSH" },
            { "rsa", "PEM" },
            { "ecdsa", "PKCS8" }
        };

        private string Clave(string tipo, string formato, string frase)
        {
            string ruta = Path.Combine(_directorio, $"{tipo}_{formato}_{Guid.NewGuid():N}");
            ProcessStartInfo inicio = new("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (string argumento in new[] { "-q", "-t", tipo, "-N", frase, "-f", ruta, "-C", "prueba" })
                inicio.ArgumentList.Add(argumento);
            if (tipo == "rsa")
                foreach (string argumento in new[] { "-b", "2048" })
                    inicio.ArgumentList.Add(argumento);
            if (formato != "OpenSSH")
                foreach (string argumento in new[] { "-m", formato })
                    inicio.ArgumentList.Add(argumento);

            using Process proceso = Process.Start(inicio)!;
            proceso.StandardInput.Close();
            Task<string> errores = proceso.StandardError.ReadToEndAsync();
            _ = proceso.StandardOutput.ReadToEnd();
            _ = proceso.WaitForExit(TimeSpan.FromSeconds(60)).Should().BeTrue("ssh-keygen debía terminar");
            _ = proceso.ExitCode.Should().Be(0, errores.Result);
            _ = File.Exists(ruta).Should().BeTrue($"ssh-keygen debía crear {ruta}");
            return ruta;
        }

        private FabricaConexionSsh Fabrica(string? frase = null, string? fichero = null) =>
            new(new OpcionesNativeSsh { Passphrase = frase, FicheroPassphrase = fichero }, new VerificadorHostConocido([Path.Combine(_directorio, "known_hosts")]), new LoggerDe<FabricaConexionSsh>(_log));

        private static (IReadOnlyList<PrivateKeyFile> Claves, List<string> Motivos) Cargar(FabricaConexionSsh fabrica, string ruta)
        {
            List<string> motivos = [];
            IReadOnlyList<PrivateKeyFile> claves = fabrica.CargarClaves([ruta], motivos);
            return (claves, motivos);
        }

        private void SinRastroDeLaFrase(IEnumerable<string> textos)
        {
            foreach (string texto in textos.Concat(_log.Entradas.Select(e => e.Mensaje)))
            {
                _ = texto.Should().NotContain(Frase);
                _ = texto.Should().NotContain(FraseIncorrecta);
            }
        }

        [Theory]
        [MemberData(nameof(Formatos))]
        public void ClaveCifradaConLaPassphraseDeLaVariable_SeCarga(string tipo, string formato)
        {
            // Arrange
            string ruta = Clave(tipo, formato, Frase);
            using FabricaConexionSsh fabrica = Fabrica(frase: Frase);

            // Act
            (IReadOnlyList<PrivateKeyFile> claves, List<string> motivos) = Cargar(fabrica, ruta);

            // Assert
            _ = motivos.Should().BeEmpty();
            _ = claves.Should().ContainSingle();
        }

        [Theory]
        [MemberData(nameof(Formatos))]
        public void ClaveCifradaSinPassphrase_ElMotivoLoDice(string tipo, string formato)
        {
            // Arrange
            string ruta = Clave(tipo, formato, Frase);
            using FabricaConexionSsh fabrica = Fabrica();

            // Act
            (IReadOnlyList<PrivateKeyFile> claves, List<string> motivos) = Cargar(fabrica, ruta);

            // Assert
            _ = claves.Should().BeEmpty();
            _ = motivos.Should().ContainSingle().Which.Should().Contain(ruta).And.Contain("cifrada con passphrase").And.Contain("no hay ninguna configurada");
        }

        [Theory]
        [MemberData(nameof(Formatos))]
        public void PassphraseIncorrecta_ElMotivoLoDiceSinMostrarla(string tipo, string formato)
        {
            // Arrange
            string ruta = Clave(tipo, formato, Frase);
            using FabricaConexionSsh fabrica = Fabrica(frase: FraseIncorrecta);

            // Act
            (IReadOnlyList<PrivateKeyFile> claves, List<string> motivos) = Cargar(fabrica, ruta);

            // Assert
            _ = claves.Should().BeEmpty();
            _ = motivos.Should().ContainSingle().Which.Should().Contain(ruta).And.Contain("la passphrase de NATIVE_SSH_PASSPHRASE no la abre");
            SinRastroDeLaFrase(motivos);
        }

        // Fuera de Linux, NATIVE_SSH_PASSPHRASE_FICHERO no se admite (sus comprobaciones de dueño y permisos son de Linux):
        // el motivo lo dice y la clave no se carga, aunque el fichero tenga la passphrase buena.
        [Fact]
        public void FicheroDeLaPassphrase_NoSeAdmiteYElMotivoLoDice()
        {
            // Arrange
            string ruta = Clave("ed25519", "OpenSSH", Frase);
            string fichero = Path.Combine(_directorio, "frase");
            File.WriteAllText(fichero, Frase);
            using FabricaConexionSsh fabrica = Fabrica(fichero: fichero);

            // Act
            (IReadOnlyList<PrivateKeyFile> claves, List<string> motivos) = Cargar(fabrica, ruta);

            // Assert
            _ = claves.Should().BeEmpty();
            _ = motivos.Should().ContainSingle().Which.Should().Contain("NATIVE_SSH_PASSPHRASE_FICHERO solo se admite en Linux");
            SinRastroDeLaFrase(motivos);
        }
    }
}
