using System.Diagnostics;
using System.Runtime.Versioning;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Con una entrada de NATIVE_SSH_SOLO_LECTURA que no se entiende, el binario no arranca (ni por stdio ni por HTTP): sale
    // con código 2 y solo el mensaje que la nombra, sin el volcado de pila de una excepción sin capturar.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    public sealed class ArranqueSoloLecturaTests
    {
        [Theory]
        [InlineData("root@prod@ejemplo")]
        [InlineData("prod, mal entrada")]
        [InlineData("--http", "prod:22:2222")]
        public async Task EntradaNoValida_SaleConElMensajeSinVolcadoDePila(params string[] argumentos)
        {
            // Arrange
            string configuracion = argumentos[^1];
            ProcessStartInfo inicio = new(ClienteMcpBinario.RutaBinario, argumentos[..^1])
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
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
