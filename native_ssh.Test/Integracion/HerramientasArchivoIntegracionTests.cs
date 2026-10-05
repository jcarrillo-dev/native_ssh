using System.Diagnostics;
using FluentAssertions;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;

namespace NativeSsh.Test.Integracion
{
    // Herramientas de archivo contra el sshd local: lo que depende del shell real (enlaces, permisos, tiempos...).
    [Collection(ColeccionSshd.Nombre)]
    [Trait("Categoria", "Integracion")]
    public sealed class HerramientasArchivoIntegracionTests : IAsyncDisposable
    {
        private readonly SshdLocal _sshd;
        private readonly string _directorio = Path.Combine(Path.GetTempPath(), $"ns_archivos_{Guid.NewGuid():N}");
        private readonly GestorSesiones _gestor;

        public HerramientasArchivoIntegracionTests(SshdLocal sshd)
        {
            _sshd = sshd;
            _ = Directory.CreateDirectory(_directorio);
            OpcionesNativeSsh opciones = sshd.Opciones();
            _gestor = new GestorSesiones(
                sshd.Fabrica(opciones),
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
        }

        private string Destino => _sshd.Destino.Clave;

        public async ValueTask DisposeAsync()
        {
            await _gestor.DisposeAsync();
            if (Directory.Exists(_directorio))
                Directory.Delete(_directorio, recursive: true);
        }

        private HerramientasSsh Herramientas(OpcionesNativeSsh? opciones = null, TimeSpan? timeout = null) =>
            new(_gestor, opciones ?? _sshd.Opciones()) { TimeoutForzado = timeout };

        private string Ruta(string nombre) => Path.Combine(_directorio, nombre);

        private static string Bash(string comando)
        {
            ProcessStartInfo inicio = new("bash", ["-c", comando]) { RedirectStandardOutput = true };
            using Process proceso = Process.Start(inicio)!;
            string salida = proceso.StandardOutput.ReadToEnd();
            proceso.WaitForExit();
            return salida.Trim();
        }

        [Fact]
        public async Task LeerArchivo_BloqueadoEnUnaFifo_LanzaTiempoAgotadoYElShellSigueUsable()
        {
            // Arrange
            string fifo = Ruta("fifo");
            _ = Bash($"mkfifo '{fifo}'");
            string normal = Ruta("normal.txt");
            await File.WriteAllTextAsync(normal, "uno\n");
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(1));

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync(Destino, fifo))
                .Should().ThrowAsync<NativeSshException>()).Which;
            string siguiente = await herramientas.LeerArchivoAsync(Destino, normal);

