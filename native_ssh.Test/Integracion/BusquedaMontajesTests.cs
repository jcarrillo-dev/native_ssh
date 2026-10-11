using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // ssh_find desde '/' (C13): con 'find -L', /proc/<pid>/cwd (y /proc/<pid>/task/*/cwd) llevan otra vez a la raíz y el
    // árbol se recorría una vez por proceso: resultados duplicados y el plazo agotado en un servidor real. Los sistemas de
    // ficheros virtuales del kernel que hay por debajo de la ruta se podan siempre (salvo si la ruta está dentro de uno) y
    // los montajes que no se recorren salen en la línea final '[montajes_no_explorados: …]'.
    [Collection(ColeccionSshd.Nombre)]
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El gestor se libera en IAsyncLifetime.DisposeAsync.")]
    public sealed class BusquedaMontajesTests : IAsyncLifetime
    {
        private readonly SshdLocal _sshd;
        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;
        private readonly string _directorio = Path.Combine(Path.GetTempPath(), $"ns_c13_{Guid.NewGuid():N}");
        private readonly string _objetivo = $"objetivo_{Guid.NewGuid():N}";
        private readonly Process _vecino;

        public BusquedaMontajesTests(SshdLocal sshd)
        {
            _sshd = sshd;
            _ = Directory.CreateDirectory(Path.Combine(_directorio, _objetivo));
            // Un proceso con su directorio de trabajo junto al objetivo: /proc/<pid>/cwd lleva a él sin ser un antecesor
            // de la ruta (find -L sí detecta y salta los bucles hacia un antecesor, como /proc/self/cwd → '/').
            _vecino = Process.Start(new ProcessStartInfo("sleep", ["300"]) { WorkingDirectory = _directorio })!;
            OpcionesNativeSsh opciones = sshd.Opciones();
            _gestor = new GestorSesiones(sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            _herramientas = new HerramientasSsh(_gestor, opciones);
        }

        private string Destino => _sshd.Destino.Clave;

        // Ruta del objetivo desde '/', sin la barra inicial (como la devuelve ssh_find), y su profundidad.
        private string RutaObjetivo => Path.Combine(_directorio, _objetivo)[1..];

        private int Profundidad => RutaObjetivo.Split('/').Length;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            await _gestor.DisposeAsync();
            _vecino.Kill();
            await _vecino.WaitForExitAsync();
            _vecino.Dispose();
            if (Directory.Exists(_directorio))
                Directory.Delete(_directorio, recursive: true);
        }

        // Antes salía también como proc/<pid>/cwd/<objetivo> (y por cada hilo, proc/<pid>/task/<tid>/cwd/<objetivo>).
        [Fact]
        public async Task Find_DesdeLaRaiz_DevuelveElObjetivoUnaSolaVezYNoEntraEnProc()
        {
            // Act
            string salida = await _herramientas.BuscarPorNombreAsync(Destino, _objetivo, "/", "dir", profundidadMax: Profundidad + 3);

            // Assert
            string[] lineas = salida.Split('\n');
            _ = lineas[0].Should().Be(RutaObjetivo);
            _ = lineas.Should().NotContain(l => l.StartsWith("proc/", StringComparison.Ordinal));
            _ = lineas.Should().ContainSingle(l => l.EndsWith(_objetivo, StringComparison.Ordinal));
            _ = lineas[^1].Should().StartWith("[montajes_no_explorados: ").And.Contain("/proc").And.Contain("/sys").And.EndWith("]");
        }

        // Con cruzarMontajes=false no sale del sistema de ficheros de la ruta: el objetivo está en otro montaje (el del
        // directorio temporal), que sale en la línea final. Solo si de verdad es otro sistema de ficheros (medido con stat).
        [Fact]
        public async Task Find_SinCruzarMontajes_NoEntraEnOtroSistemaDeFicherosYLoNombra()
        {
            // Arrange
            string[] medida = BashLocal.Ejecutar($"stat -c '%d' / && stat -c '%d %m' '{_directorio}'").Split('\n');
            string montaje = medida[1][(medida[1].IndexOf(' ', StringComparison.Ordinal) + 1)..];
            bool isOtroSistema = medida[0] != medida[1].Split(' ')[0] && montaje != "/";

            // Act
            string sinCruzar = await _herramientas.BuscarPorNombreAsync(Destino, _objetivo, "/", "dir", profundidadMax: Profundidad + 3, cruzarMontajes: false);
            string enSuMontaje = await _herramientas.BuscarPorNombreAsync(Destino, _objetivo, montaje, "dir", profundidadMax: Profundidad + 3, cruzarMontajes: false);

            // Assert
            _ = isOtroSistema.Should().BeTrue($"el directorio temporal debe estar en otro sistema de ficheros que '/' para esta prueba (medido: {string.Join(" | ", medida)})");
            _ = sinCruzar.Should().StartWith($"No se encontraron elementos que coincidan con '{_objetivo}' en '/'.\n\n[montajes_no_explorados: ");
            _ = sinCruzar.Split('\n')[^1].Split(", ").Should().Contain(montaje);
            _ = enSuMontaje.Split('\n')[0].Should().Be(Path.GetRelativePath(montaje, Path.Combine(_directorio, _objetivo)));
        }

        // Si la ruta pedida está dentro de un sistema de ficheros virtual, se busca en él (lo pidió explícitamente).
        [Fact]
        public async Task Find_RutaDentroDeProc_BuscaEnEl()
        {
            // Act
            string salida = await _herramientas.BuscarPorNombreAsync(Destino, "status", "/proc/self", "file", profundidadMax: 1);

            // Assert
            _ = salida.Should().Be("status");
        }
    }
}
