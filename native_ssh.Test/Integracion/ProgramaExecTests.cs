using System.Diagnostics;
using System.Runtime.Versioning;
using FluentAssertions;
using NativeSsh.Protocolo;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // El programa de los canales exec ejecutado con bash local, como lo haría el shell de login del servidor.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    public sealed class ProgramaExecTests : IDisposable
    {
        // Tras salir bash, lo que tarde en cerrarse su salida: solo un proceso que sobreviva al script (p. ej. un watchdog
        // que no muere) la mantiene abierta.
        private static readonly TimeSpan CierreSalida = TimeSpan.FromSeconds(5);

        private readonly string _raiz = Path.Combine(Path.GetTempPath(), $"ns_programa_{Guid.NewGuid():N}");
        private readonly string _sinTimeout;

        public ProgramaExecTests() => _sinTimeout = GranjaBinarios.Crear(Path.Combine(_raiz, "sin-timeout"), "timeout");

        public void Dispose()
        {
            if (Directory.Exists(_raiz))
                Directory.Delete(_raiz, recursive: true);
        }

        private static (int Codigo, string Salida, string Errores, TimeSpan Duracion) Ejecutar(string comando, string path, TimeSpan limite)
        {
            ProcessStartInfo inicio = new("/usr/bin/bash", ["-c", comando]) { RedirectStandardOutput = true, RedirectStandardError = true };
            inicio.Environment["PATH"] = path;
            Stopwatch reloj = Stopwatch.StartNew();
            using Process proceso = Process.Start(inicio)!;
            Task<string> salida = proceso.StandardOutput.ReadToEndAsync();
            Task<string> errores = proceso.StandardError.ReadToEndAsync();
            bool isTerminado = proceso.WaitForExit(limite);
            if (!isTerminado)
            {
                proceso.Kill(entireProcessTree: true);
                proceso.WaitForExit();
            }

            // Leer hasta el final sin límite colgaría la suite si sobrevive un proceso con la salida abierta.
            return Task.WaitAll([salida, errores], CierreSalida)
                ? (isTerminado ? proceso.ExitCode : -1, salida.Result, errores.Result, reloj.Elapsed)
                : throw new TimeoutException($"La salida del programa no se cerró {CierreSalida.TotalSeconds:0} s después de salir bash: sigue vivo un proceso que la mantiene abierta (¿el watchdog?).");
        }

        [Fact]
        public void SinTimeout_ConservaSalidaErroresYCodigoSinRuidoDelControlDeTrabajos()
        {
            // Act
            (int codigo, string salida, string errores, _) = Ejecutar(ConstructorComandoExec.Construir("echo fuera; echo error >&2; exit 7", null, TimeSpan.FromSeconds(30)), _sinTimeout, TimeSpan.FromSeconds(20));

            // Assert
            _ = codigo.Should().Be(7);
            _ = salida.Should().Be("fuera\n");
            _ = errores.Should().Be("error\n");
        }

        [Fact]
        public async Task SinTimeout_ElWatchdogMataElGrupoDelScriptAunqueIgnoreTermYDevuelve124()
        {
            // Arrange
            long hijo = 600_000 + Random.Shared.Next(100_000);
            long principal = hijo + 100_000;
            string comando = ConstructorComandoExec.Construir($"sleep {hijo} & trap '' TERM; sleep {principal}; wait", null, TimeSpan.FromSeconds(1));

            try
            {
                // Act
                (int codigo, _, _, TimeSpan duracion) = Ejecutar(comando, _sinTimeout, TimeSpan.FromSeconds(20));
                string vivos = await CanalesExecIntegracionTests.EsperarSinProcesosSleepAsync(hijo, principal);

                // Assert: TERM al grupo al vencer el plazo y KILL 2 s después.
                _ = codigo.Should().Be(124);
                _ = duracion.Should().BeLessThan(TimeSpan.FromSeconds(10));
                _ = vivos.Should().BeEmpty();
            }
            finally
            {
                CanalesExecIntegracionTests.MatarProcesosSleep(hijo, principal);
            }
        }

        [Fact]
        public void ConPathDeLogin_ElScriptYLaBusquedaDeTimeoutLoUsan()
        {
            // Arrange: el PATH del canal (el del proceso) tiene 'timeout'; el del login, un programa propio y no 'timeout'.
            string login = Directory.CreateDirectory(Path.Combine(_raiz, "login")).FullName;
            string sonda = Path.Combine(login, "ns_sonda_path");
            File.WriteAllText(sonda, "#!/usr/bin/bash\necho sonda\n");
            File.SetUnixFileMode(sonda, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string comando = ConstructorComandoExec.Construir("ns_sonda_path; command -v timeout || echo sin-timeout; echo \"$PATH\"", null, TimeSpan.FromSeconds(30), $"{login}:{_sinTimeout}");

            // Act
            (int codigo, string salida, _, _) = Ejecutar(comando, Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin", TimeSpan.FromSeconds(20));

            // Assert
            _ = codigo.Should().Be(0);
            _ = salida.Should().Be($"sonda\nsin-timeout\n{login}:{_sinTimeout}\n");
        }

        [Fact]
        public async Task SinTimeout_UnScriptRapidoNoDejaElWatchdogVivo()
        {
            // Arrange: el plazo (único) es lo que duerme el watchdog.
            int plazo = 5_000 + Random.Shared.Next(1_000);

            try
            {
                // Act
                (int codigo, string salida, _, TimeSpan duracion) = Ejecutar(ConstructorComandoExec.Construir("echo rapido", null, TimeSpan.FromSeconds(plazo)), _sinTimeout, TimeSpan.FromSeconds(20));
                string vivos = await CanalesExecIntegracionTests.EsperarSinProcesosSleepAsync(plazo, plazo);

                // Assert
                _ = codigo.Should().Be(0);
                _ = salida.Should().Be("rapido\n");
                _ = duracion.Should().BeLessThan(TimeSpan.FromSeconds(5));
                _ = vivos.Should().BeEmpty();
            }
            finally
            {
                CanalesExecIntegracionTests.MatarProcesosSleep(plazo, plazo);
            }
        }
    }
}
