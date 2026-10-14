using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;
using Xunit.Abstractions;

namespace NativeSsh.Test.Integracion
{
    // Estrés de los canales exec: cientos de desbordamientos del tope con carga de CPU, un ssh_exec largo en curso y
    // lecturas concurrentes. Fuera de la suite por defecto (FactEstres).
    [Collection(ColeccionSshd.Nombre)]
    [Trait("Categoria", "Estres")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El gestor se libera en IAsyncLifetime.DisposeAsync.")]
    public sealed class EstresCanalesExecTests : IAsyncLifetime
    {
        private const int Tope = 64 * 1024;
        private const int Rondas = 150;
        private const string LecturaPequena = "[Líneas 1-1 de 1]\nuno\n";

        private readonly SshdLocal _sshd;
        private readonly ITestOutputHelper _salida;
        private readonly string _directorio = Path.Combine(Path.GetTempPath(), $"ns_estres_{Guid.NewGuid():N}");
        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;

        public EstresCanalesExecTests(SshdLocal sshd, ITestOutputHelper salida)
        {
            _sshd = sshd;
            _salida = salida;
            _ = Directory.CreateDirectory(_directorio);
            // Sin freno del limitador: una desconexión se vería como una conexión nueva, no como un error de cupo.
            OpcionesNativeSsh opciones = sshd.Opciones(maxConexiones: 1000) with { MaxBytesSalidaPorPeticion = Tope };
            _gestor = new GestorSesiones(
                sshd.Fabrica(opciones),
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
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

        // Bucles ocupados acotados con 'timeout', hijos del bash de la carga: se paran por su padre, sin buscarlos por nombre.
        private static Process IniciarCarga(string etiqueta, int procesos, int segundos) =>
            Process.Start(new ProcessStartInfo("bash", ["-c", $"for i in $(seq 1 {procesos}); do timeout {segundos} sh -c 'while :; do :; done' {etiqueta} & done; wait"]))!;

        private static void PararCarga(Process carga)
        {
            // Cada 'timeout' reenvía la TERM a su bucle y sale; después 'wait' vuelve y sale el bash de la carga.
            using Process pkill = Process.Start(new ProcessStartInfo("pkill", ["-P", carga.Id.ToString(CultureInfo.InvariantCulture)]))!;
            pkill.WaitForExit();
            _ = carga.WaitForExit(TimeSpan.FromSeconds(10));
            carga.Dispose();
        }

        // Una operación que supera el tope por cada variante (stdout, stdout+stderr, sin fin y la lectura de un archivo
        // grande por la herramienta). Devuelve null si acabó como se esperaba.
        private async Task<string?> DesbordarAsync(int variante, string grande)
        {
            try
            {
                if (variante == 3)
                {
                    string texto = await _herramientas.LeerArchivoAsync(Destino, grande);
                    return $"la lectura de {grande} no falló: {texto.Length} caracteres";
                }

                string script = variante switch
                {
                    0 => $"head -c {Tope + 1} /dev/zero",
                    1 => $"head -c {Tope / 2} /dev/zero; head -c {(Tope / 2) + 1} /dev/zero >&2",
                    _ => "yes"
                };
                ResultadoCanalExec resultado = await _gestor.EjecutarExecAsync(Destino, script, TimeSpan.FromSeconds(30), CancellationToken.None);
                return resultado.HasExcedidoTope ? null : $"'{script}' no superó el tope (código {resultado.CodigoSalida})";
            }
            catch (NativeSshException ex) when (variante == 3 && ex.Message.StartsWith("La salida superó", StringComparison.Ordinal))
            {
                return null;
            }
            catch (Exception ex)
            {
                return $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        private async Task<string?> LeerPequenoAsync(string pequeno)
        {
            try
            {
                string texto = await _herramientas.LeerArchivoAsync(Destino, pequeno);
                return texto == LecturaPequena ? null : $"lectura inesperada: {texto}";
            }
            catch (Exception ex)
            {
                return $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        [FactEstres]
        public async Task Desbordamientos_ConCargaComandoLargoYLecturas_NoCortanLaConexionNiEnvianSenales()
        {
            // Arrange
            string grande = Path.Combine(_directorio, "grande.txt");
            string pequeno = Path.Combine(_directorio, "pequeno.txt");
            await File.WriteAllLinesAsync(grande, Enumerable.Range(0, 20).Select(i => new string((char)('a' + i), 8 * 1024)));
            await File.WriteAllTextAsync(pequeno, "uno\n");
            _ = await _herramientas.EjecutarAsync(Destino, "export NS_VIVA=si").ComoComando();
            ResultadoComando largo = await _herramientas.EjecutarAsync(Destino, "sleep 600; echo fin", esperarSegundos: 0).ComoComando();
            int aceptadas = _sshd.ConexionesAceptadas();
            int senales = _sshd.ContarLineasLog(SshdLocal.LineasSenalODesconexion);
            string etiqueta = $"ns_carga_{Guid.NewGuid():N}";
            Process carga = IniciarCarga(etiqueta, Environment.ProcessorCount * 2, 180);
            List<string> fallos = [];
            int desbordamientos = 0;
            int lecturas = 0;
            Stopwatch reloj = Stopwatch.StartNew();

            // Act
            try
            {
                for (int ronda = 0; ronda < Rondas; ronda++)
                {
                    Task<string?>[] operaciones = [.. Enumerable.Range(0, 4).Select(k => DesbordarAsync((ronda + k) % 4, grande)), .. Enumerable.Range(0, 2).Select(_ => LeerPequenoAsync(pequeno))];
                    string?[] resultados = await Task.WhenAll(operaciones);
                    desbordamientos += 4;
                    lecturas += 2;
                    fallos.AddRange(resultados.OfType<string>());
                }
            }
            finally
            {
                PararCarga(carga);
            }

            reloj.Stop();
            ResultadoComando enCurso = await _herramientas.LeerSalidaAsync(largo.Id).ComoComando();
            ResultadoComando viva = await _herramientas.EjecutarAsync(Destino, "echo \"$NS_VIVA\"", esperarSegundos: 0).ComoComando();
            ResultadoComando cancelado = await _herramientas.CancelarAsync(largo.Id).ComoComando();
            viva = await _herramientas.LeerSalidaAsync(viva.Id, esperarSegundos: 20).ComoComando();
            int nuevas = _sshd.ConexionesAceptadas() - aceptadas;
            int lineas = _sshd.ContarLineasLog(SshdLocal.LineasSenalODesconexion) - senales;
            _salida.WriteLine($"{desbordamientos} desbordamientos y {lecturas} lecturas en {reloj.Elapsed.TotalSeconds:0.0} s con {Environment.ProcessorCount * 2} bucles de carga en {Environment.ProcessorCount} CPU: {fallos.Count} fallos, {lineas} líneas de señal o desconexión en el log de sshd, {nuevas} conexiones TCP nuevas; comando largo {enCurso.Estado}; variable '{viva.Salida.Trim()}'.");
            foreach (IGrouping<string, string> grupo in fallos.GroupBy(f => f))
                _salida.WriteLine($"{grupo.Count()} x {grupo.Key}");

            // Assert
            _ = fallos.Should().BeEmpty();
            _ = lineas.Should().Be(0);
            _ = nuevas.Should().Be(0);
            _ = enCurso.Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
            _ = cancelado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = viva.Salida.Should().Be("si\n");
            _ = viva.Aviso.Should().BeNull();
        }
    }
}
