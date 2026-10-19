using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;
using Xunit.Abstractions;

namespace NativeSsh.Test.Integracion
{
    // Lo tecleado con ssh_input que el comando no lee (enviado antes de que lo pida o cuando ya no lee, varias líneas, un
    // ssh_input con '\n' o '\r' dentro, Ctrl+D) queda en el terminal al terminar el comando. La cola de cada envío espera
    // el centinela del canal, que es la primera línea del envío siguiente, y descarta sin ejecutarlo todo lo que llega antes
    // (ConstructorComando.EscribirCola): sin plazos, así que ni el volumen, ni la carga, ni la latencia, ni Ctrl+D, ni una
    // función 'read' lo cambian. El recuento llega en el marcador del envío siguiente: el aviso va en la respuesta del
    // comando siguiente. secreto=true rechaza el texto de varias líneas. Con el sshd local (bash real, terminal real).
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class EntradaPorAdelantadoTests(ITestOutputHelper salida) : IAsyncLifetime
    {
        private readonly SshdPropio _sshd = new(string.Empty);
        private readonly string _testigos = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_tecleo_{Guid.NewGuid():N}")).FullName;
        private GestorSesiones? _gestor;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (_gestor is not null)
                await _gestor.DisposeAsync();
            _sshd.Dispose();
            Directory.Delete(_testigos, recursive: true);
        }

        private HerramientasSsh Herramientas()
        {
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones();
            _gestor = new GestorSesiones(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            return new HerramientasSsh(_gestor, opciones);
        }

        // Abre el shell antes de la prueba: con la máquina cargada, abrirlo puede tardar más que las esperas cortas de las
        // pruebas, y ssh_input se rechaza mientras el shell se abre.
        private static async Task CalentarAsync(HerramientasSsh h, string destino) =>
            _ = (await h.EjecutarAsync(destino, "true", esperarSegundos: 60, timeoutSegundos: 60).ComoComando()).Estado.Should().Be(EstadoPeticionEnum.Completado);

        private string Testigo() => Path.Combine(_testigos, Guid.NewGuid().ToString("N"));

        private static string Canario() => $"Canario{Guid.NewGuid():N}"[..20];

        private static string AvisoDe(string idAnterior, string lineas) => $"El comando anterior de este shell ({idAnterior}) no leyó {lineas} de entrada tecleada";

        // Un ssh_input con dos líneas (separadas por '\n' o '\r') a un comando que no lee: ninguna se ejecuta; el final del
        // comando no dice nada (aún no se sabe) y la respuesta del comando siguiente dice que se descartaron 2, una sola vez.
        [Theory]
        [InlineData("\n")]
        [InlineData("\r")]
        public async Task DosLineasEnUnaEntrada_NoSeEjecutanYAvisaElComandoSiguiente(string separador)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            string testigo = Testigo();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 2", esperarSegundos: 1).ComoComando();

            // Act
            ResultadoComando entrada = await h.EnviarEntradaAsync(lento.Id, $"respuesta1{separador}touch {testigo}", esperarSegundos: 8).ComoComando();
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 10).ComoComando();
            ResultadoComando otro = await h.EjecutarAsync(destino, "echo otro", esperarSegundos: 10).ComoComando();
            await Task.Delay(300);

