using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Protocolo;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;
using Xunit.Abstractions;

namespace NativeSsh.Test.Integracion
{
    // ssh_exec con guardarSalida=true (C12) con bash real (sshd local, misma máquina: el archivo se comprueba directamente).
    // Cada prueba usa su propio prefijo (OpcionesNativeSsh.PrefijoGuardado) dentro de un directorio temporal propio, para no
    // compartir el /tmp/native_ssh-<usuario> de la máquina ni pisarse entre pruebas.
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    [Trait("Categoria", "SoloLinux")]
    public sealed partial class GuardarSalidaTests(ITestOutputHelper salida) : IAsyncLifetime
    {
        private readonly SshdPropio _sshd = new(string.Empty);
        private readonly string _raiz = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_guardar_{Guid.NewGuid():N}")).FullName;
        private GestorSesiones? _gestor;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (_gestor is not null)
                await _gestor.DisposeAsync();
            _sshd.Dispose();
            _ = Process.Start("chmod", ["-R", "u+rwx", _raiz])?.WaitForExit(5000);
            Directory.Delete(_raiz, recursive: true);
        }

        private string Prefijo => Path.Combine(_raiz, "native_ssh");

        private string Base => $"{Prefijo}-{Environment.UserName}";

        private HerramientasSsh Herramientas()
        {
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones() with { PrefijoGuardado = Prefijo };
            _gestor = new GestorSesiones(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            return new HerramientasSsh(_gestor, opciones);
        }

        private string Destino => _sshd.Sshd.Destino.Clave;

        private static string Modo(string ruta) => Convert.ToString((int)File.GetUnixFileMode(ruta), 8);

        private Task<ResultadoComando> GuardarAsync(HerramientasSsh h, string comando, int esperarSegundos = 60, int timeoutSegundos = 120) =>
            h.EjecutarAsync(Destino, comando, timeoutSegundos: timeoutSegundos, esperarSegundos: esperarSegundos, guardarSalida: true).ComoComando();

        private async Task<ResultadoComando> EsperarFinAsync(HerramientasSsh h, ResultadoComando r) =>
            r.Estado == EstadoPeticionEnum.Ejecutando ? await h.LeerSalidaAsync(r.Id, esperarSegundos: 60).ComoComando() : r;

        [GeneratedRegex("^(?<dir>.+)/(?<shell>[0-9a-f]{32})/(?<id>[0-9a-f]{32})\\.out$")]
        private static partial Regex RegexArchivo();

        // Más de 1 MiB: el archivo tiene la salida entera (sin el tope del búfer), las líneas y los bytes cuadran, la respuesta
        // trae solo las 5 primeras líneas, la ruta es base/<id del shell>/<id>.out y los permisos son 0700/0700/0600.
        [Fact]
        public async Task SalidaDeMasDe1MiB_ArchivoIntegroLineasBytesYPermisos()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            StringBuilder esperada = new();
            for (int i = 1; i <= 300_000; i++)
                _ = esperada.Append(i.ToString(CultureInfo.InvariantCulture)).Append('\n');

            // Act
            ResultadoComando r = await EsperarFinAsync(h, await GuardarAsync(h, "seq 1 300000"));

            // Assert
            salida.WriteLine($"{r.Estado} {r.CodigoSalida} {r.Archivo} {r.Lineas} {r.Bytes} aviso '{r.Aviso}'");
            _ = (r.Estado, r.CodigoSalida).Should().Be((EstadoPeticionEnum.Completado, 0));
            _ = r.Salida.Should().Be("1\n2\n3\n4\n5\n");
            Match ruta = RegexArchivo().Match(r.Archivo ?? string.Empty);
            _ = ruta.Success.Should().BeTrue(r.Archivo);
            _ = ruta.Groups["dir"].Value.Should().Be(Base);
            _ = ruta.Groups["id"].Value.Should().Be(r.Id);
            string contenido = await File.ReadAllTextAsync(r.Archivo!);
            _ = contenido.Should().Be(esperada.ToString());
            _ = r.Lineas.Should().Be(300_000);
            _ = r.Bytes.Should().Be(Encoding.UTF8.GetByteCount(contenido));
            _ = r.Bytes.Should().BeGreaterThan(1024 * 1024);
            _ = (Modo(Base), Modo(Path.GetDirectoryName(r.Archivo!)!), Modo(r.Archivo!)).Should().Be(("700", "700", "600"));
            _ = r.Aviso.Should().Contain(r.Archivo).And.Contain("ssh_read_file").And.Contain("ssh_search");
        }

