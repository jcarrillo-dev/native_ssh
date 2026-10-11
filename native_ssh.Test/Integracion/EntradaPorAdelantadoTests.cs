using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using Xunit.Abstractions;

namespace NativeSsh.Test.Integracion
{
    // Lo tecleado con ssh_input que el comando no lee (enviado antes de que lo pida, varias líneas, o un ssh_input con '\n' o
    // '\r' dentro) queda en el terminal al terminar el comando. Antes, el 'read' de la cola consumía la primera línea y bash
    // ejecutaba las demás como órdenes, sin que ninguna respuesta lo dijera (y con secreto=true, la segunda línea salía sin
    // enmascarar en el 'orden no encontrada' del comando siguiente). Ahora la cola descarta las líneas completas pendientes
    // antes del marcador, sin ejecutarlas, y la respuesta dice cuántas (ConstructorComando, MarcadorFin); secreto=true
    // rechaza el texto de varias líneas. Con el sshd local (bash real, terminal real).
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
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

        private string Testigo() => Path.Combine(_testigos, Guid.NewGuid().ToString("N"));

        private static string Canario() => $"Canario{Guid.NewGuid():N}"[..20];

        // Un ssh_input con dos líneas (separadas por '\n' o '\r') a un comando que no lee: ninguna se ejecuta y la respuesta
        // que trae el final del comando dice que se descartaron 2; el comando siguiente no ve nada de ellas.
        [Theory]
        [InlineData("\n")]
        [InlineData("\r")]
        public async Task DosLineasEnUnaEntrada_NoSeEjecutanYSeAvisa(string separador)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            string testigo = Testigo();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 2", esperarSegundos: 1);

