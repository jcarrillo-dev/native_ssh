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
    // Matriz de los caracteres especiales del terminal en modo canónico (los de 'stty -a' con sus valores por defecto, NUL y
    // el retroceso ^H, que no es VERASE) enviados con ssh_input, con el sshd local (bash real, terminal real). Para cada uno:
    // (1) tecleado por adelantado en medio de una línea, con dos órdenes detrás, a un comando que no lee; (2) al final de lo
    // tecleado (enter=false), justo antes del envío siguiente; (3) a un programa que lee una línea ('head -n 1'). Nada
    // tecleado se ejecuta, el comando siguiente se ejecuta sin esperar a su plazo, el recuento de lo descartado es exacto y
    // se ve qué recibe el programa. Lo medido con dash y el ash de busybox (otros shells que pueden ejecutar la cola: uno
    // anidado) está en el README. Política de ssh_input: Ctrl+C (VINTR), Ctrl+\ (VQUIT) y Ctrl+Z (VSUSP) se rechazan siempre
    // (para interrumpir, ssh_cancel) y Ctrl+V (VLNEXT) como último carácter con enter=false también; con secreto=true,
    // cualquier control C0 o DEL se rechaza.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    public sealed class EntradaCaracteresDeControlTests(ITestOutputHelper salida) : IAsyncLifetime
    {
        private readonly SshdPropio _sshd = new(string.Empty);
        private readonly string _testigos = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_ctrl_{Guid.NewGuid():N}")).FullName;
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

        private static async Task CalentarAsync(HerramientasSsh h, string destino) =>
            _ = (await h.EjecutarAsync(destino, "true", esperarSegundos: 60, timeoutSegundos: 60)).Estado.Should().Be(EstadoPeticionEnum.Completado);

        private string Testigo() => Path.Combine(_testigos, Guid.NewGuid().ToString("N"));

        private static string Canario() => $"Canario{Guid.NewGuid():N}"[..20];

        private static string AvisoDe(string idAnterior, int lineas) => $"El comando anterior de este shell ({idAnterior}) no leyó {SesionShell.DescribirLineas(lineas)} de entrada tecleada";

        // El comando siguiente: se ejecuta (no se lo come una espera colgada) y su aviso dice exactamente lo descartado.
        private async Task<ResultadoComando> SiguienteAsync(HerramientasSsh h, string destino, string idAnterior, int descartadas, string texto)
        {
            ResultadoComando siguiente = await h.EjecutarAsync(destino, $"echo {texto}", esperarSegundos: 15, timeoutSegundos: 10);
            salida.WriteLine($"  siguiente {siguiente.Estado} '{siguiente.Salida}' aviso '{siguiente.Aviso}'");
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Salida.Should().Be(texto + "\n");
            _ = descartadas == 0 ? siguiente.Aviso.Should().BeNull() : siguiente.Aviso.Should().Contain(AvisoDe(idAnterior, descartadas));
            return siguiente;
        }

        // nombre, carácter, código del comando con el carácter en medio (1) y al final (2) (-1: ssh_input lo rechaza),
        // líneas descartadas en (1) y (2), y lo que recibe 'head -n 1' en (3) en hexadecimal (null: el carácter lo mata o se
        // rechaza).
        public static TheoryData<string, char, int, int, int, int, string?> Caracteres => new()
        {
            // VINTR, VQUIT y VSUSP: se rechazan siempre (para interrumpir, ssh_cancel).
            { "VINTR", '\u0003', -1, -1, 0, 0, null },
            { "VQUIT", '\u001c', -1, -1, 0, 0, null },
            { "VSUSP", '\u001a', -1, -1, 0, 0, null },
            // VEOF: en medio de una línea entrega lo escrito sin salto (se junta con lo siguiente).
            { "VEOF", '\u0004', 0, 0, 2, 1, "61620a" },
            // VERASE (^?), VKILL (^U), VWERASE (^W): editan la línea a medias (solo lo tecleado).
            { "VERASE", '\u007f', 0, 0, 2, 1, "620a" },
            { "VKILL", '\u0015', 0, 0, 2, 0, "620a" },
            { "VWERASE", '\u0017', 0, 0, 2, 0, "620a" },
            // VLNEXT (^V): el siguiente carácter va literal; al final con enter=false se rechaza.
            { "VLNEXT", '\u0016', 0, -1, 2, 0, "61620a" },
            // VREPRINT (^R) y VDISCARD (^O): sin eco no hacen nada; llegan tal cual.
            { "VREPRINT", '\u0012', 0, 0, 2, 1, "6112620a" },
            { "VDISCARD", '\u000f', 0, 0, 2, 1, "610f620a" },
            // VSTART (^Q) y VSTOP (^S): con -ixon (inicialización) son caracteres normales.
            { "VSTART", '\u0011', 0, 0, 2, 1, "6111620a" },
            { "VSTOP", '\u0013', 0, 0, 2, 1, "6113620a" },
            // NUL: no termina línea (VEOL sin definir); 'read' de bash lo quita, 'head' lo pasa.
            { "NUL", '\0', 0, 0, 2, 1, "6100620a" },
            // ^H no es VERASE (que es ^?): un carácter más.
            { "BS", '\b', 0, 0, 2, 1, "6108620a" },
        };

        [Theory]
        [MemberData(nameof(Caracteres))]
        public async Task CaracterEspecial_TecleadoOLeido_NadaSeEjecutaYElRecuentoEsExacto(string nombre, char caracter, int codigoEnMedio, int codigoAlFinal, int descartadasEnMedio, int descartadasAlFinal, string? recibido)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            string uno = Testigo();
            string dos = Testigo();
            salida.WriteLine(nombre);

            // Act y Assert (1): en medio, con dos órdenes detrás, a un comando que no lee.
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 1.5", esperarSegundos: 0);
            await Task.Delay(400);
            await EnviarOEsperarRechazoAsync(h, lento.Id, $"uno{caracter}touch {uno}\ntouch {dos}", enter: true, codigoEnMedio);
            ResultadoComando fin = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 15);
            salida.WriteLine($"  (1) fin {fin.Estado} {fin.CodigoSalida} '{fin.Salida}' aviso '{fin.Aviso}'");
            _ = fin.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = fin.CodigoSalida.Should().Be(Math.Max(codigoEnMedio, 0));
            _ = await SiguienteAsync(h, destino, lento.Id, codigoEnMedio < 0 ? 0 : descartadasEnMedio, "uno");
            _ = (File.Exists(uno) || File.Exists(dos)).Should().BeFalse("nada de lo tecleado se ejecuta como orden");

            // (2): al final de lo tecleado (enter=false), lo siguiente que llega es el envío del comando siguiente.
            lento = await h.EjecutarAsync(destino, "sleep 1.5", esperarSegundos: 0);
            await Task.Delay(400);
            await EnviarOEsperarRechazoAsync(h, lento.Id, $"dos{caracter}", enter: false, codigoAlFinal);
            fin = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 15);
            salida.WriteLine($"  (2) fin {fin.Estado} {fin.CodigoSalida} '{fin.Salida}' aviso '{fin.Aviso}'");
            _ = fin.CodigoSalida.Should().Be(Math.Max(codigoAlFinal, 0));
            _ = await SiguienteAsync(h, destino, lento.Id, codigoAlFinal < 0 ? 0 : descartadasAlFinal, "dos");

            // (3): a un programa que lee una línea.
            ResultadoComando lector = await h.EjecutarAsync(destino, "head -n 1 | od -An -tx1 | tr -d ' \\n'", esperarSegundos: 0);
            await Task.Delay(400);
            await EnviarOEsperarRechazoAsync(h, lector.Id, $"a{caracter}b", enter: true, codigoEnMedio);
            if (codigoEnMedio < 0)
                _ = await h.EnviarEntradaAsync(lector.Id, "b", esperarSegundos: 15);
            fin = await h.LeerSalidaAsync(lector.Id, esperarSegundos: 15);
            salida.WriteLine($"  (3) fin {fin.Estado} {fin.CodigoSalida} '{fin.Salida}' aviso '{fin.Aviso}'");
            if (recibido is not null)
                _ = fin.Salida.Should().Be(recibido);
            _ = await SiguienteAsync(h, destino, lector.Id, 0, "tres");
        }

        private static async Task EnviarOEsperarRechazoAsync(HerramientasSsh h, string id, string texto, bool enter, int codigo)
        {
            Func<Task> enviar = () => h.EnviarEntradaAsync(id, texto, enter: enter, esperarSegundos: 0);
            if (codigo < 0)
                _ = (await enviar.Should().ThrowAsync<NativeSshException>()).Which.Message.Should().StartWith("No se envió la entrada");
            else
                _ = await enviar.Should().NotThrowAsync();
        }

        // Con secreto=true, cualquier control C0 o DEL se rechaza sin enviar nada (ni registrarlo en un mensaje).
        [Theory]
        [InlineData('\u0003')]
        [InlineData('\u0004')]
        [InlineData('\u0015')]
        [InlineData('\u0016')]
        [InlineData('\t')]
        [InlineData('\u001b')]
        [InlineData('\u007f')]
        [InlineData('\0')]
        public async Task SecretoConUnControl_SeRechazaSinEnviarNada(char control)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            string canario = Canario();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 2", esperarSegundos: 0);
            await Task.Delay(400);

            // Act
            Func<Task> enviar = () => h.EnviarEntradaAsync(lento.Id, $"{control}{canario}", esperarSegundos: 0, secreto: true);

            // Assert
            NativeSshException error = (await enviar.Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Be(HerramientasSsh.MensajeSecretoNoAdmitido).And.NotContain(canario);
            ResultadoComando fin = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 10);
            _ = fin.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = fin.CodigoSalida.Should().Be(0);
            ResultadoComando siguiente = await SiguienteAsync(h, destino, lento.Id, 0, "siguiente");
            _ = (fin.Salida + siguiente.Salida).Should().NotContain(canario);
        }

        // (Q03 de la auditoría del tramo 1-quater) Ctrl+C con ssh_input, solo o con texto detrás, recién lanzado el comando o
        // con él ya en marcha: se rechaza sin enviar nada, el comando sigue y termina como siempre y nada se ejecuta como orden
        // (antes, Ctrl+C abortaba la línea del protocolo y lo de después se ejecutaba).
        [Theory]
        [InlineData("\u0003", false, 500)]
        [InlineData("uno\u0003touch {0}", true, 500)]
        [InlineData("\u0003touch {0}", true, 0)]
        [InlineData("\u001ctouch {0}", true, 500)]
        public async Task CtrlCConSshInput_SeRechazaYNadaSeEjecuta(string plantilla, bool enter, int esperaMs)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            string testigo = Testigo();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 1.5; echo fin", esperarSegundos: 0, timeoutSegundos: 20);
            await Task.Delay(esperaMs);

            // Act
            Func<Task> enviar = () => h.EnviarEntradaAsync(lento.Id, string.Format(System.Globalization.CultureInfo.InvariantCulture, plantilla, testigo), enter: enter, esperarSegundos: 0);
            NativeSshException error = (await enviar.Should().ThrowAsync<NativeSshException>()).Which;
            ResultadoComando fin = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 15);
            _ = await SiguienteAsync(h, destino, lento.Id, 0, "siguiente");
            await Task.Delay(300);

            // Assert
            _ = error.Message.Should().Be(HerramientasSsh.MensajeSenalDelTerminal);
            _ = File.Exists(testigo).Should().BeFalse();
            _ = (fin.Estado, fin.CodigoSalida, fin.Salida).Should().Be((EstadoPeticionEnum.Completado, 0, "fin\n"));
        }

        // ssh_cancel para un bucle del propio bash, una función, una sustitución de órdenes y una tubería (la interrupción con
        // la línea de recuperación aparte): el comando termina cancelado, sin cerrar el canal, y el shell sigue (variables).
        [Theory]
        [InlineData("while :; do :; done")]
        [InlineData("while :; do sleep 0.2; done")]
        [InlineData("f() { while :; do sleep 0.1; done; echo NO; }; f; echo NO")]
        [InlineData("x=$(sleep 30); echo NO")]
        [InlineData("sleep 30 | cat; echo NO")]
        public async Task SshCancel_ParaElComandoYElShellSigue(string comando)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            _ = await h.EjecutarAsync(destino, "export NS_VARIABLE=conservada", esperarSegundos: 10);
            ResultadoComando bucle = await h.EjecutarAsync(destino, comando, esperarSegundos: 0, timeoutSegundos: 60);
            await Task.Delay(500);

            // Act
            _ = await h.CancelarAsync(bucle.Id);
            ResultadoComando final = await h.LeerSalidaAsync(bucle.Id, esperarSegundos: 15);
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo \"$NS_VARIABLE\"", esperarSegundos: 10);

            // Assert
            salida.WriteLine($"final {final.Estado} '{final.Salida}' aviso '{final.Aviso}'; siguiente '{siguiente.Salida}' aviso '{siguiente.Aviso}'");
            _ = final.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = final.Aviso.Should().Contain("se envió Ctrl+C al proceso").And.NotContain("se cerró");
            _ = final.Salida.Should().NotContain("NO");
            _ = siguiente.Salida.Should().Be("conservada\n");
            _ = siguiente.Aviso.Should().BeNull();
        }

        // (Q05) Un programa que atrapa Ctrl+C y lee una o dos líneas: al vencer el plazo, el Ctrl+C de la interrupción lo
        // despierta y lee lo que llega (la línea vacía del salto y, el segundo, el centinela); la cola no cuenta como tecleada
        // ninguna línea del protocolo y nadie avisa de líneas que nadie tecleó.
        [Theory]
        [InlineData("read -r r")]
        [InlineData("read -r r; read -r r2")]
        public async Task ProgramaQueAtrapaCtrlCYLee_NoAvisaDeLineasQueNadieTecleo(string lectura)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);

            // Act
            ResultadoComando interrumpido = await h.EjecutarAsync(destino, $"trap '{lectura}; echo leido' INT; sleep 30; trap - INT", esperarSegundos: 30, timeoutSegundos: 3);
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 15);

            // Assert
            salida.WriteLine($"interrumpido {interrumpido.Estado} '{interrumpido.Salida}' aviso '{interrumpido.Aviso}'; siguiente '{siguiente.Salida}' aviso '{siguiente.Aviso}'");
            _ = (interrumpido.Aviso + siguiente.Aviso).Should().NotContain("no leyó");
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Salida.Should().Be("siguiente\n");
        }

        // Una señal que el usuario atrapa en el shell llega mientras la cola espera el centinela ('read' interrumpido, estado
        // > 128): no es una línea tecleada y nadie avisa de nada.
        [Theory]
        [InlineData("trap 'true' USR1; ( sleep 1; kill -USR1 $$ ) & echo lanzado")]
        [InlineData("trap 'true' USR1; ( for k in 1 2 3; do sleep 0.4; kill -USR1 $$; done ) & echo lanzado")]
        [InlineData("trap 'true' CHLD; sleep 1 & echo lanzado")]
        public async Task SenalAtrapadaDuranteLaEspera_NoCuentaComoLinea(string comando)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);

            // Act
            ResultadoComando lanzado = await h.EjecutarAsync(destino, comando, esperarSegundos: 10);
            await Task.Delay(2500);
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "trap - USR1 CHLD; echo siguiente", esperarSegundos: 10);

            // Assert
            salida.WriteLine($"lanzado '{lanzado.Salida}'; siguiente '{siguiente.Salida}' aviso '{siguiente.Aviso}'");
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Aviso.Should().BeNull();
        }

        // (Q01, CV4) Mil Ctrl+D seguidos y una orden a un comando que no lee: el tope de EOF seguidos termina el shell, que nunca
        // vuelve a leer órdenes del terminal; la orden no se ejecuta y nadie dice que se descartó sin ejecutarse.
        [Fact]
        public async Task MilCtrlDSeguidosYUnaOrden_ElShellTerminaYLaOrdenNoSeEjecuta()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            string testigo = Testigo();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 2", esperarSegundos: 0);
            await Task.Delay(500);

            // Act
            _ = await h.EnviarEntradaAsync(lento.Id, new string('\u0004', 1000) + $"touch {testigo}", esperarSegundos: 0);
            _ = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 15);
            ResultadoComando siguiente = await h.EjecutarAsync(destino, "echo siguiente", esperarSegundos: 30);
            ResultadoComando otro = await h.EjecutarAsync(destino, "echo otro", esperarSegundos: 30);
            await Task.Delay(300);

            // Assert
            salida.WriteLine($"siguiente {siguiente.Estado} '{siguiente.Salida}' aviso '{siguiente.Aviso}'; otro {otro.Estado} '{otro.Salida}' aviso '{otro.Aviso}'");
            _ = File.Exists(testigo).Should().BeFalse();
            _ = (siguiente.Aviso + otro.Aviso).Should().NotContain("ninguna se ejecutó");
            _ = otro.Salida.Should().Be("otro\n");
        }

        // (Q02, CV3) Una línea a medias (enter=false) tan larga como el búfer de línea del terminal (4095) o más, o dos que se
        // acumulan: el salto delante del centinela la termina, el comando siguiente se ejecuta y el recuento es 1.
        [Theory]
        [InlineData(4000, 1)]
        [InlineData(4094, 1)]
        [InlineData(4095, 1)]
        [InlineData(8000, 1)]
        [InlineData(3000, 2)]
        public async Task LineaAMediasMuyLarga_ElComandoSiguienteSeEjecutaYCuentaUna(int bytes, int veces)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            string testigo = Testigo();
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 2", esperarSegundos: 0);
            await Task.Delay(500);
            for (int i = 0; i < veces; i++)
                _ = await h.EnviarEntradaAsync(lento.Id, new string('x', bytes), enter: false, esperarSegundos: 0);
            _ = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 15);

            // Act
            ResultadoComando siguiente = await h.EjecutarAsync(destino, $"touch {testigo}; echo siguiente", esperarSegundos: 20, timeoutSegundos: 10);

            // Assert
            salida.WriteLine($"siguiente {siguiente.Estado} '{siguiente.Salida}' aviso '{siguiente.Aviso}'");
            _ = (siguiente.Estado, File.Exists(testigo)).Should().Be((EstadoPeticionEnum.Completado, true));
            _ = siguiente.Salida.Should().Be("siguiente\n");
            _ = siguiente.Aviso.Should().Contain(AvisoDe(lento.Id, 1));
        }

        // Las líneas vacías tecleadas se cuentan; la que deja el salto que precede al centinela, no: el recuento es exacto.
        [Theory]
        [InlineData("", true, 1)]
        [InlineData("\n", true, 2)]
        [InlineData("\n\n", false, 2)]
        [InlineData("uno", true, 1)]
        [InlineData("uno\n", false, 1)]
        public async Task LineasVacias_SeCuentanSalvoLaQueDejaElSalto(string texto, bool enter, int descartadas)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 1.5", esperarSegundos: 0);
            await Task.Delay(400);

            // Act
            _ = await h.EnviarEntradaAsync(lento.Id, texto, enter: enter, esperarSegundos: 0);
            _ = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 15);

            // Assert
            _ = await SiguienteAsync(h, destino, lento.Id, descartadas, "siguiente");
        }

        // (Q06, CV7) El aviso de lo descartado sale una sola vez entre todas las respuestas: el final del comando que lo trae y
        // las del comando siguiente (antes salía en cada respuesta del siguiente y otra vez en el final).
        [Fact]
        public async Task AvisoDelAnterior_UnaSolaVezEntreTodasLasRespuestas()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            ResultadoComando c1 = await h.EjecutarAsync(destino, "sleep 1.5", esperarSegundos: 0);
            await Task.Delay(400);
            _ = await h.EnviarEntradaAsync(c1.Id, "uno\ndos", esperarSegundos: 0);
            _ = await h.LeerSalidaAsync(c1.Id, esperarSegundos: 15);

            // Act
            ResultadoComando c2 = await h.EjecutarAsync(destino, "sleep 2", esperarSegundos: 0);
            await Task.Delay(300);
            ResultadoComando c3 = await h.EjecutarAsync(destino, "sleep 4; echo c3", esperarSegundos: 0);
            List<string?> avisos = [c2.Aviso, c3.Aviso];
            for (int i = 0; i < 3; i++)
                avisos.Add((await h.LeerSalidaAsync(c3.Id, esperarSegundos: 1)).Aviso);
            avisos.Add((await h.LeerSalidaAsync(c3.Id, esperarSegundos: 10)).Aviso);
            avisos.Add((await h.LeerSalidaAsync(c2.Id, esperarSegundos: 5)).Aviso);
            avisos.Add((await h.LeerSalidaAsync(c2.Id, esperarSegundos: 5)).Aviso);

            // Assert
            foreach (string? aviso in avisos)
                salida.WriteLine($"aviso '{aviso}'");
            _ = avisos.Count(a => a is not null && a.Contains("no leyó 2 líneas", StringComparison.Ordinal)).Should().Be(1);
        }

        // ^S tecleado: sin -ixon detendría la salida del terminal y el marcador no llegaría (el comando esperaría su plazo).
        [Fact]
        public async Task CtrlSTecleado_NoDetieneLaSalida()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            await CalentarAsync(h, destino);
            ResultadoComando lento = await h.EjecutarAsync(destino, "sleep 1; echo fin", esperarSegundos: 0, timeoutSegundos: 10);
            await Task.Delay(300);

            // Act
            _ = await h.EnviarEntradaAsync(lento.Id, "\u0013", enter: false, esperarSegundos: 0);
            ResultadoComando fin = await h.LeerSalidaAsync(lento.Id, esperarSegundos: 8);

            // Assert
            _ = (fin.Estado, fin.Salida).Should().Be((EstadoPeticionEnum.Completado, "fin\n"));
        }
    }
}