        // RC8: con varios avisos, primero el de guardarSalida (la ruta y cómo leerla) y después las sugerencias de rendimiento.
        [Fact]
        public async Task ConAvisoDeAntipatron_LaRutaDelArchivoVaPrimero()
        {
            // Arrange
            HerramientasSsh h = Herramientas();

            // Act
            ResultadoComando r = await EsperarFinAsync(h, await GuardarAsync(h, $"find {_raiz} -name '*.nada'"));

            // Assert
            salida.WriteLine($"aviso '{r.Aviso}'");
            _ = r.Aviso.Should().StartWith($"Salida completa guardada en {r.Archivo}").And.Contain("Sugerencia de rendimiento");
        }

        // RC3: una barra de progreso con '\r' se ve como en un terminal (lo último que mostró), en la vista previa de
        // guardarSalida y sin guardarSalida, aunque la línea pase del recorte de la vista previa; el archivo guarda los bytes
        // tal cual.
        [Fact]
        public async Task BarraDeProgreso_SeVeComoEnUnTerminalYElArchivoTalCual()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            const string Comando = "printf 'a 10%%\\rb 20%%\\rc 30%%\\n'; for i in $(seq 1 200); do printf 'progreso %d%%\\r' $i; done; printf '\\r\\nfin\\n'";

            // Act
            ResultadoComando guardada = await EsperarFinAsync(h, await GuardarAsync(h, Comando));
            ResultadoComando directa = await h.EjecutarAsync(Destino, Comando, esperarSegundos: 30).ComoComando();

