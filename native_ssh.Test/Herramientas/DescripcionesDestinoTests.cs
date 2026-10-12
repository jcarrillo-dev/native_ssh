using System.ComponentModel;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using ModelContextProtocol.Server;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Herramientas
{
    // El parámetro 'destino' de todas las herramientas admite un alias de ~/.ssh/config: su descripción lo dice (el LLM
    // solo ve la descripción).
    public sealed class DescripcionesDestinoTests
    {
        public static TheoryData<string> HerramientasConDestino()
        {
            TheoryData<string> datos = [];
            foreach (MethodInfo metodo in typeof(HerramientasSsh).GetMethods())
                if (metodo.GetCustomAttribute<McpServerToolAttribute>() is { } herramienta && metodo.GetParameters().Any(p => p.Name == "destino"))
                    datos.Add(herramienta.Name!);
            return datos;
        }

        [Fact]
        public void ConDestino_SonDoce() =>
            HerramientasConDestino().Should().HaveCount(12);

        [Theory]
        [MemberData(nameof(HerramientasConDestino))]
        public void ElDestinoMencionaElAlias(string nombre)
        {
            // Arrange
            MethodInfo metodo = typeof(HerramientasSsh).GetMethods().Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == nombre);

            // Act
            string descripcion = metodo.GetParameters().Single(p => p.Name == "destino").GetCustomAttribute<DescriptionAttribute>()!.Description;

            // Assert
            _ = descripcion.Should().Contain("alias de ~/.ssh/config");
        }
    }
}
