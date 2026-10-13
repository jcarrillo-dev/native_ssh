using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // sshd cuyas sesiones (el shell y también los canales exec) tienen el servidor en español (SetEnv): los mensajes de
    // stat y de las demás órdenes salen traducidos y 'sort' ordena con las reglas del español. Puerto: el de
    // NATIVE_SSH_PUERTO_PRUEBAS (22022 por defecto) más 400.
    public sealed class SshdLocalIdioma : IDisposable
    {
        public SshdLocal Sshd { get; } = new(Puerto(), ".estado-idioma", null, "SetEnv LANG=es_ES.UTF-8 LANGUAGE=es_ES:es");

        public void Dispose() => Sshd.Dispose();

        private static string Puerto() =>
            (int.Parse(Environment.GetEnvironmentVariable("NATIVE_SSH_PUERTO_PRUEBAS") ?? "22022", CultureInfo.InvariantCulture) + 400).ToString(CultureInfo.InvariantCulture);
    }

    [CollectionDefinition(Nombre)]
    public sealed class ColeccionSshdIdioma : ICollectionFixture<SshdLocalIdioma>
    {
        public const string Nombre = "sshd local en español";
    }

    // Las herramientas no dependen del idioma del servidor: distinguen «sin permiso» de «no existe» sin leer mensajes
    // traducidos, el tipo de ssh_stat es un valor fijo y la escritura atómica sigue comprobando los atributos.
    [Collection(ColeccionSshdIdioma.Nombre)]
    [Trait("Categoria", "Integracion")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El gestor se libera en IAsyncLifetime.DisposeAsync.")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class HerramientasIdiomaIntegracionTests : IAsyncLifetime
    {
        private readonly SshdLocal _sshd;
        private readonly string _directorio = Path.Combine(Path.GetTempPath(), $"ns_idioma_{Guid.NewGuid():N}");
        private readonly GestorSesiones _gestor;

        public HerramientasIdiomaIntegracionTests(SshdLocalIdioma idioma)
        {
            _sshd = idioma.Sshd;
            _ = Directory.CreateDirectory(_directorio);
            OpcionesNativeSsh opciones = _sshd.Opciones();
            _gestor = new GestorSesiones(_sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
        }

        private string Destino => _sshd.Destino.Clave;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            await _gestor.DisposeAsync();
            _ = BashLocal.Ejecutar($"chmod -R u+rwx '{_directorio}' 2>/dev/null");
            Directory.Delete(_directorio, recursive: true);
        }

        private HerramientasSsh Herramientas() => new(_gestor, _sshd.Opciones());

        private string Ruta(string nombre) => Path.Combine(_directorio, nombre);

        // Sin esta comprobación los demás tests pasarían igual con un servidor en inglés: los canales exec ordenan como en
        // español ("a" antes que "B"; en el locale C, al revés) y el shell da los mensajes de stat traducidos.
        [Fact]
        public async Task Servidor_HablaEspanolEnLosCanalesExecYEnElShell()
        {
            // Arrange
            await File.WriteAllTextAsync(Ruta("B"), "x\n");
            await File.WriteAllTextAsync(Ruta("a"), "x\n");
            HerramientasSsh herramientas = Herramientas();

            // Act
            string listado = await herramientas.ListarDirectoriosAsync(Destino, _directorio, incluirArchivos: true);
            ResultadoComando stat = await herramientas.EjecutarAsync(Destino, $"stat -- '{Ruta("no_existe")}' 2>&1", esperarSegundos: 10).ComoComando();

            // Assert
            _ = listado.Should().Be("a\nB");
            _ = stat.Salida.Should().Contain("No existe el fichero o el directorio");
        }

        [Fact]
        public async Task HerramientasDeLectura_ConElServidorEnEspanol_DistinguenSinPermisoDeNoExiste()
        {
            // Arrange
            string cerrado = Ruta("cerrado");
            string directorio = Path.Combine(cerrado, "sub");
            string archivo = Path.Combine(directorio, "archivo.txt");
            _ = Directory.CreateDirectory(directorio);
            await File.WriteAllTextAsync(archivo, "x\n");
            _ = BashLocal.Ejecutar($"chmod 000 '{cerrado}'");
            string inexistente = Ruta("Permission denied");
            HerramientasSsh herramientas = Herramientas();

            // Act
            Dictionary<string, string> sinPermiso = await HerramientasArchivoIntegracionTests.ErroresDeLecturaAsync(herramientas, Destino, archivo, directorio);
            Dictionary<string, string> noExiste = await HerramientasArchivoIntegracionTests.ErroresDeLecturaAsync(herramientas, Destino, Path.Combine(inexistente, "archivo.txt"), inexistente);

            // Assert: ssh_stat de una ruta que no existe no es un error, sino existe=false.
            _ = sinPermiso.Should().HaveCount(8).And.AllSatisfy(error => error.Value.Should().Contain("sin permiso para acceder", error.Key).And.NotContain("no existe", error.Key));
            _ = noExiste.Remove("ssh_stat");
            _ = noExiste.Should().HaveCount(7).And.AllSatisfy(error => error.Value.Should().Contain("no existe", error.Key).And.NotContain("sin permiso", error.Key));
        }

        [Fact]
        public async Task ConsultarMetadatos_ConElServidorEnEspanol_DevuelveElTipoFijo()
        {
            // Arrange
            string archivo = Ruta("archivo.txt");
            await File.WriteAllTextAsync(archivo, "x\n");

            // Act
            ResultadoMetadatos metadatos = await Herramientas().ConsultarMetadatosAsync(Destino, archivo);

            // Assert
            _ = metadatos.Tipo.Should().Be("archivo regular");
            _ = metadatos.TamanoBytes.Should().Be(2);
        }

        // La comprobación de atributos (getfattr, getfacl, ls) no depende del idioma: la escritura sigue siendo atómica.
        [Fact]
        public async Task Sobrescribir_ConElServidorEnEspanol_ConservaAclYAtributosYEsAtomica()
        {
            // Arrange
            string archivo = Ruta("con_atributos.txt");
            await File.WriteAllTextAsync(archivo, "antes\n");
            _ = BashLocal.Ejecutar($"chmod 640 '{archivo}' && setfacl -m u:nobody:r '{archivo}' && setfattr -n user.marca -v valor '{archivo}'");
            string atributos = BashLocal.Ejecutar($"getfacl -c -- '{archivo}'; getfattr -d -- '{archivo}' | grep -v '^# file:'");

            // Act
            ResultadoEscritura resultado = await Herramientas().EscribirArchivoAsync(Destino, archivo, "despues\n", overwrite: true);

            // Assert
            _ = atributos.Should().Contain("user:nobody:r--").And.Contain("user.marca=\"valor\"");
            _ = resultado.IsAtomica.Should().BeTrue();
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("despues\n");
            _ = BashLocal.Ejecutar($"getfacl -c -- '{archivo}'; getfattr -d -- '{archivo}' | grep -v '^# file:'").Should().Be(atributos);
        }
    }
}
