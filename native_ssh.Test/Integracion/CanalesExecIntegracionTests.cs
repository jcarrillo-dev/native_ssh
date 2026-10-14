using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Protocolo;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Herramientas de solo lectura por canales exec contra el sshd local.
    [Collection(ColeccionSshd.Nombre)]
    [Trait("Categoria", "Integracion")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El gestor se libera en IAsyncLifetime.DisposeAsync.")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class CanalesExecIntegracionTests : IAsyncLifetime
    {
        private static readonly string[] DirectoriosBinarios = ["/usr/bin", "/bin"];

        private readonly SshdLocal _sshd;
        private readonly string _directorio = Path.Combine(Path.GetTempPath(), $"ns_exec_{Guid.NewGuid():N}");
        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;

        public CanalesExecIntegracionTests(SshdLocal sshd)
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

        private string Ruta(string nombre) => Path.Combine(_directorio, nombre);

        private int ConexionesAceptadas() =>
            File.ReadLines(Path.Combine(_sshd.DirectorioEstado, "sshd.log")).Count(l => l.Contains("Accepted publickey", StringComparison.Ordinal));

        [Fact]
        public async Task Lectura_NoEsperaDetrasDeUnComandoLargoEnElShell()
        {
            // Arrange
            await File.WriteAllTextAsync(Ruta("a.txt"), "uno\n");
            ResultadoComando largo = await _herramientas.EjecutarAsync(Destino, "sleep 20", esperarSegundos: 0).ComoComando();
            Stopwatch reloj = Stopwatch.StartNew();

            // Act
            string texto = await _herramientas.LeerArchivoAsync(Destino, Ruta("a.txt"));
            ResultadoMetadatos metadatos = await _herramientas.ConsultarMetadatosAsync(Destino, Ruta("a.txt"));
            reloj.Stop();

            // Assert
            _ = texto.Should().Be("[Líneas 1-1 de 1]\nuno\n");
            _ = metadatos.Existe.Should().BeTrue();
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            _ = _gestor.BuscarPeticion(largo.Id).Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
            _ = (await _herramientas.CancelarAsync(largo.Id).ComoComando()).Estado.Should().Be(EstadoPeticionEnum.Cancelado);
        }

        // Mata los procesos sshd de las conexiones abiertas con el sshd local sin tocar el proceso maestro: el TCP se
        // corta como en una caída de red.
        private void MatarConexionesDelSshd()
        {
            string maestro = File.ReadAllText(Path.Combine(_sshd.DirectorioEstado, "sshd.pid")).Trim();
            _ = Bash($"matar() {{ for c in $(pgrep -P $1); do matar $c; done; case \"$(ps -o comm= -p $1)\" in sshd*) kill -9 $1;; esac; }}; for c in $(pgrep -P {maestro}); do matar $c; done");
        }

        private static string Bash(string comando)
        {
            ProcessStartInfo inicio = new("bash", ["-c", comando]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using Process proceso = Process.Start(inicio)!;
            string salida = proceso.StandardOutput.ReadToEnd();
            _ = proceso.StandardError.ReadToEnd();
            proceso.WaitForExit();
            return salida.Trim();
        }

        [Fact]
        public async Task CaidaDeLaConexion_ConOSinLecturaPrevia_ElSiguienteExecRecreaElShellYSeEjecuta()
        {
            // Arrange: cada caída obliga a reconectar; el limitador no debe frenar la prueba.
            OpcionesNativeSsh opciones = _sshd.Opciones(maxConexiones: 1000);
            await using GestorSesiones gestor = new(
                _sshd.Fabrica(opciones),
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);
            await File.WriteAllTextAsync(Ruta("a.txt"), "uno\n");
            _ = await herramientas.EjecutarAsync(Destino, "echo previo").ComoComando();
            List<string> fallos = [];

            // Act: con el shell vivo, caída y luego ssh_exec directo, o una lectura (reconecta por exec) y ssh_exec.
            for (int i = 0; i < 10; i++)
            {
                bool isConLectura = i % 2 == 1;
                MatarConexionesDelSshd();
                await EsperasPrueba.EsperarHastaAsync(() => !gestor.ListarSesiones().Single().IsConectada);
                string lectura = isConLectura ? await herramientas.LeerArchivoAsync(Destino, Ruta("a.txt")) : "[Líneas 1-1 de 1]\nuno\n";
                ResultadoComando comando = await herramientas.EjecutarAsync(Destino, $"echo tras_{i}", esperarSegundos: 15).ComoComando();
                if (lectura != "[Líneas 1-1 de 1]\nuno\n" || comando.Estado != EstadoPeticionEnum.Completado || comando.Salida != $"tras_{i}\n" || comando.Aviso?.Contains("Shell remoto recreado", StringComparison.Ordinal) != true)
                    fallos.Add($"{i} (lectura previa: {isConLectura}): lectura='{lectura}' estado={comando.Estado} salida='{comando.Salida}' aviso='{comando.Aviso}'");
            }

            // Assert
            _ = fallos.Should().BeEmpty();
            _ = gestor.ListarSesiones().Single().ConexionesAbiertas.Should().Be(11);
        }

        // Procesos 'sleep <n>' vivos en el servidor (es la misma máquina). Se filtra por el nombre exacto del programa:
        // un 'pgrep -f' encontraría también el propio shell que ejecuta la comprobación.
        internal static string ProcesosSleepVivos(long primero, long segundo) =>
            Bash($"ps -eo args= | awk -v a={primero} -v b={segundo} '$1==\"sleep\" && ($2==a || $2==b)'");

        // Espera hasta 5 s a que no quede ninguno y devuelve los que sigan vivos.
        internal static async Task<string> EsperarSinProcesosSleepAsync(long primero, long segundo)
        {
            string vivos = ProcesosSleepVivos(primero, segundo);
            for (int i = 0; i < 50 && vivos.Length > 0; i++)
            {
                await Task.Delay(100);
                vivos = ProcesosSleepVivos(primero, segundo);
            }

            return vivos;
        }

        [Fact]
        public async Task ExecConTiempoAgotado_MataElProcesoRemotoYSusHijosAunqueIgnorenTerm()
        {
            // Arrange: un hijo en segundo plano y un proceso principal que ignora TERM (su 'sleep' hereda la señal ignorada).
            long hijo = 700_000 + Random.Shared.Next(100_000);
            long principal = hijo + 100_000;
            using ProcesosAnotados anotados = new();
            string script = $"{anotados.AnotarGrupo}sleep {hijo} & trap '' TERM; sleep {principal}; wait";
            string resultado;

            // Act
            try
            {
                try
                {
                    ResultadoCanalExec ejecucion = await _gestor.EjecutarExecAsync(Destino, script, TimeSpan.FromSeconds(2), CancellationToken.None);
                    resultado = $"código {ejecucion.CodigoSalida}";
                }
                catch (NativeSshException ex)
                {
                    resultado = ex.Message;
                }

                string vivos = await EsperarSinProcesosSleepAsync(hijo, principal);

                // Assert: 'timeout' manda TERM a los 2 s (muere el hijo) y KILL 2 s después (el resto), antes que el límite local.
                _ = vivos.Should().BeEmpty();
                _ = resultado.Should().BeOneOf("código 124", "código 137");
            }
            finally
            {
                anotados.Matar();
            }
        }

        [Theory]
        [InlineData("archivo", "000")]
        [InlineData("directorio", "000")]
        [InlineData("directorio", "600")]
        public async Task Buscar_RutaExplicitaSinPermisoDeLectura_LanzaErrorEnLugarDeSinCoincidencias(string tipo, string modo)
        {
            // Arrange
            string ruta = Ruta($"protegido_{tipo}");
            if (tipo == "archivo")
                await File.WriteAllTextAsync(ruta, "secreto\n");
            else
                await File.WriteAllTextAsync(Path.Combine(Directory.CreateDirectory(ruta).FullName, "dentro.txt"), "secreto\n");
            _ = Bash($"chmod {modo} '{ruta}'");

            try
            {
                // Act
                NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.BuscarAsync(Destino, "secreto", ruta))
                    .Should().ThrowAsync<NativeSshException>()).Which;

                // Assert
                _ = error.Message.Should().Contain("sin permiso de lectura").And.Contain(ruta);
            }
            finally
            {
                _ = Bash($"chmod 700 '{ruta}'");
            }
        }

        [Fact]
        public async Task Lectura_ArchivoLatin1_AvisaDeQueNoEsUtf8()
        {
            // Arrange
            await File.WriteAllBytesAsync(Ruta("latin1.txt"), [.. "a"u8, 0xF1, .. "o\nsin final"u8]);

            // Act
            string texto = await _herramientas.LeerArchivoAsync(Destino, Ruta("latin1.txt"));

            // Assert: sin salto final, el aviso va en su propia línea.
            _ = texto.Should().Be("[Líneas 1-2 de 2]\na\uFFFDo\nsin final\n[aviso: contenido no UTF-8; bytes inválidos sustituidos por U+FFFD]");
        }

        [Fact]
        public async Task Lectura_ArchivoCrlf_DevuelveSusRetornosDeCarroYTextoUtf8Fiel()
        {
            // Arrange
            await File.WriteAllTextAsync(Ruta("crlf.txt"), "uno ñ\r\ndos \u001b[31mrojo\u001b[0m\r\n");

            // Act
            string texto = await _herramientas.LeerArchivoAsync(Destino, Ruta("crlf.txt"));

            // Assert
            _ = texto.Should().Be("[Líneas 1-2 de 2]\nuno ñ\r\ndos \u001b[31mrojo\u001b[0m\r\n");
        }

        [Fact]
        public async Task LecturasYComandos_ComparteUnaSolaConexionTcp()
        {
            // Arrange
            await File.WriteAllTextAsync(Ruta("a.txt"), "uno\n");
            int aceptadasAntes = ConexionesAceptadas();

            // Act
            string[] lecturas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _herramientas.LeerArchivoAsync(Destino, Ruta("a.txt"))));
            ResultadoComando comando = await _herramientas.EjecutarAsync(Destino, "echo hola").ComoComando();
            _ = await _herramientas.BuscarAsync(Destino, "uno", _directorio);
            _ = await _herramientas.BuscarPorNombreAsync(Destino, "*.txt", _directorio);
            _ = await _herramientas.ListarDirectoriosAsync(Destino, _directorio, incluirArchivos: true);
            await EsperasPrueba.EsperarHastaAsync(() => ConexionesAceptadas() > aceptadasAntes);
            await Task.Delay(300);

            // Assert
            _ = lecturas.Should().AllSatisfy(l => l.Should().Be("[Líneas 1-1 de 1]\nuno\n"));
            _ = comando.Salida.Should().Be("hola\n");
            _ = _herramientas.Estado().Sesiones.Should().ContainSingle().Which.ConexionesAbiertas.Should().Be(1);
            _ = (ConexionesAceptadas() - aceptadasAntes).Should().Be(1);
        }

        [Fact]
        public async Task SetCwd_LasLecturasUsanElDirectorioYElShellRecreadoLoConserva()
        {
            // Arrange
            await File.WriteAllTextAsync(Ruta("relativo.txt"), "relativo\n");

            // Act
            _ = await _herramientas.FijarDirectorioAsync(Destino, _directorio);
            string lectura = await _herramientas.LeerArchivoAsync(Destino, "relativo.txt");
            _ = _herramientas.ReiniciarShell(Destino);
            ResultadoComando pwd = await _herramientas.EjecutarAsync(Destino, "pwd; echo \"$__NS_CWD\"").ComoComando();
            _ = await _herramientas.FijarDirectorioAsync(Destino, "");
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.LeerArchivoAsync(Destino, "relativo.txt"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = lectura.Should().Be("[Líneas 1-1 de 1]\nrelativo\n");
            _ = pwd.Salida.Should().Be($"{_directorio}\n{_directorio}\n");
            _ = error.Message.Should().Contain("no existe");
        }

        [Fact]
        public async Task Lectura_SalidaQueSuperaElTope_LanzaErrorYLaConexionSigueUsable()
        {
            // Arrange: tope de 64 KiB y 200 KiB de salida.
            OpcionesNativeSsh opciones = _sshd.Opciones() with { MaxBytesSalidaPorPeticion = 64 * 1024 };
            await using GestorSesiones gestor = new(
                _sshd.Fabrica(opciones),
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);
            await File.WriteAllLinesAsync(Ruta("grande.txt"), Enumerable.Range(0, 2000).Select(i => new string('x', 100)));
            await File.WriteAllTextAsync(Ruta("pequeno.txt"), "ok\n");

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync(Destino, Ruta("grande.txt"), 1, 2000))
                .Should().ThrowAsync<NativeSshException>()).Which;
            string siguiente = await herramientas.LeerArchivoAsync(Destino, Ruta("pequeno.txt"));

            // Assert
            _ = error.Message.Should().Contain("superó el máximo de 65536 bytes");
            _ = siguiente.Should().Be("[Líneas 1-1 de 1]\nok\n");
            _ = herramientas.Estado().Sesiones.Should().ContainSingle().Which.ConexionesAbiertas.Should().Be(1);
        }

        [Theory]
        [InlineData("sh")]
        [InlineData("bash")]
        [InlineData("zsh")]
        [InlineData("fish")]
        [InlineData("dash")]
        [InlineData("tcsh")]
        public void ComandoExec_LoEjecutaBashSeaCualSeaElShellDeLogin(string shell)
        {
            // Arrange: el comando del canal exec lo interpreta el shell de login, que puede no ser bash.
            string? ruta = DirectoriosBinarios.Select(d => Path.Combine(d, shell)).FirstOrDefault(File.Exists);
            if (ruta is null)
                return;
            string comando = ConstructorComandoExec.Construir("printf '%s|%s\\n' \"uno 'dos'\" \"$((1+2))\"; [[ -n $BASH_VERSION ]] && pwd; exit 7", _directorio, TimeSpan.FromSeconds(10));
            ProcessStartInfo inicio = new(ruta, ["-c", comando]) { RedirectStandardOutput = true, RedirectStandardError = true };

            // Act
            using Process proceso = Process.Start(inicio)!;
            string salida = proceso.StandardOutput.ReadToEnd();
            string errores = proceso.StandardError.ReadToEnd();
            proceso.WaitForExit();

            // Assert
            _ = salida.Should().Be($"uno 'dos'|3\n{_directorio}\n", errores);
            _ = proceso.ExitCode.Should().Be(7);
        }
    }
}
