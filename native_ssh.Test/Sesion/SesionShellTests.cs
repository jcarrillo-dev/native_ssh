using FluentAssertions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Buffers;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Test.Sesion.Falsos;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Sesion
{
    public class SesionShellTests
    {
        private static readonly TimeSpan Limite = TimeSpan.FromSeconds(5);
        private static readonly DestinoSsh Destino = CrearDestino();

        private static DestinoSsh CrearDestino()
        {
            _ = DestinoSsh.TryParse("prueba@servidor", "x", out DestinoSsh destino);
            return destino;
        }

        private static RespuestaFalsa Guion(string comando) => comando switch
        {
            "colgado" => new("empezando\n", 0, ComportamientoFalsoEnum.Colgado),
            "terco" => new("", 0, ComportamientoFalsoEnum.IgnoraCtrlC),
            "pregunta" => new("¿Nombre? ", 0, ComportamientoFalsoEnum.PideEntrada),
            "exit" => new("", 0, ComportamientoFalsoEnum.Salir),
            "sudo -i" => new("root# ", 0, ComportamientoFalsoEnum.AbreShellAnidado),
            "termina-anidado" => new("dentro\n", 0, ComportamientoFalsoEnum.TerminaEnShellAnidado),
            "ssh -tt gemelo" => new("gemelo$ ", 0, ComportamientoFalsoEnum.AbreShellSinTestigo),
            "falla" => new("error\n", 3),
            _ => new($"eco:{comando}\n")
        };

        private static OpcionesNativeSsh Opciones(int maxConexiones = 4, double timeoutInicializacionSegundos = 3) => new()
        {
            EsperaTrasCtrlC = TimeSpan.FromMilliseconds(300),
            TimeoutInicializacionShell = TimeSpan.FromSeconds(timeoutInicializacionSegundos),
            TimeoutConexion = TimeSpan.FromSeconds(3),
            MaxConexionesPorVentana = maxConexiones
        };

        private static SesionShell Crear(FabricaFalsa fabrica, OpcionesNativeSsh? opciones = null, ILogger? logger = null)
        {
            opciones ??= Opciones();
            return new SesionShell(Destino, fabrica, new LimitadorConexiones(opciones, TimeProvider.System), opciones, logger ?? NullLogger.Instance);
        }

        private static Peticion Nueva(string comando, double timeoutSegundos = 10) =>
            new(Destino, comando, TimeSpan.FromSeconds(timeoutSegundos), 1 << 20, TimeProvider.System);

        private static async Task<Peticion> EjecutarAsync(SesionShell sesion, string comando, double timeoutSegundos = 10)
        {
            Peticion peticion = Nueva(comando, timeoutSegundos);
            sesion.Encolar(peticion);
            await peticion.Finalizacion.WaitAsync(Limite);
            return peticion;
        }

        private static string Texto(Peticion peticion)
        {
            byte[] destino = new byte[(int)peticion.Salida.Longitud];
            LecturaSalida lectura = peticion.Salida.Leer(0, destino);
            return Encoding.UTF8.GetString(destino, 0, lectura.Copiados);
        }

        [Fact]
        public async Task Comando_DevuelveSalidaYCodigo_SinMotd()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);

            // Act
            Peticion hola = await EjecutarAsync(sesion, "hola");
            Peticion falla = await EjecutarAsync(sesion, "falla");

            // Assert
            _ = hola.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = hola.CodigoSalida.Should().Be(0);
            _ = Texto(hola).Should().Be("eco:hola\n");
            _ = hola.Aviso.Should().BeNull();
            _ = falla.CodigoSalida.Should().Be(3);
            _ = fabrica.Conexiones.Should().Be(1);
            _ = fabrica.Ultima!.ShellsAbiertos.Should().Be(1);
            _ = sesion.ComandosEjecutados.Should().Be(2);
        }

        [Fact]
        public async Task LoginLentoConReadline_ElPrimerComandoSeEjecutaDeVerdad()
        {
            // Arrange: el login tarda más que el reenvío (2 s) y luego ejecuta la línea original y el reenvío.
            FabricaFalsa fabrica = new(Guion, login: LoginFalsoEnum.LentoConReadline, retrasoLogin: TimeSpan.FromMilliseconds(2300));
            await using SesionShell sesion = Crear(fabrica, Opciones(timeoutInicializacionSegundos: 6));

            // Act
            Peticion primera = await EjecutarAsync(sesion, "export NS_X=1");
            Peticion segunda = await EjecutarAsync(sesion, "dos");

            // Assert
            _ = primera.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = Texto(primera).Should().Be("eco:export NS_X=1\n");
            _ = Texto(segunda).Should().Be("eco:dos\n");
            _ = fabrica.Ultima!.Comandos.Should().Equal(["export NS_X=1", "dos"]);
            _ = fabrica.Ultima.CanalActual!.MarcadoresInicializacion.Should().Be(2);
            _ = fabrica.Ultima.CanalActual.Sincronizaciones.Should().Be(1);
        }

        [Fact]
        public async Task LoginQueComeLaInicializacion_ElReenvioYLaSincronizacionBastan()
        {
            // Arrange: el login se traga la primera escritura; solo se ejecuta el reenvío.
            FabricaFalsa fabrica = new(Guion, login: LoginFalsoEnum.ComeInicializacion, retrasoLogin: TimeSpan.FromMilliseconds(300));
            await using SesionShell sesion = Crear(fabrica);

            // Act
            Peticion primera = await EjecutarAsync(sesion, "uno");

            // Assert
            _ = primera.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = Texto(primera).Should().Be("eco:uno\n");
            _ = fabrica.Ultima!.CanalActual!.MarcadoresInicializacion.Should().Be(1);
            _ = fabrica.Ultima.CanalActual.Sincronizaciones.Should().Be(1);
        }

        [Fact]
        public async Task SinReenvio_NoSeEnviaLineaDeSincronizacion()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);

            // Act
            Peticion primera = await EjecutarAsync(sesion, "uno");

            // Assert
            _ = Texto(primera).Should().Be("eco:uno\n");
            _ = fabrica.Ultima!.CanalActual!.MarcadoresInicializacion.Should().Be(1);
            _ = fabrica.Ultima.CanalActual.Sincronizaciones.Should().Be(0);
        }

        [Fact]
        public async Task CapturaPath_ConRuidoQueEsBase64Antes_NoAceptaUnPathCorrupto()
        {
            // Arrange: otro proceso escribe en el terminal justo antes de la salida de la captura.
            FabricaFalsa fabrica = new(Guion) { PathLogin = "/opt/herramientas/bin:/usr/bin", RuidoAntesCapturaPath = "QUJD\n" };
            await using SesionShell sesion = Crear(fabrica);

            // Act
            _ = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = sesion.PathLogin.Should().Be("/opt/herramientas/bin:/usr/bin");
        }

        [Fact]
        public async Task CapturaPath_ConRuidoAntesYDespues_SeLeeEntreSusMarcadores()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion)
            {
                PathLogin = "/opt/mi dir/bin:/usr/bin",
                RuidoAntesCapturaPath = "Mensaje de difusión: reinicio a las 22:00\n",
                RuidoTrasCapturaPath = "[1]+  Hecho    sleep 1\n"
            };
            await using SesionShell sesion = Crear(fabrica);

            // Act
            _ = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = sesion.PathLogin.Should().Be("/opt/mi dir/bin:/usr/bin");
        }

        [Theory]
        [InlineData("/usr/bin:bin", "entrada relativa")]
        [InlineData("/usr/bin::/bin", "entrada vacía")]
        [InlineData("/usr/bin:", "entrada vacía")]
        [InlineData("/usr/bin:/opt/a\u0007b", "caracteres de control")]
        [InlineData("", "PATH vacío")]
        public async Task CapturaPath_PathNoValido_SeDescartaYSeRegistra(string path, string motivo)
        {
            // Arrange
            using LoggerPrueba logger = new();
            FabricaFalsa fabrica = new(Guion) { PathLogin = path };
            await using SesionShell sesion = Crear(fabrica, logger: logger);

            // Act
            Peticion peticion = await EjecutarAsync(sesion, "hola");

            // Assert: el shell funciona igual; los canales exec usarán el PATH de sshd.
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = sesion.PathLogin.Should().BeNull();
            _ = logger.Entradas.Should().ContainSingle(e => e.Evento == 31 && e.Nivel == LogLevel.Warning).Which.Mensaje.Should().Contain(motivo);
        }

        [Fact]
        public async Task CapturaPath_PathQueNoEsUtf8_SeDescartaYSeRegistra()
        {
            // Arrange
            using LoggerPrueba logger = new();
            FabricaFalsa fabrica = new(Guion) { BytesPathLogin = [.. "/usr/bin:/opt/"u8, 0xFF, .. "x"u8] };
            await using SesionShell sesion = Crear(fabrica, logger: logger);

            // Act
            _ = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = sesion.PathLogin.Should().BeNull();
            _ = logger.Entradas.Should().ContainSingle(e => e.Evento == 31).Which.Mensaje.Should().Contain("UTF-8");
        }

        [Fact]
        public async Task Cola_EjecutaEnOrden()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion[] peticiones = [Nueva("a"), Nueva("b"), Nueva("c")];

            // Act
            foreach (Peticion? peticion in peticiones)
                sesion.Encolar(peticion);
            await Task.WhenAll(peticiones.Select(p => p.Finalizacion)).WaitAsync(Limite);

            // Assert
            _ = fabrica.Ultima!.Comandos.Should().Equal(["a", "b", "c"]);
            _ = peticiones.Should().AllSatisfy(p => p.Estado.Should().Be(EstadoPeticionEnum.Completado));
        }

        [Fact]
        public async Task TiempoAgotado_RecuperaElMismoShell()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);

            // Act
            Peticion colgado = await EjecutarAsync(sesion, "colgado", timeoutSegundos: 0.3);
            Peticion siguiente = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = colgado.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            _ = colgado.CodigoSalida.Should().Be(130);
            _ = Texto(colgado).Should().Contain("empezando");
            _ = Texto(colgado).Should().NotContain("__NSX");
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Aviso.Should().BeNull();
            _ = fabrica.Ultima!.ShellsAbiertos.Should().Be(1);
        }

        [Fact]
        public async Task Cancelar_EnEjecucion()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion colgado = Nueva("colgado");
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(colgado).Contains("empezando"));

            // Act
            _ = colgado.SolicitarCancelacion().Should().BeTrue();
            await colgado.Finalizacion.WaitAsync(Limite);
            Peticion siguiente = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = colgado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = fabrica.Ultima!.ShellsAbiertos.Should().Be(1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CtrlCAUnShellAnidado_CierraElCanalLoDiceYElSiguienteNoSeEjecutaDentro(bool isPlazo)
        {
            // Arrange: B1 de la auditoría del E-septies (Z06). El comando deja un shell interactivo anidado (sudo -i, su -,
            // ssh a otra máquina, bash -i) que no muere con Ctrl+C: la línea de recuperación la ejecuta él.
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");
            CanalFalso viejo = fabrica.Ultima!.CanalActual!;
            Peticion anidado = Nueva("sudo -i", timeoutSegundos: isPlazo ? 0.5 : 60);
            sesion.Encolar(anidado);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(anidado).Contains("root# "));

            // Act
            if (!isPlazo)
                _ = anidado.SolicitarCancelacion();
            await anidado.Finalizacion.WaitAsync(Limite);
            Peticion siguiente = await EjecutarAsync(sesion, "hola");

            // Assert: no se da la cancelación por buena en silencio; el canal se cierra y el siguiente va a un shell nuevo.
            _ = anidado.Estado.Should().Be(isPlazo ? EstadoPeticionEnum.TiempoAgotado : EstadoPeticionEnum.Cancelado);
            _ = anidado.Aviso.Should().Contain("shell interactivo anidado").And.Contain("se cerró el canal del shell").And.Contain("SIGHUP")
                .And.Contain("tmux").And.Contain("shell nuevo").And.Contain("se perderán").And.Contain("ssh_set_cwd").And.NotContain("no confirmó");
            _ = viejo.LineasEnShellAjeno.Should().Be(1);
            _ = viejo.IsDispuesto.Should().BeTrue();
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = Texto(siguiente).Should().Be("eco:hola\n");
            _ = siguiente.Aviso.Should().Contain("recreado");
            _ = fabrica.Ultima.CanalActual.Should().NotBeSameAs(viejo);
            _ = fabrica.Ultima.CanalActual!.LineasEnShellAjeno.Should().Be(0);
            _ = fabrica.Conexiones.Should().Be(1);
        }

        [Fact]
        public async Task CtrlCAUnProcesoNormal_ConLaComprobacionDeIdentidad_ConservaElShellSinAviso()
        {
            // Arrange: falso positivo que no debe darse: el Ctrl+C termina el proceso y la recuperación la ejecuta el shell
            // de la sesión, también tras recrearlo (otro $$).
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");
            sesion.SolicitarReset();
            _ = await EjecutarAsync(sesion, "hola");
            CanalFalso canal = fabrica.Ultima!.CanalActual!;
            Peticion colgado = Nueva("colgado");
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(colgado).Contains("empezando"));

            // Act
            _ = colgado.SolicitarCancelacion();
            await colgado.Finalizacion.WaitAsync(Limite);
            Peticion siguiente = await EjecutarAsync(sesion, "dos");

            // Assert
            _ = colgado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = colgado.Aviso.Should().Be("Cancelado: se envió Ctrl+C al proceso.");
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Aviso.Should().BeNull();
            _ = fabrica.Ultima.CanalActual.Should().BeSameAs(canal);
            _ = canal.Pid.Should().NotBe(4242, "el shell recreado es otro bash");
            _ = canal.LineasEnShellAjeno.Should().Be(0);
            _ = fabrica.Ultima.ShellsAbiertos.Should().Be(2);
        }

        [Fact]
        public async Task ComandoCuyaColaEjecutaOtroShell_TerminaComoShellPerdidoYCierraElCanal()
        {
            // Arrange: defensa en profundidad; la cola de un comando la ejecuta un shell distinto del de la sesión.
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");
            CanalFalso viejo = fabrica.Ultima!.CanalActual!;

            // Act
            Peticion ajeno = await EjecutarAsync(sesion, "termina-anidado");
            Peticion siguiente = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = ajeno.Estado.Should().Be(EstadoPeticionEnum.ShellPerdido);
            _ = ajeno.CodigoSalida.Should().Be(-1);
            _ = ajeno.Aviso.Should().Contain("no la ejecutó el shell de la sesión").And.Contain("o uno que sustituyó al de la sesión con exec")
                .And.Contain("no son fiables").And.Contain("Se cerró el canal del shell").And.Contain("shell nuevo").And.NotContain("no confirmó");
            _ = viejo.IsDispuesto.Should().BeTrue();
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Aviso.Should().Contain("recreado");
        }

        // B1 de la auditoría del E-octies: el shell que recibe Ctrl+C tiene el mismo $$ y $PPID que el de la sesión (un shell en
        // un contenedor gemelo, al que se entró con ssh -tt, o el programa que deja un exec), pero no el testigo del canal.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CtrlCAUnShellConLaMismaIdentidadPeroSinElTestigo_CierraElCanalYElSiguienteNoSeEjecutaDentro(bool isPlazo)
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");
            CanalFalso viejo = fabrica.Ultima!.CanalActual!;
            Peticion gemelo = Nueva("ssh -tt gemelo", timeoutSegundos: isPlazo ? 0.5 : 60);
            sesion.Encolar(gemelo);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(gemelo).Contains("gemelo$ "));

            // Act
            if (!isPlazo)
                _ = gemelo.SolicitarCancelacion();
            await gemelo.Finalizacion.WaitAsync(Limite);
            Peticion siguiente = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = viejo.IdentidadActual.Should().StartWith($"{viejo.Pid} {viejo.PidPadre} ", "el otro shell tiene el mismo $$ y $PPID");
            _ = gemelo.Estado.Should().Be(isPlazo ? EstadoPeticionEnum.TiempoAgotado : EstadoPeticionEnum.Cancelado);
            _ = gemelo.Aviso.Should().Contain("shell interactivo anidado").And.Contain("sustituyó al de la sesión con exec").And.Contain("se cerró el canal del shell");
            _ = viejo.LineasEnShellAjeno.Should().Be(1);
            _ = viejo.IsDispuesto.Should().BeTrue();
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = Texto(siguiente).Should().Be("eco:hola\n");
            _ = siguiente.Aviso.Should().Contain("recreado");
            _ = fabrica.Ultima.CanalActual.Should().NotBeSameAs(viejo);
        }

        // Cada canal guarda su propio testigo: el de un shell recreado (ssh_reset_shell) no es el del anterior.
        [Fact]
        public async Task CadaShellNuevo_GuardaUnTestigoDistinto()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");
            string primero = fabrica.Ultima!.CanalActual!.IdentidadActual;

            // Act
            sesion.SolicitarReset();
            Peticion tras = await EjecutarAsync(sesion, "hola");
            string segundo = fabrica.Ultima.CanalActual!.IdentidadActual;

            // Assert
            _ = tras.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = primero.Split(' ')[2].Should().MatchRegex("^[0-9a-f]{32}$");
            _ = segundo.Split(' ')[2].Should().MatchRegex("^[0-9a-f]{32}$").And.NotBe(primero.Split(' ')[2]);
        }

        // A05 de la auditoría del E-octies: el aviso dice si el servidor confirmó el cierre del canal o no.
        [Fact]
        public async Task CtrlCAUnShellAnidado_ConElCierreSinConfirmar_LoDiceSinAfirmarQueTermino()
        {
            // Arrange
            using ManualResetEventSlim confirmacion = new(false);
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");
            fabrica.Ultima!.CanalActual!.RetenerCierre(confirmacion);
            Peticion anidado = Nueva("sudo -i");
            sesion.Encolar(anidado);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(anidado).Contains("root# "));

            // Act
            try
            {
                _ = anidado.SolicitarCancelacion();
                await anidado.Finalizacion.WaitAsync(Limite);
            }
            finally
            {
                confirmacion.Set();
            }

            // Assert
            _ = anidado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = anidado.Aviso.Should().Contain("se cerró el canal del shell de ssh_exec, aunque el servidor no confirmó el cierre").And.Contain("siga vivo sin terminal")
                .And.NotContain("normalmente termina");
        }

        // EN4 (del S4): sin bash, el shell de login responde al 'exec bash' de la inicialización y sale; el error lo dice en
        // lugar de «se cerró durante la inicialización», y no se guarda: el comando siguiente vuelve a intentarlo.
        [Fact]
        public async Task ServidorSinBash_ElShellSeCierraEnLaInicializacion_ElErrorDiceQueFaltaBashSinCache()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion) { RespuestaSinBash = "-ash: exec: line 1: bash: not found" };
            await using SesionShell sesion = Crear(fabrica);

            // Act
            Peticion primero = await EjecutarAsync(sesion, "hola");
            Peticion segundo = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = primero.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = primero.Aviso.Should().Contain("no tiene bash").And.Contain("«-ash: exec: line 1: bash: not found»").And.Contain("native_ssh lo necesita")
                .And.NotContain("exec bash --noediting");
            _ = segundo.Aviso.Should().Be(primero.Aviso);
            _ = fabrica.Ultima!.ShellsAbiertos.Should().Be(2);
        }

        [Fact]
        public async Task ShellQueSeCierraEnLaInicializacionPorOtraCausa_ElErrorIncluyeLoQueEscribioSinElProtocolo()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion) { RespuestaSinBash = "Cuenta caducada: contacta con el administrador" };
            await using SesionShell sesion = Crear(fabrica);

            // Act
            Peticion primero = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = primero.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = primero.Aviso.Should().Contain("se cerró durante la inicialización").And.Contain("Cuenta caducada: contacta con el administrador»")
                .And.NotContain("exec bash").And.NotContain("no tiene bash");
        }

        // EN4 por la vía exec: el 'bash -c' del comando no existe; 127, nada en stdout y el error del shell de login en stderr.
        [Fact]
        public async Task ServidorSinBash_CanalExec_ElErrorDiceQueFaltaBash()
        {
            // Arrange
            // El guion recibe el script con el 'cd' del principio.
            FabricaFalsa fabrica = new(c => c.EndsWith("\nsin-bash", StringComparison.Ordinal) ? new("ash: bash: not found", 127)
                : c.EndsWith("\notro-127", StringComparison.Ordinal) ? new("ash: rg: not found", 127)
                : Guion(c));
            await using SesionShell sesion = Crear(fabrica);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => sesion.EjecutarExecAsync("sin-bash", TimeSpan.FromSeconds(10), CancellationToken.None))
                .Should().ThrowAsync<NativeSshException>()).Which;
            ResultadoCanalExec otro = await sesion.EjecutarExecAsync("otro-127", TimeSpan.FromSeconds(10), CancellationToken.None);

            // Assert
            _ = error.Message.Should().Contain("no tiene bash").And.Contain("«ash: bash: not found»").And.Contain("native_ssh lo necesita");
            _ = otro.CodigoSalida.Should().Be(127, "otro programa que falta no es falta de bash");
        }

        [Fact]
        public async Task InicializacionSinIdentidadDelShell_FallaConMensajePropioYCierraElCanal()
        {
            // Arrange: sin $$ ni $PPID no se podría detectar un shell anidado tras Ctrl+C.
            FabricaFalsa fabrica = new(Guion) { IsSinIdentidad = true };
            await using SesionShell sesion = Crear(fabrica);

            // Act
            Peticion hola = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = hola.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = hola.Aviso.Should().Contain("identificador de proceso");
            _ = fabrica.Ultima!.CanalActual!.IsDispuesto.Should().BeTrue();
            _ = sesion.IsShellActivo.Should().BeFalse();
        }

        [Fact]
        public async Task Cancelar_Encolada_NoLlegaAlShell()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion colgado = Nueva("colgado");
            Peticion encolada = Nueva("hola");
            sesion.Encolar(colgado);
            sesion.Encolar(encolada);
            // Ejecutando se marca antes de abrir el shell: hay que esperar a que el comando haya llegado.
            await EsperasPrueba.EsperarHastaAsync(() => Texto(colgado).Contains("empezando"));

            // Act
            _ = encolada.SolicitarCancelacion();
            _ = colgado.SolicitarCancelacion();
            await Task.WhenAll(colgado.Finalizacion, encolada.Finalizacion).WaitAsync(Limite);
            _ = await EjecutarAsync(sesion, "despues");

            // Assert
            _ = encolada.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = fabrica.Ultima!.Comandos.Should().Equal(["colgado", "despues"]);
        }

        [Fact]
        public async Task IgnoraCtrlC_CierraSoloElCanalYRecreaElShell()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);

            // Act
            Peticion terco = await EjecutarAsync(sesion, "terco", timeoutSegundos: 0.3);
            Peticion siguiente = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = terco.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            _ = terco.Aviso.Should().Contain("se cerró el canal");
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Aviso.Should().Contain("recreado");
            _ = fabrica.Conexiones.Should().Be(1);
            _ = fabrica.Ultima!.ShellsAbiertos.Should().Be(2);
        }

        [Fact]
        public async Task CierreDelCanalQueElServidorNoConfirma_NoDetieneLaCola()
        {
            // Arrange: cerrar el canal no vuelve hasta que el servidor confirma, y no confirma (su bash no muere al perder
            // el terminal); el proceso en curso ignora Ctrl+C, así que hay que cerrar el canal.
            using ManualResetEventSlim confirmacion = new(false);
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");
            fabrica.Ultima!.CanalActual!.RetenerCierre(confirmacion);
            Stopwatch reloj = Stopwatch.StartNew();
            Peticion terco;
            Peticion siguiente;

            try
            {
                // Act
                terco = await EjecutarAsync(sesion, "terco", timeoutSegundos: 1);
                siguiente = await EjecutarAsync(sesion, "dos");
            }
            finally
            {
                confirmacion.Set();
            }

            // Assert: quien cierra espera como mucho un segundo y la cola sigue con un shell nuevo.
            _ = terco.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = Texto(siguiente).Should().Be("eco:dos\n");
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
            _ = fabrica.Ultima.ShellsAbiertos.Should().Be(2);
        }

        [Fact]
        public async Task EntradaInteractiva()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion pregunta = Nueva("pregunta");
            sesion.Encolar(pregunta);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(pregunta).Contains("¿Nombre?"));

            // Act
            _ = sesion.EnviarEntrada(pregunta, "ana", isEnviarEnter: true).Should().BeTrue();
            await pregunta.Finalizacion.WaitAsync(Limite);

            // Assert
            _ = pregunta.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = Texto(pregunta).Should().Contain("recibido:ana");
        }

        [Fact]
        public async Task Entrada_EnPeticionNoActiva_SeRechaza()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion terminada = await EjecutarAsync(sesion, "hola");

            // Act & Assert
            _ = sesion.EnviarEntrada(terminada, "x", isEnviarEnter: true).Should().BeFalse();
            _ = sesion.EnviarEntrada(Nueva("nunca-encolada"), "x", isEnviarEnter: true).Should().BeFalse();
        }

        [Fact]
        public async Task Sigint_EnviaCtrlCAlCanal()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion colgado = Nueva("colgado");
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(colgado).Contains("empezando"));

            // Act
            _ = colgado.SolicitarCancelacion();
            await colgado.Finalizacion.WaitAsync(Limite);

            // Assert
            _ = fabrica.Ultima!.SigintsRecibidos.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task EntradaConElCanalDelShellYaCerrado_SeRechazaConMensajeClaroSinEscribirEnEl()
        {
            // Arrange: el comando espera entrada y el servidor cierra el canal (exit); el cierre se detiene justo después de
            // observarse, con la petición aún en ejecución.
            using LoggerPrueba logger = new() { EventoBloqueante = 43 };
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica, logger: logger);
            Peticion pregunta = Nueva("pregunta");
            sesion.Encolar(pregunta);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(pregunta).Contains("Nombre", StringComparison.Ordinal));
            CanalFalso canal = fabrica.Ultima!.CanalActual!;

            // Act
            canal.Cerrar();
            await logger.Bloqueado.WaitAsync(Limite);
            Action enviar = () => sesion.EnviarEntrada(pregunta, "hola", isEnviarEnter: true);
            NativeSshException error = enviar.Should().Throw<NativeSshException>().Which;
            EstadoPeticionEnum estadoAlRechazar = pregunta.Estado;
            logger.Liberar();
            await pregunta.Finalizacion.WaitAsync(Limite);

            // Assert
            _ = logger.IsEsperaAgotada.Should().BeFalse();
            _ = estadoAlRechazar.Should().Be(EstadoPeticionEnum.Ejecutando);
            _ = error.Message.Should().Contain("canal del shell se cerró").And.Contain("no se envió");
            _ = canal.EscriturasTrasCerrar.Should().Be(0);
            _ = pregunta.Estado.Should().Be(EstadoPeticionEnum.ShellPerdido);
        }

        [Fact]
        public async Task EntradaMientrasLaInterrupcionCierraElCanal_SeRechazaSinAtribuirElCierreAlServidor()
        {
            // Arrange: el proceso ignora Ctrl+C y la interrupción escala al cierre del canal, que se detiene recién observado.
            using LoggerPrueba logger = new() { EventoBloqueante = 43 };
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica, logger: logger);
            Peticion terco = Nueva("terco", timeoutSegundos: 1);
            sesion.Encolar(terco);

            // Act
            await logger.Bloqueado.WaitAsync(Limite);
            bool isEnviada = sesion.EnviarEntrada(terco, "hola", isEnviarEnter: true);
            logger.Liberar();
            await terco.Finalizacion.WaitAsync(Limite);

            // Assert: como en cualquier momento de la interrupción, la entrada se rechaza sin más.
            _ = logger.IsEsperaAgotada.Should().BeFalse();
            _ = isEnviada.Should().BeFalse();
            _ = terco.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
        }

        [Fact]
        public async Task EntradaTardia_NoPasaAComandoPosterior()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion primera = await EjecutarAsync(sesion, "hola");

            // Act
            bool enviada = sesion.EnviarEntrada(primera, "entrada_tardia", isEnviarEnter: true);
            Peticion segunda = Nueva("pregunta");
            sesion.Encolar(segunda);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(segunda).Contains("¿Nombre?"));

            // Assert
            _ = enviada.Should().BeFalse();
            _ = segunda.Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
            _ = Texto(segunda).Should().NotContain("entrada_tardia");

            _ = sesion.EnviarEntrada(segunda, "respuesta_valida", isEnviarEnter: true).Should().BeTrue();
            await segunda.Finalizacion.WaitAsync(Limite);
            _ = Texto(segunda).Should().Contain("recibido:respuesta_valida");
        }

        [Fact]
        public async Task Exit_PierdeElShellYElSiguienteLoRecrea()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);

            // Act
            Peticion salir = await EjecutarAsync(sesion, "exit");
            Peticion siguiente = await EjecutarAsync(sesion, "hola");

            // Assert: no se sabe si el comando llegó a ejecutarse (el canal pudo cerrarse antes), así que no lo afirma.
            _ = salir.Estado.Should().Be(EstadoPeticionEnum.ShellPerdido);
            _ = salir.Aviso.Should().Contain("antes de que terminara el comando").And.NotContain("durante el comando");
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Aviso.Should().Contain("recreado");
            _ = fabrica.Conexiones.Should().Be(1);
            _ = fabrica.Ultima!.ShellsAbiertos.Should().Be(2);
        }

        [Fact]
        public async Task ConexionCaida_Reconecta()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");

            // Act
            fabrica.Ultima!.Caer();
            await EsperasPrueba.EsperarHastaAsync(() => !sesion.IsShellActivo);
            Peticion siguiente = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = fabrica.Conexiones.Should().Be(2);
            _ = sesion.ConexionesAbiertas.Should().Be(2);
        }

        [Fact]
        public async Task CaidaDeLaConexionConComandoEnCurso_TerminaComoShellPerdidoYLoDice()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion colgado = Nueva("colgado");
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(colgado).Contains("empezando"));

            // Act: el adaptador cierra el canal en cuanto SSH.NET avisa de la caída (Leer devuelve 0).
            fabrica.Ultima!.Caer();
            await colgado.Finalizacion.WaitAsync(Limite);
            Peticion siguiente = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = colgado.Estado.Should().Be(EstadoPeticionEnum.ShellPerdido);
            _ = colgado.Aviso.Should().Contain("Se perdió la conexión SSH").And.NotContain("durante el comando");
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Aviso.Should().Contain("recreado");
            _ = fabrica.Conexiones.Should().Be(2);
        }

        [Fact]
        public async Task ReabrirElShellSobreUnaConexionQueCae_NoDevuelveElErrorCrudoDeSshNet()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");
            sesion.SolicitarReset();
            fabrica.Ultima!.CaerAlAbrirShell();

            // Act
            Peticion fallido = await EjecutarAsync(sesion, "dos");
            Peticion siguiente = await EjecutarAsync(sesion, "tres");

            // Assert
            _ = fallido.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = fallido.Aviso.Should().Contain("Se perdió la conexión SSH").And.Contain("no llegó a enviarse").And.NotContain("waitHandle").And.NotContain("Value cannot be null");
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = Texto(siguiente).Should().Be("eco:tres\n");
            _ = fabrica.Conexiones.Should().Be(2);
        }

        [Fact]
        public async Task InicializacionQueNoTerminaASuTiempo_LaPistaNombraLasCausasPosibles()
        {
            // Arrange: un login que tarda más que el plazo de inicialización y se come lo que recibe mientras tanto.
            FabricaFalsa fabrica = new(Guion, login: LoginFalsoEnum.ComeInicializacion, retrasoLogin: TimeSpan.FromSeconds(5));
            await using SesionShell sesion = Crear(fabrica, Opciones(timeoutInicializacionSegundos: 1));

            // Act
            Peticion peticion = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = peticion.Aviso.Should().Contain("no completó la inicialización en 1 s").And.Contain("login").And.Contain("no respondió");
        }

        [Fact]
        public async Task CaidaQueNoCierraElCanal_NoMuestraUnShellActivoSinConexion()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");

            // Act: el canal sigue abierto un instante tras la caída (o nunca se cierra, como en SSH.NET sin adaptar).
            fabrica.Ultima!.CaerSinCerrarCanal();

            // Assert
            _ = sesion.IsConectada.Should().BeFalse();
            _ = sesion.IsShellActivo.Should().BeFalse();
        }

        [Fact]
        public async Task TiempoAgotado_ConElCanalCaidoSinAdmitirEscrituras_NoAfirmaQueSeEnvioCtrlCNiQueLaConexionSigueAbierta()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion colgado = Nueva("colgado", timeoutSegundos: 1);
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(colgado).Contains("empezando"));

            // Act
            fabrica.Ultima!.CaerSinCerrarCanal();
            await colgado.Finalizacion.WaitAsync(Limite);

            // Assert
            _ = colgado.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            _ = colgado.Aviso.Should().Contain("no se pudo enviar Ctrl+C").And.Contain("se perdió la conexión SSH")
                .And.NotContain("se envió Ctrl+C").And.NotContain("no respondió a Ctrl+C").And.NotContain("sigue abierta");
        }

        [Fact]
        public async Task TiempoAgotado_SinRespuestaACtrlCYConLaConexionCaida_NoAfirmaQueLaConexionSigueAbierta()
        {
            // Arrange: 'terco' ignora Ctrl+C y la conexión cae mientras se espera su respuesta (ya enviada la línea de
            // recuperación, que va en otra escritura tras una pausa).
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion terco = Nueva("terco", timeoutSegundos: 0.3);
            sesion.Encolar(terco);
            await EsperasPrueba.EsperarHastaAsync(() => fabrica.Ultima?.SigintsRecibidos > 0);
            CanalFalso canal = fabrica.Ultima!.CanalActual!;
            int lineas = canal.LineasRecibidas;

            // Act
            await EsperasPrueba.EsperarHastaAsync(() => canal.LineasRecibidas >= lineas + 2);
            fabrica.Ultima.CaerSinCerrarCanal();
            await terco.Finalizacion.WaitAsync(Limite);

            // Assert
            _ = terco.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            _ = terco.Aviso.Should().Contain("no respondió a Ctrl+C").And.Contain("se perdió la conexión SSH").And.NotContain("sigue abierta");
        }

        [Fact]
        public async Task TiempoAgotado_ConLaConexionCaidaEntreCtrlCYLaLineaDeRecuperacion_DiceQueNoPudoEnviarLaLinea()
        {
            // Arrange: la conexión cae justo después de Ctrl+C, durante la pausa anterior a la línea de recuperación.
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion terco = Nueva("terco", timeoutSegundos: 0.3);
            sesion.Encolar(terco);

            // Act
            await EsperasPrueba.EsperarHastaAsync(() => fabrica.Ultima?.SigintsRecibidos > 0);
            fabrica.Ultima!.CaerSinCerrarCanal();
            await terco.Finalizacion.WaitAsync(Limite);

            // Assert
            _ = terco.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            _ = terco.Aviso.Should().Contain("se envió Ctrl+C").And.Contain("Se perdió la conexión SSH antes de enviar la línea").And.NotContain("no respondió");
        }

        [Fact]
        public async Task ConexionSustituidaPorUnaLectura_EnLaVentanaAntesDeDescartarLaCaida_ElComandoNoUsaElShellViejo()
        {
            // Arrange: shell vivo en la primera conexión, que cae sin cerrar su canal (SSH.NET antes de enterarse).
            using LoggerPrueba logger = new() { EventoBloqueante = 32 };
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica, logger: logger);
            _ = await EjecutarAsync(sesion, "a");
            fabrica.Ultima!.CaerSinCerrarCanal();

            // Act: una lectura reconecta y se detiene justo antes de descartar la conexión caída, con _conexion ya
            // sustituida y el canal viejo aún en _canal; entonces llega un ssh_exec.
            Task<ResultadoCanalExec> lectura = Task.Run(() => sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(10), CancellationToken.None));
            await logger.Bloqueado.WaitAsync(Limite);
            Peticion comando = await EjecutarAsync(sesion, "b");
            logger.Liberar();
            ResultadoCanalExec resultado = await lectura.WaitAsync(Limite);

            // Assert: el canal viejo no es de la conexión vigente, así que se abre un shell nuevo sobre ella.
            _ = logger.IsEsperaAgotada.Should().BeFalse();
            _ = comando.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = Texto(comando).Should().Be("eco:b\n");
            _ = comando.Aviso.Should().Contain("recreado");
            _ = resultado.CodigoSalida.Should().Be(0);
            _ = fabrica.Conexiones.Should().Be(2);
            _ = fabrica.Ultima!.ShellsAbiertos.Should().Be(1);
        }

        [Fact]
        public async Task AperturaDelShellSinRespuesta_ElComandoVenceASuTiempoYElSiguienteEsperaEsaMismaApertura()
        {
            // Arrange: la apertura del canal del shell no responde ni atiende ningún token (SSH.NET con el servidor
            // congelado: solo termina cuando responde o cae la conexión).
            using SemaphoreSlim apertura = new(0);
            FabricaFalsa fabrica = new(Guion) { BloqueoAperturaShell = apertura };
            await using SesionShell sesion = Crear(fabrica);
            Stopwatch reloj = Stopwatch.StartNew();
            Peticion primero;
            TimeSpan duracion;
            Peticion segundo;

            try
            {
                // Act
                primero = await EjecutarAsync(sesion, "uno", timeoutSegundos: 1);
                duracion = reloj.Elapsed;
                segundo = Nueva("dos");
                sesion.Encolar(segundo);
                await EsperasPrueba.EsperarHastaAsync(() => segundo.Estado == EstadoPeticionEnum.Ejecutando);
                await Task.Delay(200);
            }
            finally
            {
                _ = apertura.Release(2);
            }

            await segundo.Finalizacion.WaitAsync(Limite);

            // Assert: el primero vence sin llegar al shell y lo dice; el segundo usa el canal de aquella apertura.
            _ = primero.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            _ = primero.Aviso.Should().Contain("no llegó a enviarse").And.Contain("apertura del shell");
            _ = duracion.Should().BeLessThan(TimeSpan.FromSeconds(3));
            _ = segundo.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = Texto(segundo).Should().Be("eco:dos\n");
            _ = fabrica.Ultima!.AperturasShell.Should().Be(1);
            _ = fabrica.Ultima.Comandos.Should().Equal(["dos"]);
        }

        [Fact]
        public async Task AperturaDelShellAbandonadaQueTerminaDespues_SuCanalSeCierraAlCerrarLaSesion()
        {
            // Arrange: el comando vence mientras se abre el shell; la apertura termina después y nadie la usa.
            using SemaphoreSlim apertura = new(0);
            FabricaFalsa fabrica = new(Guion) { BloqueoAperturaShell = apertura };
            SesionShell sesion = Crear(fabrica);
            Peticion comando;
            try
            {
                comando = await EjecutarAsync(sesion, "uno", timeoutSegundos: 1);
            }
            finally
            {
                _ = apertura.Release();
            }

            await EsperasPrueba.EsperarHastaAsync(() => fabrica.Ultima!.CanalActual is not null);
            CanalFalso canal = fabrica.Ultima!.CanalActual!;

            // Act
            await sesion.DisposeAsync().AsTask().WaitAsync(Limite);

            // Assert
            _ = comando.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            await EsperasPrueba.EsperarHastaAsync(() => canal.IsDispuesto);
        }

        [Fact]
        public async Task Dispose_ConElBucleColgadoEnUnaEscritura_LiberaLaConexionYElComandoTerminaCancelado()
        {
            // Arrange: escribir en el shell no vuelve hasta que se libera la conexión (SSH.NET con la ventana del servidor
            // agotada); el bucle se queda dentro al enviar el comando.
            FabricaFalsa fabrica = new(Guion);
            SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");
            fabrica.Ultima!.ColgarEscrituras();
            Peticion colgado = Nueva("dos");
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => fabrica.Ultima.CanalActual!.EscriturasColgadas == 1);
            Stopwatch reloj = Stopwatch.StartNew();

            // Act
            await sesion.DisposeAsync().AsTask().WaitAsync(Limite);

            // Assert
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
            _ = colgado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = fabrica.Ultima.IsConectada.Should().BeFalse();
        }

        [Fact]
        public async Task EnviarEntrada_ConElBucleColgadoEnUnaEscritura_NoSeCuelgaYDiceComoLiberarlo()
        {
            // Arrange: el bucle se queda dentro de la escritura del comando, con _candadoEscritura tomado.
            FabricaFalsa fabrica = new(Guion);
            SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");
            fabrica.Ultima!.ColgarEscrituras();
            Peticion colgado = Nueva("dos");
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => fabrica.Ultima.CanalActual!.EscriturasColgadas == 1);
            Stopwatch reloj = Stopwatch.StartNew();

            // Act
            Func<Task> enviar = () => Task.Run(() => sesion.EnviarEntrada(colgado, "x", isEnviarEnter: true)).WaitAsync(Limite);

            // Assert: la entrada se rechaza al vencer su espera, sin quedarse esperando a la escritura del bucle.
            _ = (await enviar.Should().ThrowAsync<NativeSshException>()).Which.Message.Should().Contain("ssh_disconnect").And.Contain("no se envió");
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
            await sesion.DisposeAsync().AsTask().WaitAsync(Limite);
        }

        [Fact]
        public async Task EnviarEntrada_EsperandoAlCandadoDeEscritura_AtiendeSuTokenYElCierreDeLaSesion()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");
            fabrica.Ultima!.ColgarEscrituras();
            Peticion colgado = Nueva("dos");
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => fabrica.Ultima.CanalActual!.EscriturasColgadas == 1);
            using CancellationTokenSource cancelacion = new(TimeSpan.FromMilliseconds(200));
            Stopwatch reloj = Stopwatch.StartNew();

            // Act
            Func<Task> cancelada = () => Task.Run(() => sesion.EnviarEntrada(colgado, "x", isEnviarEnter: true, cancelacion.Token)).WaitAsync(Limite);
            Task<bool> durante = Task.Run(() => sesion.EnviarEntrada(colgado, "y", isEnviarEnter: true));
            _ = await cancelada.Should().ThrowAsync<OperationCanceledException>();
            TimeSpan tiempoCancelada = reloj.Elapsed;
            await sesion.DisposeAsync().AsTask().WaitAsync(Limite);
            Func<Task> cerrada = () => durante.WaitAsync(Limite);

            // Assert: ninguna se queda esperando a que el servidor acepte la escritura del bucle.
            _ = tiempoCancelada.Should().BeLessThan(TimeSpan.FromSeconds(1.5));
            _ = (await cerrada.Should().ThrowAsync<NativeSshException>()).Which.Message.Should().Contain("cerr");
        }

        [Fact]
        public async Task Cancelar_ConLaEntradaPropiaColgadaEnLaEscritura_VuelveSinEnviarCtrlCYLaEntradaDiceQuePasoAlCerrar()
        {
            // Arrange: el comando sigue en ejecución y su propia entrada se queda dentro de la escritura (SSH.NET con el envío
            // TCP lleno o la ventana del canal agotada), con _candadoEscritura tomado.
            FabricaFalsa fabrica = new(Guion);
            SesionShell sesion = Crear(fabrica);
            Peticion colgado = Nueva("colgado", timeoutSegundos: 60);
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(colgado).Contains("empezando"));
            fabrica.Ultima!.ColgarEscrituras();
            CanalFalso canal = fabrica.Ultima.CanalActual!;
            Task<bool> entrada = Task.Run(() => sesion.EnviarEntrada(colgado, "datos", isEnviarEnter: true));
            await EsperasPrueba.EsperarHastaAsync(() => canal.EscriturasColgadas == 1);
            Stopwatch reloj = Stopwatch.StartNew();

            // Act: ssh_cancel.
            Task<bool> solicitud = Task.Run(colgado.SolicitarCancelacion);
            bool isSolicitudVuelta = await Task.WhenAny(solicitud, Task.Delay(TimeSpan.FromSeconds(1))) == solicitud;
            bool isTerminado = await Task.WhenAny(colgado.Finalizacion, Task.Delay(Limite)) == colgado.Finalizacion;
            TimeSpan duracion = reloj.Elapsed;
            EstadoPeticionEnum estado = colgado.Estado;
            string? aviso = colgado.Aviso;
            int escriturasColgadas = canal.EscriturasColgadas;
            await sesion.DisposeAsync().AsTask().WaitAsync(Limite);
            Func<Task> entradaAlCerrar = () => entrada.WaitAsync(Limite);

            // Assert: quien cancela vuelve al momento; el comando termina al vencer la espera acotada al candado, sin
            // intentar escribir Ctrl+C y diciendo que el proceso sigue; la entrada falla con su causa real al cerrar.
            _ = isSolicitudVuelta.Should().BeTrue();
            _ = isTerminado.Should().BeTrue();
            _ = duracion.Should().BeLessThan(TimeSpan.FromSeconds(4));
            _ = estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = aviso.Should().Contain("no se envió Ctrl+C").And.Contain("sigue en ejecución").And.Contain("ssh_disconnect");
            _ = escriturasColgadas.Should().Be(1);
            _ = canal.SigintsRecibidos.Should().Be(0);
            _ = (await entradaAlCerrar.Should().ThrowAsync<NativeSshException>()).Which.Message.Should().Contain("ssh_disconnect").And.Contain("parte de la entrada").And.NotContain("no admite");
        }

        [Fact]
        public async Task Cancelar_ConLaEntradaPropiaAtascada_ElSiguienteComandoAbreUnShellNuevoSinEsperarlaYElViejoSeCierraAlTerminarElla()
        {
            // Arrange: la entrada del comando se queda dentro de la escritura (el servidor deja de leer un rato) y después
            // el servidor la acepta. ssh_cancel vuelve sin poder enviar Ctrl+C.
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");
            Peticion colgado = Nueva("colgado", timeoutSegundos: 60);
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(colgado).Contains("empezando"));
            CanalFalso viejo = fabrica.Ultima!.CanalActual!;
            using ManualResetEventSlim retencion = new(false);
            viejo.RetenerEscrituras(retencion);
            Task<bool> entrada = Task.Run(() => sesion.EnviarEntrada(colgado, "datos", isEnviarEnter: true));
            await EsperasPrueba.EsperarHastaAsync(() => viejo.EscriturasRetenidas == 1);
            _ = colgado.SolicitarCancelacion();
            await colgado.Finalizacion.WaitAsync(Limite);

            // Act: el comando siguiente, sin ssh_disconnect y con la entrada aún atascada.
            Peticion siguiente = Nueva("siguiente");
            sesion.Encolar(siguiente);
            bool isSiguienteTerminado = await Task.WhenAny(siguiente.Finalizacion, Task.Delay(Limite)) == siguiente.Finalizacion;
            bool isViejoDispuestoConLaEntradaAtascada = viejo.IsDispuesto;
            retencion.Set();
            bool isEntradaEnviada = await entrada.WaitAsync(Limite);
            await EsperasPrueba.EsperarHastaAsync(() => viejo.IsDispuesto);

            // Assert: el comando siguiente va a un shell nuevo de la misma conexión, no a la entrada del proceso cancelado;
            // el canal viejo no se cierra con la escritura dentro (EQ4), sino cuando esta termina.
            _ = colgado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = colgado.Aviso.Should().Contain("no se envió Ctrl+C").And.Contain("shell nuevo").And.Contain("se perderán").And.Contain("ssh_disconnect")
                .And.NotContain("esperan su turno");
            _ = isSiguienteTerminado.Should().BeTrue();
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = Texto(siguiente).Should().Be("eco:siguiente\n");
            _ = siguiente.Aviso.Should().Contain("recreado");
            _ = fabrica.Ultima.CanalActual.Should().NotBeSameAs(viejo);
            _ = fabrica.Ultima.ShellsAbiertos.Should().Be(2);
            _ = fabrica.Conexiones.Should().Be(1);
            _ = viejo.SigintsRecibidos.Should().Be(0);
            _ = isViejoDispuestoConLaEntradaAtascada.Should().BeFalse();
            _ = viejo.CierresConEscrituraEnCurso.Should().Be(0);
            _ = isEntradaEnviada.Should().BeTrue();
        }

        [Fact]
        public async Task CanalAbandonadoConLaEntradaColgadaHastaCerrarLaSesion_SeCierraAlLiberarseLaConexionYNoAntes()
        {
            // Arrange: la entrada no termina hasta que se libera la conexión (ssh_disconnect).
            FabricaFalsa fabrica = new(Guion);
            SesionShell sesion = Crear(fabrica);
            Peticion colgado = Nueva("colgado", timeoutSegundos: 60);
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(colgado).Contains("empezando"));
            fabrica.Ultima!.ColgarEscrituras();
            CanalFalso viejo = fabrica.Ultima.CanalActual!;
            Task<bool> entrada = Task.Run(() => sesion.EnviarEntrada(colgado, "datos", isEnviarEnter: true));
            await EsperasPrueba.EsperarHastaAsync(() => viejo.EscriturasColgadas == 1);
            _ = colgado.SolicitarCancelacion();
            await colgado.Finalizacion.WaitAsync(Limite);
            await Task.Delay(300);
            bool isDispuestoAntesDeCerrar = viejo.IsDispuesto;

            // Act
            await sesion.DisposeAsync().AsTask().WaitAsync(Limite);
            Func<Task> entradaAlCerrar = () => entrada.WaitAsync(Limite);
            NativeSshException fallo = (await entradaAlCerrar.Should().ThrowAsync<NativeSshException>()).Which;
            await EsperasPrueba.EsperarHastaAsync(() => viejo.IsDispuesto);

            // Assert: la entrada falla al cerrarse la conexión y dice en qué estado terminó ya el comando.
            _ = isDispuestoAntesDeCerrar.Should().BeFalse();
            _ = viejo.CierresConEscrituraEnCurso.Should().Be(0);
            _ = fallo.Message.Should().Contain("parte de la entrada").And.Contain("ya había terminado como cancelado").And.NotContain("termina como cancelado");
        }

        [Fact]
        public async Task CanalAbandonado_CuentaParaElCupoDeCanalesExecHastaQueSeCierra()
        {
            // Arrange: un shell abandonado con su entrada retenida y los canales exec que se abran, sin terminar.
            using SemaphoreSlim bloqueo = new(0);
            FabricaFalsa fabrica = new(Guion) { BloqueoExec = bloqueo };
            await using SesionShell sesion = Crear(fabrica);
            Peticion colgado = Nueva("colgado", timeoutSegundos: 60);
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(colgado).Contains("empezando"));
            CanalFalso viejo = fabrica.Ultima!.CanalActual!;
            using ManualResetEventSlim retencion = new(false);
            viejo.RetenerEscrituras(retencion);
            Task<bool> entrada = Task.Run(() => sesion.EnviarEntrada(colgado, "datos", isEnviarEnter: true));
            await EsperasPrueba.EsperarHastaAsync(() => viejo.EscriturasRetenidas == 1);
            _ = colgado.SolicitarCancelacion();
            await colgado.Finalizacion.WaitAsync(Limite);
            Task<ResultadoCanalExec>[] lecturas = [.. Enumerable.Range(0, SesionShell.MaxCanalesExecSimultaneos - 1).Select(_ => sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(30), CancellationToken.None))];
            await EsperasPrueba.EsperarHastaAsync(() => fabrica.Ultima.ExecActivos == SesionShell.MaxCanalesExecSimultaneos - 1);

            // Act: una lectura más no obtiene cupo mientras el shell abandonado siga abierto; al cerrarse, sí.
            NativeSshException error = (await FluentActions.Awaiting(() => sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(1), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)))
                .Should().ThrowAsync<NativeSshException>()).Which;
            string? avisoSesionConElAbandonado = sesion.AvisoEscrituraBloqueada;
            int abandonados = sesion.ShellsAbandonados;
            retencion.Set();
            _ = await entrada.WaitAsync(Limite);
            await EsperasPrueba.EsperarHastaAsync(() => viejo.IsDispuesto);
            await EsperasPrueba.EsperarHastaAsync(() => sesion.ShellsAbandonados == 0);
            _ = bloqueo.Release(SesionShell.MaxCanalesExecSimultaneos + 1);
            _ = await Task.WhenAll(lecturas).WaitAsync(Limite);
            ResultadoCanalExec otra = await sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(5), CancellationToken.None).WaitAsync(Limite);

            // Assert
            _ = error.Message.Should().Contain("sin llegar a obtener canal").And.Contain("shell abandonado");
            _ = abandonados.Should().Be(1);
            _ = avisoSesionConElAbandonado.Should().Contain("shell anterior").And.Contain("ssh_disconnect");
            _ = sesion.AvisoEscrituraBloqueada.Should().BeNull();
            _ = otra.CodigoSalida.Should().Be(0);
            _ = fabrica.Ultima.MaxExecSimultaneos.Should().Be(SesionShell.MaxCanalesExecSimultaneos - 1);
        }

        [Fact]
        public async Task RechazoDelServidorConUnShellAbandonado_ElShellYLasLecturasLoNombranYNoMandanEsperarALecturasQueNoHay()
        {
            // Arrange: Z01 de la auditoría del E-septies. Un shell abandonado (entrada retenida) ocupa una sesión y el
            // servidor rechaza los canales nuevos (MaxSessions), sin ninguna lectura en curso.
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion colgado = Nueva("colgado", timeoutSegundos: 60);
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(colgado).Contains("empezando"));
            CanalFalso viejo = fabrica.Ultima!.CanalActual!;
            using ManualResetEventSlim retencion = new(false);
            viejo.RetenerEscrituras(retencion);
            Task<bool> entrada = Task.Run(() => sesion.EnviarEntrada(colgado, "datos", isEnviarEnter: true));
            await EsperasPrueba.EsperarHastaAsync(() => viejo.EscriturasRetenidas == 1);
            _ = colgado.SolicitarCancelacion();
            await colgado.Finalizacion.WaitAsync(Limite);
            fabrica.Ultima.IsRechazandoAperturas = true;

            // Act
            Peticion siguiente = await EjecutarAsync(sesion, "siguiente");
            NativeSshException lectura = (await FluentActions.Awaiting(() => sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(5), CancellationToken.None))
                .Should().ThrowAsync<NativeSshException>()).Which;
            int lecturasActivas = sesion.CanalesExecActivos;
            retencion.Set();
            _ = await entrada.WaitAsync(Limite);
            await EsperasPrueba.EsperarHastaAsync(() => sesion.ShellsAbandonados == 0);

            // Assert: nombran el shell abandonado, dicen que reintentar no sirve y que ssh_disconnect lo libera; no piden
            // esperar a lecturas que no existen ni cuentan como lectura la propia operación rechazada.
            _ = lecturasActivas.Should().Be(0);
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = siguiente.Aviso.Should().Contain("MaxSessions").And.Contain("1 shell(s) abandonado(s)").And.Contain("reintentar no sirve").And.Contain("ssh_disconnect")
                .And.NotContain("lecturas en curso").And.NotContain("herramientas de lectura (en curso");
            _ = lectura.Message.Should().Contain("MaxSessions").And.Contain("1 shell(s) abandonado(s)").And.Contain("reintentar no sirve").And.Contain("ssh_disconnect")
                .And.NotContain("herramientas de lectura (en curso").And.NotContain("sigue disponible");
        }

        [Fact]
        public async Task RechazoDelServidorSinShellsAbandonados_SoloNombraComoCausaLasLecturasYLosCierresQueHay()
        {
            // Arrange: una lectura en curso retenida y el servidor rechazando canales nuevos.
            using SemaphoreSlim bloqueo = new(0);
            using ManualResetEventSlim confirmacion = new(false);
            FabricaFalsa fabrica = new(Guion) { BloqueoExec = bloqueo };
            await using SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");
            Task<ResultadoCanalExec> enCurso = sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(30), CancellationToken.None);
            await EsperasPrueba.EsperarHastaAsync(() => fabrica.Ultima!.ExecActivos == 1);
            fabrica.Ultima!.IsRechazandoAperturas = true;
            sesion.SolicitarReset();

            // Act: con la lectura en curso; sin nada; y con un shell anterior cuyo cierre el servidor no confirma.
            Peticion conLectura = await EjecutarAsync(sesion, "uno");
            NativeSshException lecturaConLectura = (await FluentActions.Awaiting(() => sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(5), CancellationToken.None))
                .Should().ThrowAsync<NativeSshException>()).Which;
            _ = bloqueo.Release();
            _ = await enCurso.WaitAsync(Limite);
            Peticion sinNada = await EjecutarAsync(sesion, "dos");
            fabrica.Ultima.IsRechazandoAperturas = false;
            _ = await EjecutarAsync(sesion, "tres");
            fabrica.Ultima.CanalActual!.RetenerCierre(confirmacion);
            fabrica.Ultima.IsRechazandoAperturas = true;
            Peticion conCierrePendiente;
            try
            {
                sesion.SolicitarReset();
                conCierrePendiente = await EjecutarAsync(sesion, "cuatro");
            }
            finally
            {
                confirmacion.Set();
            }

            // Assert
            _ = conLectura.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = conLectura.Aviso.Should().Contain("1 canal(es) de las herramientas de lectura").And.Contain("cuando terminen las lecturas en curso").And.NotContain("abandonado");
            _ = lecturaConLectura.Message.Should().Contain("1 canal(es) de las herramientas de lectura").And.Contain("reintenta en unos segundos").And.NotContain("abandonado");
            _ = sinNada.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = sinNada.Aviso.Should().Contain("no hay en esta conexión ningún shell ni lecturas en curso").And.Contain("ssh_disconnect").And.NotContain("cuando terminen");
            _ = conCierrePendiente.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = conCierrePendiente.Aviso.Should().Contain("1 shell(s) anterior(es) cuyo cierre el servidor aún no ha confirmado").And.NotContain("cuando terminen");
        }

        [Fact]
        public async Task EntradaConElCanalCerradoPorElServidorSinQueLaLecturaLoVea_DiceQueElCanalDejoDeAdmitirEscrituras()
        {
            // Arrange: el servidor cerró el canal y la conexión sigue viva, pero la lectura aún no ha visto el cierre.
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion pregunta = Nueva("pregunta");
            sesion.Encolar(pregunta);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(pregunta).Contains("¿Nombre?"));
            fabrica.Ultima!.CanalActual!.RechazarEscrituras();

            // Act
            Action enviar = () => sesion.EnviarEntrada(pregunta, "ana", isEnviarEnter: true);

            // Assert
            _ = enviar.Should().Throw<NativeSshException>().Which.Message.Should().Contain("dejó de admitir escrituras").And.Contain("parte de la entrada")
                .And.NotContain("perdió la conexión");
        }

        [Fact]
        public async Task EntradaColgadaCuyoComandoTerminaComoShellPerdido_AlCerrarLaSesionNoDiceQueTerminaComoCancelado()
        {
            // Arrange: la entrada se queda dentro de la escritura y, mientras tanto, el servidor cierra el canal (el comando
            // termina como shell perdido); después, ssh_disconnect.
            FabricaFalsa fabrica = new(Guion);
            SesionShell sesion = Crear(fabrica);
            Peticion colgado = Nueva("colgado", timeoutSegundos: 60);
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(colgado).Contains("empezando"));
            fabrica.Ultima!.ColgarEscrituras();
            CanalFalso canal = fabrica.Ultima.CanalActual!;
            Task<bool> entrada = Task.Run(() => sesion.EnviarEntrada(colgado, "datos", isEnviarEnter: true));
            await EsperasPrueba.EsperarHastaAsync(() => canal.EscriturasColgadas == 1);
            canal.Cerrar();
            await colgado.Finalizacion.WaitAsync(Limite);

            // Act
            await sesion.DisposeAsync().AsTask().WaitAsync(Limite);
            Func<Task> entradaAlCerrar = () => entrada.WaitAsync(Limite);

            // Assert
            _ = colgado.Estado.Should().Be(EstadoPeticionEnum.ShellPerdido);
            _ = (await entradaAlCerrar.Should().ThrowAsync<NativeSshException>()).Which.Message.Should().Contain("ya había terminado como shell perdido")
                .And.NotContain("termina como cancelado");
        }

        [Fact]
        public async Task Cancelar_EnviaCtrlCEnSuPropiaEscrituraYDespuesLaLineaDeRecuperacion()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion colgado = Nueva("colgado", timeoutSegundos: 60);
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(colgado).Contains("empezando"));
            CanalFalso canal = fabrica.Ultima!.CanalActual!;

            // Act
            _ = colgado.SolicitarCancelacion();
            await colgado.Finalizacion.WaitAsync(Limite);
            Peticion siguiente = await EjecutarAsync(sesion, "siguiente");

            // Assert: el shell se conserva (la línea de recuperación llegó al shell, no al proceso).
            _ = colgado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = colgado.Aviso.Should().Be("Cancelado: se envió Ctrl+C al proceso.");
            _ = canal.SigintsRecibidos.Should().Be(1);
            _ = canal.CtrlCConOtrosBytes.Should().Be(0);
            _ = Texto(siguiente).Should().Be("eco:siguiente\n");
            _ = siguiente.Aviso.Should().BeNull();
            _ = fabrica.Ultima.ShellsAbiertos.Should().Be(1);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task Cancelar_ProcesoQueSeTragaLasLineasTrasCtrlC_ComoSudo_ConservaElShell(int lineasTragadas)
        {
            // Arrange: como 'sudo <cmd>' con use_pty, el proceso se traga lo que llega mientras muere (con 2 líneas, también
            // la primera línea de recuperación entera: hace falta reenviarla).
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion colgado = Nueva("colgado", timeoutSegundos: 60);
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(colgado).Contains("empezando"));
            CanalFalso canal = fabrica.Ultima!.CanalActual!;
            canal.TragarLineasTrasCtrlC(lineasTragadas);

            // Act
            _ = colgado.SolicitarCancelacion();
            await colgado.Finalizacion.WaitAsync(Limite);
            Peticion siguiente = await EjecutarAsync(sesion, "siguiente");

            // Assert
            _ = colgado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = colgado.Aviso.Should().Be("Cancelado: se envió Ctrl+C al proceso.");
            _ = Texto(siguiente).Should().Be("eco:siguiente\n");
            _ = siguiente.Aviso.Should().BeNull();
            _ = fabrica.Ultima.ShellsAbiertos.Should().Be(1);
            _ = canal.SigintsRecibidos.Should().Be(1);
        }

        [Fact]
        public async Task Cancelar_ConElMarcadorDeRecuperacionLento_ElReenvioNoDejaUnMarcadorSueltoEnLaSalidaDelSiguiente()
        {
            // Arrange: la primera línea de recuperación sí llega, pero su marcador tarda más que media espera tras Ctrl+C,
            // así que también se ejecuta el reenvío y su marcador llega durante el comando siguiente.
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion colgado = Nueva("colgado", timeoutSegundos: 60);
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(colgado).Contains("empezando"));
            CanalFalso canal = fabrica.Ultima!.CanalActual!;
            canal.RetrasoMarcadorRecuperacion = TimeSpan.FromMilliseconds(250);

            // Act
            _ = colgado.SolicitarCancelacion();
            await colgado.Finalizacion.WaitAsync(Limite);
            Peticion pregunta = Nueva("pregunta");
            sesion.Encolar(pregunta);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(pregunta).Contains("¿Nombre?"));
            await Task.Delay(800);
            bool isEnviada = sesion.EnviarEntrada(pregunta, "ana", isEnviarEnter: true);
            await pregunta.Finalizacion.WaitAsync(Limite);

            // Assert
            _ = colgado.Aviso.Should().Be("Cancelado: se envió Ctrl+C al proceso.");
            _ = isEnviada.Should().BeTrue();
            _ = pregunta.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = Texto(pregunta).Should().Be("¿Nombre? recibido:ana\n");
            _ = fabrica.Ultima.ShellsAbiertos.Should().Be(1);
        }

        [Fact]
        public async Task EntradaQueFallaAlEscribir_LanzaConLaCausaRealEnLugarDeDevolverFalse()
        {
            // Arrange: la conexión cae sin que el canal se entere (escribir en él falla).
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            Peticion pregunta = Nueva("pregunta");
            sesion.Encolar(pregunta);
            await EsperasPrueba.EsperarHastaAsync(() => Texto(pregunta).Contains("¿Nombre?"));
            fabrica.Ultima!.CaerSinCerrarCanal();

            // Act
            Action enviar = () => sesion.EnviarEntrada(pregunta, "ana", isEnviarEnter: true);

            // Assert
            _ = enviar.Should().Throw<NativeSshException>().Which.Message.Should().Contain("perdió la conexión SSH").And.Contain("parte de la entrada");
        }

        [Fact]
        public async Task TiempoAgotado_SinRespuestaACtrlCNiConfirmacionDelCierre_NoAfirmaQueLaConexionSigueAbierta()
        {
            // Arrange: el proceso ignora Ctrl+C y el servidor no confirma el cierre del canal (congelado, o su bash no muere
            // al perder el terminal): no se puede saber si la conexión sigue viva.
            using ManualResetEventSlim confirmacion = new(false);
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");
            fabrica.Ultima!.CanalActual!.RetenerCierre(confirmacion);
            Peticion terco;
            Peticion confirmado;

            try
            {
                // Act
                terco = await EjecutarAsync(sesion, "terco", timeoutSegundos: 0.3);
            }
            finally
            {
                confirmacion.Set();
            }

            confirmado = await EjecutarAsync(sesion, "terco", timeoutSegundos: 0.3);

            // Assert: solo con el cierre confirmado por el servidor se afirma que la conexión sigue abierta.
            _ = terco.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            _ = terco.Aviso.Should().Contain("no respondió a Ctrl+C").And.Contain("no confirmó el cierre").And.NotContain("sigue abierta");
            _ = confirmado.Aviso.Should().Contain("no respondió a Ctrl+C").And.Contain("sigue abierta");
        }

        [Fact]
        public async Task EscrituraBloqueada_ElComandoEnCursoLoAvisaHastaQueTermina()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");
            fabrica.Ultima!.ColgarEscrituras();
            Peticion colgado = Nueva("dos");
            sesion.Encolar(colgado);
            await EsperasPrueba.EsperarHastaAsync(() => fabrica.Ultima.CanalActual!.EscriturasColgadas == 1);
            string? avisoInicial = colgado.Aviso;

            // Act: pasa el umbral del aviso.
            await EsperasPrueba.EsperarHastaAsync(() => sesion.SegundosEnEscritura >= SesionShell.UmbralAvisoEscrituraBloqueada.TotalSeconds, Limite);
            string? avisoBloqueado = colgado.Aviso;
            string? avisoSesion = sesion.AvisoEscrituraBloqueada;
            await sesion.DisposeAsync().AsTask().WaitAsync(Limite);

            // Assert: el aviso solo aparece al superar el umbral y desaparece al terminar (el estado final tiene el suyo).
            _ = avisoInicial.Should().BeNull();
            _ = avisoBloqueado.Should().Contain("ssh_disconnect").And.Contain("ssh_cancel");
            _ = avisoSesion.Should().Contain("ssh_disconnect").And.Contain("intentando enviar datos");
            _ = sesion.SegundosEnEscritura.Should().BeNull();
            _ = sesion.AvisoEscrituraBloqueada.Should().BeNull();
            _ = colgado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = colgado.Aviso.Should().NotContain("ssh_disconnect");
        }

        [Fact]
        public async Task Limitador_RechazaLaReconexion()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica, Opciones(maxConexiones: 1));
            _ = await EjecutarAsync(sesion, "hola");

            // Act
            fabrica.Ultima!.Caer();
            Peticion rechazada = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = rechazada.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = rechazada.Aviso.Should().Contain("Reintenta");
            _ = fabrica.Conexiones.Should().Be(1);
        }

        [Fact]
        public async Task ErrorAlConectar_PenalizaElSiguienteIntento()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion) { ErrorAlConectar = new NativeSshException("Autenticación rechazada") };
            await using SesionShell sesion = Crear(fabrica);

            // Act
            Peticion primera = await EjecutarAsync(sesion, "hola");
            Peticion segunda = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = primera.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = primera.Aviso.Should().Contain("Autenticación rechazada");
            _ = segunda.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = segunda.Aviso.Should().Contain("Reintenta");
        }

        [Fact]
        public async Task Reset_AbreShellNuevoSinAviso()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");

            // Act
            sesion.SolicitarReset();
            Peticion siguiente = await EjecutarAsync(sesion, "hola");

            // Assert
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Aviso.Should().BeNull();
            _ = fabrica.Ultima!.ShellsAbiertos.Should().Be(2);
        }

        [Fact]
        public async Task Dispose_CancelaLoPendiente()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            SesionShell sesion = Crear(fabrica);
            Peticion colgado = Nueva("colgado");
            Peticion encolada = Nueva("hola");
            sesion.Encolar(colgado);
            sesion.Encolar(encolada);
            await EsperasPrueba.EsperarHastaAsync(() => colgado.Estado == EstadoPeticionEnum.Ejecutando);

            // Act
            await sesion.DisposeAsync();
            await sesion.DisposeAsync();
            Peticion tardia = Nueva("tarde");
            sesion.Encolar(tardia);

            // Assert
            _ = colgado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = encolada.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = tardia.Estado.Should().Be(EstadoPeticionEnum.Error);
        }

        [Fact]
        public async Task Dispose_ConUnComandoEnCurso_NuncaLoTerminaComoError()
        {
            // Arrange: la carrera (el bucle ve la sesión cerrada antes de que se cancele su token) es muy estrecha, así
            // que se repite muchas veces en paralelo.
            ConcurrentQueue<string> fallos = new();

            // Act
            await Parallel.ForAsync(0, 4000, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (_, _) =>
            {
                SesionShell sesion = Crear(new FabricaFalsa(Guion));
                Peticion colgado = Nueva("colgado");
                sesion.Encolar(colgado);
                await EsperasPrueba.EsperarHastaAsync(() => colgado.Estado == EstadoPeticionEnum.Ejecutando);
                await sesion.DisposeAsync();
                if (colgado.Estado != EstadoPeticionEnum.Cancelado)
                    fallos.Enqueue($"{colgado.Estado}: {colgado.Aviso}");
            });

            // Assert
            _ = fallos.Should().BeEmpty();
        }

        [Fact]
        public async Task Dispose_EnLaVentanaEntreMarcarElCierreYCancelarlo_ElComandoQueIbaAConectarTerminaCancelado()
        {
            // Arrange: una lectura está conectando y retiene el candado de conexión; un comando espera ese candado.
            using LoggerPrueba logger = new() { EventoBloqueante = 40 };
            using SemaphoreSlim conexion = new(0);
            FabricaFalsa fabrica = new(Guion) { BloqueoConexion = conexion };
            SesionShell sesion = Crear(fabrica, logger: logger);
            Task<ResultadoCanalExec> lectura = sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(10), CancellationToken.None);
            await EsperasPrueba.EsperarHastaAsync(() => fabrica.ConexionesRetenidas == 1);
            Peticion comando = Nueva("hola");
            sesion.Encolar(comando);
            await EsperasPrueba.EsperarHastaAsync(() => comando.Estado == EstadoPeticionEnum.Ejecutando);

            // Act: el cierre se detiene recién marcado, con _cierre aún sin cancelar; en esa ventana la lectura termina de
            // conectar y el comando obtiene el candado, ve la sesión cerrada y falla.
            Task cierre = Task.Run(() => sesion.DisposeAsync().AsTask());
            await logger.Bloqueado.WaitAsync(Limite);
            _ = conexion.Release();
            await comando.Finalizacion.WaitAsync(Limite);
            EstadoPeticionEnum estadoEnLaVentana = comando.Estado;
            logger.Liberar();
            await cierre.WaitAsync(Limite);
            _ = await Record.ExceptionAsync(() => lectura);

            // Assert: el fallo se atribuye al cierre aunque _cierre no estuviera cancelado.
            _ = logger.IsEsperaAgotada.Should().BeFalse();
            _ = estadoEnLaVentana.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = comando.Aviso.Should().Contain("se cerró");
        }

        [Fact]
        public async Task Exec_AlCerrarLaSesion_TerminaConErrorYNoAbreOtraConexion()
        {
            // Arrange
            FabricaFalsa fabrica = new(_ => new RespuestaFalsa("", 0, ComportamientoFalsoEnum.Colgado));
            SesionShell sesion = Crear(fabrica);
            Task<ResultadoCanalExec> enCurso = sesion.EjecutarExecAsync("sleep 100", TimeSpan.FromSeconds(30), CancellationToken.None);
            await EsperasPrueba.EsperarHastaAsync(() => sesion.CanalesExecActivos == 1);

            // Act
            await sesion.DisposeAsync();
            NativeSshException error = (await FluentActions.Awaiting(() => enCurso).Should().ThrowAsync<NativeSshException>()).Which;
            NativeSshException tardio = (await FluentActions.Awaiting(() => sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(5), CancellationToken.None))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("se cerró");
            _ = tardio.Message.Should().Contain("está cerrada");
            _ = fabrica.Conexiones.Should().Be(1);
            _ = fabrica.Ultima!.IsConectada.Should().BeFalse();
        }

        [Fact]
        public async Task Exec_SiLaSesionSeCierraAlIrAUsarLaConexion_DiceQueSeCerroEnLugarDeUnErrorCrudo()
        {
            // Arrange: la lectura obtiene la conexión y, antes de usarla, ssh_disconnect cierra la sesión y la libera.
            FabricaFalsa fabrica = new(Guion);
            SesionShell sesion = Crear(fabrica);
            _ = await sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(5), CancellationToken.None);
            fabrica.Ultima!.AlConsultarConexion = () => sesion.DisposeAsync().AsTask().GetAwaiter().GetResult();

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(5), CancellationToken.None))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("se cerró").And.Contain("ssh_disconnect").And.NotContain("disposed");
            _ = sesion.CanalesExecActivos.Should().Be(0);
        }

        [Fact]
        public async Task Exec_CancelacionDelLlamante_PropagaOperationCanceledYLiberaElCupo()
        {
            // Arrange
            FabricaFalsa fabrica = new(_ => new RespuestaFalsa("", 0, ComportamientoFalsoEnum.Colgado));
            await using SesionShell sesion = Crear(fabrica);
            using CancellationTokenSource cancelacion = new(TimeSpan.FromMilliseconds(200));

            // Act & Assert: el cupo se libera cuando el canal termina de cerrarse, justo después de la cancelación.
            _ = await FluentActions.Awaiting(() => sesion.EjecutarExecAsync("sleep 100", TimeSpan.FromSeconds(30), cancelacion.Token))
                .Should().ThrowAsync<OperationCanceledException>();
            await EsperasPrueba.EsperarHastaAsync(() => sesion.CanalesExecActivos == 0, TimeSpan.FromSeconds(2));
        }

        [Fact]
        public async Task Exec_AperturaColgada_FallaAlCancelarYNoLiberaElCupoHastaQueElCanalSeCierra()
        {
            // Arrange: la apertura de los canales exec no responde ni atiende el token (SSH.NET con el servidor congelado).
            using SemaphoreSlim apertura = new(0);
            FabricaFalsa fabrica = new(Guion) { BloqueoAperturaExec = apertura };
            await using SesionShell sesion = Crear(fabrica);
            using CancellationTokenSource cancelacion = new(TimeSpan.FromMilliseconds(300));
            Stopwatch reloj = Stopwatch.StartNew();

            // Act
            Task<ResultadoCanalExec>[] colgadas = [.. Enumerable.Range(0, SesionShell.MaxCanalesExecSimultaneos).Select(_ => sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(30), cancelacion.Token))];
            foreach (Task<ResultadoCanalExec> colgada in colgadas)
                _ = await FluentActions.Awaiting(() => colgada.WaitAsync(Limite)).Should().ThrowAsync<OperationCanceledException>();
            TimeSpan espera = reloj.Elapsed;
            int activosTrasCancelar = sesion.CanalesExecActivos;
            using CancellationTokenSource cancelacionQuinta = new(TimeSpan.FromMilliseconds(300));
            _ = await FluentActions.Awaiting(() => sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(30), cancelacionQuinta.Token).WaitAsync(Limite))
                .Should().ThrowAsync<OperationCanceledException>();
            int maximoEnLaConexion = fabrica.Ultima!.MaxExecSimultaneos;
            _ = apertura.Release(SesionShell.MaxCanalesExecSimultaneos + 1);
            await EsperasPrueba.EsperarHastaAsync(() => sesion.CanalesExecActivos == 0);
            ResultadoCanalExec siguiente = await sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(5), CancellationToken.None).WaitAsync(Limite);

            // Assert: el quinto ni llega a la conexión mientras los cuatro abandonados siguen abriéndose.
            _ = espera.Should().BeLessThan(TimeSpan.FromSeconds(3));
            _ = activosTrasCancelar.Should().Be(SesionShell.MaxCanalesExecSimultaneos);
            _ = maximoEnLaConexion.Should().Be(SesionShell.MaxCanalesExecSimultaneos);
            _ = siguiente.CodigoSalida.Should().Be(0);
        }

        [Fact]
        public async Task Exec_QueVenceSinConfirmarseLaApertura_NoPrometeElPlazoRemoto()
        {
            // Arrange: el servidor no confirma la apertura del canal (SSH.NET con el servidor congelado o sin leer).
            using SemaphoreSlim apertura = new(0);
            FabricaFalsa fabrica = new(Guion) { BloqueoAperturaExec = apertura };
            await using SesionShell sesion = Crear(fabrica);

            // Act: límite local de 1 s + 3 s.
            NativeSshException error = (await FluentActions.Awaiting(() => sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(1), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)))
                .Should().ThrowAsync<NativeSshException>()).Which;
            _ = apertura.Release();

            // Assert
            _ = error.Message.Should().Contain("no terminó en 1 s").And.Contain("sin que el servidor confirmara la apertura").And.Contain("no se sabe si el comando llegó a arrancar")
                .And.NotContain("como mucho al vencer su plazo");
        }

        [Fact]
        public async Task Exec_QueVenceEsperandoCupoRetenidoPorAperturasSinConfirmar_NoPrometeElPlazoRemoto()
        {
            // Arrange: cuatro lecturas abandonadas cuyas aperturas el servidor no confirma retienen todo el cupo.
            using SemaphoreSlim apertura = new(0);
            FabricaFalsa fabrica = new(Guion) { BloqueoAperturaExec = apertura };
            await using SesionShell sesion = Crear(fabrica);
            Task<ResultadoCanalExec>[] abandonadas = [.. Enumerable.Range(0, SesionShell.MaxCanalesExecSimultaneos).Select(_ => sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(1), CancellationToken.None))];
            foreach (Task<ResultadoCanalExec> abandonada in abandonadas)
                _ = await FluentActions.Awaiting(() => abandonada.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<NativeSshException>();

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(1), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)))
                .Should().ThrowAsync<NativeSshException>()).Which;
            _ = apertura.Release(SesionShell.MaxCanalesExecSimultaneos + 1);
            await EsperasPrueba.EsperarHastaAsync(() => sesion.CanalesExecActivos == 0);
            ResultadoCanalExec siguiente = await sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(5), CancellationToken.None).WaitAsync(Limite);

            // Assert: dice cuántas siguen sin confirmar y no promete el plazo remoto; al confirmarse, el cupo vuelve.
            _ = error.Message.Should().Contain("sin llegar a obtener canal").And.Contain($"{SesionShell.MaxCanalesExecSimultaneos} de ellos con la apertura aún sin confirmar")
                .And.Contain("ssh_disconnect").And.NotContain("como mucho hasta el plazo");
            _ = siguiente.CodigoSalida.Should().Be(0);
        }

        [Fact]
        public async Task Exec_QueVenceConLaAperturaConfirmada_PrometeElPlazoRemoto()
        {
            // Arrange: el canal se abre y el comando no termina.
            using SemaphoreSlim bloqueo = new(0);
            FabricaFalsa fabrica = new(Guion) { BloqueoExec = bloqueo };
            await using SesionShell sesion = Crear(fabrica);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(1), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("no terminó en 1 s").And.Contain("como mucho al vencer su plazo").And.NotContain("confirmara");
        }

        [Fact]
        public async Task Exec_QueVenceEsperandoLaConexion_DiceQueNoLlegoAObtenerCanal()
        {
            // Arrange: la conexión no termina de establecerse antes que el límite local de la lectura (1 s + 3 s).
            using SemaphoreSlim conexion = new(0);
            FabricaFalsa fabrica = new(Guion) { BloqueoConexion = conexion };
            await using SesionShell sesion = Crear(fabrica, Opciones() with { TimeoutConexion = TimeSpan.FromSeconds(30) });
            NativeSshException error;

            try
            {
                // Act
                error = (await FluentActions.Awaiting(() => sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(1), CancellationToken.None))
                    .Should().ThrowAsync<NativeSshException>()).Which;
            }
            finally
            {
                _ = conexion.Release();
            }

            // Assert
            _ = error.Message.Should().Contain("no terminó en 1 s").And.Contain("sin llegar a obtener canal").And.Contain("conexión").And.NotContain("canal exec se cierra");
        }

        [Fact]
        public async Task Exec_ErrorDelCanal_SeConvierteEnNativeSshException()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion);
            await using SesionShell sesion = Crear(fabrica);
            _ = await EjecutarAsync(sesion, "hola");
            fabrica.ErrorAlConectar = new InvalidOperationException("sin red");
            fabrica.Ultima!.Caer();

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => sesion.EjecutarExecAsync("true", TimeSpan.FromSeconds(5), CancellationToken.None))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("sin red");
        }
    }
}
