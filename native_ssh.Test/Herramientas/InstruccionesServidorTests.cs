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
            // La excepción nombra solo las dos vías permitidas: echo >, tee y los heredocs siguen prohibidos (como en ssh_write_file).
            _ = Texto.Should().Contain("editar parte de un archivo: ssh_replace_file_content (nunca sed -i, echo > ni heredocs); un archivo mayor que su límite fijo solo se edita escribiéndolo entero por partes con ssh_write_file o con herramientas del servidor por ssh_exec (como sed -i), nunca con echo >, tee ni heredocs;")
                .And.NotContain("salvo en un archivo mayor");
            _ = exec.Should().Contain("ni para editar (usa ssh_replace_file_content / ssh_write_file; un archivo mayor que el límite fijo de ssh_replace_file_content sí se edita aquí con herramientas del servidor como sed -i, nunca con echo >, tee ni heredocs)");
            _ = DescripcionDe("ssh_write_file").Should().Contain("NUNCA uses echo >, tee ni heredocs por ssh_exec para escribir un archivo");
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
                .And.Contain("la guarde él en el known_hosts que nombra el error (en uno de ellos si nombra varios;")
                .And.Contain("si no es ninguno de los que nombra el error, hay que copiarla a uno de ellos")
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

        // El rechazo por no poder comprobarlo con demasiadas resoluciones a la vez sí pide repetir la llamada: las instrucciones
        // distinguen ese caso del destino de solo lectura, que no se reintenta.
        [Fact]
        public void SoloLectura_ElRechazoPorCupoLlenoSiSeReintenta()
        {
            // Arrange
            string cupo = DestinosSoloLectura.MensajeNoComprobado("ana@prod:22", "ssh_exec", "prod", isCupoLleno: true);

            // Assert
            _ = cupo.Should().Contain("no se pudo comprobar si es de solo lectura").And.Contain("demasiadas resoluciones a la vez").And.Contain("vuelve a intentarlo en unos segundos");
            _ = Texto.Should().Contain("Si el rechazo dice que no se pudo comprobar si es de solo lectura, sigue lo que diga el mensaje: con demasiadas resoluciones a la vez, repite la misma llamada en unos segundos.");
        }
    }
}
