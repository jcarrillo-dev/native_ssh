using FluentAssertions;
using System.Linq;
using System.Text;
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

        private static SesionShell Crear(FabricaFalsa fabrica, OpcionesNativeSsh? opciones = null)
        {
            opciones ??= Opciones();
            return new SesionShell(Destino, fabrica, new LimitadorConexiones(opciones, TimeProvider.System), opciones, NullLogger.Instance);
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

            // Assert
            _ = salir.Estado.Should().Be(EstadoPeticionEnum.ShellPerdido);
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
        public async Task Exec_CancelacionDelLlamante_PropagaOperationCanceledYLiberaElCupo()
        {
            // Arrange
            FabricaFalsa fabrica = new(_ => new RespuestaFalsa("", 0, ComportamientoFalsoEnum.Colgado));
            await using SesionShell sesion = Crear(fabrica);
            using CancellationTokenSource cancelacion = new(TimeSpan.FromMilliseconds(200));

            // Act & Assert
            _ = await FluentActions.Awaiting(() => sesion.EjecutarExecAsync("sleep 100", TimeSpan.FromSeconds(30), cancelacion.Token))
                .Should().ThrowAsync<OperationCanceledException>();
            _ = sesion.CanalesExecActivos.Should().Be(0);
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
