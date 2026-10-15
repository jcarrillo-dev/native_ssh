using System.Runtime.Versioning;
using FluentAssertions;
using NativeSsh.Herramientas;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Por el binario y por stdio, la respuesta a initialize lleva las instrucciones del servidor tal cual (la de HTTP, en
    // ModoHttpTokenTests). No hace falta ningún servidor SSH: solo se inicializa.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [Trait("Categoria", "SoloLinux")]
    public sealed class InstruccionesInitializeTests
    {
        [Fact]
        public async Task Stdio_InitializeLlevaLasInstrucciones()
        {
            // Act
            await using ClienteMcpBinario cliente = await ClienteMcpBinario.IniciarAsync(new Dictionary<string, string>());

            // Assert
            _ = cliente.Instrucciones.Should().Be(InstruccionesServidor.Texto);
        }
    }
}
