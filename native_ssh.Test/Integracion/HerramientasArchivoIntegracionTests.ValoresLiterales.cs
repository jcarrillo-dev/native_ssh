using System.Linq;
using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // Los valores del usuario (rutas, patrones, extensión, directorio de trabajo) llegan al servidor byte a byte, por
    // canal exec y por el shell PTY. Normalizar los saltos de línea del script ya interpolado convertía un CR, CRLF, FF,
    // NEL (U+0085), LS (U+2028) o PS (U+2029) de una ruta en '\n' y la herramienta operaba sobre otro archivo: el señuelo
    // con '\n' en su lugar.
    public sealed partial class HerramientasArchivoIntegracionTests
    {
        public static TheoryData<string> SeparadoresDeLinea => ["\r", "\r\n", "\f", "\u0085", "\u2028", "\u2029"];

        public static TheoryData<string, bool> SeparadoresDeLineaConYSinSudo
        {
            get
            {
                TheoryData<string, bool> datos = [];
                foreach (string separador in SeparadoresDeLinea)
                    foreach (bool isSudo in new[] { false, true })
                        datos.Add(separador, isSudo);
                return datos;
            }
        }

        [Theory]
        [MemberData(nameof(SeparadoresDeLinea))]
        public async Task HerramientasDeLectura_RutaYPatronesConUnSeparadorDeLinea_OperanSobreElNombreExactoYNoSobreElSenuelo(string separador)
        {
            // Arrange: cada nombre con el separador tiene un señuelo con '\n' en su lugar.
            string objetivo = Ruta($"x{separador}y");
            await File.WriteAllTextAsync(objetivo, "OBJETIVO\n");
            await File.WriteAllTextAsync(Ruta("x\ny"), "SENUELO\n");
            string carpeta = Ruta($"d{separador}e");
            _ = Directory.CreateDirectory(carpeta);
            await File.WriteAllTextAsync(Path.Combine(carpeta, "dentro.txt"), "OBJETIVO\n");
            _ = Directory.CreateDirectory(Ruta("d\ne"));
            await File.WriteAllTextAsync(Path.Combine(Ruta("d\ne"), "senuelo.txt"), "SENUELO\n");
            string extension = Ruta("extension");
            _ = Directory.CreateDirectory(extension);
            await File.WriteAllTextAsync(Path.Combine(extension, $"f.t{separador}xt"), "OBJETIVO\n");
            await File.WriteAllTextAsync(Path.Combine(extension, "f.t\nxt"), "OBJETIVO\n");
            HerramientasSsh herramientas = Herramientas();

            // Act: por canal exec.
            string leido = await herramientas.LeerArchivoAsync(Destino, objetivo);
            ResultadoMetadatos metadatos = await herramientas.ConsultarMetadatosAsync(Destino, objetivo);
            string buscadoEnLaRuta = await herramientas.BuscarAsync(Destino, "OBJETIVO|SENUELO", objetivo);
            string buscadoPorExtension = await herramientas.BuscarAsync(Destino, "OBJETIVO", extension, $"t{separador}xt");
            string encontradoPorNombre = await herramientas.BuscarPorNombreAsync(Destino, $"x{separador}y", _directorio, profundidadMax: 1);
            string encontradoEnLaCarpeta = await herramientas.BuscarPorNombreAsync(Destino, "*.txt", carpeta);
            string listado = await herramientas.ListarDirectoriosAsync(Destino, carpeta, incluirArchivos: true);

            // Assert
            _ = leido.Should().Be("[Líneas 1-1 de 1]\nOBJETIVO\n");
            _ = metadatos.TamanoBytes.Should().Be(9);
            _ = buscadoEnLaRuta.Should().Contain(":1:OBJETIVO").And.NotContain("SENUELO");
            _ = buscadoPorExtension.Should().StartWith($"{Path.Combine(extension, $"f.t{separador}xt")}:1:OBJETIVO\n\n[motor: ");
            _ = encontradoPorNombre.Should().Be($"x{separador}y");
            _ = encontradoEnLaCarpeta.Should().Be("dentro.txt");
            _ = listado.Should().Be("dentro.txt");
        }

        // Un patrón de ssh_search es una regex de una sola línea: el CRLF (que incluye '\n') no puede coincidir.
        [Theory]
        [InlineData("\r")]
        [InlineData("\f")]
        [InlineData("\u0085")]
        [InlineData("\u2028")]
        [InlineData("\u2029")]
        public async Task Buscar_PatronConUnSeparadorDeLinea_BuscaElPatronExactoYNoLasDosMitades(string separador)
        {
            // Arrange: con '\n' en lugar del separador, grep buscaría "a" o "b" (un patrón por línea) y rg daría error.
            string carpeta = Ruta("patrones");
            _ = Directory.CreateDirectory(carpeta);
            await File.WriteAllTextAsync(Path.Combine(carpeta, "objetivo.txt"), $"a{separador}b\n");
            await File.WriteAllTextAsync(Path.Combine(carpeta, "senuelo.txt"), "a\nb\n");
            HerramientasSsh herramientas = Herramientas();

            // Act
            string resultado = await herramientas.BuscarAsync(Destino, $"a{separador}b", carpeta);

            // Assert
            _ = resultado.Should().StartWith($"{Path.Combine(carpeta, "objetivo.txt")}:1:a{separador}b\n\n[motor: ");
            _ = resultado.Should().NotContain("senuelo");
        }

        [Theory]
        [MemberData(nameof(SeparadoresDeLineaConYSinSudo))]
        public async Task Escrituras_RutaConUnSeparadorDeLinea_EscribenEnElNombreExactoYNoEnElSenuelo(string separador, bool isSudo)
        {
            // Arrange: en una carpeta propia (el sudo simulado deja su 'bin' en el directorio de la prueba).
            string carpeta = Ruta("datos");
            _ = Directory.CreateDirectory(carpeta);
            string objetivo = Path.Combine(carpeta, $"x{separador}y");
            string senuelo = Path.Combine(carpeta, "x\ny");
            string nuevo = Path.Combine(carpeta, $"n{separador}w");
            await File.WriteAllTextAsync(objetivo, "OBJETIVO\n");
            await File.WriteAllTextAsync(senuelo, "SENUELO\n");
            HerramientasSsh herramientas = Herramientas();
            if (isSudo)
                await SimularSudoAsync(herramientas);

            // Act: por el shell PTY.
            ResultadoEscritura reemplazo = await herramientas.ReemplazarContenidoArchivoAsync(Destino, objetivo, "OBJETIVO", "CAMBIADO", sudo: isSudo);
            string trasReemplazo = await File.ReadAllTextAsync(objetivo);
            _ = await herramientas.EscribirArchivoAsync(Destino, objetivo, "SOBRESCRITO\n", overwrite: true, sudo: isSudo);
            _ = await herramientas.EscribirArchivoAsync(Destino, objetivo, "ANEXO\n", append: true, sudo: isSudo);
            _ = await herramientas.EscribirArchivoAsync(Destino, nuevo, "NUEVO\n", sudo: isSudo);

            // Assert
            _ = reemplazo.Reemplazos.Should().Be(1);
            _ = trasReemplazo.Should().Be("CAMBIADO\n");
            _ = (await File.ReadAllTextAsync(objetivo)).Should().Be("SOBRESCRITO\nANEXO\n");
            _ = (await File.ReadAllTextAsync(nuevo)).Should().Be("NUEVO\n");
            _ = (await File.ReadAllTextAsync(senuelo)).Should().Be("SENUELO\n");
            _ = Directory.GetFileSystemEntries(carpeta).Should().BeEquivalentTo([objetivo, senuelo, nuevo]);
        }

        // ssh_replace_file_content solo adapta al separador del archivo los saltos '\r\n' y '\n' del texto objetivo y del de
        // reemplazo: FF, NEL, LS, PS y un '\r' suelto son contenido y se escriben tal cual.
        [Theory]
        [InlineData("\n")]
        [InlineData("\r\n")]
        public async Task ReemplazarContenido_TextosConSeparadoresQueNoSonSaltos_SeEscribenTalCual(string salto)
        {
            // Arrange
            const string Separadores = "\f\u0085\u2028\u2029\r";
            string archivo = Ruta("separadores.txt");
            await File.WriteAllTextAsync(archivo, $"uno{salto}a{Separadores}b{salto}tres{salto}");
            HerramientasSsh herramientas = Herramientas();

            // Act: el objetivo lleva los separadores y un '\n' que debe casar con el salto del archivo.
            ResultadoEscritura resultado = await herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, $"a{Separadores}b\ntres", $"x{Separadores}y\nz\r\nw");

            // Assert
            _ = resultado.Reemplazos.Should().Be(1);
            _ = (await File.ReadAllBytesAsync(archivo)).Should().Equal(System.Text.Encoding.UTF8.GetBytes($"uno{salto}x{Separadores}y{salto}z{salto}w{salto}"));
        }

        // Blancos y separadores en los extremos: un directorio llamado " " es un nombre más (no el directorio actual ni
        // $HOME), la extensión no se recorta y los listados conservan los blancos del primer nombre y del último (y el
        // '\r' de las líneas encontradas). Por canal exec y, para ssh_set_cwd, por el shell PTY.
        [Fact]
        public async Task Herramientas_BlancosYSeparadoresEnLosExtremos_SeRespetanDeExtremoAExtremo()
        {
            // Arrange: en el directorio " ", nombres que empiezan o terminan en blancos o separadores; fuera, un señuelo.
            string blanco = Ruta(" ");
            _ = Directory.CreateDirectory(blanco);
            string[] nombres = [" a.txt", "b.txt\u2028", "c. txt", "\u2029d.txt "];
            foreach (string nombre in nombres)
                await File.WriteAllTextAsync(Path.Combine(blanco, nombre), "  objetivo \r\n");
            await File.WriteAllTextAsync(Ruta("senuelo.txt"), "objetivo\n");
            HerramientasSsh herramientas = Herramientas();
            _ = await herramientas.FijarDirectorioAsync(Destino, _directorio);

            // Act
            string listado = await herramientas.ListarDirectoriosAsync(Destino, " ", incluirArchivos: true);
            string encontrados = await herramientas.BuscarPorNombreAsync(Destino, "*", " ");
            string busqueda = await herramientas.BuscarAsync(Destino, "objetivo", " ");
            string porExtension = await herramientas.BuscarAsync(Destino, "objetivo", " ", " txt");
            ResultadoDirectorioTrabajo fijado = await herramientas.FijarDirectorioAsync(Destino, " ");
            string leido = await herramientas.LeerArchivoAsync(Destino, "\u2029d.txt ");

            // Assert
            string ordenados = string.Join('\n', nombres.Order(StringComparer.Ordinal));
            _ = listado.Should().Be(ordenados);
            _ = encontrados.Should().Be(ordenados);
            string[] lineas = busqueda[..busqueda.IndexOf("\n\n[motor: ", StringComparison.Ordinal)].Split('\n');
            _ = lineas.Should().HaveCount(4).And.AllSatisfy(linea => linea.Should().StartWith(" /").And.EndWith(":1:  objetivo \r"));
            _ = lineas.Order(StringComparer.Ordinal).Should().Equal(nombres.Order(StringComparer.Ordinal).Select(n => $" /{n}:1:  objetivo \r"));
            _ = porExtension.Should().StartWith(" /c. txt:1:  objetivo \r\n\n[motor: ");
            _ = fijado.Directorio.Should().Be(blanco);
            _ = leido.Should().Be("[Líneas 1-1 de 1]\n  objetivo \r\n");
        }

        // El directorio de trabajo vuelve del PTY en base64 entre marcadores: LimpiarAnsi quitaría el CR de un CRLF y Trim
        // los NEL, LS y PS finales. Después lo usan las rutas relativas por canal exec, por el PTY y en ssh_exec.
        [Theory]
        [MemberData(nameof(SeparadoresDeLinea))]
        public async Task FijarDirectorio_RutaConUnSeparadorDeLinea_FijaElDirectorioExactoParaLasDemasHerramientas(string separador)
        {
            // Arrange: uno con el separador en medio y otro con él al final, con sus señuelos ('\n' en su lugar, o sin él).
            string enMedio = Ruta($"d{separador}e");
            string alFinal = Ruta($"f{separador}");
            string[] senuelos = [Ruta("d\ne"), Ruta("f\n"), Ruta("f")];
            foreach (string carpeta in new[] { enMedio, alFinal })
            {
                _ = Directory.CreateDirectory(carpeta);
                await File.WriteAllTextAsync(Path.Combine(carpeta, "dentro.txt"), "OBJETIVO\n");
            }

            foreach (string senuelo in senuelos)
            {
                _ = Directory.CreateDirectory(senuelo);
                await File.WriteAllTextAsync(Path.Combine(senuelo, "dentro.txt"), "SENUELO\n");
            }

            HerramientasSsh herramientas = Herramientas();

            // Act
            ResultadoDirectorioTrabajo fijadoEnMedio = await herramientas.FijarDirectorioAsync(Destino, enMedio);
            string leido = await herramientas.LeerArchivoAsync(Destino, "dentro.txt");
            _ = await herramientas.EscribirArchivoAsync(Destino, "nuevo.txt", "NUEVO\n");
            ResultadoComando comando = await herramientas.EjecutarAsync(Destino, "cat dentro.txt", esperarSegundos: 10);
            ResultadoDirectorioTrabajo fijadoAlFinal = await herramientas.FijarDirectorioAsync(Destino, alFinal);
            string leidoAlFinal = await herramientas.LeerArchivoAsync(Destino, "dentro.txt");

            // Assert
            _ = fijadoEnMedio.Directorio.Should().Be(enMedio);
            _ = leido.Should().Be("[Líneas 1-1 de 1]\nOBJETIVO\n");
            _ = (await File.ReadAllTextAsync(Path.Combine(enMedio, "nuevo.txt"))).Should().Be("NUEVO\n");
            _ = comando.Salida.Should().Be("OBJETIVO\n");
            _ = fijadoAlFinal.Directorio.Should().Be(alFinal);
            _ = leidoAlFinal.Should().Be("[Líneas 1-1 de 1]\nOBJETIVO\n");
            _ = senuelos.Should().AllSatisfy(senuelo => Directory.GetFileSystemEntries(senuelo).Should().ContainSingle());
        }
    }
}
