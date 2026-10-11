using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;
using Xunit.Abstractions;

namespace NativeSsh.Test.Integracion
{
    // Dónde puede acabar el texto de ssh_input (CI1/CT4). Medido antes del cambio (c1/medir_fuga_input.py): con
    // NATIVE_SSH_NIVEL_LOG=Trace, el SDK de MCP escribía en stderr el mensaje JSON-RPC completo de la llamada (con el texto) y
    // el de la respuesta; con Information, nada. ssh_status no lo muestra (solo el comando), pero la salida que devuelven
    // ssh_input y ssh_output sí, si el programa lo repite: el terminal se inicializa sin eco (stty -echo), pero un programa
    // puede reactivarlo (stty echo, un readline) o imprimirlo (cat, echo). Ahora: el SDK nunca escribe en Trace (Arranque:
    // ni el texto ni las respuestas llegan a los logs, con secreto o sin él), y con secreto=true sus apariciones en la salida
    // se sustituyen por '*'. Las pruebas en espejo (secreto=false) demuestran que el canario sí habría salido.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    public sealed class EntradaSecretaTests(ITestOutputHelper salida) : IAsyncLifetime
    {
        // Lee una línea con el eco del terminal reactivado y dice su longitud (que el programa la recibió entera).
        private const string ComandoConEco = "stty echo; read -r x; stty -echo; echo \"longitud:${#x}\"";

        private readonly SshdPropio _sshd = new(string.Empty);
        private GestorSesiones? _gestor;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (_gestor is not null)
                await _gestor.DisposeAsync();
            _sshd.Dispose();
        }

        private static string Canario() => $"Canario{Guid.NewGuid():N}"[..20];

        private HerramientasSsh Herramientas()
        {
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones();
            _gestor = new GestorSesiones(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            return new HerramientasSsh(_gestor, opciones);
        }

        private Dictionary<string, string> EntornoBinario() => new()
        {
            ["NATIVE_SSH_NIVEL_LOG"] = "Trace",
            ["NATIVE_SSH_DIRECTORIO_SSH"] = _sshd.Sshd.DirectorioEstado,
            ["NATIVE_SSH_IDENTIDADES"] = _sshd.Sshd.RutaClave
        };

        [Theory]
        [InlineData(ComandoConEco, false)]
        [InlineData(ComandoConEco, true)]
        [InlineData("head -n 1", false)]
        [InlineData("head -n 1", true)]
        public async Task EcoOProgramaQueLoImprime_ConSecretoNoApareceEnLaSalida(string comando, bool isSecreto)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            string canario = Canario();
            ResultadoComando lectura = await herramientas.EjecutarAsync(destino, comando, esperarSegundos: 2);

            // Act
            ResultadoComando entrada = await herramientas.EnviarEntradaAsync(lectura.Id, canario, esperarSegundos: 10, secreto: isSecreto);
            ResultadoComando completa = await herramientas.LeerSalidaAsync(lectura.Id, 0);
            salida.WriteLine($"entrada: {entrada.Salida}| aviso: {entrada.Aviso}\ncompleta: {completa.Salida}");

            // Assert
            _ = entrada.Estado.Should().Be(EstadoPeticionEnum.Completado);
            if (isSecreto)
            {
                string mascara = new('*', canario.Length);
                _ = entrada.Salida.Should().NotContain(canario[..4]).And.Contain(mascara);
                _ = completa.Salida.Should().NotContain(canario[..4]).And.Contain(mascara);
                _ = entrada.Aviso.Should().Contain(HerramientasSsh.AvisoEntradaSecreta);
            }
            else
            {
                _ = entrada.Salida.Should().Contain(canario, "sin secreto, el eco o el programa lo devuelven: la prueba detecta la fuga");
                _ = completa.Salida.Should().Contain(canario);
                _ = entrada.Aviso.Should().BeNull();
            }

            if (comando == ComandoConEco)
                _ = completa.Salida.Should().EndWith($"longitud:{canario.Length}\n", "el programa recibe el texto entero");
        }