            // Assert
            _ = guardada.Salida.Should().Be("c 30%\nprogreso 200%\nfin\n");
            _ = directa.Salida.Should().Be("c 30%\nprogreso 200%\nfin\n");
            string archivo = await File.ReadAllTextAsync(guardada.Archivo!);
            _ = archivo.Should().StartWith("a 10%\rb 20%\rc 30%\nprogreso 1%\rprogreso 2%\r").And.EndWith("progreso 200%\r\r\nfin\n");
        }

        // El comando se evalúa byte a byte como sin guardarSalida: comillas simples y dobles, varias líneas y un heredoc.
        [Fact]
        public async Task ComandoConComillasYHeredoc_SeEjecutaTalCual()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            const string Comando = "printf '%s\\n' \"it's\" 'a\"b' \\\n  'c'\ncat <<'FIN'\nlínea $HOME '\nFIN";

            // Act
            ResultadoComando r = await GuardarAsync(h, Comando);
            ResultadoComando sinGuardar = await h.EjecutarAsync(Destino, Comando, esperarSegundos: 30).ComoComando();

            // Assert
            _ = (await File.ReadAllTextAsync(r.Archivo!)).Should().Be(sinGuardar.Salida).And.Be("it's\na\"b\nc\nlínea $HOME '\n");
        }

        // stdout y stderr, en orden, al archivo; el código de salida es el del comando.
        [Fact]
        public async Task ComandoQueFalla_ConservaElCodigoYGuardaStdoutYStderr()
        {
            // Arrange
            HerramientasSsh h = Herramientas();

            // Act
            ResultadoComando r = await GuardarAsync(h, "echo salida; echo error >&2; (exit 3)");

            // Assert
            _ = (r.Estado, r.CodigoSalida).Should().Be((EstadoPeticionEnum.Completado, 3));
            _ = (await File.ReadAllTextAsync(r.Archivo!)).Should().Be("salida\nerror\n");
            _ = (r.Lineas, r.Bytes).Should().Be((2L, 13L));
            _ = r.Salida.Should().Be("salida\nerror\n");
        }

        // Una última línea sin salto cuenta como línea (como en la cabecera de ssh_read_file); un archivo vacío, 0 y 0.
        [Theory]
        [InlineData("printf 'a\\nb'", 2, 3, "a\nb")]
        [InlineData("printf 'a\\n'", 1, 2, "a\n")]
        [InlineData("true", 0, 0, "")]
        [InlineData("printf 'x'", 1, 1, "x")]
        public async Task Lineas_UnaFinalSinSaltoCuenta(string comando, long lineas, long bytes, string vista)
        {
            // Arrange
            HerramientasSsh h = Herramientas();

            // Act
            ResultadoComando r = await GuardarAsync(h, comando);

            // Assert
            _ = (r.Lineas, r.Bytes, r.Salida).Should().Be((lineas, bytes, vista));
        }

        // Vista previa acotada en bytes: una línea enorme (aquí de 600 KB) se corta a 512 bytes sin partir un carácter, con una
        // marca; las demás líneas siguen saliendo (de lo que cabe en el primer MiB del archivo, que es lo que se lee).
        [Fact]
        public async Task VistaPrevia_LineasMuyLargasSeCortanSinPartirCaracteres()
        {
            // Arrange
            HerramientasSsh h = Herramientas();

            // Act
            ResultadoComando r = await GuardarAsync(h, "head -c 600000 /dev/zero | tr '\\0' 'x'; printf '\\na'; printf 'é%.0s' $(seq 1 800); printf '\\ncorta\\n'");

            // Assert
            salida.WriteLine(r.Salida);
            string[] lineas = r.Salida.Split('\n');
            _ = lineas.Length.Should().Be(4);
            _ = Encoding.UTF8.GetByteCount(lineas[0].Replace(" […]", "", StringComparison.Ordinal)).Should().BeLessThanOrEqualTo(SalidaGuardada.MaxBytesLineaVistaPrevia);
            _ = lineas[0].Should().EndWith(" […]").And.NotContain("\uFFFD");
            _ = lineas[1].Should().EndWith(" […]").And.StartWith("aé").And.NotContain("\uFFFD");
            _ = Encoding.UTF8.GetByteCount(lineas[1].Replace(" […]", "", StringComparison.Ordinal)).Should().Be(SalidaGuardada.MaxBytesLineaVistaPrevia - 1, "el byte 512 parte una 'é': se deja fuera");
            _ = lineas[2].Should().Be("corta");
            _ = r.Lineas.Should().Be(3);
        }

        // Lectura posterior con ssh_read_file y búsqueda con ssh_search sobre la ruta del control.
        [Fact]
        public async Task ElArchivoSeLeeConSshReadFileYSeBuscaConSshSearch()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando r = await GuardarAsync(h, "seq 1 1000");

            // Act
            string tramo = await h.LeerArchivoAsync(Destino, r.Archivo!, 500, 502);
            string busqueda = await h.BuscarAsync(Destino, "^777$", r.Archivo!);

            // Assert
            _ = tramo.Should().Be("[Líneas 500-502 de 1000]\n500\n501\n502\n");
            _ = busqueda.Should().Contain(":777:777");
        }

        // Al vencer esperarSegundos con el comando en marcha: el control ya da la ruta (sin líneas ni bytes) y ssh_output, al
        // terminar, el mismo control completo y la vista previa.
        [Fact]
        public async Task EnEjecucion_LaRutaYaSaleYSshOutputDaElResto()
        {
            // Arrange
            HerramientasSsh h = Herramientas();

            // Act
            ResultadoComando enCurso = await GuardarAsync(h, "echo antes; sleep 4; echo despues", esperarSegundos: 2);
            ResultadoComando fin = await h.LeerSalidaAsync(enCurso.Id, esperarSegundos: 30).ComoComando();

            // Assert
            salida.WriteLine($"en curso: {enCurso.Estado} {enCurso.Archivo} '{enCurso.Aviso}'");
            _ = enCurso.Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
            _ = enCurso.Archivo.Should().NotBeNull().And.EndWith($"/{enCurso.Id}.out");
            _ = enCurso.Lineas.Should().BeNull();
            _ = enCurso.Bytes.Should().BeNull();
            _ = enCurso.Salida.Should().BeEmpty();
            _ = enCurso.Aviso.Should().Contain("ssh_output");
            _ = (fin.Estado, fin.Archivo, fin.Lineas, fin.Salida).Should().Be((EstadoPeticionEnum.Completado, enCurso.Archivo, 2L, "antes\ndespues\n"));
        }

        // Compatible con __NS_CWD y con el estado del shell: el comando corre en el directorio fijado y sus 'export' cuentan.
        [Fact]
        public async Task ConSshSetCwd_YVariablesDelShell()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string directorio = Directory.CreateDirectory(Path.Combine(_raiz, "cwd con espacio")).FullName;
            _ = await h.FijarDirectorioAsync(Destino, directorio);

            // Act
            ResultadoComando pwd = await GuardarAsync(h, "pwd; export NS_GUARDADO=si");
            ResultadoComando variable = await h.EjecutarAsync(Destino, "echo $NS_GUARDADO", esperarSegundos: 30).ComoComando();

            // Assert
            _ = pwd.Salida.Should().Be(directorio + "\n");
            _ = variable.Salida.Should().Be("si\n");
        }

        // Los avisos de antipatrón siguen funcionando: miran el comando pedido, no el envoltorio.
        [Fact]
        public async Task AvisosDeAntipatron_SiguenFuncionando()
        {
            // Arrange
            HerramientasSsh h = Herramientas();

            // Act
            ResultadoComando r = await GuardarAsync(h, "grep -r nada_que_encontrar_ns /dev/null; true");

            // Assert
            _ = r.Aviso.Should().Contain("ssh_search");
            _ = (await h.EjecutarAsync(Destino, "true", esperarSegundos: 30).ComoComando()).Aviso.Should().BeNull();
        }

        // Limpieza: ssh_reset_shell, ssh_disconnect y el cierre de la sesión borran el directorio del shell (el base se queda).
        // Un shell recreado tras una caída no lo borra (GuardarSalidaTests.Conservacion).
        [Theory]
        [InlineData("reset")]
        [InlineData("disconnect")]
        [InlineData("cierre")]
        public async Task Limpieza_BorraElDirectorioDelShell(string camino)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando r = await GuardarAsync(h, "echo hola");
            string directorio = Path.GetDirectoryName(r.Archivo!)!;
            _ = File.Exists(r.Archivo!).Should().BeTrue();

            // Act
            switch (camino)
            {
                case "reset":
                    _ = h.ReiniciarShell(Destino);
                    break;
                case "disconnect":
                    _ = await h.DesconectarAsync(Destino);
                    break;
                default:
                    await _gestor!.DisposeAsync();
                    _gestor = null;
                    break;
            }

            // Assert
            await EsperasPrueba.EsperarHastaAsync(() => !Directory.Exists(directorio), TimeSpan.FromSeconds(15));
            _ = Directory.Exists(Base).Should().BeTrue();
        }

        // Interrumpido por su plazo (Ctrl+C): no hay marca final; el aviso da la ruta de lo escrito hasta entonces, que sigue
        // ahí (el shell sobrevive), sin recuento; con ssh_cancel, lo mismo y además que ssh_reset_shell o ssh_disconnect lo
        // habrían borrado.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Interrumpido_AvisaConLaRutaYLoEscritoSigue(bool isCancelado)
        {
            // Arrange
            HerramientasSsh h = Herramientas();

            // Act
            ResultadoComando r = await GuardarAsync(h, "echo antes; sleep 30", esperarSegundos: isCancelado ? 2 : 30, timeoutSegundos: isCancelado ? 60 : 2);
            if (isCancelado)
                r = await h.CancelarAsync(r.Id).ComoComando();

            // Assert
            salida.WriteLine($"{r.Estado} '{r.Aviso}'");
            _ = r.Estado.Should().Be(isCancelado ? EstadoPeticionEnum.Cancelado : EstadoPeticionEnum.TiempoAgotado);
            _ = r.Lineas.Should().BeNull();
            _ = r.Aviso.Should().Contain($"está en {r.Archivo}").And.Contain("sin recuento");
            _ = (r.Aviso ?? string.Empty).Contains("ssh_reset_shell o ssh_disconnect", StringComparison.Ordinal).Should().Be(isCancelado);
            _ = (await File.ReadAllTextAsync(r.Archivo!)).Should().Be("antes\n");
        }

        // Con MaxSessions 1 el shell ocupa la única sesión: la limpieza de ssh_reset_shell (un canal exec) la rechaza el
        // servidor y vuelve a la lista; ssh_disconnect, ya con el shell cerrado, la reintenta y borra el directorio.
        [Fact]
        public async Task LimpiezaRechazadaPorMaxSessions_SeReintentaAlDesconectar()
        {
            // Arrange
            using SshdPropio restringido = new("MaxSessions 1");
            OpcionesNativeSsh opciones = restringido.Sshd.Opciones() with { PrefijoGuardado = Prefijo };
            await using GestorSesiones gestor = new(restringido.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh h = new(gestor, opciones);
            string destino = restringido.Sshd.Destino.Clave;
            ResultadoComando r = await h.EjecutarAsync(destino, "echo hola", esperarSegundos: 30, guardarSalida: true).ComoComando();
            string directorio = Path.GetDirectoryName(r.Archivo!)!;

            // Act
            _ = h.ReiniciarShell(destino);
            await Task.Delay(2000);
            bool isTrasReset = Directory.Exists(directorio);
            _ = await h.DesconectarAsync(destino);

            // Assert
            _ = isTrasReset.Should().BeTrue("el servidor rechaza el canal exec mientras el shell ocupa la única sesión");
            _ = Directory.Exists(directorio).Should().BeFalse();
        }

        // Tras ssh_reset_shell el siguiente guardarSalida usa otro directorio (el del shell nuevo).
        [Fact]
        public async Task TrasReset_OtroDirectorio()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando primero = await GuardarAsync(h, "echo uno");

            // Act
            _ = h.ReiniciarShell(Destino);
            ResultadoComando segundo = await GuardarAsync(h, "echo dos");

            // Assert
            _ = Path.GetDirectoryName(segundo.Archivo!).Should().NotBe(Path.GetDirectoryName(primero.Archivo!));
            _ = (await File.ReadAllTextAsync(segundo.Archivo!)).Should().Be("dos\n");
        }

        // Directorio base ajeno: un enlace, un directorio con otro modo o un archivo en su lugar: error claro, el comando no se
        // ejecuta y no se escribe nada (tampoco a través del enlace).
        [Theory]
        [InlineData("enlace")]
        [InlineData("modo755")]
        [InlineData("archivo")]
        public async Task BaseAjeno_ErrorSinEjecutarNiEscribir(string caso)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string destinoEnlace = Directory.CreateDirectory(Path.Combine(_raiz, "atacante")).FullName;
            switch (caso)
            {
                case "enlace":
                    File.SetUnixFileMode(destinoEnlace, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    _ = Directory.CreateSymbolicLink(Base, destinoEnlace);
                    break;
                case "modo755":
                    _ = Directory.CreateDirectory(Base);
                    File.SetUnixFileMode(Base, (UnixFileMode)Convert.ToInt32("755", 8));
                    break;
                default:
                    await File.WriteAllTextAsync(Base, "x");
                    break;
            }

            string testigo = Path.Combine(_raiz, "ejecutado");

            // Act
            ResultadoComando r = await GuardarAsync(h, $"touch '{testigo}'; echo secreto");

            // Assert
            salida.WriteLine($"{r.Estado} '{r.Aviso}'");
            _ = (r.Estado, r.CodigoSalida).Should().Be((EstadoPeticionEnum.Error, -1));
            _ = r.Aviso.Should().StartWith("El comando NO se ejecutó").And.Contain(Base);
            _ = File.Exists(testigo).Should().BeFalse();
            _ = Directory.GetFileSystemEntries(destinoEnlace).Should().BeEmpty("no se escribe nada a través del enlace");
            _ = r.Archivo.Should().BeNull();
        }

        // El directorio del shell sustituido por un enlace entre dos comandos: error, sin seguirlo.
        [Fact]
        public async Task DirectorioDelShellSustituidoPorUnEnlace_Error()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando primero = await GuardarAsync(h, "echo uno");
            string directorio = Path.GetDirectoryName(primero.Archivo!)!;
            string otro = Directory.CreateDirectory(Path.Combine(_raiz, "otro")).FullName;
            Directory.Delete(directorio, recursive: true);
            _ = Directory.CreateSymbolicLink(directorio, otro);

            // Act
            ResultadoComando r = await GuardarAsync(h, "echo dos");

            // Assert
            _ = r.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = r.Aviso.Should().Contain("ssh_reset_shell");
            _ = Directory.GetFileSystemEntries(otro).Should().BeEmpty();
        }

        // Barrido al crear el directorio de un shell: los de otros shells sin nada modificado en 24 h se borran; uno reciente
        // (u otro con un archivo reciente) se queda.
        [Fact]
        public async Task Barrido_BorraLosRestosDeMasDe24Horas()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            _ = Directory.CreateDirectory(Base);
            File.SetUnixFileMode(Base, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string viejo = Directory.CreateDirectory(Path.Combine(Base, new string('a', 32))).FullName;
            string viejoArchivo = Path.Combine(viejo, "x.out");
            await File.WriteAllTextAsync(viejoArchivo, "viejo");
            File.SetLastWriteTimeUtc(viejoArchivo, DateTime.UtcNow.AddDays(-2));
            Directory.SetLastWriteTimeUtc(viejo, DateTime.UtcNow.AddDays(-2));
            string activo = Directory.CreateDirectory(Path.Combine(Base, new string('b', 32))).FullName;
            await File.WriteAllTextAsync(Path.Combine(activo, "x.out"), "activo");
            Directory.SetLastWriteTimeUtc(activo, DateTime.UtcNow.AddDays(-2));

            // Act
            _ = await GuardarAsync(h, "echo hola");

            // Assert
            _ = Directory.Exists(viejo).Should().BeFalse();
            _ = Directory.Exists(activo).Should().BeTrue("tiene un archivo modificado hace menos de 24 h");
        }

        // Cupo por shell: con 100 archivos (o 256 MiB) ya guardados, el comando no se ejecuta y el mensaje dice cómo liberar.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Cupo_PorEncimaNoSeEjecuta(bool isPorBytes)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando primero = await GuardarAsync(h, "echo uno");
            string directorio = Path.GetDirectoryName(primero.Archivo!)!;
            if (isPorBytes)
            {
                using FileStream grande = File.Create(Path.Combine(directorio, "grande.out"));
                grande.SetLength(SalidaGuardada.MaxBytesPorShell);
            }
            else
                for (int i = 1; i < SalidaGuardada.MaxArchivosPorShell; i++)
                    await File.WriteAllTextAsync(Path.Combine(directorio, $"{i}.out"), "x");

            string testigo = Path.Combine(_raiz, "ejecutado");

            // Act
            ResultadoComando r = await GuardarAsync(h, $"touch '{testigo}'");

            // Assert
            salida.WriteLine(r.Aviso);
            _ = r.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = r.Aviso.Should().Contain("ssh_reset_shell").And.Contain(directorio);
            _ = File.Exists(testigo).Should().BeFalse();
        }

        // Mucho escrito directamente en el terminal (más que el búfer de 1 MiB): la respuesta sigue funcionando, se lee la marca
        // final (la ruta y el recuento) y lo que se lee del búfer se queda en su capacidad.
        [Fact]
        public async Task MuchoEnElTerminal_LaRespuestaSigueYLaMarcaFinalSeLee()
        {
            // Arrange
            HerramientasSsh h = Herramientas();

            // Act
            ResultadoComando r = await GuardarAsync(h, "head -c 3145728 /dev/zero | tr '\\0' 't' > /dev/tty; echo al_archivo");
            ResultadoComando otra = await h.LeerSalidaAsync(r.Id).ComoComando();

            // Assert
            _ = (r.Estado, r.CodigoSalida, r.Lineas, r.Bytes).Should().Be((EstadoPeticionEnum.Completado, 0, 1L, 11L));
            _ = r.Archivo.Should().EndWith($"/{r.Id}.out");
            _ = r.Salida.Should().StartWith("al_archivo\n--- escrito directamente en el terminal");
            _ = otra.Lineas.Should().Be(1);
        }

        // Un secreto enviado con ssh_input no sale en la vista previa (viene del archivo, sin enmascarar).
        [Fact]
        public async Task ConSecreto_SinVistaPrevia()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando lector = await GuardarAsync(h, "read -r x; echo \"leído:$x\"", esperarSegundos: 2);

            // Act
            ResultadoComando r = await h.EnviarEntradaAsync(lector.Id, "SecretoCanario123", esperarSegundos: 30, secreto: true).ComoComando();

            // Assert
            _ = r.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = r.Salida.Should().NotContain("SecretoCanario123");
            _ = r.Aviso.Should().Contain("Sin vista previa");
            _ = (await File.ReadAllTextAsync(r.Archivo!)).Should().Be("leído:SecretoCanario123\n");
        }

        // Lo que el comando escribe directamente en el terminal (no va al archivo) sale aparte en el bloque de salida.
        [Fact]
        public async Task EscritoEnElTerminal_SaleAparte()
        {
            // Arrange
            HerramientasSsh h = Herramientas();

            // Act
            ResultadoComando r = await GuardarAsync(h, "echo al_archivo; echo al_terminal > /dev/tty");

            // Assert
            salida.WriteLine(r.Salida);
            _ = r.Salida.Should().StartWith("al_archivo\n").And.Contain("al_terminal").And.NotContain("__NSG");
            _ = (await File.ReadAllTextAsync(r.Archivo!)).Should().Be("al_archivo\n");
        }
    }
}
