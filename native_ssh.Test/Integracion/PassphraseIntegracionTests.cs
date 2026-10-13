using System.Runtime.Versioning;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Ssh;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // CI3 de punta a punta: la clave del sshd local, cifrada con una passphrase (copia en un directorio temporal), autentica
    // con la passphrase de la variable o del fichero.
    [Collection(ColeccionSshd.Nombre)]
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class PassphraseIntegracionTests(SshdLocal sshd) : IDisposable
    {
        private const string Frase = "frase de prueba con espacios";
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_frase_int_{Guid.NewGuid():N}")).FullName;

        public void Dispose() => Directory.Delete(_directorio, recursive: true);

        private string ClaveCifrada()
        {
            string ruta = Path.Combine(_directorio, "cliente_cifrada");
            File.Copy(sshd.RutaClave, ruta);
            File.SetUnixFileMode(ruta, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _ = BashLocal.Ejecutar($"ssh-keygen -q -p -P '' -N '{Frase}' -f '{ruta}'");
            _ = File.ReadAllText(ruta).Should().NotBe(File.ReadAllText(sshd.RutaClave), "la copia debe quedar cifrada");
            return ruta;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClaveCifrada_AutenticaConLaPassphraseDeLaVariableODelFichero(bool isFichero)
        {
            // Arrange
            string clave = ClaveCifrada();
            string? fichero = null;
            if (isFichero)
            {
                fichero = Path.Combine(_directorio, "frase");
                File.WriteAllText(fichero, Frase + "\n");
                File.SetUnixFileMode(fichero, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            OpcionesNativeSsh opciones = sshd.Opciones() with { RutasIdentidad = [clave], Passphrase = isFichero ? null : Frase, FicheroPassphrase = fichero };
            using FabricaConexionSsh fabrica = sshd.Fabrica(opciones);

            // Act
            using IConexionSsh conexion = await fabrica.ConectarAsync(sshd.Destino, CancellationToken.None);

            // Assert
            _ = conexion.IsConectada.Should().BeTrue();
        }
    }
}
