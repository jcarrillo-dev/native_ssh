using System.Runtime.Versioning;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NativeSsh.Comun;
using NativeSsh.Ssh;
using NativeSsh.Test.Utilidades;
using Renci.SshNet;

namespace NativeSsh.Test.Ssh
{
    // CI3: claves privadas con passphrase. La passphrase sale de NATIVE_SSH_PASSPHRASE o de un fichero que solo pueda leer su
    // dueño (NATIVE_SSH_PASSPHRASE_FICHERO), nunca del LLM; si falta, es incorrecta o el fichero no vale, el motivo lo dice
    // sin mostrarla, ni en los mensajes ni en el log. Claves generadas con ssh-keygen en un directorio temporal propio.
    [SupportedOSPlatform("linux")]
    public sealed class PassphraseClavesTests : IDisposable
    {
        private const string Frase = "Canario-passphrase-7Qx";
        private const string FraseIncorrecta = "Canario-incorrecta-3Kz";
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_frase_{Guid.NewGuid():N}")).FullName;
        private readonly LoggerPrueba _log = new();

        public void Dispose()
        {
            _log.Dispose();
            Directory.Delete(_directorio, recursive: true);
        }

        // Formatos: OpenSSH (el de ssh-keygen por defecto), PEM (PKCS#1, «Proc-Type: 4,ENCRYPTED») y PKCS#8 («BEGIN
        // ENCRYPTED PRIVATE KEY»): SSH.NET avisa de la falta de passphrase con una excepción propia en los dos primeros, pero
        // no en PKCS#8.
        public static TheoryData<string, string> Formatos => new()
        {
            { "ed25519", "OpenSSH" },
            { "rsa", "PEM" },
            { "ecdsa", "PKCS8" }
        };

        private string Clave(string tipo, string formato, string frase)
        {
            string ruta = Path.Combine(_directorio, $"{tipo}_{formato}_{Guid.NewGuid():N}");
            string opcionFormato = formato == "OpenSSH" ? "" : $"-m {formato}";
            string bits = tipo == "rsa" ? "-b 2048" : "";
            _ = BashLocal.Ejecutar($"ssh-keygen -q -t {tipo} {bits} {opcionFormato} -N '{frase}' -f '{ruta}' -C prueba");
            _ = File.Exists(ruta).Should().BeTrue($"ssh-keygen debía crear {ruta}");
            return ruta;
        }

        private string FicheroFrase(string contenido, UnixFileMode modo)
        {
            string ruta = Path.Combine(_directorio, $"{Guid.NewGuid():N}.frase");
            File.WriteAllText(ruta, contenido);
            File.SetUnixFileMode(ruta, modo);
            return ruta;
        }

        private FabricaConexionSsh Fabrica(string? frase = null, string? fichero = null) =>
            new(new OpcionesNativeSsh { Passphrase = frase, FicheroPassphrase = fichero }, new VerificadorHostConocido([Path.Combine(_directorio, "known_hosts")]), new LoggerDe<FabricaConexionSsh>(_log));