            // Assert
            _ = error.Message.Should().Contain("TiempoAgotado");
            _ = siguiente.Should().Be("[Líneas 1-1 de 1]\nuno\n");
        }

        [Fact]
        public async Task ReemplazarContenido_ArchivoMayorQueElMaximoEditable_SeRechazaSinTocarlo()
        {
            // Arrange: una línea de más del límite (773 184 bytes con el máximo por defecto).
            string archivo = Ruta("grande.txt");
            string contenido = "objetivo\n" + new string('x', 800_000) + "\n";
            await File.WriteAllTextAsync(archivo, contenido);
            DateTime modificado = File.GetLastWriteTimeUtc(archivo);
            HerramientasSsh herramientas = Herramientas();

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "objetivo", "cambiado", 1, 1))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("supera el máximo editable");
            _ = error.Message.Should().Contain("773184");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be(contenido);
            _ = File.GetLastWriteTimeUtc(archivo).Should().Be(modificado);
            _ = Directory.GetFiles(_directorio).Should().ContainSingle();
        }

        [Fact]
        public async Task LeerArchivo_RangoCuyaSalidaSuperaElMaximo_LanzaErrorSinDevolverLaCola()
        {
            // Arrange: 2 000 líneas de 1 000 bytes = 2 MB > 1 MiB por petición.
            string archivo = Ruta("largo.txt");
            await File.WriteAllLinesAsync(archivo, Enumerable.Range(1, 2000).Select(i => $"{i:D6}" + new string('y', 993)));
            HerramientasSsh herramientas = Herramientas();

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync(Destino, archivo, 1, 2000))
                .Should().ThrowAsync<NativeSshException>()).Which;
            string parcial = await herramientas.LeerArchivoAsync(Destino, archivo, 1999, 2000);

            // Assert
            _ = error.Message.Should().Contain("superó el máximo");
            _ = parcial.Should().StartWith("[Líneas 1999-2000 de 2000]\n001999");
        }

        [Fact]
        public async Task BuscarFindYListar_ResultadosAcotadosEnElServidorConAvisoDeLimite()
        {
            // Arrange: 300 archivos con una coincidencia cada uno y 300 subdirectorios.
            for (int i = 0; i < 300; i++)
            {
                await File.WriteAllTextAsync(Ruta($"a{i:D3}.txt"), "coincidencia_acotada\n");
                _ = Directory.CreateDirectory(Ruta($"d{i:D3}"));
            }

            HerramientasSsh herramientas = Herramientas();

            // Act
            string busqueda = await herramientas.BuscarAsync(Destino, "coincidencia_acotada", _directorio, maxResultados: 5);
            long bytesBusqueda = UltimaPeticion("grep -rnI").Salida.Longitud;
            string nombres = await herramientas.BuscarPorNombreAsync(Destino, "a*.txt", _directorio, maxResultados: 5);
            long bytesNombres = UltimaPeticion("-name 'a*.txt'").Salida.Longitud;
            string listado = await herramientas.ListarDirectoriosAsync(Destino, _directorio, profundidadMax: 1, maxResultados: 5);
            long bytesListado = UltimaPeticion("-type d -print").Salida.Longitud;

            // Assert: llegan como mucho limite + 1 líneas y se sigue avisando del límite.
            _ = busqueda.Should().Contain("[Límite alcanzado: se muestran 5 coincidencias");
            _ = busqueda.Split('\n').Count(l => l.Contains("coincidencia_acotada", StringComparison.Ordinal) && !l.StartsWith('[')).Should().Be(5);
            _ = bytesBusqueda.Should().BeLessThan(6 * 200);
            _ = nombres.Should().StartWith("a000.txt\na001.txt\na002.txt\na003.txt\na004.txt\n\n[Límite alcanzado: se muestran 5 resultados");
            _ = bytesNombres.Should().BeLessThan(6 * 20);
            _ = listado.Should().StartWith("d000\nd001\nd002\nd003\nd004\n\n[Límite alcanzado: se muestran 5 directorios");
            _ = bytesListado.Should().BeLessThan(6 * 20);
        }

        [Fact]
        public async Task LeerArchivo_InicioNegativo_DevuelveLasUltimasLineasConLaMismaCabecera()
        {
            // Arrange
            string archivo = Ruta("diez.txt");
            await File.WriteAllLinesAsync(archivo, Enumerable.Range(1, 10).Select(i => $"l{i}"));
            string vacio = Ruta("vacio.txt");
            await File.WriteAllTextAsync(vacio, string.Empty);
            string sinSalto = Ruta("sin_salto.txt");
            await File.WriteAllTextAsync(sinSalto, "a\nb\nc");
            HerramientasSsh herramientas = Herramientas();

            // Act
            string ultimas = await herramientas.LeerArchivoAsync(Destino, archivo, -3);
            string todas = await herramientas.LeerArchivoAsync(Destino, archivo, -20);
            string deVacio = await herramientas.LeerArchivoAsync(Destino, vacio, -5);
            string finSinSalto = await herramientas.LeerArchivoAsync(Destino, sinSalto, -2);
            string completoSinSalto = await herramientas.LeerArchivoAsync(Destino, sinSalto);

            // Assert
            _ = ultimas.Should().Be("[Líneas 8-10 de 10]\nl8\nl9\nl10\n");
            _ = todas.Should().Be("[Líneas 1-10 de 10]\n" + string.Concat(Enumerable.Range(1, 10).Select(i => $"l{i}\n")));
            _ = deVacio.Should().Be("[Líneas 0-0 de 0]\n");
            _ = finSinSalto.Should().Be("[Líneas 2-3 de 3]\nb\nc");
            _ = completoSinSalto.Should().Be("[Líneas 1-3 de 3]\na\nb\nc");
        }

        private Peticion UltimaPeticion(string fragmentoComando) =>
            _gestor.ListarPeticiones().First(p => p.Comando.Contains(fragmentoComando, StringComparison.Ordinal));
    }
}
