using System.Diagnostics;
using System.Text;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Con una entrada de NATIVE_SSH_SOLO_LECTURA que no se entiende, el binario no arranca (ni por stdio ni por HTTP): sale
    // con código 2 y solo el mensaje que la nombra, sin el volcado de pila de una excepción sin capturar. El mensaje llega en
    // UTF-8 (también en Windows, sea cual sea la página de códigos de la consola).
    [Trait("Categoria", "Integracion")]
    public sealed class ArranqueSoloLecturaTests
    {
        [Theory]
        [InlineData("root@prod@ejemplo")]
        [InlineData("prod, mal entrada")]
        [InlineData("root@.")]
        [InlineData("--http", "prod:22:2222")]
        public async Task EntradaNoValida_SaleConElMensajeSinVolcadoDePila(params string[] argumentos)
        {
            // Arrange
            string configuracion = argumentos[^1];
            ProcessStartInfo inicio = new(ClienteMcpBinario.RutaBinario, argumentos[..^1])
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            };
            inicio.Environment[DestinosSoloLectura.Variable] = configuracion;
            _ = DestinosSoloLectura.TryParsear(configuracion, out _, out string? esperado).Should().BeFalse();

            // Act
            using Process proceso = Process.Start(inicio)!;
            Task<string> errores = proceso.StandardError.ReadToEndAsync();
            Task<string> salida = proceso.StandardOutput.ReadToEndAsync();
            using CancellationTokenSource plazo = new(TimeSpan.FromSeconds(30));
            await proceso.WaitForExitAsync(plazo.Token);

            // Assert
            _ = proceso.ExitCode.Should().Be(2);
            _ = (await errores).Should().Be(esperado + Environment.NewLine);
            _ = (await salida).Should().BeEmpty();
        }
    }
}
