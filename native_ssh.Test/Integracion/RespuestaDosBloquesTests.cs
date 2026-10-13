using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Contrato de las respuestas de ssh_exec, ssh_output, ssh_input y ssh_cancel (C11): dos bloques de texto MCP, siempre los
    // dos y en este orden: el control (JSON compacto en una sola línea, sin la salida) y la salida tal cual, sin escapar (vacía
    // si no hay). Con bash real (sshd local), por las herramientas y por el binario (lo que recibe de verdad el cliente).
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class RespuestaDosBloquesTests : IAsyncLifetime
    {
        // Comillas, barra invertida, tabulador, varias líneas y caracteres no ASCII: lo que dentro de un JSON se escapaba.
        private const string ComandoEspecial = "printf 'a\\tb \"c\" \\\\ d\\nlínea € 2\\n'";
        private const string SalidaEspecial = "a\tb \"c\" \\ d\nlínea € 2\n";

        private readonly SshdPropio _sshd = new(string.Empty);
        private GestorSesiones? _gestor;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (_gestor is not null)
                await _gestor.DisposeAsync();
            _sshd.Dispose();
        }

        private HerramientasSsh Herramientas()
        {
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones();
            _gestor = new GestorSesiones(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            return new HerramientasSsh(_gestor, opciones);
        }

        private static (JsonNode Control, string Salida) Bloques(CallToolResult respuesta)
        {
            _ = respuesta.IsError.Should().NotBe(true);
            _ = respuesta.Content.Should().HaveCount(2).And.AllBeOfType<TextContentBlock>();
            string control = ((TextContentBlock)respuesta.Content[0]).Text;
            _ = control.Should().StartWith("{").And.NotContain("\n", "el control va en una sola línea: es la primera del archivo si el cliente vuelca la respuesta");
            JsonNode nodo = JsonNode.Parse(control)!;
            _ = nodo.AsObject().ContainsKey("salida").Should().BeFalse("la salida va en el segundo bloque");
            return (nodo, ((TextContentBlock)respuesta.Content[1]).Text);
        }

        [Fact]
        public async Task Exec_ControlPrimeroEnUnaLineaYLaSalidaTalCual()
        {
            // Arrange
            HerramientasSsh h = Herramientas();

            // Act
            CallToolResult respuesta = await h.EjecutarAsync(_sshd.Sshd.Destino.Clave, ComandoEspecial, esperarSegundos: 30);

            // Assert
            (JsonNode control, string salida) = Bloques(respuesta);
            _ = salida.Should().Be(SalidaEspecial, "byte a byte, sin escapar");
            _ = control["estado"]!.GetValue<string>().Should().Be("Completado");
            _ = control["codigoSalida"]!.GetValue<int>().Should().Be(0);
            _ = control["siguienteByte"]!.GetValue<long>().Should().Be(Encoding.UTF8.GetByteCount(SalidaEspecial));
            _ = control["id"]!.GetValue<string>().Should().HaveLength(32);
        }

        [Fact]
        public async Task Exec_SinSalida_ElSegundoBloqueVaVacio()
        {
            // Arrange
            HerramientasSsh h = Herramientas();

            // Act
            CallToolResult respuesta = await h.EjecutarAsync(_sshd.Sshd.Destino.Clave, "true", esperarSegundos: 30);

            // Assert
            (JsonNode control, string salida) = Bloques(respuesta);
            _ = salida.Should().BeEmpty();
            _ = control["estado"]!.GetValue<string>().Should().Be("Completado");
        }

        // Una salida de varias páginas: cada ssh_output da su tramo en el segundo bloque, desde el siguienteByte del anterior, y
        // los tramos juntos son la salida completa.
        [Fact]
        public async Task Output_Paginado_LosTramosJuntosSonLaSalidaCompleta()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            StringBuilder esperada = new();
            for (int i = 1; i <= 40_000; i++)
                _ = esperada.Append(i.ToString(CultureInfo.InvariantCulture)).Append('\n');

            // Act
            (JsonNode control, string salida) = Bloques(await h.EjecutarAsync(_sshd.Sshd.Destino.Clave, "seq 1 40000", esperarSegundos: 60));
            StringBuilder juntada = new(salida);
            int paginas = 1;
            while (control["hasMasSalida"]!.GetValue<bool>())
            {
                long desde = control["siguienteByte"]!.GetValue<long>();
                (control, salida) = Bloques(await h.LeerSalidaAsync(control["id"]!.GetValue<string>(), desde));
                _ = control["desdeByte"]!.GetValue<long>().Should().Be(desde);
                _ = juntada.Append(salida);
                paginas++;
            }

            // Assert
            _ = paginas.Should().BeGreaterThan(1);
            _ = juntada.ToString().Should().Be(esperada.ToString());
        }

        [Fact]
        public async Task InputYCancel_TambienEnDosBloques()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destino = _sshd.Sshd.Destino.Clave;
            (JsonNode lector, _) = Bloques(await h.EjecutarAsync(destino, "read -r x; echo \"leído:$x\"", esperarSegundos: 30));
            (JsonNode lento, _) = Bloques(await h.EjecutarAsync(destino, "sleep 30", esperarSegundos: 0));

            // Act
            (JsonNode entrada, string salidaEntrada) = Bloques(await h.EnviarEntradaAsync(lector["id"]!.GetValue<string>(), "\"hola\" \\", esperarSegundos: 30));
            (JsonNode cancelado, string _) = Bloques(await h.CancelarAsync(lento["id"]!.GetValue<string>()));

            // Assert
            _ = entrada["estado"]!.GetValue<string>().Should().Be("Completado");
            _ = salidaEntrada.Should().Be("leído:\"hola\" \\\n");
            _ = cancelado["estado"]!.GetValue<string>().Should().Be("Cancelado");
        }

        // Lo que recibe de verdad el cliente por stdio, del binario: los dos bloques de tipo texto.
        [Fact]
        public async Task Binario_DosBloquesDeTexto()
        {
            // Arrange
            await using ClienteMcpBinario cliente = await ClienteMcpBinario.IniciarAsync(new Dictionary<string, string>
            {
                ["NATIVE_SSH_DIRECTORIO_SSH"] = _sshd.Sshd.DirectorioEstado,
                ["NATIVE_SSH_IDENTIDADES"] = _sshd.Sshd.RutaClave
            });

            // Act
            (bool isError, IReadOnlyList<(string Tipo, string Texto)> bloques) = await cliente.LlamarBloquesAsync("ssh_exec", new JsonObject { ["destino"] = _sshd.Sshd.Destino.Clave, ["comando"] = ComandoEspecial, ["esperarSegundos"] = 30 });
            (bool isErrorVacio, IReadOnlyList<(string Tipo, string Texto)> vacio) = await cliente.LlamarBloquesAsync("ssh_exec", new JsonObject { ["destino"] = _sshd.Sshd.Destino.Clave, ["comando"] = "true", ["esperarSegundos"] = 30 });

            // Assert
            _ = (isError, isErrorVacio).Should().Be((false, false));
            _ = bloques.Should().HaveCount(2);
            _ = bloques[0].Tipo.Should().Be("text");
            _ = bloques[0].Texto.Should().StartWith("{\"id\":").And.NotContain("\n").And.Contain("\"estado\":\"Completado\"").And.NotContain("\"salida\"");
            _ = bloques[1].Should().Be(("text", SalidaEspecial));
            _ = vacio.Should().HaveCount(2);
            _ = vacio[1].Should().Be(("text", string.Empty));
        }
    }
}
