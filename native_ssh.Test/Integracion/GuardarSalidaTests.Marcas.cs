using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // guardarSalida: nada de lo que hay en el archivo viaja por el terminal. Las marcas del envoltorio solo llevan números y
    // la ruta (que no sale de la salida), no llegan nunca al búfer (ninguna página ni ningún desdeByte enseña un trozo) y la
    // vista previa se lee después por un canal exec, nunca si se envió un secreto. Con 'set -x' o 'set -v' en el shell, la
    // traza tampoco lleva nada del archivo.
    public sealed partial class GuardarSalidaTests
    {
        private const string SecretoPrueba = "hunter2-SECRETO-xyz";

        // ¿Algún trozo en base64 del texto (sea cual sea su alineación) decodifica a algo que contiene 'buscado'?
        private static bool HasBase64De(string texto, string buscado)
        {
            foreach (Match m in RegexBase64().Matches(texto))
            {
                string t = m.Value.TrimEnd('=');
                for (int quitar = 0; quitar < 4 && t.Length - quitar >= 8; quitar++)
                {
                    string c = t[..(t.Length - quitar)];
                    c = c[..(c.Length - (c.Length % 4))];
                    try
                    {
                        if (Encoding.UTF8.GetString(Convert.FromBase64String(c)).Contains(buscado, StringComparison.Ordinal))
                            return true;
                    }
                    catch (FormatException)
                    {
                    }
                }
            }

            return false;
        }

        [GeneratedRegex("[A-Za-z0-9+/]{8,}={0,2}")]
        private static partial Regex RegexBase64();

        // Todas las páginas desde 0 (siguiendo siguienteByte) hasta el estado final, con sus avisos.
        private async Task<string> LeerTodoAsync(HerramientasSsh h, string id)
        {
            StringBuilder todo = new();
            ResultadoComando pagina = await h.LeerSalidaAsync(id, 0, esperarSegundos: 30).ComoComando();
            _ = todo.Append(pagina.Salida).Append(pagina.Aviso);
            while (pagina.HasMasSalida || !pagina.Estado.IsFinal())
            {
                pagina = await h.LeerSalidaAsync(id, pagina.SiguienteByte, esperarSegundos: 30).ComoComando();
                _ = todo.Append(pagina.Salida).Append(pagina.Aviso);
            }

            return todo.ToString();
        }

        // ssh_output desde cada byte de los tramos dados: lo que devuelve cada lectura, junto.
        private static async Task<string> LeerDesdeCadaByteAsync(HerramientasSsh h, string id, IEnumerable<long> desdes)
        {
            StringBuilder todo = new();
            foreach (long desde in desdes)
            {
                ResultadoComando r = await h.LeerSalidaAsync(id, desde).ComoComando();
                _ = todo.Append(r.Salida).Append('\u0001').Append(r.Aviso).Append('\u0001');
            }

            return todo.ToString();
        }

        private static IEnumerable<long> Alrededor(long centro, int radio) =>
            Enumerable.Range(0, (2 * radio) + 1).Select(i => centro - radio + i).Where(d => d >= 0);

        // (K01) Un programa lee un secreto por ssh_input, lo imprime en su salida (va al archivo) y escribe casi 64 KiB en el
        // terminal, de modo que lo que viene detrás cae en el borde de la página de 64 KiB: ni las páginas ni ssh_output desde
        // cualquier byte cerca del borde y del final llevan el secreto (ni en base64) ni restos de las marcas.
        [Theory]
        [InlineData(65400)]
        [InlineData(65450)]
        [InlineData(65490)]
        [InlineData(65520)]
        public async Task ConSecreto_MuchoEnElTerminal_NingunaPaginaNiDesdeByteLlevaElSecretoNiLasMarcas(int bytesTerminal)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando r = await GuardarAsync(h, $"IFS= read -rs P; echo \"S=$P\"; head -c {bytesTerminal} /dev/zero | tr '\\0' a > /dev/tty", esperarSegundos: 1);
            await Task.Delay(500);

            // Act
            ResultadoComando fin = await h.EnviarEntradaAsync(r.Id, SecretoPrueba, esperarSegundos: 30, secreto: true).ComoComando();
            string paginas = await LeerTodoAsync(h, r.Id);
            ResultadoComando ultima = await h.LeerSalidaAsync(r.Id, 0).ComoComando();
            string desdeCadaByte = await LeerDesdeCadaByteAsync(h, r.Id, Alrededor(64 * 1024, 200).Concat(Alrededor(ultima.SiguienteByte, 200)));
            string todo = fin.Salida + fin.Aviso + paginas + desdeCadaByte;

            // Assert
            _ = fin.Estado.Should().Be(EstadoPeticionEnum.Completado, fin.Aviso);
            _ = (await File.ReadAllTextAsync(fin.Archivo!)).Should().Be($"S={SecretoPrueba}\n", "precondición: el programa imprimió el secreto en su salida");
            _ = todo.Should().NotContain(SecretoPrueba).And.NotContain("NSG_");
            _ = HasBase64De(todo, SecretoPrueba).Should().BeFalse("el secreto no puede llegar en base64");
            _ = HasBase64De(todo, "S=hunter").Should().BeFalse();
        }

        // (K02) Sin secreto y sin nada en el terminal: ssh_output desde cada byte (también los que caerían dentro de una marca)
        // no enseña restos de las marcas ni la vista previa en base64, y el bloque de salida no dice «escrito directamente en
        // el terminal» cuando el comando no escribió nada en él.
        [Fact]
        public async Task SinNadaEnElTerminal_DesdeCualquierByte_NiRestosDeMarcasNiTerminalFalso()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando r = await GuardarAsync(h, "printf 'linea uno\\nlinea dos\\n'");

            // Act
            string desdeCadaByte = await LeerDesdeCadaByteAsync(h, r.Id, Enumerable.Range(0, (int)r.SiguienteByte + 300).Select(i => (long)i - 200));

            // Assert
            _ = (r.Estado, r.Salida).Should().Be((EstadoPeticionEnum.Completado, "linea uno\nlinea dos\n"));
            _ = desdeCadaByte.Should().NotContain("NSG_").And.NotContain("escrito directamente en el terminal");
            _ = HasBase64De(desdeCadaByte, "linea uno").Should().BeFalse();
        }

        // (K03) Con 'set -x', 'set -v' o los dos en el shell de la sesión: con un secreto leído e impreso por el programa, nada
        // de lo que devuelven las herramientas lo lleva (ni en base64); sin secreto, la traza que va al terminal no lleva el
        // contenido del archivo en base64 (la vista previa sí sale, en claro, porque no hubo secreto; la traza del propio
        // comando va al archivo con su salida de error). El contenido no está tal cual en el texto del comando: 'set -v' enseña
        // lo que se envía al shell, que lleva el comando.
        [Theory]
        [InlineData("set -x")]
        [InlineData("set -v")]
        [InlineData("set -xv")]
        public async Task ConTrazaDelShell_NadaDelArchivoViajaPorElTerminal(string opcion)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            _ = await h.EjecutarAsync(Destino, opcion, esperarSegundos: 30).ComoComando();
            ResultadoComando lector = await GuardarAsync(h, "IFS= read -rs P; echo \"S=$P\"", esperarSegundos: 1);
            await Task.Delay(500);

            // Act
            ResultadoComando conSecreto = await h.EnviarEntradaAsync(lector.Id, SecretoPrueba, esperarSegundos: 30, secreto: true).ComoComando();
            string todoConSecreto = conSecreto.Salida + conSecreto.Aviso + await LeerTodoAsync(h, lector.Id);
            ResultadoComando sinSecreto = await GuardarAsync(h, "printf 'dato-%s-123\\n' del-archivo");
            string todoSinSecreto = await LeerTodoAsync(h, sinSecreto.Id);

            // Assert
            salida.WriteLine($"con secreto: {todoConSecreto}\nsin secreto: {todoSinSecreto}");
            _ = conSecreto.Estado.Should().Be(EstadoPeticionEnum.Completado, conSecreto.Aviso);
            _ = (await File.ReadAllTextAsync(conSecreto.Archivo!)).Should().Contain($"S={SecretoPrueba}\n", "precondición (la traza del comando también va al archivo)");
            _ = todoConSecreto.Should().NotContain(SecretoPrueba).And.NotContain("NSG_");
            _ = HasBase64De(todoConSecreto, SecretoPrueba).Should().BeFalse();
            _ = sinSecreto.Salida.Should().Contain("dato-del-archivo-123\n", "la vista previa (con la traza del comando, que va al archivo)");
            _ = HasBase64De(todoSinSecreto, "dato-del-archivo").Should().BeFalse("la traza no lleva el contenido del archivo");
            _ = todoSinSecreto.Should().NotContain("NSG_");
        }
    }
}