            // Assert
            salida.WriteLine($"entrada {entrada.Estado} aviso '{entrada.Aviso}'; siguiente '{siguiente.Salida}' aviso '{siguiente.Aviso}'; otro '{otro.Aviso}'");
            _ = File.Exists(testigo).Should().BeFalse("la segunda línea de lo tecleado no debe ejecutarse como orden");
            _ = entrada.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = entrada.CodigoSalida.Should().Be(0);
            _ = entrada.Aviso.Should().BeNull("el recuento solo se conoce al llegar el comando siguiente");
            _ = siguiente.Salida.Should().Be("siguiente\n");
            _ = siguiente.Aviso.Should().Contain(AvisoDe(lento.Id, "2 líneas")).And.Contain("se descartaron sin ejecutarse");
            _ = otro.Aviso.Should().BeNull("el recuento se reinicia en cada comando");
        }

        // Dos ssh_input de una línea cada uno, sin lector: tampoco se ejecuta el segundo.
        [Fact]
        public async Task DosEntradasDeUnaLinea_NoSeEjecutanYSeAvisa()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            string testigo = Testigo();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 2", esperarSegundos: 1).ComoComando();

            // Act
            _ = await h.EnviarEntradaAsync(lento.Id, "respuesta1", esperarSegundos: 0).ComoComando();
            _ = await h.EnviarEntradaAsync(lento.Id, $"touch {testigo}", esperarSegundos: 8).ComoComando();
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 10).ComoComando();
            await Task.Delay(300);

            // Assert
            _ = File.Exists(testigo).Should().BeFalse("una segunda entrada tecleada por adelantado no debe ejecutarse como orden");
            _ = siguiente.Aviso.Should().Contain(AvisoDe(lento.Id, "2 líneas"));
        }

        // El aviso sale exactamente una vez entre todas las respuestas: si nadie ha leído el final del comando que lo trae
        // (lanzado con esperarSegundos=0), lo da la respuesta siguiente del shell, y ni las demás ni ese final lo repiten; si
        // ya se leyó, no se repite.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AvisoDeLoDescartado_SaleUnaSolaVez(bool isFinalLeido)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 1", esperarSegundos: 0).ComoComando();
            await Task.Delay(300);
            _ = await h.EnviarEntradaAsync(lento.Id, "ClaveUno\nClaveDos", esperarSegundos: 0).ComoComando();
            ResultadoComando previo = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 5).ComoComando();

            // Act
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "sleep 0.3; echo siguiente", esperarSegundos: 0).ComoComando();
            ResultadoComando? leido = isFinalLeido ? await h.LeerSalidaAsync(siguiente.Id, esperarSegundos: 10).ComoComando() : null;
            ResultadoComando otro = await h.EjecutarAsync(destino, "echo otro", esperarSegundos: 10).ComoComando();
            ResultadoComando tercero = await h.EjecutarAsync(destino, "echo tercero", esperarSegundos: 10).ComoComando();
            ResultadoComando finalSiguiente = await h.LeerSalidaAsync(siguiente.Id).ComoComando();

            // Assert
            salida.WriteLine($"previo '{previo.Aviso}'; otro '{otro.Aviso}'; tercero '{tercero.Aviso}'; final siguiente '{finalSiguiente.Aviso}'");
            _ = previo.Aviso.Should().BeNull();
            _ = finalSiguiente.Aviso.Should().BeNull("el aviso ya salió en otra respuesta");
            _ = leido?.Aviso.Should().Contain(AvisoDe(lento.Id, "2 líneas"));
            _ = isFinalLeido
                ? otro.Aviso.Should().BeNull("el final que avisaba ya se leyó")
                : otro.Aviso.Should().Contain($"El final del comando {siguiente.Id}, que aún no se ha leído, avisa de que el comando {lento.Id} no leyó 2 líneas");
            _ = tercero.Aviso.Should().BeNull("el aviso no se repite más de una vez");
            _ = otro.Salida.Should().Be("otro\n");
        }

        // Ids de 32 hexadecimales que cita un aviso.
        private static List<string> IdsCitados(string? aviso)
        {
            List<string> ids = [];
            string texto = aviso ?? string.Empty;
            int inicio = 0;
            for (int i = 0; i <= texto.Length; i++)
            {
                if (i < texto.Length && char.IsAsciiHexDigitLower(texto[i]))
                    continue;
                if (i - inicio >= 32)
                    ids.Add(texto[inicio..i]);
                inicio = i + 1;
            }

            return ids;
        }

        // (V06) El comando que trae el aviso se lanza con esperarSegundos=0 y nadie lee su final; antes del ssh_exec siguiente
        // va una herramienta de archivo o ssh_set_cwd por el shell, cuya respuesta no da avisos: el aviso llega igualmente, una
        // vez, en la respuesta del ssh_exec siguiente (antes se perdía: la sesión lo enlazaba solo con la petición siguiente).
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AvisoConHerramientaEnMedio_LlegaAlSshExecSiguiente(bool isSetCwd)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 1", esperarSegundos: 0).ComoComando();
            await Task.Delay(300);
            _ = await h.EnviarEntradaAsync(lento.Id, "uno\ndos", esperarSegundos: 0).ComoComando();
            _ = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 15).ComoComando();
            ResultadoComando trae = await h.EjecutarAsync(destino, "sleep 0.5", esperarSegundos: 0).ComoComando();
            await Task.Delay(1500);

            // Act
            if (isSetCwd)
                _ = await h.FijarDirectorioAsync(destino, _testigos);
            else
                _ = await h.EscribirArchivoAsync(destino, Path.Combine(_testigos, "medio.txt"), "hola\n");
            ResultadoComando d = await h.EjecutarAsync(destino, "echo D", esperarSegundos: 15).ComoComando();
            ResultadoComando e = await h.EjecutarAsync(destino, "echo E", esperarSegundos: 15).ComoComando();
            ResultadoComando finalTrae = await h.LeerSalidaAsync(trae.Id).ComoComando();

            // Assert
            salida.WriteLine($"D '{d.Aviso}'; E '{e.Aviso}'; final '{finalTrae.Aviso}'");
            _ = d.Aviso.Should().Contain($"El final del comando {trae.Id}, que aún no se ha leído, avisa de que el comando {lento.Id} no leyó 2 líneas");
            _ = new[] { e.Aviso, finalTrae.Aviso }.Should().AllSatisfy(a => (a ?? string.Empty).Should().NotContain("no leyó"));
        }

        // El aviso pendiente de un comando del shell anterior llega tras ssh_reset_shell, pero no dice que sea «de este
        // shell»: el comando que lo trae era del shell que se cerró.
        [Fact]
        public async Task AvisoPendienteTrasResetShell_NoDiceQueEsDeEsteShell()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 1", esperarSegundos: 0).ComoComando();
            await Task.Delay(300);
            _ = await h.EnviarEntradaAsync(lento.Id, "uno\ndos", esperarSegundos: 0).ComoComando();
            _ = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 15).ComoComando();
            ResultadoComando trae = await h.EjecutarAsync(destino, "sleep 0.5", esperarSegundos: 0).ComoComando();
            await Task.Delay(1500);

            // Act
            _ = h.ReiniciarShell(destino);
            ResultadoComando d = await h.EjecutarAsync(destino, "echo D", esperarSegundos: 15).ComoComando();

            // Assert
            salida.WriteLine($"D '{d.Aviso}'");
            _ = d.Aviso.Should().Contain($"El final del comando {trae.Id}, que aún no se ha leído, avisa de que el comando {lento.Id} no leyó 2 líneas");
            _ = d.Aviso.Should().NotContain("de este shell");
        }

        // La petición que trae el aviso es la de una herramienta de archivo o de ssh_set_cwd (su envío llegó tras lo que el
        // comando no leyó) y su respuesta no da avisos: el ssh_exec siguiente lo da sin citar el id de esa petición, que el cliente
        // no ha visto. ssh_set_cwd va por otro camino que la escritura (HerramientasSsh.EjecutarHerramientaAsync).
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AvisoTraidoPorUnaHerramienta_NoCitaSuId(bool isSetCwd)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 1", esperarSegundos: 0).ComoComando();
            await Task.Delay(300);
            _ = await h.EnviarEntradaAsync(lento.Id, "uno\ndos", esperarSegundos: 0).ComoComando();
            _ = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 15).ComoComando();

            // Act
            if (isSetCwd)
                _ = await h.FijarDirectorioAsync(destino, _testigos);
            else
                _ = await h.EscribirArchivoAsync(destino, Path.Combine(_testigos, "trae.txt"), "hola\n");
            ResultadoComando b = await h.EjecutarAsync(destino, "echo B", esperarSegundos: 15).ComoComando();
            ResultadoComando c = await h.EjecutarAsync(destino, "echo C", esperarSegundos: 15).ComoComando();

            // Assert
            salida.WriteLine($"B '{b.Aviso}'; C '{c.Aviso}'");
            _ = b.Aviso.Should().Contain($"Una herramienta de archivo o ssh_set_cwd anterior avisa de que el comando {lento.Id} no leyó 2 líneas");
            _ = IdsCitados(b.Aviso).Should().OnlyContain(id => id == lento.Id);
            _ = (c.Aviso ?? string.Empty).Should().NotContain("no leyó");
        }

        // Dos avisos sin entregar a la vez: el comando que trae el primero no se lee, lo que se le teclea tampoco lo lee, y la
        // escritura que llega después trae el segundo. El ssh_exec siguiente da los dos (antes, el segundo tapaba al primero).
        [Fact]
        public async Task DosAvisosSinEntregar_LlegaCadaUnoUnaVez()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 1", esperarSegundos: 0).ComoComando();
            await Task.Delay(300);
            _ = await h.EnviarEntradaAsync(lento.Id, "uno\ndos", esperarSegundos: 0).ComoComando();
            _ = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 15).ComoComando();
            ResultadoComando trae = await h.EjecutarAsync(destino, "sleep 1.5", esperarSegundos: 0).ComoComando();
            await Task.Delay(500);
            _ = await h.EnviarEntradaAsync(trae.Id, "tres", esperarSegundos: 0).ComoComando();
            await Task.Delay(2000);

            // Act
            _ = await h.EscribirArchivoAsync(destino, Path.Combine(_testigos, "dos.txt"), "hola\n");
            ResultadoComando d = await h.EjecutarAsync(destino, "echo D", esperarSegundos: 15).ComoComando();
            ResultadoComando e = await h.EjecutarAsync(destino, "echo E", esperarSegundos: 15).ComoComando();
            ResultadoComando finalTrae = await h.LeerSalidaAsync(trae.Id).ComoComando();

            // Assert
            salida.WriteLine($"D '{d.Aviso}'; E '{e.Aviso}'; final '{finalTrae.Aviso}'");
            _ = d.Aviso.Should().Contain($"el comando {lento.Id} no leyó 2 líneas").And.Contain($"el comando {trae.Id} no leyó 1 línea");
            _ = IdsCitados(d.Aviso).Should().OnlyContain(id => id == lento.Id || id == trae.Id);
            _ = new[] { e.Aviso, finalTrae.Aviso }.Should().AllSatisfy(a => (a ?? string.Empty).Should().NotContain("no leyó"));
        }

        // (M07) La herramienta de archivo cuya petición trae el aviso no termina: su error lo da (con el id de la petición).
        // Una función 'local' del usuario deja colgada la validación de ssh_set_cwd de una ruta que no existe (sin_acceso
        // llama a local sin 'command'; '[' ya va con él).
        [Fact]
        public async Task HerramientaQueNoTermina_SuErrorDaElAviso()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            _ = await h.EjecutarAsync(destino, "local() { sleep 30; }", esperarSegundos: 15).ComoComando();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 1", esperarSegundos: 0).ComoComando();
            await Task.Delay(300);
            _ = await h.EnviarEntradaAsync(lento.Id, "uno\ndos", esperarSegundos: 0).ComoComando();
            _ = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 15).ComoComando();
            HerramientasSsh corta = new(_gestor!, _sshd.Sshd.Opciones()) { TimeoutForzado = TimeSpan.FromSeconds(2) };

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => corta.FijarDirectorioAsync(destino, Path.Combine(_testigos, "no_existe")))
                .Should().ThrowAsync<NativeSshException>()).Which;
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "unset -f local; echo siguiente", esperarSegundos: 15).ComoComando();

            // Assert
            salida.WriteLine($"error '{error.Message}'; siguiente '{siguiente.Aviso}'");
            _ = error.Message.Should().Contain(AvisoDe(lento.Id, "2 líneas"), "lo dice el error de la herramienta").And.Contain("no se completó");
            _ = (siguiente.Aviso ?? string.Empty).Should().NotContain("no leyó");
        }

        // Dos avisos sin dar y la herramienta que trae el más reciente no termina: su error da el suyo sin recorrer los
        // anteriores, y el más antiguo (el que trae un comando cuyo final nadie leyó) sigue pendiente detrás de ella. El ssh_exec
        // siguiente lo da: la cadena se compacta por detrás de la cabeza ya avisada (antes, con la cabeza avisada se soltaba
        // la cadena entera y ese aviso se perdía).
        [Fact]
        public async Task AvisoPendienteDetrasDeUnaHerramientaQueNoTermina_LlegaAlSiguiente()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            _ = await h.EjecutarAsync(destino, "local() { sleep 30; }", esperarSegundos: 15).ComoComando();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 1", esperarSegundos: 0).ComoComando();
            await Task.Delay(300);
            _ = await h.EnviarEntradaAsync(lento.Id, "uno\ndos", esperarSegundos: 0).ComoComando();
            _ = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 15).ComoComando();
            ResultadoComando trae = await h.EjecutarAsync(destino, "sleep 1.5", esperarSegundos: 0).ComoComando();
            await Task.Delay(500);
            _ = await h.EnviarEntradaAsync(trae.Id, "tres", esperarSegundos: 0).ComoComando();
            await Task.Delay(2000);
            HerramientasSsh corta = new(_gestor!, _sshd.Sshd.Opciones()) { TimeoutForzado = TimeSpan.FromSeconds(2) };

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => corta.FijarDirectorioAsync(destino, Path.Combine(_testigos, "no_existe")))
                .Should().ThrowAsync<NativeSshException>()).Which;
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "unset -f local; echo siguiente", esperarSegundos: 15).ComoComando();
            ResultadoComando otro = await h.EjecutarAsync(destino, "echo otro", esperarSegundos: 15).ComoComando();

            // Assert
            salida.WriteLine($"error '{error.Message}'; siguiente '{siguiente.Aviso}'; otro '{otro.Aviso}'");
            _ = error.Message.Should().Contain(AvisoDe(trae.Id, "1 línea"), "lo dice el error de la herramienta");
            _ = siguiente.Aviso.Should().Contain($"El final del comando {trae.Id}, que aún no se ha leído, avisa de que el comando {lento.Id} no leyó 2 líneas");
            _ = (otro.Aviso ?? string.Empty).Should().NotContain("no leyó");
        }

        // Una línea a medias (enter=false) se junta con el centinela del envío siguiente: no se ejecuta y cuenta como una.
        [Theory]
        [InlineData("", "1 línea")]
        [InlineData("l1\nl2\n", "3 líneas")]
        public async Task LineaAMedias_NoSeEjecutaYCuentaComoUna(string completas, string recuento)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            string testigo = Testigo();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 2; (exit 7)", esperarSegundos: 1).ComoComando();

            // Act
            ResultadoComando entrada = await h.EnviarEntradaAsync(lento.Id, $"{completas}touch {testigo}", enter: false, esperarSegundos: 8).ComoComando();
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 10).ComoComando();
            await Task.Delay(300);

            // Assert
            salida.WriteLine($"entrada {entrada.Estado} {entrada.CodigoSalida} aviso '{entrada.Aviso}'; siguiente '{siguiente.Salida}' {siguiente.CodigoSalida} aviso '{siguiente.Aviso}'");
            _ = File.Exists(testigo).Should().BeFalse();
            _ = entrada.CodigoSalida.Should().Be(7);
            _ = siguiente.Salida.Should().Be("siguiente\n");
            _ = siguiente.CodigoSalida.Should().Be(0);
            _ = siguiente.Aviso.Should().Contain(AvisoDe(lento.Id, recuento));
        }

        // El comando lee una línea y termina: solo se descarta (y se cuenta) la que no leyó; el código es el del comando.
        [Fact]
        public async Task ComandoQueLeeUnaLinea_SeDescartaSoloElResto()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            string testigo = Testigo();
            ResultadoComando lector = await h.EjecutarAsync(destino, "sleep 2; read -r a; echo \"leido:$a\"; exit_code() { return 3; }; exit_code", esperarSegundos: 1).ComoComando();

            // Act
            ResultadoComando entrada = await h.EnviarEntradaAsync(lector.Id, $"uno\ntouch {testigo}", esperarSegundos: 8).ComoComando();
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 10).ComoComando();
            await Task.Delay(300);

            // Assert
            salida.WriteLine($"entrada {entrada.Estado} {entrada.CodigoSalida} '{entrada.Salida}'; siguiente aviso '{siguiente.Aviso}'");
            _ = File.Exists(testigo).Should().BeFalse();
            _ = entrada.Salida.Should().Contain("leido:uno");
            _ = entrada.CodigoSalida.Should().Be(3);
            _ = siguiente.Aviso.Should().Contain(AvisoDe(lector.Id, "1 línea"));
        }

        // Muchas líneas de una vez (más de lo que cabe en una línea del terminal en modo canónico, 4095 bytes): se descartan
        // todas y el recuento es exacto.
        [Fact]
        public async Task MuchasLineas_SeDescartanTodasYSeCuentan()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            string testigo = Testigo();
            string texto = string.Join('\n', Enumerable.Range(0, 300).Select(i => $"linea {i} {new string('x', 40)}")) + $"\ntouch {testigo}";
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 2", esperarSegundos: 1).ComoComando();

            // Act
            _ = await h.EnviarEntradaAsync(lento.Id, texto, esperarSegundos: 10).ComoComando();
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 10).ComoComando();
            await Task.Delay(300);

            // Assert
            salida.WriteLine($"siguiente '{siguiente.Salida}' aviso '{siguiente.Aviso}'");
            _ = File.Exists(testigo).Should().BeFalse();
            _ = siguiente.Aviso.Should().Contain(AvisoDe(lento.Id, "301 líneas"));
            _ = siguiente.Salida.Should().Be("siguiente\n");
        }

        // Ctrl+D al principio de una línea es un EOF del terminal ('read' devuelve 1 sin leer nada): no corta la espera del
        // centinela; cuenta como una línea (E01 de la revisión del tramo 1-ter). En una llamada: EOF, línea vacía, dos
        // órdenes y el salto final (4); en dos: EOF y su salto, y la orden (3).
        [Theory]
        [InlineData(true, "4 líneas")]
        [InlineData(false, "3 líneas")]
        public async Task CtrlDYDespuesOrdenes_NingunaSeEjecutaYSeCuentanTodas(bool isUnaLlamada, string recuento)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            string a = Testigo();
            string b = Testigo();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 2", esperarSegundos: 0).ComoComando();
            await Task.Delay(500);

            // Act
            if (isUnaLlamada)
                _ = await h.EnviarEntradaAsync(lento.Id, $"\u0004\ntouch {a}\ntouch {b}", esperarSegundos: 0).ComoComando();
            else
            {
                _ = await h.EnviarEntradaAsync(lento.Id, "\u0004", esperarSegundos: 0).ComoComando();
                _ = await h.EnviarEntradaAsync(lento.Id, $"touch {a}", esperarSegundos: 0).ComoComando();
            }

            ResultadoComando fin = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 10).ComoComando();
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 10).ComoComando();
            await Task.Delay(300);

            // Assert
            salida.WriteLine($"fin {fin.Estado}; siguiente '{siguiente.Salida}' aviso '{siguiente.Aviso}'");
            _ = (File.Exists(a) || File.Exists(b)).Should().BeFalse();
            _ = siguiente.Salida.Should().Be("siguiente\n");
            _ = siguiente.Aviso.Should().Contain(AvisoDe(lento.Id, recuento));
        }

        // Dos Ctrl+D seguidos y una orden en la misma línea (E03 sin secreto): no se ejecuta.
        [Fact]
        public async Task DosCtrlDYUnaOrden_NoSeEjecuta()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            string testigo = Testigo();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 2", esperarSegundos: 0).ComoComando();
            await Task.Delay(500);

            // Act
            _ = await h.EnviarEntradaAsync(lento.Id, $"\u0004\u0004touch {testigo}", esperarSegundos: 4).ComoComando();
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 10).ComoComando();
            await Task.Delay(300);

            // Assert
            salida.WriteLine($"siguiente '{siguiente.Salida}' aviso '{siguiente.Aviso}'");
            _ = File.Exists(testigo).Should().BeFalse();
            _ = siguiente.Aviso.Should().Contain(AvisoDe(lento.Id, "3 líneas"));
        }

        // Ctrl+D, una línea y después un secreto de una sola línea (E02, cara B): el secreto no se ejecuta como orden, así que
        // ningún 'orden no encontrada' lo devuelve sin enmascarar en el comando siguiente.
        [Fact]
        public async Task CtrlDLineaYSecreto_ElSecretoNoApareceEnNingunaRespuesta()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            List<string> respuestas = [];
            string[] canarios = [.. Enumerable.Range(0, 3).Select(_ => Canario())];

            // Act
            foreach (string canario in canarios)
            {
                ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 1.5", esperarSegundos: 0).ComoComando();
                await Task.Delay(400);
                _ = await h.EnviarEntradaAsync(lento.Id, "\u0004", esperarSegundos: 0).ComoComando();
                _ = await h.EnviarEntradaAsync(lento.Id, "relleno", esperarSegundos: 0).ComoComando();
                ResultadoComando entrada = await h.EnviarEntradaAsync(lento.Id, canario, esperarSegundos: 3, secreto: true).ComoComando();
                ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo Y; sleep 0.3", esperarSegundos: 4).ComoComando();
                ResultadoComando previo = await h.LeerSalidaAsync(lento.Id).ComoComando();
                respuestas.AddRange([entrada.Salida, siguiente.Salida, previo.Salida, siguiente.Aviso ?? ""]);
                _ = siguiente.Aviso.Should().Contain(AvisoDe(lento.Id, "4 líneas"));
            }

            // Assert
            _ = respuestas.Should().AllSatisfy(r => canarios.Should().AllSatisfy(c => r.Should().NotContain(c)));
        }

        // Una función o un alias 'read' (R01 de la revisión del tramo 1-ter) no cuelgan la espera: el comando siguiente
        // termina enseguida y lo tecleado tampoco se ejecuta.
        [Theory]
        [InlineData("read() { return 0; }")]
        [InlineData("shopt -s expand_aliases; alias read=true")]
        public async Task ReadRedefinida_NiCuelgaNiEjecutaLoTecleado(string definicion)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            string testigo = Testigo();
            ResultadoComando def = await h.EjecutarAsync(destino, definicion, esperarSegundos: 12, timeoutSegundos: 8).ComoComando();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 1.5", esperarSegundos: 0, timeoutSegundos: 8).ComoComando();
            await Task.Delay(400);
            _ = await h.EnviarEntradaAsync(lento.Id, $"x\ntouch {testigo}", esperarSegundos: 4).ComoComando();

            // Act
            Stopwatch reloj = Stopwatch.StartNew();
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 12, timeoutSegundos: 8).ComoComando();
            await Task.Delay(300);

            // Assert
            salida.WriteLine($"definición {def.Estado}; siguiente {siguiente.Estado} en {reloj.Elapsed.TotalSeconds:F1} s, aviso '{siguiente.Aviso}'");
            _ = (def.Estado, siguiente.Estado).Should().Be((EstadoPeticionEnum.Completado, EstadoPeticionEnum.Completado));
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
            _ = File.Exists(testigo).Should().BeFalse();
            _ = siguiente.Aviso.Should().Contain(AvisoDe(lento.Id, "2 líneas"));
        }

        // Lo tecleado que sigue pendiente cuando se interrumpe el comando (Ctrl+C con la pausa de ET3 y la línea de
        // recuperación) no se ejecuta: el núcleo vacía la entrada del terminal al convertir Ctrl+C en señal.
        [Fact]
        public async Task TecleadoPendienteYCtrlC_NadaSeEjecuta()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            string a = Testigo();
            string b = Testigo();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 30", esperarSegundos: 0).ComoComando();
            await Task.Delay(500);
            _ = await h.EnviarEntradaAsync(lento.Id, $"touch {a}\ntouch {b}", esperarSegundos: 0).ComoComando();

            // Act
            ResultadoComando cancelado = await h.CancelarAsync(lento.Id).ComoComando();
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 10).ComoComando();
            await Task.Delay(300);

            // Assert
            salida.WriteLine($"cancelado {cancelado.Estado} '{cancelado.Aviso}'; siguiente '{siguiente.Salida}' aviso '{siguiente.Aviso}'");
            _ = cancelado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = (File.Exists(a) || File.Exists(b)).Should().BeFalse();
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Salida.Should().Be("siguiente\n");
        }

        // Lo descartado antes de un comando que después se interrumpe lo dice el final de ese comando (el recuento llega en el
        // marcador de la línea de recuperación).
        [Fact]
        public async Task ComandoInterrumpidoTrasLoDescartado_AvisaIgual()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 1.5", esperarSegundos: 0).ComoComando();
            await Task.Delay(400);
            _ = await h.EnviarEntradaAsync(lento.Id, "uno\ndos", esperarSegundos: 4).ComoComando();

            // Act
            ResultadoComando interrumpido = await h.EjecutarAsync(destino, "sleep 30", esperarSegundos: 15, timeoutSegundos: 1).ComoComando();
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 10).ComoComando();

            // Assert
            salida.WriteLine($"interrumpido {interrumpido.Estado} '{interrumpido.Aviso}'; siguiente aviso '{siguiente.Aviso}'");
            _ = interrumpido.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            _ = interrumpido.Aviso.Should().Contain(AvisoDe(lento.Id, "2 líneas"));
            _ = siguiente.Aviso.Should().BeNull();
        }

        // Un proceso en segundo plano que lee del terminal. Si lee una línea, se come la vacía que precede al centinela
        // (ConstructorComando.EscribirPrefijoCentinela) y el comando siguiente se ejecuta. Si lee dos, compite con el shell
        // por las líneas del envío siguiente: según quién lea antes, se come el centinela (la cola sigue esperándolo, se come
        // el comando y este agota su plazo: Ctrl+C aborta la espera) o una línea del comando (que entonces no se ejecuta:
        // MarcadorFin.CodigoEnvioIncompleto). En ningún caso se ejecuta el comando a medias ni se da por completado sin
        // ejecutarse, el shell no se queda atascado (el comando de después funciona) y no se da por descartada ninguna línea:
        // lo que leyó la cola era el comando del cliente. Antes, una línea descartada de verdad, para que un recuento viejo no se
        // cuele en el aviso del comando interrumpido.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task HijoQueLeeDelTerminal_NoAtascaElShellNiDaPorDescartadoElComando(int lineas)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 1", esperarSegundos: 0).ComoComando();
            await Task.Delay(300);
            _ = await h.EnviarEntradaAsync(lento.Id, "descartada", esperarSegundos: 3).ComoComando();
            bool isUnaLinea = lineas == 1;
            string lectura = isUnaLinea ? "read -r x" : "read -r x; read -r x";
            ResultadoComando hijo = await h.EjecutarAsync(destino, $"({lectura}; echo \"hijo:${{#x}}\") </dev/tty & sleep 0.3", esperarSegundos: 10).ComoComando();

            // Act
            List<(ResultadoComando Resultado, string Testigo)> comandos = [];
            for (int i = 0; i < 3; i++)
            {
                string propio = Testigo();
                comandos.Add((await h.EjecutarAsync(destino, $"touch {propio}; echo comando{i}", esperarSegundos: 15, timeoutSegundos: 3).ComoComando(), propio));
            }

            await Task.Delay(300);

            // Assert
            _ = hijo.Aviso.Should().Contain(AvisoDe(lento.Id, "1 línea"));
            foreach ((ResultadoComando resultado, string propio) in comandos)
            {
                salida.WriteLine($"{resultado.Estado} '{resultado.Salida}' aviso '{resultado.Aviso}'");
                _ = (resultado.Estado == EstadoPeticionEnum.Completado).Should().Be(File.Exists(propio), "se da por completado solo si se ejecutó, y entero");
                _ = resultado.Estado.Should().BeOneOf(EstadoPeticionEnum.Completado, EstadoPeticionEnum.TiempoAgotado, EstadoPeticionEnum.Error);
                if (resultado.Estado == EstadoPeticionEnum.Error)
                    _ = resultado.Aviso.Should().Be(SesionShell.MensajeEnvioIncompleto);
                _ = (resultado.Aviso ?? string.Empty).Should().NotContain("no leyó");
            }

            _ = (comandos[^1].Resultado.Estado, comandos[^1].Resultado.Salida).Should().Be((EstadoPeticionEnum.Completado, "comando2\n"));
            if (isUnaLinea)
                _ = (comandos[0].Resultado.Estado, comandos[0].Resultado.Salida.Contains("comando0\n", StringComparison.Ordinal)).Should().Be((EstadoPeticionEnum.Completado, true));
        }

        // secreto=true con un fin de línea en medio ('\n', '\r' o Ctrl+D): se rechaza sin enviar nada (ni la primera línea) y
        // sin el texto en el mensaje; el comando sigue y termina sin nada descartado.
        [Theory]
        [InlineData("\n", true)]
        [InlineData("\r", true)]
        [InlineData("\r\n", true)]
        [InlineData("\u0004", true)]
        [InlineData("\n", false)]
        [InlineData("\r\n", false)]
        [InlineData("\u0004", false)]
        public async Task SecretoDeVariasLineas_SeRechazaSinEnviarNada(string separador, bool isEnter)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            string testigo = Testigo();
            string canario = Canario();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 2", esperarSegundos: 1).ComoComando();

            // Act
            Func<Task> enviar = () => h.EnviarEntradaAsync(lento.Id, $"{canario}{separador}touch {testigo}", enter: isEnter, esperarSegundos: 0, secreto: true).ComoComando();

            // Assert
            NativeSshException error = (await enviar.Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Be(HerramientasSsh.MensajeSecretoNoAdmitido).And.NotContain(canario);
            _ = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 8).ComoComando();
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 10).ComoComando();
            await Task.Delay(300);
            _ = File.Exists(testigo).Should().BeFalse();
            _ = siguiente.Aviso.Should().BeNull("no se envió nada");
            _ = siguiente.Salida.Should().Be("siguiente\n");
        }

        // El salto final no cuenta: con enter=false, un '\n' o un '\r' al final del secreto se admite y llega al comando, y
        // lo que se enmascara es el secreto sin ese salto (antes se registraba con él y el eco del programa salía en claro).
        [Theory]
        [InlineData("\n")]
        [InlineData("\r")]
        public async Task SecretoConSaltoFinalYSinEnter_SeAdmite(string salto)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            string canario = Canario();
            ResultadoComando lector = await h.EjecutarAsync(destino, "read -r x; echo \"longitud:${#x} eco:$x\"", esperarSegundos: 1).ComoComando();
            await Task.Delay(500);

            // Act
            ResultadoComando entrada = await h.EnviarEntradaAsync(lector.Id, canario + salto, enter: false, esperarSegundos: 8, secreto: true).ComoComando();
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 10).ComoComando();

            // Assert
            salida.WriteLine($"entrada {entrada.Estado} '{entrada.Salida}' aviso '{entrada.Aviso}'");
            _ = entrada.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = entrada.Salida.Should().Contain("longitud:20").And.Contain("eco:*").And.NotContain(canario);
            _ = siguiente.Aviso.Should().BeNull("el comando leyó toda la entrada");
        }
        // 'set -x' con BASH_XTRACEFD en la salida
        // estándar: ningún comando se ejecuta (997), tampoco el que intenta deshacerlo (unalias, unset -f, set +x; medido en
        // la revisión del tramo 1-septies). El mensaje no lo propone como remedio: remite a ssh_reset_shell, que sí lo arregla.
        [Theory]
        [InlineData("BASH_XTRACEFD=1; set -x", "set +x")]
        public async Task EnvioQueNoCoincide_ElRemedioQueProponeElMensajeFunciona(string preparacion, string deshacer)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            _ = await h.EjecutarAsync(destino, preparacion, esperarSegundos: 15).ComoComando();

            // Act
            ResultadoComando intento = await h.EjecutarAsync(destino, deshacer, esperarSegundos: 15, timeoutSegundos: 10).ComoComando();
            string sinReset = Testigo();
            ResultadoComando tras = await h.EjecutarAsync(destino, $"touch {sinReset}", esperarSegundos: 15, timeoutSegundos: 10).ComoComando();
            _ = h.ReiniciarShell(destino);
            string conReset = Testigo();
            ResultadoComando nuevo = await h.EjecutarAsync(destino, $"touch {conReset}", esperarSegundos: 30, timeoutSegundos: 30).ComoComando();

            // Assert
            salida.WriteLine($"deshacer: {intento.Estado} '{intento.Aviso}'; después: {tras.Estado}; tras ssh_reset_shell: {nuevo.Estado}");
            _ = intento.Estado.Should().Be(EstadoPeticionEnum.Error, "deshacer con ssh_exec tampoco se ejecuta");
            _ = File.Exists(sinReset).Should().BeFalse();
            _ = intento.Aviso.Should().Contain("ssh_reset_shell").And.NotContainAny(["set +x", "unset -f", "unalias"], "el mensaje no puede proponer un remedio que no se ejecuta");
            _ = (nuevo.Estado, File.Exists(conReset)).Should().Be((EstadoPeticionEnum.Completado, true));
        }
        private string Puerta(string nombre) =>
            $"touch {Path.Combine(_testigos, "s" + nombre)}; while [ ! -e {Path.Combine(_testigos, "g" + nombre)} ]; do sleep 0.02; done";

        private async Task EsperarFicheroAsync(string nombre)
        {
            string ruta = Path.Combine(_testigos, nombre);
            for (int k = 0; k < 1500 && !File.Exists(ruta); k++)
                await Task.Delay(20);
        }

        // Más avisos pendientes que el tope de 16 (caso Y01 de la revisión del tramo 1-septies, adoptada): 20 escrituras,
        // cada una con la línea 'x' tecleada al comando anterior pendiente de avisar (la respuesta de ese ssh_input se cancela
        // en vuelo, así que no recorre la cadena). El ssh_exec siguiente detalla los 16 más recientes y resume los otros 4
        // con su número de líneas; el de después, ninguno. Antes no decía que hubo más.
        [Fact]
        public async Task MasDe16AvisosPendientes_LaRespuestaResumeLosQueNoDetalla()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            const int N = Peticion.MaxAvisosTecleadoEnlazados + 4;
            ResultadoComando actual = await h.EjecutarAsync(destino, Puerta("0"), esperarSegundos: 0).ComoComando();
            await EsperarFicheroAsync("s0");
            for (int k = 0; k < N; k++)
            {
                Task<ResultadoEscritura> escritura = h.EscribirArchivoAsync(destino, Path.Combine(_testigos, $"w{k}"), "w\n");
                await Task.Delay(50);
                ResultadoComando siguiente = await h.EjecutarAsync(destino, Puerta((k + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)), esperarSegundos: 0).ComoComando();
                using CancellationTokenSource cancelar = new();
                Task entrada = h.EnviarEntradaAsync(actual.Id, "x", esperarSegundos: 30, cancellationToken: cancelar.Token);
                await Task.Delay(200);
                await cancelar.CancelAsync();
                await entrada.ContinueWith(_ => { }, TaskScheduler.Default);
                await File.WriteAllTextAsync(Path.Combine(_testigos, $"g{k}"), "");
                _ = await escritura;
                await EsperarFicheroAsync($"s{k + 1}");
                actual = siguiente;
            }

            await File.WriteAllTextAsync(Path.Combine(_testigos, $"g{N}"), "");

            // Act
            ResultadoComando f = await h.EjecutarAsync(destino, "echo F", esperarSegundos: 15).ComoComando();
            ResultadoComando g = await h.EjecutarAsync(destino, "echo G", esperarSegundos: 15).ComoComando();

            // Assert
            salida.WriteLine($"F: {f.Aviso}\nG: {g.Aviso}");
            string avisoF = f.Aviso ?? string.Empty;
            _ = ((avisoF.Length - avisoF.Replace("no leyó", "", StringComparison.Ordinal).Length) / "no leyó".Length).Should().Be(Peticion.MaxAvisosTecleadoEnlazados);
            _ = avisoF.Should().Contain("Además, otros 4 comandos anteriores no leyeron 4 líneas de entrada tecleada con ssh_input");
            _ = g.Aviso.Should().BeNull();
        }
    }
}
