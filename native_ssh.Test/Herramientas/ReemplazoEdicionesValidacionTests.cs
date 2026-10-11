using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Sesion.Falsos;

namespace NativeSsh.Test.Herramientas
{
    // ssh_replace_file_content con 'ediciones' (CI5): lo que se rechaza antes de enviar nada, cómo llega la lista por JSON
    // (contexto generado para AOT) y lo que dice la descripción.
    public sealed class ReemplazoEdicionesValidacionTests : IAsyncDisposable
    {
        private readonly FabricaFalsa _fabrica = new(_ => new RespuestaFalsa("ok\n"));
        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;

        public ReemplazoEdicionesValidacionTests()
        {
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana" };
            _gestor = new GestorSesiones(_fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            _herramientas = new HerramientasSsh(_gestor, opciones);
        }

        public ValueTask DisposeAsync() => _gestor.DisposeAsync();

        [Fact]
        public async Task Ediciones_Invalidas_SeRechazanSinEnviarNada()
        {
            // Arrange
            (string Mensaje, Func<Task> Llamada)[] casos =
            [
                ("Usa 'ediciones' o targetContent/replacementContent", () => _herramientas.ReemplazarContenidoArchivoAsync("servidor", "a.txt", "x", "y", ediciones: [new EdicionTexto("a", "b")])),
                ("Usa 'ediciones' o targetContent/replacementContent", () => _herramientas.ReemplazarContenidoArchivoAsync("servidor", "a.txt", lineaInicio: 1, lineaFin: 2, ediciones: [new EdicionTexto("a", "b")])),
                ("Usa 'ediciones' o targetContent/replacementContent", () => _herramientas.ReemplazarContenidoArchivoAsync("servidor", "a.txt", allowMultiple: true, ediciones: [new EdicionTexto("a", "b")])),
                ("Edición 2 de 2: su targetContent no puede estar vacío", () => _herramientas.ReemplazarContenidoArchivoAsync("servidor", "a.txt", ediciones: [new EdicionTexto("a", "b"), new EdicionTexto(string.Empty, "c")])),
                ("El targetContent de la edición 1 contiene un carácter UTF-16 inválido", () => _herramientas.ReemplazarContenidoArchivoAsync("servidor", "a.txt", ediciones: [new EdicionTexto("a\ud800", "b")])),
                ("El texto a reemplazar (targetContent) no puede estar vacío", () => _herramientas.ReemplazarContenidoArchivoAsync("servidor", "a.txt")),
                ("El texto a reemplazar (targetContent) no puede estar vacío", () => _herramientas.ReemplazarContenidoArchivoAsync("servidor", "a.txt", ediciones: [])),
            ];

            // Act & Assert
            foreach ((string mensaje, Func<Task> llamada) in casos)
                _ = (await FluentActions.Awaiting(llamada).Should().ThrowAsync<NativeSshException>()).Which.Message.Should().StartWith(mensaje);
            _ = _fabrica.Ultima.Should().BeNull();
        }

        // La lista llega del cliente MCP como JSON con los nombres en camelCase; allowMultiple es opcional.
        [Fact]
        public void Ediciones_DesdeJson_SeLeenConElContextoGenerado()
        {
            // Act
            EdicionTexto[]? ediciones = JsonSerializer.Deserialize("""[{"targetContent":"a","replacementContent":"b"},{"targetContent":"c","replacementContent":"d","allowMultiple":true}]""", ContextoJsonNativeSsh.Default.EdicionTextoArray);

            // Assert
            _ = ediciones.Should().Equal(new EdicionTexto("a", "b"), new EdicionTexto("c", "d", AllowMultiple: true));
        }

        [Fact]
        public void Descripcion_ExplicaEdicionesTodasONinguna()
        {
            // Act
            MethodInfo metodo = typeof(HerramientasSsh).GetMethod(nameof(HerramientasSsh.ReemplazarContenidoArchivoAsync))!;
            string herramienta = metodo.GetCustomAttribute<DescriptionAttribute>()!.Description;
            string parametro = metodo.GetParameters().Single(p => p.Name == "ediciones").GetCustomAttribute<DescriptionAttribute>()!.Description;

            // Assert
            _ = herramienta.Should().Contain("usa SIEMPRE 'ediciones' en una sola llamada").And.Contain("todas o ninguna").And.Contain("reemplazos (el total)");
            _ = parametro.Should().Contain("{targetContent, replacementContent, allowMultiple").And.Contain("No se combina con targetContent");
        }
    }
}
