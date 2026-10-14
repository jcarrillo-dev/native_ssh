using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // Herramientas de lectura (canales exec) en Debian (GNU) y Alpine (busybox): mismas respuestas en las dos.
    public sealed partial class ContenedorSudoTests
    {
        // Árbol de prueba del usuario: archivos de texto, CRLF, Latin-1, sin salto final, un enlace, un archivo ilegible, un
        // directorio que no se puede atravesar y directorios podados (.git, node_modules) con coincidencias que no deben salir.
        private static string PrepararProyecto(Entorno entorno)
        {
            _ = entorno.Servidor.ComoUsuario("""
                cd "$1"
                mkdir -p proyecto/sub/profundo proyecto/.git/objetos proyecto/node_modules/paquete
                printf 'uno\ndos 123\ntres\n' > proyecto/a.txt
                printf 'def f():\n    return 42\n' > proyecto/sub/b.py
                printf 'x = 7\n' > proyecto/node_modules/paquete/c.py
                printf 'en git 99\n' > proyecto/.git/objetos/d.txt
                printf 'linea1\r\nlinea2\r\n' > proyecto/crlf.txt
                printf 'caf\351\n' > proyecto/latin1.txt
                printf 'uno\nsin salto' > proyecto/sinsalto.txt
                ln -s a.txt proyecto/enlace
                printf 'secreto\n' > proyecto/privado.txt
                chmod 000 proyecto/privado.txt
                mkdir proyecto/cerrado
                chmod 000 proyecto/cerrado
                """, entorno.DirectorioUsuario);
            return $"{entorno.DirectorioUsuario}/proyecto";
        }

        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task LeerArchivo_RangosColaCrlfYNoUtf8_IgualEnLasDosDistros(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            string proyecto = PrepararProyecto(entorno);

            // Act
            string completo = await herramientas.LeerArchivoAsync(entorno.Destino, $"{proyecto}/a.txt");
            string rango = await herramientas.LeerArchivoAsync(entorno.Destino, $"{proyecto}/a.txt", 2, 3);
            string cola = await herramientas.LeerArchivoAsync(entorno.Destino, $"{proyecto}/a.txt", -2);
            string crlf = await herramientas.LeerArchivoAsync(entorno.Destino, $"{proyecto}/crlf.txt");
            string latin1 = await herramientas.LeerArchivoAsync(entorno.Destino, $"{proyecto}/latin1.txt");
            string colaSinSalto = await herramientas.LeerArchivoAsync(entorno.Destino, $"{proyecto}/sinsalto.txt", -1);

            // Assert
            _ = completo.Should().Be("[Líneas 1-3 de 3]\nuno\ndos 123\ntres\n");
            _ = rango.Should().Be("[Líneas 2-3 de 3]\ndos 123\ntres\n");
            _ = cola.Should().Be("[Líneas 2-3 de 3]\ndos 123\ntres\n");
            _ = crlf.Should().Be("[Líneas 1-2 de 2]\nlinea1\r\nlinea2\r\n");
            _ = latin1.Should().Be($"[Líneas 1-1 de 1]\ncaf�\n{HerramientasSsh.AvisoContenidoNoUtf8}");
            _ = colaSinSalto.Should().Be("[Líneas 2-2 de 2]\nsin salto");
        }

        // Un archivo sin salto de línea final se devuelve tal cual (sin añadirle uno) al leerlo por rango, también con
        // busybox: la lectura por rango usaba 'sed -n "A,Bp"', y el sed de busybox añade un salto de línea a una última
        // línea que no lo tiene; ahora usa 'head -n B | tail -n +A', que lo conserva en los dos (Bloque S, P3; AO2; era un
        // fallo conocido en Alpine).
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task LeerArchivo_SinSaltoFinal_DevuelveElContenidoTalCual(string distro) =>
            await LeerSinSaltoFinalAsync(distro);

        private async Task LeerSinSaltoFinalAsync(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            string proyecto = PrepararProyecto(entorno);

            // Act
            string contenido = await herramientas.LeerArchivoAsync(entorno.Destino, $"{proyecto}/sinsalto.txt");

            // Assert
            _ = contenido.Should().Be("[Líneas 1-2 de 2]\nuno\nsin salto");
        }

        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task LeerArchivo_SinPermisoNoAtravesableInexistenteODispositivo_ErrorClaro(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            string proyecto = PrepararProyecto(entorno);

            // Act
            NativeSshException privado = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync(entorno.Destino, $"{proyecto}/privado.txt")).Should().ThrowAsync<NativeSshException>()).Which;
            NativeSshException cerrado = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync(entorno.Destino, $"{proyecto}/cerrado/x")).Should().ThrowAsync<NativeSshException>()).Which;
            NativeSshException inexistente = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync(entorno.Destino, $"{proyecto}/no_existe")).Should().ThrowAsync<NativeSshException>()).Which;
            NativeSshException dispositivo = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync(entorno.Destino, "/dev/zero")).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = privado.Message.Should().Be($"Error: sin permiso de lectura: {proyecto}/privado.txt");
            _ = cerrado.Message.Should().Be($"Error: sin permiso para acceder a la ruta: {proyecto}/cerrado/x (el directorio {proyecto}/cerrado no se puede atravesar).");
            _ = inexistente.Message.Should().Be($"Error: el archivo no existe: {proyecto}/no_existe");
            _ = dispositivo.Message.Should().Be("Error: /dev/zero no es un archivo regular (es una FIFO, un socket o un dispositivo): no se lee.");
        }

        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task ConsultarMetadatos_TiposFijosPropietarioYEnlace_IgualEnLasDosDistros(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            string proyecto = PrepararProyecto(entorno);

            // Act
            ResultadoMetadatos archivo = await herramientas.ConsultarMetadatosAsync(entorno.Destino, $"{proyecto}/a.txt");
            ResultadoMetadatos enlace = await herramientas.ConsultarMetadatosAsync(entorno.Destino, $"{proyecto}/enlace");
            ResultadoMetadatos directorio = await herramientas.ConsultarMetadatosAsync(entorno.Destino, $"{proyecto}/sub");
            ResultadoMetadatos dispositivo = await herramientas.ConsultarMetadatosAsync(entorno.Destino, "/dev/null");
            ResultadoMetadatos inexistente = await herramientas.ConsultarMetadatosAsync(entorno.Destino, $"{proyecto}/no_existe");
            NativeSshException cerrado = (await FluentActions.Awaiting(() => herramientas.ConsultarMetadatosAsync(entorno.Destino, $"{proyecto}/cerrado/x")).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = (archivo.Tipo, archivo.TamanoBytes, archivo.Permisos, archivo.Propietario, archivo.Grupo).Should().Be(("archivo regular", 17L, "644", "prueba", "prueba"));
            _ = archivo.DestinoEnlace.Should().BeNull();
            _ = (enlace.Tipo, enlace.DestinoEnlace).Should().Be(("enlace simbólico", "a.txt"));
            _ = (directorio.Tipo, directorio.Permisos).Should().Be(("directorio", "755"));
            _ = dispositivo.Tipo.Should().Be("dispositivo de caracteres");
            _ = inexistente.Existe.Should().BeFalse();
            _ = cerrado.Message.Should().Contain("no se puede atravesar");
        }

        // Debian (GNU grep con -P): ssh_search con PCRE, filtro de extensión y exclusiones (.git, node_modules).
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        public async Task Buscar_PcreExtensionYExclusiones_ConGrepDeGnu(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            string proyecto = PrepararProyecto(entorno);

            // Act
            string digitos = await herramientas.BuscarAsync(entorno.Destino, "\\d{2,}", proyecto);
            string python = await herramientas.BuscarAsync(entorno.Destino, "return|x =", proyecto, "py");

            // Assert
            _ = digitos.Should().Contain($"{proyecto}/a.txt:2:dos 123").And.Contain($"{proyecto}/sub/b.py:2:    return 42")
                .And.NotContain("/.git/").And.NotContain("node_modules").And.EndWith("[motor: grep -P]");
            // privado.txt y cerrado/ no se pueden leer: el motor los calla y sale con 2, y la respuesta avisa (A-undecies, AU3).
            _ = python.Should().Be($"{proyecto}/sub/b.py:2:    return 42\n\n{AvisoCodigoDosSinDetalle}\n[motor: grep -P]");
        }

        // Sin rg ni grep de GNU (busybox, Alpine), ssh_search recorre el árbol con find y busca con grep -E, con las mismas
        // exclusiones y el filtro de extensión. Antes usaba 'grep -E' con opciones de GNU grep (--exclude-dir, --include)
        // que el de busybox no tiene: toda búsqueda fallaba con su ayuda y aun así anunciaba «[motor: grep -E]» (Bloque S,
        // P2; AO1; era un fallo conocido).
        [FactContenedor]
        public async Task Alpine_Buscar_EreExtensionYExclusiones_ConGrepDeBusybox()
        {
            // Arrange
            await using Entorno entorno = Preparar(ContenedorSudo.Alpine);
            HerramientasSsh herramientas = entorno.Herramientas();
            string proyecto = PrepararProyecto(entorno);

            // Act
            string digitos = await herramientas.BuscarAsync(entorno.Destino, "[0-9]{2,}", proyecto);
            string python = await herramientas.BuscarAsync(entorno.Destino, "return|x =", proyecto, "py");

            // Assert
            _ = digitos.Should().Contain($"{proyecto}/a.txt:2:dos 123").And.Contain($"{proyecto}/sub/b.py:2:    return 42")
                .And.NotContain("/.git/").And.NotContain("node_modules").And.EndWith("[motor: grep -E]");
            // privado.txt y cerrado/ no se pueden leer: el motor los calla y sale con 2, y la respuesta avisa (A-undecies, AU3).
            _ = python.Should().Be($"{proyecto}/sub/b.py:2:    return 42\n\n{AvisoCodigoDosSinDetalle}\n[motor: grep -E]");
        }

        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task Buscar_RutaInexistenteOIlegible_ErrorClaro(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            string proyecto = PrepararProyecto(entorno);

            // Act
            NativeSshException inexistente = (await FluentActions.Awaiting(() => herramientas.BuscarAsync(entorno.Destino, "x", $"{proyecto}/no_existe")).Should().ThrowAsync<NativeSshException>()).Which;
            NativeSshException cerrado = (await FluentActions.Awaiting(() => herramientas.BuscarAsync(entorno.Destino, "x", $"{proyecto}/cerrado")).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = inexistente.Message.Should().Be($"Error: la ruta de búsqueda no existe: {proyecto}/no_existe");
            _ = cerrado.Message.Should().Be($"Error: sin permiso de lectura en la ruta de búsqueda: {proyecto}/cerrado");
        }

        // ssh_find y ssh_list_dir: mismas rutas relativas y misma poda en las dos distribuciones; ssh_set_cwd y una lectura
        // relativa.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task BuscarPorNombreYListar_MismaPodaYRutasRelativas_IgualEnLasDosDistros(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            string proyecto = PrepararProyecto(entorno);

            // Act
            string python = await herramientas.BuscarPorNombreAsync(entorno.Destino, "*.py", proyecto);
            string directorios = await herramientas.BuscarPorNombreAsync(entorno.Destino, "*", proyecto, "dir");
            string arbol = await herramientas.ListarDirectoriosAsync(entorno.Destino, proyecto, incluirArchivos: true);
            NativeSshException cerrado = (await FluentActions.Awaiting(() => herramientas.ListarDirectoriosAsync(entorno.Destino, $"{proyecto}/cerrado")).Should().ThrowAsync<NativeSshException>()).Which;
            ResultadoDirectorioTrabajo cwd = await herramientas.FijarDirectorioAsync(entorno.Destino, proyecto);
            string relativo = await herramientas.LeerArchivoAsync(entorno.Destino, "sub/b.py");
            string textos = await herramientas.BuscarPorNombreAsync(entorno.Destino, "*.txt");

            // Assert
            _ = python.Should().Be("sub/b.py");
            _ = directorios.Should().Be(".git\ncerrado\nnode_modules\nsub\nsub/profundo");
            _ = arbol.Should().Be(".git/\na.txt\ncerrado/\ncrlf.txt\nenlace\nlatin1.txt\nnode_modules/\nprivado.txt\nsinsalto.txt\nsub/\nsub/b.py\nsub/profundo/");
            _ = cerrado.Message.Should().Be($"Error: sin permiso para listar el directorio: {proyecto}/cerrado");
            _ = cwd.Directorio.Should().Be(proyecto);
            _ = relativo.Should().Be("[Líneas 1-2 de 2]\ndef f():\n    return 42\n");
            _ = textos.Should().Be("a.txt\ncrlf.txt\nlatin1.txt\nprivado.txt\nsinsalto.txt");
        }

        // RC9: ssh_find con tamanoMin/tamanoMax igual en GNU (con -printf) y en busybox (sin -printf ni unidades M/G en -size,
        // que redondea k: medido), en bytes e inclusivo, con el tamaño delante y de mayor a menor.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task BuscarPorNombre_ConTamano_IgualEnLasDosDistros(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            _ = entorno.Servidor.ComoUsuario("""
                cd "$1"
                mkdir -p tamanos/sub
                head -c 1000 /dev/zero > tamanos/mil.log
                head -c 1024 /dev/zero > tamanos/un_k.log
                head -c 1500000 /dev/zero > tamanos/sub/grande.log
                : > tamanos/vacio.log
                """, entorno.DirectorioUsuario);
            string carpeta = $"{entorno.DirectorioUsuario}/tamanos";

            // Act
            string minimo = await herramientas.BuscarPorNombreAsync(entorno.Destino, "*.log", carpeta, tamanoMin: "1k");
            string maximo = await herramientas.BuscarPorNombreAsync(entorno.Destino, "*.log", carpeta, tamanoMax: "1000");
            string megas = await herramientas.BuscarPorNombreAsync(entorno.Destino, "*", carpeta, tamanoMin: "1M");

            // Assert
            _ = minimo.Should().Be("1.4 MiB\tsub/grande.log\n1 KiB\tun_k.log");
            _ = maximo.Should().Be("1000 B\tmil.log\n0 B\tvacio.log");
            _ = megas.Should().Be("1.4 MiB\tsub/grande.log");
        }
    }
}
