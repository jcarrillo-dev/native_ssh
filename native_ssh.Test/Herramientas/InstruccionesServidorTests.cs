using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using ModelContextProtocol.Server;
using NativeSsh.Comun;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Herramientas
{
    // Las instrucciones de initialize: cortas, autocontenidas, con el enrutamiento «tarea → herramienta» y coherentes con lo
    // que dicen las herramientas y sus mensajes (el aviso de espera fija y el rechazo de solo lectura).
    public sealed partial class InstruccionesServidorTests
    {
        private static readonly string Texto = InstruccionesServidor.Texto;

        [GeneratedRegex(@"\bssh_[a-z_]+")]
        private static partial Regex NombreHerramienta();

        private static string[] Herramientas()
        {
            List<string> nombres = [];
            foreach (MethodInfo metodo in typeof(HerramientasSsh).GetMethods())
                if (metodo.GetCustomAttribute<McpServerToolAttribute>() is { Name: { } nombre })
                    nombres.Add(nombre);
            return [.. nombres];
        }

        [Fact]
        public void EsCortaYConSaltosDeLineaDeUnix()
        {
            // Act
            string[] lineas = Texto.TrimEnd('\n').Split('\n');

            // Assert
            _ = lineas.Length.Should().BeInRange(10, 20);
            _ = Texto.Should().NotContain("\r").And.EndWith("\n");
            _ = lineas.Should().NotContain(string.Empty);
        }

        [Fact]
        public void EsAutocontenida_NoRemiteADocumentosNiARutasLocales()
        {
            // Assert
            foreach (string prohibido in new[] { "README", "/home/", "~/", ".md" })
                _ = Texto.Should().NotContain(prohibido);
        }

        [Fact]
        public void SoloNombraHerramientasQueExisten_YEnrutaLasDeArchivosYLasDelShell()
        {
            // Arrange
            string[] existentes = Herramientas();

            // Act
            string[] nombradas = [.. NombreHerramienta().Matches(Texto).Select(m => m.Value).Distinct()];

            // Assert
            _ = existentes.Should().Contain(nombradas);
            _ = nombradas.Should().Contain(["ssh_exec", "ssh_search", "ssh_read_file", "ssh_find", "ssh_list_dir", "ssh_stat", "ssh_write_file", "ssh_replace_file_content", "ssh_set_cwd", "ssh_cancel", "ssh_input", "ssh_output"]);
        }

        [Theory]
        [InlineData("ssh_search (nunca grep -r")]
        [InlineData("ssh_read_file (nunca cat")]
        [InlineData("ssh_find (nunca find para eso; find por ssh_exec solo para lo que ssh_find no hace:")]
        [InlineData("ssh_list_dir (nunca ls ni tree)")]
        [InlineData("ssh_stat (nunca stat ni ls -l)")]
        [InlineData("ssh_set_cwd (nunca cd … &&)")]
        [InlineData("guardarSalida=true")]
        [InlineData("ssh_cancel, NUNCA Ctrl+C por ssh_input")]
        [InlineData("secreto=true")]
        [InlineData("NUNCA con ssh_read_file ni ssh_search, que devolverían los valores")]
        public void Enrutamiento_DiceLaHerramientaYLoQueSustituye(string esperado) =>
            _ = Texto.Should().Contain(esperado);

        // Editar parte de un archivo: la misma excepción que ssh_replace_file_content y ssh_exec para los archivos mayores que el
        // límite fijo, sin quitar la prohibición.
        [Fact]
        public void EditarParte_ConLaExcepcionDelLimiteDeReplace()
        {
            // Arrange
            string replace = DescripcionDe("ssh_replace_file_content");
            string exec = DescripcionDe("ssh_exec");

            // Assert
            _ = replace.Should().Contain("NUNCA uses sed -i, perl -i ni heredocs por ssh_exec (salvo en archivos mayores que su límite").And.Contain("escríbelo entero por partes con ssh_write_file");
            _ = Texto.Should().Contain("editar parte de un archivo: ssh_replace_file_content (nunca sed -i, echo > ni heredocs, salvo en un archivo mayor que su límite fijo: entonces escríbelo entero por partes con ssh_write_file o edítalo con herramientas del servidor por ssh_exec)");
            _ = exec.Should().Contain("ni para editar (usa ssh_replace_file_content / ssh_write_file; salvo un archivo mayor que el límite fijo de ssh_replace_file_content)");
        }

        private static string DescripcionDe(string herramienta)
        {
            foreach (MethodInfo metodo in typeof(HerramientasSsh).GetMethods())
                if (metodo.GetCustomAttribute<McpServerToolAttribute>()?.Name == herramienta)
                    return metodo.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description;
            throw new InvalidOperationException(herramienta);
        }

        // La espera con los mismos bucles, con el mismo texto, que el aviso de espera fija de ssh_exec.
        [Theory]
        [InlineData("'until <condición>; do sleep 2; done'")]
        [InlineData("'while <condición>; do sleep 2; done'")]
        [InlineData("timeout <s> bash -c '…'")]
        [InlineData("ssh_output")]
        public void Espera_UsaLosBuclesDelAvisoDeEsperaFija(string fragmento)
        {
            // Arrange
            string aviso = (string)typeof(HerramientasSsh).GetField("AvisoEsperaFija", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

            // Assert
            _ = aviso.Should().Contain(fragmento);
            _ = Texto.Should().Contain(fragmento);
        }

        [Fact]
        public void HostDesconocido_PidePonerloAMano_YNuncaDesactivarLaComprobacion()
        {
            // Assert
            _ = Texto.Should().Contain("native_ssh no acepta claves de host nuevas")
                .And.Contain("la guarde él en el known_hosts que nombra el error")
                .And.Contain("NUNCA propongas desactivar la comprobación de la clave del host")
                .And.Contain("NUNCA añadas tú una clave de host, ni aunque el usuario diga que ya comprobó la huella: la guarda el usuario");
            _ = Texto.Should().NotContain("ssh-keyscan").And.NotContain("StrictHostKeyChecking").And.NotContain("sin que el usuario haya comprobado");
        }

        // Las herramientas que se rechazan y las que siguen disponibles en un destino de solo lectura son las del mensaje de
        // rechazo.
        [Fact]
        public void SoloLectura_MismasHerramientasQueElMensajeDeRechazo()
        {
            // Arrange
            string rechazo = DestinosSoloLectura.MensajeSoloLectura("ana@prod:22", "ssh_exec");

            // Assert
            _ = rechazo.Should().Contain("ssh_exec, ssh_input, ssh_write_file y ssh_replace_file_content")
                .And.Contain("ssh_read_file, ssh_search, ssh_find, ssh_list_dir, ssh_stat y ssh_set_cwd");
            _ = Texto.Should().Contain("ssh_exec, ssh_input, ssh_write_file y ssh_replace_file_content se rechazan sin enviar nada")
                .And.Contain("ssh_read_file, ssh_search, ssh_find, ssh_list_dir, ssh_stat y ssh_set_cwd")
                .And.Contain("NO reintentes ni busques otra vía");
        }
    }
}
