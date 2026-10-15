using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using FluentAssertions;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // La infraestructura de sshd de las pruebas, con instancias propias en puertos libres (no toca las de las colecciones).
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    public sealed class SshdLocalTests
    {
        private static int PuertoLibre() => PuertosPruebas.Libre();

        private static void BorrarEstado(string directorioEstado)
        {
            string ruta = Path.Combine(SshdLocal.BuscarDirectorioPruebas(), directorioEstado);
            if (Directory.Exists(ruta))
                Directory.Delete(ruta, recursive: true);
        }

        // sshd-local.sh hace caso de NATIVE_SSH_DIR_ESTADO, NATIVE_SSH_FORCE_COMMAND y NATIVE_SSH_SSHD_EXTRA: si el fixture
        // no las fijara, las heredaría del entorno de quien lanza las pruebas (el script dejaría la clave y known_hosts en
        // otro directorio que el que lee C#, o forzaría otro shell). Vacías, el script usa sus valores por defecto.
        [Fact]
        public void EntornoDelScript_SinValoresPropios_FijaElDirectorioDeEstadoDeCSharpYLosValoresPorDefecto()
        {
            // Act
            Dictionary<string, string> entorno = SshdLocal.EntornoScript("/ruta/del/estado", null, null, null);

            // Assert
            _ = entorno.Should().Equal(new Dictionary<string, string>
            {
                ["NATIVE_SSH_DIR_ESTADO"] = "/ruta/del/estado",
                ["NATIVE_SSH_FORCE_COMMAND"] = string.Empty,
                ["NATIVE_SSH_SSHD_EXTRA"] = string.Empty
            });
        }

        [Fact]
        public void EntornoDelScript_ConValoresPropios_LosPasaTodos()
        {
            // Act
            Dictionary<string, string> entorno = SshdLocal.EntornoScript("/e", "22500", "/bin/sh", "MaxSessions 2");

            // Assert
            _ = entorno.Should().Equal(new Dictionary<string, string>
            {
                ["NATIVE_SSH_DIR_ESTADO"] = "/e",
                ["NATIVE_SSH_FORCE_COMMAND"] = "/bin/sh",
                ["NATIVE_SSH_SSHD_EXTRA"] = "MaxSessions 2",
                ["NATIVE_SSH_PUERTO_PRUEBAS"] = "22500"
            });
        }

        // Sin su variable, los sshd de las colecciones con login lento y restringido siguen al bloque de la ejecución
        // (antes, 25022 y 26022 fijos: dos ejecuciones en paralelo con NATIVE_SSH_PUERTO_PRUEBAS distintos chocaban).
        [Theory]
        [InlineData(null, 22042, PuertosPruebas.DesplazamientoLento, "22242")]
        [InlineData("", 22042, PuertosPruebas.DesplazamientoLento, "22242")]
        [InlineData(null, 21042, PuertosPruebas.DesplazamientoRestringido, "21342")]
        [InlineData("25022", 22042, PuertosPruebas.DesplazamientoLento, "25022")]
        public void PuertoColeccion_SinSuVariable_SeDerivaDelPuertoDePruebas(string? variable, int puertoPruebas, int desplazamiento, string esperado)
        {
            // Act
            string puerto = PuertosPruebas.PuertoColeccion(variable, puertoPruebas, desplazamiento);

            // Assert
            _ = puerto.Should().Be(esperado);
        }

        // El sshd en español (antes en X+400, dentro del rango X400–X999 del que PuertosPruebas.Libre() elige los sshd
        // manuales: una prueba que eligiera ese puerto mientras la colección lo usaba no arrancaba su sshd, «Address already
        // in use») queda fuera de ese rango y no coincide con el de ningún otro sshd de colección ni con el HTTP de los humos.
        [Theory]
        [InlineData(22022)]
        [InlineData(26072)]
        public void PuertoIdioma_FueraDelRangoManualYDeLosDemasPuertosDerivados(int puertoPruebas)
        {
            // Arrange
            int bloque = puertoPruebas / 1000 * 1000;
            int[] otros = [puertoPruebas, puertoPruebas + 100, puertoPruebas + PuertosPruebas.DesplazamientoLento, puertoPruebas + PuertosPruebas.DesplazamientoRestringido];

            // Act
            int puerto = int.Parse(SshdLocalIdioma.Puerto(null, puertoPruebas), CultureInfo.InvariantCulture);

            // Assert
            _ = puerto.Should().BeInRange(bloque, bloque + 399);
            _ = otros.Should().NotContain(puerto);
        }

        // Los puertos derivados de NATIVE_SSH_PUERTO_PRUEBAS (X: X+50 español, X+100 HTTP, X+200 lento, X+300 restringido) solo
        // quedan fuera del rango manual X400–X999 si las tres últimas cifras de X están entre 000 y 099 (convención X0nn).
        [Theory]
        [InlineData(22022)]
        [InlineData(26022)]
        [InlineData(28072)]
        [InlineData(22099)]
        public void BloqueDePuertos_ConX0nn_SeAdmite(int puertoPruebas)
        {
            // Act
            Action derivar = () =>
            {
                _ = PuertosPruebas.PuertoColeccion(null, puertoPruebas, PuertosPruebas.DesplazamientoRestringido);
                _ = PuertosPruebas.RangoManualDe(puertoPruebas);
            };

            // Assert
            _ = derivar.Should().NotThrow();
        }

        [Theory]
        [InlineData(22100)]
        [InlineData(22360)]
        [InlineData(24542)]
        [InlineData(22950)]
        public void BloqueDePuertos_ConDerivadosEnElRangoManual_FallaConUnMensajeClaro(int puertoPruebas)
        {
            // Act
            Action derivarLento = () => PuertosPruebas.PuertoColeccion(null, puertoPruebas, PuertosPruebas.DesplazamientoLento);
            Action rango = () => PuertosPruebas.RangoManualDe(puertoPruebas);

            // Assert
            _ = derivarLento.Should().Throw<InvalidOperationException>().WithMessage($"*NATIVE_SSH_PUERTO_PRUEBAS={puertoPruebas}*0nn*");
            _ = rango.Should().Throw<InvalidOperationException>().WithMessage($"*NATIVE_SSH_PUERTO_PRUEBAS={puertoPruebas}*0nn*");
        }

        // Con un bloque de 65000 o más, el rango manual (y los derivados de 65535 en adelante) pasaría del último puerto, 65535.
        [Theory]
        [InlineData(65022)]
        [InlineData(65099)]
        [InlineData(65435)]
        public void BloqueDePuertos_QuePasaDe65535_FallaConUnMensajeClaro(int puertoPruebas)
        {
            // Act
            Action derivar = () => PuertosPruebas.PuertoColeccion(null, puertoPruebas, PuertosPruebas.DesplazamientoLento);
            Action rango = () => PuertosPruebas.RangoManualDe(puertoPruebas);

            // Assert
            _ = derivar.Should().Throw<InvalidOperationException>().WithMessage($"*NATIVE_SSH_PUERTO_PRUEBAS={puertoPruebas}*65535*");
            _ = rango.Should().Throw<InvalidOperationException>().WithMessage($"*NATIVE_SSH_PUERTO_PRUEBAS={puertoPruebas}*65535*");
        }

        [Fact]
        public void BloqueDePuertos_UltimoBloqueQueCabe_SeAdmite() =>
            _ = PuertosPruebas.RangoManualDe(64022).Should().Be((64400, 64999));

        // Con su variable fijada, el puerto de la colección no se deriva y no depende del bloque.
        [Fact]
        public void BloqueDePuertos_ConLaVariableDeLaColeccion_NoSeComprueba() =>
            _ = PuertosPruebas.PuertoColeccion("25022", 22360, PuertosPruebas.DesplazamientoLento).Should().Be("25022");

        [Fact]
        [Trait("Categoria", "SoloLinux")]
        public void PuertoLibre_EsDelRangoManualDelBloqueDeLasPruebasYNoEsEfimero()
        {
            // Arrange: NATIVE_SSH_PUERTO_PRUEBAS (X022) fija el bloque de mil puertos de esta ejecución; los sshd manuales
            // usan X400–X999.
            string? variable = Environment.GetEnvironmentVariable("NATIVE_SSH_PUERTO_PRUEBAS");
            int bloque = (variable is null ? 22022 : int.Parse(variable, CultureInfo.InvariantCulture)) / 1000 * 1000;
            string[] efimero = File.ReadAllText("/proc/sys/net/ipv4/ip_local_port_range").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // Act
            int puerto = PuertoLibre();

            // Assert
            _ = puerto.Should().BeInRange(bloque + 400, bloque + 999);
            _ = puerto.Should().NotBeInRange(int.Parse(efimero[0], CultureInfo.InvariantCulture), int.Parse(efimero[1], CultureInfo.InvariantCulture));
        }

        [Fact]
        [Trait("Categoria", "SoloLinux")]
        public void Parar_ConElMaestroQueNoAtiendeTerm_EsperaASuFinYDejaElPuertoLibre()
        {
            // Arrange: detenido con SIGSTOP, el sshd no atiende el TERM de 'parar' (como uno que tarda en salir).
            int puerto = PuertoLibre();
            string estado = $".estado-parada-{Guid.NewGuid():N}";
            SshdLocal sshd = new(puerto.ToString(CultureInfo.InvariantCulture), estado, null);
            string pid = sshd.PidMaestro;

            try
            {
                _ = BashLocal.Ejecutar($"kill -STOP {pid}");

                // Act
                sshd.Dispose();
                string estadoProceso = BashLocal.Ejecutar($"ps -o stat= -p {pid} || true");
                using TcpListener nuevo = new(IPAddress.Loopback, puerto);
                Action escuchar = nuevo.Start;

                // Assert: ni vivo ni detenido (como mucho, pendiente de que su padre lo recoja), y el puerto libre.
                _ = estadoProceso.Should().Match(e => e.Length == 0 || e.StartsWith('Z'));
                _ = escuchar.Should().NotThrow();
            }
            finally
            {
                _ = BashLocal.Ejecutar($"kill -KILL {pid} 2>/dev/null || true");
                BorrarEstado(estado);
            }
        }

        [Fact]
        [Trait("Categoria", "SoloLinux")]
        public async Task Parar_ConSesionesDetenidasConSigstop_NoLasDejaVivas()
        {
            // Arrange: una conexión con sus sshd-session detenidos, como los deja una prueba que muere antes de reanudarlos.
            int puerto = PuertoLibre();
            string estado = $".estado-sesiones-{Guid.NewGuid():N}";
            SshdLocal sshd = new(puerto.ToString(CultureInfo.InvariantCulture), estado, null);
            string[] sesiones = [];

            try
            {
                await using (SesionShell sesion = sshd.Sesion())
                {
                    _ = await sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(10), CancellationToken.None);
                    sesiones = sshd.ProcesosDeConexion();
                    _ = BashLocal.Ejecutar($"kill -STOP {string.Join(' ', sesiones)}");

                    // Act
                    sshd.Dispose();
                }

                string restos = BashLocal.Ejecutar($"for p in {string.Join(' ', sesiones)}; do ps -o pid=,stat= -p $p; done");

                // Assert: ni detenidas ni vivas (como mucho, pendientes de que alguien las recoja).
                _ = sesiones.Should().NotBeEmpty();
                _ = restos.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().NotContain(l => !l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[1].StartsWith('Z'));
            }
            finally
            {
                _ = BashLocal.Ejecutar($"for p in {string.Join(' ', sesiones)}; do kill -CONT $p; kill -KILL $p; done 2>/dev/null; true");
                BorrarEstado(estado);
            }
        }

        [Fact]
        [Trait("Categoria", "SoloLinux")]
        public void Iniciar_DesdeUnProcesoConfinadoAUnaCpu_ElServidorPuedeUsarTodasLasCpu()
        {
            // Arrange: como las pruebas de estrés, que confinan su proceso y su carga con taskset; el sshd lo heredaría y,
            // con su sshd-session en planificación 'idle' (ananicy lo hace), no recibiría CPU mientras dure la carga.
            string directorioPruebas = SshdLocal.BuscarDirectorioPruebas();
            string estado = Path.Combine(Path.GetTempPath(), $"ns_afinidad_{Guid.NewGuid():N}");
            string entorno = $"NATIVE_SSH_DIR_ESTADO='{estado}' NATIVE_SSH_PUERTO_PRUEBAS={PuertoLibre()}";

            try
            {
                // Act
                _ = BashLocal.Ejecutar($"{entorno} taskset -c 0 bash '{directorioPruebas}/sshd-local.sh' iniciar");
                string permitidas = BashLocal.Ejecutar($"grep Cpus_allowed_list /proc/$(cat '{estado}/sshd.pid')/status | cut -f2");
                string todas = BashLocal.Ejecutar("echo 0-$(( $(nproc --all) - 1 ))");

                // Assert
                _ = permitidas.Should().Be(todas);
            }
            finally
            {
                _ = BashLocal.Ejecutar($"{entorno} bash '{directorioPruebas}/sshd-local.sh' parar");
                if (Directory.Exists(estado))
                    Directory.Delete(estado, recursive: true);
            }
        }

        [Fact]
        [Trait("Categoria", "SoloLinux")]
        public void Restringido_SiSshdNoArranca_NoDejaSuGranjaDeBinarios()
        {
            // Arrange: el puerto ya está ocupado, así que sshd no puede escuchar en él.
            int libre = PuertoLibre();
            using TcpListener ocupante = new(IPAddress.Loopback, libre);
            ocupante.Start();
            string puerto = libre.ToString(CultureInfo.InvariantCulture);
            string granja = Path.Combine(Path.GetTempPath(), $"ns_login_{Guid.NewGuid():N}");
            string estado = $".estado-fallo-{Guid.NewGuid():N}";

            try
            {
                // Act
                Action arrancar = () => _ = new SshdLocalRestringido(puerto, estado, granja);

                // Assert
                _ = arrancar.Should().Throw<InvalidOperationException>();
                _ = Directory.Exists(granja).Should().BeFalse();
            }
            finally
            {
                if (Directory.Exists(granja))
                    Directory.Delete(granja, recursive: true);
                BorrarEstado(estado);
            }
        }
    }
}
