using System.Diagnostics;
using System.Text.Json.Nodes;
using FluentAssertions;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // '--version' imprime en stdout una línea con el nombre y la versión de serverInfo y sale con 0, sin arrancar el servidor
    // ni leer nada más: tampoco las variables que impedirían arrancar (un puerto o una entrada de solo lectura no válidos).
    [Trait("Categoria", "Integracion")]
    public sealed class ArranqueVersionTests
    {
        [Fact]
        public async Task Version_ImprimeNombreYVersionDeServerInfoYSaleConCero()
        {
            // Arrange
            await using ClienteMcpBinario cliente = await ClienteMcpBinario.IniciarAsync(new Dictionary<string, string>());
            JsonNode inicializacion = JsonNode.Parse(cliente.Salida.Split('\n')[0])!;
            JsonNode servidor = inicializacion["result"]!["serverInfo"]!;
            string esperado = $"{servidor["name"]!.GetValue<string>()} {servidor["version"]!.GetValue<string>()}";

            // Act
            (int codigo, string salida, string errores) = await EjecutarAsync(["--version"], []);

            // Assert
            _ = esperado.Should().Be("native_ssh 2.0.0");
            _ = codigo.Should().Be(0);
            _ = salida.Should().Be(esperado + Environment.NewLine);
            _ = errores.Should().BeEmpty();
        }

        [Theory]
        [InlineData("NATIVE_SSH_PUERTO_HTTP", "abc")]
        [InlineData("NATIVE_SSH_SOLO_LECTURA", "root@prod@ejemplo")]
        public async Task Version_ConVariablesQueImpedirianArrancar_NoLasLee(string variable, string valor)
        {
            // Act
            (int codigo, string salida, string errores) = await EjecutarAsync(["--version"], new Dictionary<string, string> { [variable] = valor });

            // Assert
            _ = codigo.Should().Be(0);
            _ = salida.Should().Be("native_ssh 2.0.0" + Environment.NewLine);
            _ = errores.Should().BeEmpty();
        }

        // Con más argumentos, '--version' no se acepta: error de uso (código 2), sin arrancar.
        [Fact]
        public async Task Version_ConOtrosArgumentos_ErrorDeUso()
        {
            // Act
            (int codigo, string salida, string errores) = await EjecutarAsync(["--version", "--http"], []);

            // Assert
            _ = codigo.Should().Be(2);
            _ = salida.Should().BeEmpty();
            _ = errores.Should().Contain("--version");
        }

        private static async Task<(int Codigo, string Salida, string Errores)> EjecutarAsync(string[] argumentos, Dictionary<string, string> entorno)
        {
            ProcessStartInfo inicio = new(ClienteMcpBinario.RutaBinario, argumentos)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach ((string clave, string valor) in entorno)
                inicio.Environment[clave] = valor;
            using Process proceso = Process.Start(inicio)!;
            Task<string> errores = proceso.StandardError.ReadToEndAsync();
            Task<string> salida = proceso.StandardOutput.ReadToEndAsync();
            using CancellationTokenSource plazo = new(TimeSpan.FromSeconds(30));
            await proceso.WaitForExitAsync(plazo.Token);
            return (proceso.ExitCode, await salida, await errores);
        }
    }
}
