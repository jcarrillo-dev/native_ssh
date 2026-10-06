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
        [Theory]
        [MemberData(nameof(CasosRutaQueFindTomaPorOpcion))]
        public void RutaQueFindTomaPorOpcion_TodosLosMotoresBuscanSoloDentro(string motor, string ruta, string esperado)
        {
            // Arrange
            string? anunciado = InstalarMotor(motor);
            if (anunciado is null)
                return;
            foreach ((string archivo, string texto) in new[] { ("-d/f.txt", "valor guion"), ("(/p.txt", "valor paren"), (")/q.txt", "valor cierre"), ("!/e.txt", "valor excl"), (",/c.txt", "valor coma"), (".git/g.txt", "valor git") })
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