        private (IReadOnlyList<PrivateKeyFile> Claves, List<string> Motivos) Cargar(FabricaConexionSsh fabrica, params string[] rutas)
        {
            List<string> motivos = [];
            IReadOnlyList<PrivateKeyFile> claves = fabrica.CargarClaves(rutas, motivos);
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
        [InlineData("\n")]
        [InlineData("")]
        public void ClaveCifradaConLaPassphraseDeUnFichero0600_SeCargaSinElSaltoFinal(string final)
        {
            // Arrange
            string ruta = Clave("ed25519", "OpenSSH", Frase);
            using FabricaConexionSsh fabrica = Fabrica(fichero: FicheroFrase(Frase + final, UnixFileMode.UserRead | UnixFileMode.UserWrite));

            // Act
            (IReadOnlyList<PrivateKeyFile> claves, List<string> motivos) = Cargar(fabrica, ruta);

            // Assert
            _ = motivos.Should().BeEmpty();
            _ = claves.Should().ContainSingle();
        }

        // Los espacios de los extremos son parte de la passphrase (al contrario que en el token HTTP): solo se quita el salto
        // de línea final del fichero.
        [Fact]
        public void PassphraseConEspaciosEnLosExtremos_SeRespetan()
        {
            // Arrange
            string frase = $" {Frase} ";
            string ruta = Clave("ed25519", "OpenSSH", frase);
            using FabricaConexionSsh fabrica = Fabrica(fichero: FicheroFrase(frase + "\n", UnixFileMode.UserRead));

            // Act
            (IReadOnlyList<PrivateKeyFile> claves, List<string> motivos) = Cargar(fabrica, ruta);

            // Assert
            _ = motivos.Should().BeEmpty();
            _ = claves.Should().ContainSingle();
        }

        [Theory]
        [MemberData(nameof(Formatos))]
        public void ClaveCifradaSinPassphrase_ElMotivoLoDiceYDiceDeDondeSale(string tipo, string formato)
        {
            // Arrange
            string ruta = Clave(tipo, formato, Frase);
            using FabricaConexionSsh fabrica = Fabrica();

            // Act
            (IReadOnlyList<PrivateKeyFile> claves, List<string> motivos) = Cargar(fabrica, ruta);

            // Assert
            _ = claves.Should().BeEmpty();
            string motivo = motivos.Should().ContainSingle().Subject;
            _ = motivo.Should().Contain(ruta).And.Contain("cifrada con passphrase").And.Contain("no hay ninguna configurada")
                .And.Contain("NATIVE_SSH_PASSPHRASE_FICHERO").And.Contain("NATIVE_SSH_PASSPHRASE");
            _ = _log.Entradas.Should().Contain(e => e.Nivel == LogLevel.Warning && e.Mensaje.Contains(ruta, StringComparison.Ordinal) && e.Mensaje.Contains("cifrada con passphrase", StringComparison.Ordinal));
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

        [Fact]
        public void FicheroDeLaPassphraseLegibleParaOtros_SeRechazaConElMismoCriterioQueElToken()
        {
            // Arrange
            string ruta = Clave("ed25519", "OpenSSH", Frase);
            string fichero = FicheroFrase(Frase, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            using FabricaConexionSsh fabrica = Fabrica(fichero: fichero);

            // Act
            (IReadOnlyList<PrivateKeyFile> claves, List<string> motivos) = Cargar(fabrica, ruta);

            // Assert
            _ = claves.Should().BeEmpty();
            _ = motivos.Should().ContainSingle().Which.Should().Contain($"El fichero de la passphrase de las claves ({fichero}) tiene permisos 0644");
            SinRastroDeLaFrase(motivos);
        }

        [Fact]
        public void FicheroDeLaPassphraseQueNoExiste_LoDice()
        {
            // Arrange
            string ruta = Clave("ed25519", "OpenSSH", Frase);
            string fichero = Path.Combine(_directorio, "no-existe");
            using FabricaConexionSsh fabrica = Fabrica(fichero: fichero);

            // Act
            (_, List<string> motivos) = Cargar(fabrica, ruta);

            // Assert
            _ = motivos.Should().ContainSingle().Which.Should().Contain($"No existe el fichero de la passphrase de las claves ({fichero}, de NATIVE_SSH_PASSPHRASE_FICHERO)");
        }

        // Un fichero vacío (o solo con el salto final) no es una passphrase incorrecta: el motivo dice que está vacío.
        [Theory]
        [InlineData("")]
        [InlineData("\n")]
        public void FicheroDeLaPassphraseVacio_ElMotivoDiceQueEstaVacio(string contenido)
        {
            // Arrange
            string ruta = Clave("ed25519", "OpenSSH", Frase);
            string fichero = FicheroFrase(contenido, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using FabricaConexionSsh fabrica = Fabrica(fichero: fichero);

            // Act
            (IReadOnlyList<PrivateKeyFile> claves, List<string> motivos) = Cargar(fabrica, ruta);

            // Assert
            _ = claves.Should().BeEmpty();
            _ = motivos.Should().ContainSingle().Which.Should().Contain($"El fichero de la passphrase de las claves ({fichero}) está vacío").And.NotContain("incorrecta");
        }

        // Varias líneas: el motivo lo dice (no «incorrecta») y no muestra ninguna.
        [Theory]
        [InlineData("\n")]
        [InlineData("\r\n")]
        public void FicheroDeLaPassphraseConVariasLineas_ElMotivoLoDice(string salto)
        {
            // Arrange
            string ruta = Clave("ed25519", "OpenSSH", Frase);
            string fichero = FicheroFrase($"{Frase}{salto}{FraseIncorrecta}{salto}", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using FabricaConexionSsh fabrica = Fabrica(fichero: fichero);

            // Act
            (IReadOnlyList<PrivateKeyFile> claves, List<string> motivos) = Cargar(fabrica, ruta);

            // Assert
            _ = claves.Should().BeEmpty();
            _ = motivos.Should().ContainSingle().Which.Should().Contain($"El fichero de la passphrase de las claves ({fichero}) tiene varias líneas").And.NotContain("incorrecta");
            SinRastroDeLaFrase(motivos);
        }

        [Fact]
        public void VariableYFicheroALaVez_SeRechazan()
        {
            // Arrange
            string ruta = Clave("ed25519", "OpenSSH", Frase);
            using FabricaConexionSsh fabrica = Fabrica(frase: Frase, fichero: FicheroFrase(Frase, UnixFileMode.UserRead));

            // Act
            (IReadOnlyList<PrivateKeyFile> claves, List<string> motivos) = Cargar(fabrica, ruta);

            // Assert
            _ = claves.Should().BeEmpty();
            _ = motivos.Should().ContainSingle().Which.Should().Contain("define solo una de NATIVE_SSH_PASSPHRASE o NATIVE_SSH_PASSPHRASE_FICHERO");
        }

        // Una clave sin cifrar no necesita la passphrase: el fichero (aunque no valga) ni se lee.
        [Fact]
        public void ClaveSinCifrar_NoLeeLaPassphrase()
        {
            // Arrange
            string ruta = Clave("ed25519", "OpenSSH", "");
            using FabricaConexionSsh fabrica = Fabrica(fichero: Path.Combine(_directorio, "no-existe"));

            // Act
            (IReadOnlyList<PrivateKeyFile> claves, List<string> motivos) = Cargar(fabrica, ruta);

            // Assert
            _ = motivos.Should().BeEmpty();
            _ = claves.Should().ContainSingle();
        }

        // Una clave que no se pudo abrir no se queda fuera para siempre: al arreglar la passphrase, la siguiente conexión la
        // carga sin reiniciar native_ssh.
        [Fact]
        public void ClaveQueNoSePudoAbrir_SeReintentaEnLaSiguienteCarga()
        {
            // Arrange
            string ruta = Clave("ed25519", "OpenSSH", Frase);
            string fichero = FicheroFrase(FraseIncorrecta, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using FabricaConexionSsh fabrica = Fabrica(fichero: fichero);
            (IReadOnlyList<PrivateKeyFile> antes, _) = Cargar(fabrica, ruta);
            File.WriteAllText(fichero, Frase);

            // Act
            (IReadOnlyList<PrivateKeyFile> despues, List<string> motivos) = Cargar(fabrica, ruta);

            // Assert
            _ = antes.Should().BeEmpty();
            _ = motivos.Should().BeEmpty();
            _ = despues.Should().ContainSingle().Which.Should().NotBeNull();
        }

        // Sin ninguna clave utilizable, el error de la conexión lleva el motivo de cada clave (no la passphrase).
        [Fact]
        public async Task SinClavesUtilizables_ElErrorDeConexionLlevaElMotivoDeCadaClave()
        {
            // Arrange
            string cifrada = Clave("ed25519", "OpenSSH", Frase);
            string otra = Clave("rsa", "PEM", Frase);
            OpcionesNativeSsh opciones = new() { RutasIdentidad = [cifrada, otra], Passphrase = FraseIncorrecta };
            using FabricaConexionSsh fabrica = new(opciones, new VerificadorHostConocido([Path.Combine(_directorio, "known_hosts")]), new LoggerDe<FabricaConexionSsh>(_log));
            _ = DestinoSsh.TryParse("ana@127.0.0.1:1", "x", out DestinoSsh destino);

            // Act
            Func<Task> conectar = () => fabrica.ConectarAsync(destino, CancellationToken.None);

            // Assert
            string mensaje = (await conectar.Should().ThrowAsync<NativeSshException>()).Which.Message;
            _ = mensaje.Should().Contain("No hay credenciales para ana@127.0.0.1:1").And.Contain(cifrada).And.Contain(otra)
                .And.Contain("la passphrase de NATIVE_SSH_PASSPHRASE no la abre").And.NotContain("no están soportadas");
            SinRastroDeLaFrase([mensaje]);
        }
    }

    // ILogger<T> sobre un LoggerPrueba.
    internal sealed class LoggerDe<T>(LoggerPrueba interno) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => interno.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => interno.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            interno.Log(logLevel, eventId, state, exception, formatter);
    }
}
