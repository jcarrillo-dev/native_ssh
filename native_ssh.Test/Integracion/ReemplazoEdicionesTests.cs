using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;

namespace NativeSsh.Test.Integracion
{
    // Varias ediciones en una llamada a ssh_replace_file_content (CI5): en orden, cada una sobre el resultado de la anterior,
    // todas o ninguna (si una falla no se escribe nada) y con una sola escritura con las garantías de siempre.
    [Collection(ColeccionSshd.Nombre)]
    [Trait("Categoria", "Integracion")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El gestor se libera en IAsyncLifetime.DisposeAsync.")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class ReemplazoEdicionesTests : IAsyncLifetime
    {
        private readonly SshdLocal _sshd;
        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;
        private readonly string _directorio = Path.Combine(Path.GetTempPath(), $"ns_ci5_{Guid.NewGuid():N}");

        public ReemplazoEdicionesTests(SshdLocal sshd)
        {
            _sshd = sshd;
            _ = Directory.CreateDirectory(_directorio);
            OpcionesNativeSsh opciones = sshd.Opciones();
            _gestor = new GestorSesiones(sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            _herramientas = new HerramientasSsh(_gestor, opciones);
        }

        private string Destino => _sshd.Destino.Clave;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            await _gestor.DisposeAsync();
            if (Directory.Exists(_directorio))
                Directory.Delete(_directorio, recursive: true);
        }

        private string Crear(string nombre, string contenido)
        {
            string ruta = Path.Combine(_directorio, nombre);
            File.WriteAllText(ruta, contenido);
            return ruta;
        }

        [Fact]
        public async Task Ediciones_VariasEnOrden_LasAplicaTodasEnUnaSolaEscritura()
        {
            // Arrange
            string archivo = Crear("config.ini", "[a]\nclave=uno\n[b]\nclave=dos\nfin=a\n");

            // Act
            ResultadoEscritura resultado = await _herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, ediciones:
            [
                new EdicionTexto("[a]\nclave=uno", "[a]\nclave=UNO"),
                new EdicionTexto("clave=dos", "clave=DOS"),
                new EdicionTexto("fin=a", "fin=b"),
            ]);

            // Assert
            _ = File.ReadAllText(archivo).Should().Be("[a]\nclave=UNO\n[b]\nclave=DOS\nfin=b\n");
            _ = resultado.Reemplazos.Should().Be(3);
            _ = resultado.IsAtomica.Should().BeTrue();
            _ = resultado.Mensaje.Should().StartWith($"Archivo modificado quirúrgicamente con éxito: {archivo} (3 ediciones, 3 reemplazo(s) realizado(s)).");
        }

        // Cada edición se busca en el resultado de las anteriores.
        [Fact]
        public async Task Ediciones_LaSegundaSobreElResultadoDeLaPrimera()
        {
            // Arrange
            string archivo = Crear("cadena.txt", "valor=a\n");

            // Act
            _ = await _herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, ediciones: [new EdicionTexto("=a", "=b"), new EdicionTexto("=b", "=c")]);

            // Assert
            _ = File.ReadAllText(archivo).Should().Be("valor=c\n");
        }

        // Todas o ninguna: si una no se encuentra, no se escribe nada (ni las anteriores) y el error dice cuál.
        [Fact]
        public async Task Ediciones_UnaNoSeEncuentra_NoEscribeNadaYDiceCual()
        {
            // Arrange
            const string Original = "uno\ndos\ntres\n";
            string archivo = Crear("intacto.txt", Original);
            DateTime antes = File.GetLastWriteTimeUtc(archivo);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, ediciones:
            [
                new EdicionTexto("uno", "UNO"),
                new EdicionTexto("cuatro", "CUATRO"),
                new EdicionTexto("tres", "TRES"),
            ])).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be($"Edición 2 de 3: no se encontró su 'targetContent' en '{archivo}' (se busca en todo el archivo, tras aplicar las ediciones anteriores). Revisa la indentación y los saltos de línea exactos. No se ha modificado el archivo: con 'ediciones' se aplican todas o ninguna.");
            _ = File.ReadAllText(archivo).Should().Be(Original);
            _ = File.GetLastWriteTimeUtc(archivo).Should().Be(antes);
        }

        // Ambigua sin allowMultiple: error; con allowMultiple en esa edición, las reemplaza todas.
        [Fact]
        public async Task Ediciones_AmbiguaSinAllowMultiple_FallaYConElLasReemplazaTodas()
        {
            // Arrange
            string archivo = Crear("repetido.txt", "x=1\nx=1\ny=2\n");

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, ediciones:
                [new EdicionTexto("y=2", "y=3"), new EdicionTexto("x=1", "x=9")])).Should().ThrowAsync<NativeSshException>()).Which;
            string trasError = File.ReadAllText(archivo);
            ResultadoEscritura resultado = await _herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, ediciones:
                [new EdicionTexto("y=2", "y=3"), new EdicionTexto("x=1", "x=9", AllowMultiple: true)]);

            // Assert
            _ = error.Message.Should().Be("Edición 2 de 2: se encontraron 2 apariciones de su 'targetContent' en todo el archivo. Pon allowMultiple=true en esa edición o amplía su targetContent con contexto. No se ha modificado el archivo: con 'ediciones' se aplican todas o ninguna.");
            _ = trasError.Should().Be("x=1\nx=1\ny=2\n");
            _ = File.ReadAllText(archivo).Should().Be("x=9\nx=9\ny=3\n");
            _ = resultado.Reemplazos.Should().Be(3);
        }

        // Los saltos de línea de cada edición se adaptan a los del archivo (CRLF), como con targetContent.
        [Fact]
        public async Task Ediciones_ArchivoCrlf_AdaptaLosSaltos()
        {
            // Arrange
            string archivo = Crear("windows.txt", "uno\r\ndos\r\ntres\r\n");

            // Act
            _ = await _herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, ediciones: [new EdicionTexto("uno\ndos", "UNO\nDOS"), new EdicionTexto("tres\n", "TRES\nCUATRO\n")]);

            // Assert
            _ = File.ReadAllText(archivo).Should().Be("UNO\r\nDOS\r\nTRES\r\nCUATRO\r\n");
        }
    }
}
