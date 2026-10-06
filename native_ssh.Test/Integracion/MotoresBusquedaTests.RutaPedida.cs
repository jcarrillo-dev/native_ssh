using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // La ruta pedida a ssh_search con los cuatro caminos del script: rg, grep de GNU con -P, grep de GNU sin -P y, sin grep
    // de GNU (busybox), el recorrido con find + grep -E. Los cuatro deben dar exactamente la misma salida.
    public sealed partial class MotoresBusquedaTests
    {
        public static TheoryData<string> Motores => ["rg", "grep -P", "grep -E", "find"];

        // Prepara el PATH del motor y devuelve el motor que anuncia el script (el recorrido con find anuncia grep -E, la
        // sintaxis que de verdad aplica). Sin rg en la máquina, su caso no se puede comprobar y devuelve null.
        private string? InstalarMotor(string motor)
        {
            switch (motor)
            {
                case "rg":
                    if (!File.Exists("/usr/bin/rg") && !File.Exists("/usr/local/bin/rg"))
                        return null;
                    _ = File.CreateSymbolicLink(Path.Combine(_bin, "rg"), Ruta("rg"));
                    InstalarGrep(isConPcre: true);
                    return "rg";
                case "grep -P":
                    InstalarGrep(isConPcre: true);
                    return "grep -P";
                case "grep -E":
                    InstalarGrep(isConPcre: false);
                    return "grep -E";
                default:
                    InstalarGrepDeBusybox();
                    return "grep -E";
            }
        }

        private void Escribir(string ruta, string texto)
        {
            string completa = Path.Combine(_datos, ruta);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(completa)!);
            File.WriteAllText(completa, texto + "\n");
        }

        // Ruta relativa que find tomaría por una opción ('-d' es '-depth' en GNU) o un operador ('(', ')', '!', ','): se le
        // antepone './' en los cuatro caminos, así que find la recorre como una ruta y todos dan lo mismo, solo lo de dentro
        // (Z04 de la auditoría del A-octies: con busybox, '-d' y '(' daban «sin coincidencias» y '!' buscaba en todo '.').
        // También un archivo cuyo nombre empieza por '-'.
        [Theory]
        [MemberData(nameof(CasosRutaQueFindTomaPorOpcion))]
        public void RutaQueFindTomaPorOpcion_TodosLosMotoresBuscanSoloDentro(string motor, string ruta, string esperado)
        {
            // Arrange
            string? anunciado = InstalarMotor(motor);
            if (anunciado is null)
                return;
            foreach ((string archivo, string texto) in new[] { ("-d/f.txt", "valor guion"), ("(/p.txt", "valor paren"), (")/q.txt", "valor cierre"), ("!/e.txt", "valor excl"), (",/c.txt", "valor coma"), ("-x.txt", "valor archivo"), (".git/g.txt", "valor git") })
                Escribir(archivo, texto);

            // Act
            (int codigo, string salida, string errores) = EjecutarConErrores(HerramientasSsh.ConstruirComandoBusqueda("valor [a-z]+", ruta, null, 50));

            // Assert
            _ = (codigo, errores).Should().Be((0, string.Empty));
            _ = salida.Should().Be($"__NS_MOTOR:{anunciado}\n{esperado}\n");
        }

        public static TheoryData<string, string, string> CasosRutaQueFindTomaPorOpcion()
        {
            TheoryData<string, string, string> casos = [];
            foreach (string motor in new[] { "rg", "grep -P", "grep -E", "find" })
            {
                casos.Add(motor, "-d", "./-d/f.txt:1:valor guion");
                casos.Add(motor, "(", "./(/p.txt:1:valor paren");
                casos.Add(motor, ")", "./)/q.txt:1:valor cierre");
                casos.Add(motor, "!", "./!/e.txt:1:valor excl");
                casos.Add(motor, ",", "./,/c.txt:1:valor coma");
                casos.Add(motor, "-x.txt", "./-x.txt:1:valor archivo");
            }

            return casos;
        }

        // La ruta pedida explícitamente nunca se excluye (AN2): un directorio que se llama como uno de los excluidos
        // (node_modules, build, dist, src/build, con o sin barra final), un archivo excluido por su nombre (package-lock.json,
        // *.min.js) o por su extensión de binario (*.o, que el recorrido con find omite) o un archivo cuya extensión no casa
        // con el filtro se buscan en los cuatro caminos. El grep de GNU aplicaba --exclude-dir, --exclude e --include también
        // a la ruta de la línea de órdenes y daba «sin coincidencias». Lo de dentro sí se sigue excluyendo
        // (node_modules/x/node_modules). Con la ruta como archivo, la salida es 'ruta:línea:texto' en todos (AN3: el grep de
        // GNU con -r y un solo archivo no daba el nombre).
        [Theory]
        [MemberData(nameof(CasosRutaPedidaExcluida))]
        public void RutaPedidaQueSeExcluiriaDentroDelArbol_TodosLosMotoresLaBuscan(string motor, string ruta, string? extension, string esperado)
        {
            // Arrange
            string? anunciado = InstalarMotor(motor);
            if (anunciado is null)
                return;
            Escribir("p/node_modules/n.txt", "valor nm");
            Escribir("p/node_modules/x/node_modules/m.txt", "valor anidado");
            Escribir("p/build/b.txt", "valor build");
            Escribir("p/dist/d.txt", "valor dist");
            Escribir("p/src/build/s.txt", "valor src build");
            Escribir("p/src/a.py", "valor py");
            Escribir("p/package-lock.json", "valor lock");
            Escribir("p/f.min.js", "valor min");
            Escribir("p/g.o", "valor objeto");

            // Act
            (int codigo, string salida, string errores) = EjecutarConErrores(HerramientasSsh.ConstruirComandoBusqueda("valor [a-z]+", ruta, extension, 50));

            // Assert
            _ = (codigo, errores).Should().Be((0, string.Empty));
            _ = salida.Should().Be($"__NS_MOTOR:{anunciado}\n{esperado}\n");
        }

        public static TheoryData<string, string, string?, string> CasosRutaPedidaExcluida()
        {
            TheoryData<string, string, string?, string> casos = [];
            foreach (string motor in new[] { "rg", "grep -P", "grep -E", "find" })
            {
                casos.Add(motor, "p/node_modules", null, "p/node_modules/n.txt:1:valor nm");
                casos.Add(motor, "p/node_modules/", null, "p/node_modules/n.txt:1:valor nm");
                casos.Add(motor, "p/build", null, "p/build/b.txt:1:valor build");
                casos.Add(motor, "p/dist", "txt", "p/dist/d.txt:1:valor dist");
                casos.Add(motor, "p/src/build", null, "p/src/build/s.txt:1:valor src build");
                casos.Add(motor, "p/package-lock.json", null, "p/package-lock.json:1:valor lock");
                casos.Add(motor, "p/f.min.js", null, "p/f.min.js:1:valor min");
                casos.Add(motor, "p/g.o", null, "p/g.o:1:valor objeto");
                casos.Add(motor, "p/src/a.py", "js", "p/src/a.py:1:valor py");
                casos.Add(motor, "p/src/a.py", null, "p/src/a.py:1:valor py");
            }

            return casos;
        }

        // Un binario pedido como ruta que casa nunca es «sin coincidencias»: el grep de GNU lo callaba con -I. Cada motor
        // lo dice a su manera (rg y grep de GNU, «binary file matches»; busybox da la línea), pero todos con su nombre.
        [Theory]
        [MemberData(nameof(Motores))]
        public void BinarioPedidoComoRuta_TodosLosMotoresDicenQueCasa(string motor)
        {
            // Arrange
            string? anunciado = InstalarMotor(motor);
            if (anunciado is null)
                return;
            _ = Directory.CreateDirectory(Path.Combine(_datos, "p"));
            File.WriteAllBytes(Path.Combine(_datos, "p", "datos.bin"), [.. "valor binario\n"u8, 0, 1, .. "\n"u8]);

            // Act
            (int codigo, string salida, string errores) = EjecutarConErrores(HerramientasSsh.ConstruirComandoBusqueda("valor [a-z]+", "p/datos.bin", null, 50));
            (int codigoNada, string salidaNada, _) = EjecutarConErrores(HerramientasSsh.ConstruirComandoBusqueda("no_aparece", "p/datos.bin", null, 50));

            // Assert
            _ = (codigo, errores).Should().Be((0, string.Empty));
            string[] lineas = salida.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _ = lineas[0].Should().Be($"__NS_MOTOR:{anunciado}");
            _ = lineas[1..].Should().ContainSingle().Which.Should().StartWith("p/datos.bin:");
            _ = salidaNada.Should().Be($"__NS_MOTOR:{anunciado}\n");
            _ = codigoNada.Should().Be(motor == "find" ? 0 : 1);
        }

        // Dentro del árbol, los cuatro caminos excluyen lo mismo: los directorios de la lista se podan a cualquier
        // profundidad, pero un archivo que se llama como uno de ellos (aquí 'bin' y 'build', p. ej. un script) se busca
        // (rg los excluía también como archivos), y un enlace a un directorio no se sigue (como grep -r y rg sin -L; el
        // recorrido con find no debe usar -L).
        [Theory]
        [MemberData(nameof(Motores))]
        public void DentroDelArbol_MismasExclusionesYSinSeguirEnlacesEnLosCuatroMotores(string motor)
        {
            // Arrange
            string? anunciado = InstalarMotor(motor);
            if (anunciado is null)
                return;
            Escribir("q/real/r.txt", "valor real");
            Escribir("q/herramientas/bin", "valor script bin");
            Escribir("q/build", "valor script build");
            Escribir("q/bin/x.txt", "valor dir bin");
            Escribir("q/sub/build/y.txt", "valor dir build");
            _ = Directory.CreateSymbolicLink(Path.Combine(_datos, "q", "enlace"), Path.Combine(_datos, "q", "real"));

            // Act
            (int codigo, string salida, string errores) = EjecutarConErrores(HerramientasSsh.ConstruirComandoBusqueda("valor [a-z]+", "q", null, 50));

            // Assert
            _ = (codigo, errores).Should().Be((0, string.Empty));
            string[] lineas = salida.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _ = lineas[0].Should().Be($"__NS_MOTOR:{anunciado}");
            _ = lineas[1..].Should().BeEquivalentTo("q/real/r.txt:1:valor real", "q/herramientas/bin:1:valor script bin", "q/build:1:valor script build");
        }

        // El filtro de extensión no anula las exclusiones en ningún motor: en rg manda el último glob que casa y, con el
        // filtro detrás de las exclusiones, '*' volvía a incluir .git y .cache, y '*.json', package-lock.json.
        [Theory]
        [MemberData(nameof(CasosFiltroYExclusiones))]
        public void FiltroDeExtension_NoAnulaLasExclusionesEnLosCuatroMotores(string motor, string extension, string[] esperadas)
        {
            // Arrange
            string? anunciado = InstalarMotor(motor);
            if (anunciado is null)
                return;
            Escribir("s/.git/g.json", "valor git");
            Escribir("s/.cache/c.txt", "valor cache");
            Escribir("s/package-lock.json", "valor lock");
            Escribir("s/conf.json", "valor conf");
            Escribir("s/x.lock", "valor candado");
            Escribir("s/a.txt", "valor texto");

            // Act
            (int codigo, string salida, string errores) = EjecutarConErrores(HerramientasSsh.ConstruirComandoBusqueda("valor [a-z]+", "s", extension, 50));

            // Assert
            _ = (codigo, errores).Should().Be((0, string.Empty));
            string[] lineas = salida.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _ = lineas[0].Should().Be($"__NS_MOTOR:{anunciado}");
            _ = lineas[1..].Should().BeEquivalentTo(esperadas);
        }

        public static TheoryData<string, string, string[]> CasosFiltroYExclusiones()
        {
            TheoryData<string, string, string[]> casos = [];
            foreach (string motor in new[] { "rg", "grep -P", "grep -E", "find" })
            {
                casos.Add(motor, "*", ["s/conf.json:1:valor conf", "s/a.txt:1:valor texto"]);
                casos.Add(motor, "json", ["s/conf.json:1:valor conf"]);
            }

            return casos;
        }

        // Sin grep de GNU, un error de find nunca es «sin coincidencias» (AN1): si find falla (aquí, uno que no admite la
        // expresión), el script sale con 2 y su error va a stderr. Lo que grep -rs y rg callan también se calla aquí: un
        // subdirectorio sin permiso dentro del árbol y el grep que muere por SIGPIPE cuando head ya tiene bastantes líneas.
        [Fact]
        public void SinGrepDeGnu_ErrorDeFind_NoEsSinCoincidencias()
        {
            // Arrange
            InstalarGrepDeBusybox();
            File.Delete(Path.Combine(_bin, "find"));
            string find = Path.Combine(_bin, "find");
            File.WriteAllText(find, $"#!{Ruta("bash")}\necho \"find: unrecognized: -mindepth\" >&2\nexit 1\n");
            File.SetUnixFileMode(find, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // Act
            (int codigo, string salida, string errores) = EjecutarConErrores(HerramientasSsh.ConstruirComandoBusqueda("valor", ".", null, 50));

            // Assert
            _ = codigo.Should().Be(2);
            _ = salida.Should().Be("__NS_MOTOR:grep -E\n");
            _ = errores.Should().Contain("find: unrecognized: -mindepth");
        }

        [Fact]
        public void SinGrepDeGnu_SubdirectorioSinPermisoYCorteDeHead_SinErrores()
        {
            // Arrange
            InstalarGrepDeBusybox();
            for (int i = 0; i < 400; i++)
                Escribir($"r/muchos/f{i:D3}.txt", $"valor {i}");
            Escribir("r/cerrado/x.txt", "valor cerrado");
            string cerrado = Path.Combine(_datos, "r", "cerrado");
            File.SetUnixFileMode(cerrado, UnixFileMode.None);

            try
            {
                // Act
                (int codigoTodo, string salidaTodo, string erroresTodo) = EjecutarConErrores(HerramientasSsh.ConstruirComandoBusqueda("valor 7[0-9]$", "r", null, 50));
                (int codigoCorte, string salidaCorte, string erroresCorte) = EjecutarConErrores(HerramientasSsh.ConstruirComandoBusqueda("valor", "r", null, 2));

                // Assert
                _ = (codigoTodo, erroresTodo).Should().Be((0, string.Empty));
                _ = salidaTodo.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(11, "el motor y las 10 coincidencias de muchos/");
                _ = (codigoCorte, erroresCorte).Should().Be((0, string.Empty));
                _ = salidaCorte.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(4, "el motor y el límite + 1 líneas");
            }
            finally
            {
                File.SetUnixFileMode(cerrado, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }
}
