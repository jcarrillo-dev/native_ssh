using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Server;
using NativeSsh.Comun;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Herramientas
{
    // La descripción de cada herramienta es su contrato completo (lo único que lee con seguridad un modelo sencillo): las
    // cifras, listas y campos que da salen del código y no pueden quedarse atrás si este cambia.
    public sealed class DescripcionesContratoTests
    {
        private static MethodInfo Metodo(string herramienta) =>
            typeof(HerramientasSsh).GetMethods().Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == herramienta);

        private static string Descripcion(string herramienta) => Metodo(herramienta).GetCustomAttribute<DescriptionAttribute>()!.Description;

        private static string Parametro(string herramienta, string parametro) =>
            Metodo(herramienta).GetParameters().Single(p => p.Name == parametro).GetCustomAttribute<DescriptionAttribute>()!.Description;

        private static string Constante(string nombre) =>
            (string)typeof(HerramientasSsh).GetField(nombre, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

        private static IEnumerable<string> Todas()
        {
            foreach (MethodInfo metodo in typeof(HerramientasSsh).GetMethods())
                if (metodo.GetCustomAttribute<McpServerToolAttribute>() is { Name: { } nombre })
                    yield return nombre;
        }

        // Nota D: el límite de ssh_replace_file_content es fijo; la descripción da la cifra que calcula el código y qué hacer
        // con un archivo mayor.
        [Fact]
        public void Replace_LimiteFijoConLaCifraDelCodigoYQueHacerConArchivosMayores()
        {
            // Arrange
            long limite = HerramientasSsh.CalcularMaxBytesArchivoEditable(new OpcionesNativeSsh().MaxBytesSalidaPorPeticion);

            // Act
            string descripcion = Descripcion("ssh_replace_file_content");

            // Assert
            _ = descripcion.Should().Contain($"de hasta {limite.ToString(CultureInfo.InvariantCulture)} bytes").And.Contain("límite fijo, no configurable")
                .And.Contain("append=true").And.Contain("rsync o sftp").And.NotContain("configuración por defecto");
        }

        // Lo de 'ediciones' que el modelo malinterpretaba (rango por edición, referido al original) va al principio.
        [Fact]
        public void Replace_EdicionesSinRangoYSobreElResultadoDeLaAnteriorAlPrincipio()
        {
            // Act
            string descripcion = Descripcion("ssh_replace_file_content");
            string ediciones = Parametro("ssh_replace_file_content", "ediciones");

            // Assert
            _ = descripcion.IndexOf("SIN rango de líneas", StringComparison.Ordinal).Should().BeInRange(0, 800);
            _ = descripcion.Should().Contain("tal como lo dejaron las anteriores (no en el original)");
            _ = ediciones.Should().StartWith("Varias ediciones del mismo archivo en una llamada, SIN rango de líneas: se aplican en orden, cada una buscada en todo el archivo tal como lo dejaron las anteriores");
        }

        [Fact]
        public void ReadFile_MaximoFijoSinPorDefecto() =>
            _ = Descripcion("ssh_read_file").Should().Contain($"({new OpcionesNativeSsh().MaxBytesSalidaPorPeticion / (1024 * 1024)} MiB, fijo)").And.NotContain("por defecto)");

        // ssh_exec: una cola por destino, lo que pasa al no terminar, servicios en segundo plano, todos los estados y todos los
        // campos del bloque de control, y los topes de la salida.
        [Fact]
        public void Exec_ColaEstadosCamposYTopes()
        {
            // Arrange
            OpcionesNativeSsh opciones = new();
            string[] campos = [.. typeof(ControlComando).GetConstructors().Single().GetParameters().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name!))];

            // Act
            string descripcion = Descripcion("ssh_exec");

            // Assert
            _ = descripcion.Should().Contain("UNO por destino").And.Contain("cola FIFO").And.Contain("estado 'Ejecutando'").And.Contain("2>&1 &")
                .And.Contain("un 'cd' dentro del comando solo vale para ese comando").And.Contain("o no sabes cuánto ocupará");
            foreach (string estado in Enum.GetNames<EstadoPeticionEnum>())
                _ = descripcion.Should().Contain(estado);
            foreach (string campo in campos)
                _ = descripcion.Should().Contain(campo);
            _ = descripcion.Should().Contain($"como mucho {opciones.MaxBytesPorRespuesta / 1024} KiB de salida").And.Contain($"como mucho {opciones.MaxBytesSalidaPorPeticion / (1024 * 1024)} MiB de salida");
        }

        // ssh_stat no se pide antes de cada lectura: ssh_read_file ya acota.
        [Fact]
        public void Stat_NoSeRecomiendaAntesDeLeer() =>
            _ = Descripcion("ssh_stat").Should().NotContain("antes de leer archivos potencialmente masivos").And.Contain("no hace falta antes de ssh_read_file");

        // Las exclusiones que dicen ssh_search, ssh_find y ssh_list_dir son las del código.
        [Theory]
        [InlineData("ssh_search")]
        [InlineData("ssh_find")]
        [InlineData("ssh_list_dir")]
        public void Exclusiones_LasDelCodigo(string herramienta)
        {
            // Act
            string descripcion = Descripcion(herramienta);

            // Assert
            foreach (string directorio in Constante("ExclusionDirectorios").Split(','))
                _ = descripcion.Should().Contain(directorio);
            if (herramienta == "ssh_search")
                foreach (string archivo in Constante("ExclusionArchivos").Split(','))
                    _ = descripcion.Should().Contain(archivo.Trim('\''));
        }

        [Fact]
        public void SetCwd_UnCdOUnExportAManoNoMuevenLasDemas() =>
            _ = Descripcion("ssh_set_cwd").Should().Contain("Un 'cd' dentro de ssh_exec solo dura ese comando").And.Contain("cámbialo SIEMPRE con ssh_set_cwd");

        [Fact]
        public void Cancel_EsLaUnicaFormaDeInterrumpir() =>
            _ = Descripcion("ssh_cancel").Should().Contain("NUNCA envíes Ctrl+C con ssh_input");

        // Prohibiciones imperativas en las herramientas de archivo (decisión del usuario: no se suavizan).
        [Theory]
        [InlineData("ssh_read_file")]
        [InlineData("ssh_search")]
        [InlineData("ssh_find")]
        [InlineData("ssh_list_dir")]
        [InlineData("ssh_set_cwd")]
        [InlineData("ssh_write_file")]
        [InlineData("ssh_replace_file_content")]
        public void HerramientasDeArchivo_SonCanonicasYObligatorias(string herramienta) =>
            _ = Descripcion(herramienta).Should().Contain("Herramienta canónica y obligatoria");

        // Las descripciones son autocontenidas: no remiten a documentos de fuera.
        [Fact]
        public void Ninguna_RemiteADocumentos()
        {
            foreach (string herramienta in Todas())
                _ = Descripcion(herramienta).Should().NotContain("README").And.NotContain(".md");
        }
    }
}