            // Act
            ResultadoComando entrada = await h.EnviarEntradaAsync(lento.Id, $"respuesta1{separador}touch {testigo}", esperarSegundos: 8);
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 10);
            await Task.Delay(300);

            // Assert
            salida.WriteLine($"entrada {entrada.Estado} aviso '{entrada.Aviso}'; siguiente '{siguiente.Salida}' aviso '{siguiente.Aviso}'");
            _ = File.Exists(testigo).Should().BeFalse("la segunda línea de lo tecleado no debe ejecutarse como orden");
            _ = entrada.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = entrada.CodigoSalida.Should().Be(0);
            _ = entrada.Aviso.Should().Contain("no leyó 2 líneas de entrada tecleada").And.Contain("se descartaron sin ejecutarse");
            _ = siguiente.Salida.Should().Be("siguiente\n");
            _ = siguiente.Aviso.Should().BeNull("el final del comando anterior ya se devolvió con su aviso");
        }

        // Dos ssh_input de una línea cada uno, sin lector: tampoco se ejecuta el segundo.
        [Fact]
        public async Task DosEntradasDeUnaLinea_NoSeEjecutanYSeAvisa()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            string testigo = Testigo();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 2", esperarSegundos: 1);

            // Act
            _ = await h.EnviarEntradaAsync(lento.Id, "respuesta1", esperarSegundos: 0);
            ResultadoComando entrada = await h.EnviarEntradaAsync(lento.Id, $"touch {testigo}", esperarSegundos: 8);
            _ = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 10);
            await Task.Delay(300);

            // Assert
            _ = File.Exists(testigo).Should().BeFalse("una segunda entrada tecleada por adelantado no debe ejecutarse como orden");
            _ = entrada.Aviso.Should().Contain("no leyó 2 líneas");
        }

        // El LLM no espera el final (esperarSegundos=0) y lanza otro comando: la respuesta de ese otro avisa de lo descartado
        // por el anterior, y nada de lo tecleado sale en su salida (antes: «orden no encontrada» con la segunda línea).
        [Fact]
        public async Task SinVerElFinal_ElComandoSiguienteAvisaYNoMuestraNada()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            string segunda = Canario();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 2", esperarSegundos: 0);
            await Task.Delay(800);

            // Act
            _ = await h.EnviarEntradaAsync(lento.Id, $"ClaveUno\n{segunda}", esperarSegundos: 0);
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 15);
            ResultadoComando previo = await h.LeerSalidaAsync(lento.Id);
            ResultadoComando otro = await h.EjecutarAsync(destino, "echo otro", esperarSegundos: 10);

            // Assert
            salida.WriteLine($"siguiente '{siguiente.Salida}' aviso '{siguiente.Aviso}'; previo aviso '{previo.Aviso}'; otro aviso '{otro.Aviso}'");
            _ = siguiente.Salida.Should().Be("siguiente\n");
            _ = previo.Salida.Should().NotContain(segunda);
            _ = siguiente.Aviso.Should().Contain($"El comando anterior de este shell ({lento.Id}) no leyó 2 líneas").And.Contain("sin ejecutarse");
            _ = previo.Aviso.Should().Contain("no leyó 2 líneas");
            _ = otro.Aviso.Should().BeNull();
        }

        // Una línea a medias (enter=false) no se ve antes del marcador: no se cuenta y la consume el 'read' final con el salto
        // del envío siguiente, sin ejecutarse; las líneas completas de delante sí se descartan y se cuentan.
        [Theory]
        [InlineData("")]
        [InlineData("l1\nl2\n")]
        public async Task LineaAMedias_NiSeEjecutaNiSeCuenta(string completas)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            string testigo = Testigo();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 2; (exit 7)", esperarSegundos: 1);

            // Act
            ResultadoComando entrada = await h.EnviarEntradaAsync(lento.Id, $"{completas}touch {testigo}", enter: false, esperarSegundos: 8);
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 10);
            await Task.Delay(300);

            // Assert
            salida.WriteLine($"entrada {entrada.Estado} {entrada.CodigoSalida} aviso '{entrada.Aviso}'; siguiente '{siguiente.Salida}' {siguiente.CodigoSalida}");
            _ = File.Exists(testigo).Should().BeFalse();
            _ = entrada.CodigoSalida.Should().Be(7);
            _ = siguiente.Salida.Should().Be("siguiente\n");
            _ = siguiente.CodigoSalida.Should().Be(0);
            _ = completas.Length == 0 ? entrada.Aviso.Should().BeNull() : entrada.Aviso.Should().Contain("no leyó 2 líneas");
        }

        // El comando lee una línea y termina: solo se descarta (y se cuenta) la que no leyó; el código es el del comando.
        [Fact]
        public async Task ComandoQueLeeUnaLinea_SeDescartaSoloElResto()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            string testigo = Testigo();
            ResultadoComando lector = await h.EjecutarAsync(destino, "sleep 2; read -r a; echo \"leido:$a\"; exit_code() { return 3; }; exit_code", esperarSegundos: 1);

            // Act
            ResultadoComando entrada = await h.EnviarEntradaAsync(lector.Id, $"uno\ntouch {testigo}", esperarSegundos: 8);
            await Task.Delay(300);

            // Assert
            salida.WriteLine($"entrada {entrada.Estado} {entrada.CodigoSalida} '{entrada.Salida}' aviso '{entrada.Aviso}'");
            _ = File.Exists(testigo).Should().BeFalse();
            _ = entrada.Salida.Should().Contain("leido:uno");
            _ = entrada.CodigoSalida.Should().Be(3);
            _ = entrada.Aviso.Should().Contain("no leyó 1 línea de entrada");
        }

        // Muchas líneas de una vez (más de lo que cabe en una línea del terminal en modo canónico, 4095 bytes): se descartan
        // todas y el recuento es exacto.
        [Fact]
        public async Task MuchasLineas_SeDescartanTodasYSeCuentan()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            string testigo = Testigo();
            string texto = string.Join('\n', Enumerable.Range(0, 300).Select(i => $"linea {i} {new string('x', 40)}")) + $"\ntouch {testigo}";
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 2", esperarSegundos: 1);

            // Act
            ResultadoComando entrada = await h.EnviarEntradaAsync(lento.Id, texto, esperarSegundos: 10);
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 10);
            await Task.Delay(300);

            // Assert
            salida.WriteLine($"entrada {entrada.Estado} aviso '{entrada.Aviso}'; siguiente '{siguiente.Salida}'");
            _ = File.Exists(testigo).Should().BeFalse();
            _ = entrada.Aviso.Should().Contain("no leyó 301 líneas");
            _ = siguiente.Salida.Should().Be("siguiente\n");
        }

        // secreto=true con un salto de línea en medio: se rechaza sin enviar nada (ni la primera línea) y sin el texto en el
        // mensaje; el comando sigue y termina sin nada descartado.
        [Theory]
        [InlineData("\n", true)]
        [InlineData("\r", true)]
        [InlineData("\r\n", true)]
        [InlineData("\n", false)]
        [InlineData("\r\n", false)]
        public async Task SecretoDeVariasLineas_SeRechazaSinEnviarNada(string separador, bool isEnter)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            string testigo = Testigo();
            string canario = Canario();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 2", esperarSegundos: 1);

            // Act
            Func<Task> enviar = () => h.EnviarEntradaAsync(lento.Id, $"{canario}{separador}touch {testigo}", enter: isEnter, esperarSegundos: 0, secreto: true);

            // Assert
            NativeSshException error = (await enviar.Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Be(HerramientasSsh.MensajeSecretoDeVariasLineas).And.NotContain(canario);
            ResultadoComando fin = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 8);
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 10);
            await Task.Delay(300);
            _ = File.Exists(testigo).Should().BeFalse();
            _ = fin.Aviso.Should().BeNull("no se envió nada");
            _ = siguiente.Salida.Should().Be("siguiente\n");
        }

        // El salto final no cuenta: con enter=false, un '\n' o un '\r' al final del secreto se admite y llega al comando.
        [Theory]
        [InlineData("\n")]
        [InlineData("\r")]
        public async Task SecretoConSaltoFinalYSinEnter_SeAdmite(string salto)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            string canario = Canario();
            ResultadoComando lector = await h.EjecutarAsync(destino, "read -r x; echo \"longitud:${#x}\"", esperarSegundos: 1);
            await Task.Delay(500);

            // Act
            ResultadoComando entrada = await h.EnviarEntradaAsync(lector.Id, canario + salto, enter: false, esperarSegundos: 8, secreto: true);

            // Assert
            salida.WriteLine($"entrada {entrada.Estado} '{entrada.Salida}' aviso '{entrada.Aviso}'");
            _ = entrada.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = entrada.Salida.Should().Contain("longitud:20");
            _ = entrada.Aviso.Should().NotContain("no leyó");
        }
    }
}