        // Por el binario con NATIVE_SSH_NIVEL_LOG=Trace: el texto de ssh_input no aparece en stderr con secreto ni sin él
        // (tampoco cuando la llamada falla: id inexistente), ni en ssh_status; con secreto, en ninguna respuesta.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Binario_ConLogsEnTrace_ElTextoNoLlegaALosLogs(bool isSecreto)
        {
            // Arrange
            string destino = _sshd.Sshd.Destino.Clave;
            string canario = Canario();
            string canarioFallido = Canario();
            await using ClienteMcpBinario cliente = await ClienteMcpBinario.IniciarAsync(EntornoBinario());
            (bool _, string exec) = await cliente.LlamarAsync("ssh_exec", new JsonObject { ["destino"] = destino, ["comando"] = ComandoConEco, ["esperarSegundos"] = 2 });
            string id = JsonNode.Parse(exec)!["id"]!.GetValue<string>();

            // Act
            (bool isErrorEntrada, string entrada) = await cliente.LlamarAsync("ssh_input", new JsonObject { ["id"] = id, ["texto"] = canario, ["esperarSegundos"] = 10, ["secreto"] = isSecreto });
            (bool isErrorFallida, string _) = await cliente.LlamarAsync("ssh_input", new JsonObject { ["id"] = "0123456789abcdef0123456789abcdef", ["texto"] = canarioFallido, ["secreto"] = isSecreto });
            (bool _, string estado) = await cliente.LlamarAsync("ssh_status", []);
            string respuestas = cliente.Salida;
            string errores = await cliente.TerminarAsync();
            salida.WriteLine($"entrada: {entrada}\nstderr: {errores.Length} caracteres");

            // Assert
            _ = isErrorEntrada.Should().BeFalse(entrada);
            _ = isErrorFallida.Should().BeTrue();
            _ = errores.Should().Contain("dbug:", "los logs están en Trace (y Debug)");
            _ = errores.Should().NotContain(canario).And.NotContain(canarioFallido);
            _ = estado.Should().NotContain(canario);
            _ = respuestas.Should().NotContain(canarioFallido);
            _ = isSecreto
                ? respuestas.Should().NotContain(canario[..4])
                : respuestas.Should().Contain(canario, "sin secreto, el eco del terminal lo devuelve: la prueba detecta la fuga");
        }

        // Tecleado por adelantado (CB1): el comando no lee la entrada, así que la línea la consume el 'read' de la cola del
        // protocolo (ConstructorComando.EscribirCola) al terminar. No debe quedar en ninguna variable del shell (antes quedaba
        // en __ns_d hasta el comando siguiente, y 'declare -p' o 'set' lo devolvían tal cual), ni en el historial, ni en $_,
        // ni salir en la respuesta del comando siguiente.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TecleadoPorAdelantado_NoQuedaEnElShellNiLoDevuelveElComandoSiguiente(bool isSecreto)
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            string canario = Canario();
            ResultadoComando lento = await herramientas.EjecutarAsync(destino, "sleep 3", esperarSegundos: 1);

            // Act
            ResultadoComando entrada = await herramientas.EnviarEntradaAsync(lento.Id, canario, esperarSegundos: 6, secreto: isSecreto);
            // El canario se busca partido en dos: el comando no lo contiene.
            string busqueda = $"printf '%s%s' '{canario[..10]}' '{canario[10..]}'";
            ResultadoComando siguiente = await herramientas.EjecutarAsync(destino,
                $"history | grep -cF \"$({busqueda})\"; printf '[%s]\\n' \"$_\"; declare -p __ns_d 2>/dev/null; set | grep -cF \"$({busqueda})\"; env | grep -cF \"$({busqueda})\"; echo fin", esperarSegundos: 10);
            salida.WriteLine($"entrada: {entrada.Estado} '{entrada.Salida}'\nsiguiente: '{siguiente.Salida}'");

            // Assert
            _ = entrada.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.CodigoSalida.Should().Be(0);
            _ = siguiente.Salida.Should().NotContain(canario[..10]).And.NotContain("__ns_d").And.EndWith("0\n0\nfin\n").And.StartWith("0\n[");
        }
    }
}
