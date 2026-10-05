using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Herramientas de archivo contra el sshd local: lo que depende del shell real (enlaces, permisos, tiempos...).
    [Collection(ColeccionSshd.Nombre)]
    [Trait("Categoria", "Integracion")]
    // IAsyncLifetime (no IAsyncDisposable): es lo que xUnit v2 invoca al terminar cada prueba.
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El gestor se libera en IAsyncLifetime.DisposeAsync.")]
    public sealed class HerramientasArchivoIntegracionTests : IAsyncLifetime
    {
        private readonly SshdLocal _sshd;
        private readonly string _directorio = Path.Combine(Path.GetTempPath(), $"ns_archivos_{Guid.NewGuid():N}");
        private readonly GestorSesiones _gestor;
        private readonly FabricaMedidora _fabrica;

        public HerramientasArchivoIntegracionTests(SshdLocal sshd)
        {
            _sshd = sshd;
            _ = Directory.CreateDirectory(_directorio);
            OpcionesNativeSsh opciones = sshd.Opciones();
            _fabrica = new FabricaMedidora(sshd.Fabrica(opciones));
            _gestor = new GestorSesiones(
                _fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
        }

        private string Destino => _sshd.Destino.Clave;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            await _gestor.DisposeAsync();
            // Las pruebas de sudo dejan directorios 0000: sin devolver los permisos no se podrían borrar.
            if (Directory.Exists(_directorio))
            {
                _ = Bash($"chmod -R u+rwx '{_directorio}'");
                Directory.Delete(_directorio, recursive: true);
            }
        }

        private HerramientasSsh Herramientas(OpcionesNativeSsh? opciones = null, TimeSpan? timeout = null) =>
            new(_gestor, opciones ?? _sshd.Opciones()) { TimeoutForzado = timeout };

        private string Ruta(string nombre) => Path.Combine(_directorio, nombre);

        private static string Bash(string comando)
        {
            ProcessStartInfo inicio = new("bash", ["-c", comando]) { RedirectStandardOutput = true };
            using Process proceso = Process.Start(inicio)!;
            string salida = proceso.StandardOutput.ReadToEnd();
            proceso.WaitForExit();
            return salida.Trim();
        }

        [Fact]
        public async Task LeerArchivo_BloqueadoEnUnaFifo_LanzaTiempoAgotadoYElShellSigueUsable()
        {
            // Arrange
            string fifo = Ruta("fifo");
            _ = Bash($"mkfifo '{fifo}'");
            string normal = Ruta("normal.txt");
            await File.WriteAllTextAsync(normal, "uno\n");
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(1));

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync(Destino, fifo))
                .Should().ThrowAsync<NativeSshException>()).Which;
            string siguiente = await herramientas.LeerArchivoAsync(Destino, normal);

            // Assert
            _ = error.Message.Should().Contain("tiempo agotado");
            _ = siguiente.Should().Be("[Líneas 1-1 de 1]\nuno\n");

            // Y el lector remoto no queda huérfano: sin lector, abrir la FIFO para escribir no termina (timeout = 124).
            await EsperasPrueba.EsperarHastaAsync(() => Bash($"timeout 0.5 bash -c \"echo x > '{fifo}'\"; echo $?") == "124", TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ReemplazarContenido_ArchivoMayorQueElMaximoEditable_SeRechazaSinTocarlo()
        {
            // Arrange: una línea de más del límite (773 184 bytes con el máximo por defecto).
            string archivo = Ruta("grande.txt");
            string contenido = "objetivo\n" + new string('x', 800_000) + "\n";
            await File.WriteAllTextAsync(archivo, contenido);
            DateTime modificado = File.GetLastWriteTimeUtc(archivo);
            HerramientasSsh herramientas = Herramientas();

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "objetivo", "cambiado", 1, 1))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("supera el máximo editable");
            _ = error.Message.Should().Contain("773184");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be(contenido);
            _ = File.GetLastWriteTimeUtc(archivo).Should().Be(modificado);
            _ = Directory.GetFiles(_directorio).Should().ContainSingle();
        }

        [Fact]
        public async Task LeerArchivo_RangoCuyaSalidaSuperaElMaximo_LanzaErrorSinDevolverLaCola()
        {
            // Arrange: 2 000 líneas de 1 000 bytes = 2 MB > 1 MiB por petición.
            string archivo = Ruta("largo.txt");
            await File.WriteAllLinesAsync(archivo, Enumerable.Range(1, 2000).Select(i => $"{i:D6}" + new string('y', 993)));
            HerramientasSsh herramientas = Herramientas();

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync(Destino, archivo, 1, 2000))
                .Should().ThrowAsync<NativeSshException>()).Which;
            string parcial = await herramientas.LeerArchivoAsync(Destino, archivo, 1999, 2000);

            // Assert
            _ = error.Message.Should().Contain("superó el máximo");
            _ = parcial.Should().StartWith("[Líneas 1999-2000 de 2000]\n001999");
        }

        [Fact]
        public async Task BuscarFindYListar_ResultadosAcotadosEnElServidorConAvisoDeLimite()
        {
            // Arrange: 300 archivos con una coincidencia cada uno y 300 subdirectorios.
            for (int i = 0; i < 300; i++)
            {
                await File.WriteAllTextAsync(Ruta($"a{i:D3}.txt"), "coincidencia_acotada\n");
                _ = Directory.CreateDirectory(Ruta($"d{i:D3}"));
            }

            HerramientasSsh herramientas = Herramientas();

            // Act
            string busqueda = await herramientas.BuscarAsync(Destino, "coincidencia_acotada", _directorio, maxResultados: 5);
            long bytesBusqueda = _fabrica.UltimosBytes("grep -rnI");
            string nombres = await herramientas.BuscarPorNombreAsync(Destino, "a*.txt", _directorio, maxResultados: 5);
            long bytesNombres = _fabrica.UltimosBytes("-name 'a*.txt'");
            string listado = await herramientas.ListarDirectoriosAsync(Destino, _directorio, profundidadMax: 1, maxResultados: 5);
            long bytesListado = _fabrica.UltimosBytes("-type d -print");

            // Assert: llegan como mucho limite + 1 líneas y se sigue avisando del límite.
            _ = busqueda.Should().Contain("[Límite alcanzado: se muestran 5 coincidencias");
            _ = busqueda.Split('\n').Count(l => l.Contains("coincidencia_acotada", StringComparison.Ordinal) && !l.StartsWith('[')).Should().Be(5);
            _ = bytesBusqueda.Should().BeLessThan(6 * 200);
            _ = nombres.Should().StartWith("a000.txt\na001.txt\na002.txt\na003.txt\na004.txt\n\n[Límite alcanzado: se muestran 5 resultados");
            _ = bytesNombres.Should().BeLessThan(6 * 20);
            _ = listado.Should().StartWith("d000\nd001\nd002\nd003\nd004\n\n[Límite alcanzado: se muestran 5 directorios");
            _ = bytesListado.Should().BeLessThan(6 * 20);
        }

        [Fact]
        public async Task Buscar_PatronPcre_EncuentraDigitosEIndicaElMotor()
        {
            // Arrange: con grep -E, '\d+' no encuentra nada (en ERE '\d' no es un dígito).
            await File.WriteAllTextAsync(Ruta("numeros.txt"), "valor 123\ntexto\n");
            HerramientasSsh herramientas = Herramientas();

            // Act
            string resultado = await herramientas.BuscarAsync(Destino, @"\d+", _directorio);

            // Assert
            _ = resultado.Should().StartWith($"{Ruta("numeros.txt")}:1:valor 123\n\n[motor: ");
            _ = resultado.Should().ContainAny("[motor: rg]", "[motor: grep -P]");
        }

        [Fact]
        public async Task LeerArchivo_InicioNegativo_DevuelveLasUltimasLineasConLaMismaCabecera()
        {
            // Arrange
            string archivo = Ruta("diez.txt");
            await File.WriteAllLinesAsync(archivo, Enumerable.Range(1, 10).Select(i => $"l{i}"));
            string vacio = Ruta("vacio.txt");
            await File.WriteAllTextAsync(vacio, string.Empty);
            string sinSalto = Ruta("sin_salto.txt");
            await File.WriteAllTextAsync(sinSalto, "a\nb\nc");
            HerramientasSsh herramientas = Herramientas();

            // Act
            string ultimas = await herramientas.LeerArchivoAsync(Destino, archivo, -3);
            string todas = await herramientas.LeerArchivoAsync(Destino, archivo, -20);
            string deVacio = await herramientas.LeerArchivoAsync(Destino, vacio, -5);
            string finSinSalto = await herramientas.LeerArchivoAsync(Destino, sinSalto, -2);
            string completoSinSalto = await herramientas.LeerArchivoAsync(Destino, sinSalto);

            // Assert
            _ = ultimas.Should().Be("[Líneas 8-10 de 10]\nl8\nl9\nl10\n");
            _ = todas.Should().Be("[Líneas 1-10 de 10]\n" + string.Concat(Enumerable.Range(1, 10).Select(i => $"l{i}\n")));
            _ = deVacio.Should().Be("[Líneas 0-0 de 0]\n");
            _ = finSinSalto.Should().Be("[Líneas 2-3 de 3]\nb\nc");
            _ = completoSinSalto.Should().Be("[Líneas 1-3 de 3]\na\nb\nc");
        }

        [Fact]
        public async Task ConsultarMetadatos_DevuelvePropietarioGrupoYDestinoDeEnlace()
        {
            // Arrange
            string archivo = Ruta("real.txt");
            await File.WriteAllTextAsync(archivo, "x");
            string enlace = Ruta("enlace.txt");
            _ = File.CreateSymbolicLink(enlace, archivo);
            string grupo = Bash("id -gn");
            HerramientasSsh herramientas = Herramientas();

            // Act
            ResultadoMetadatos real = await herramientas.ConsultarMetadatosAsync(Destino, archivo);
            ResultadoMetadatos deEnlace = await herramientas.ConsultarMetadatosAsync(Destino, enlace);

            // Assert
            _ = real.Propietario.Should().Be(Environment.UserName);
            _ = real.Grupo.Should().Be(grupo);
            _ = real.DestinoEnlace.Should().BeNull();
            _ = deEnlace.DestinoEnlace.Should().Be(archivo);
            _ = deEnlace.Propietario.Should().Be(Environment.UserName);
        }

        [Fact]
        public async Task ReemplazarContenido_ArchivoNoUtf8_SeRechazaSinTocarlo()
        {
            // Arrange: Latin-1 ('é' = 0xE9), inválido en UTF-8.
            string archivo = Ruta("latin1.txt");
            byte[] original = [.. "caf"u8, 0xE9, .. "\nsegunda\n"u8];
            await File.WriteAllBytesAsync(archivo, original);
            HerramientasSsh herramientas = Herramientas();

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "segunda", "otra", 1, 2))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("UTF-8");
            _ = (await File.ReadAllBytesAsync(archivo)).Should().Equal(original);
        }

        // Sustituye un comando del servidor en el shell persistente (PATH exportado) para forzar casos difíciles.
        private async Task SustituirComandoAsync(HerramientasSsh herramientas, string nombre, string script)
        {
            string bin = Ruta("bin");
            _ = Directory.CreateDirectory(bin);
            string ruta = Path.Combine(bin, nombre);
            await File.WriteAllTextAsync(ruta, "#!/bin/bash\n" + script + "\n");
            _ = Bash($"chmod 700 '{ruta}'");
            ResultadoComando export = await herramientas.EjecutarAsync(Destino, $"export PATH='{bin}':\"$PATH\"", esperarSegundos: 10);
            _ = export.CodigoSalida.Should().Be(0);
        }

        private static string Stat(string ruta) => Bash($"stat -c '%a %G %i %h' -- '{ruta}'");

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Sobrescribir_EnlaceSimbolico_EscribeEnElDestinoYConservaElEnlace(bool isReplace)
        {
            // Arrange
            string real = Ruta("real.txt");
            await File.WriteAllTextAsync(real, "antes\n");
            string enlace = Ruta("enlace.txt");
            _ = File.CreateSymbolicLink(enlace, "real.txt");
            HerramientasSsh herramientas = Herramientas();

            // Act
            _ = isReplace
                ? await herramientas.ReemplazarContenidoArchivoAsync(Destino, enlace, "antes", "despues", 1, 1)
                : await herramientas.EscribirArchivoAsync(Destino, enlace, "despues\n", overwrite: true);

            // Assert
            _ = new FileInfo(enlace).LinkTarget.Should().Be("real.txt");
            _ = (await File.ReadAllTextAsync(real)).Should().Be("despues\n");
            _ = Directory.GetFiles(_directorio).Should().HaveCount(2);
        }

        [Theory]
        [InlineData("600", false)]
        [InlineData("755", false)]
        [InlineData("600", true)]
        [InlineData("755", true)]
        public async Task Sobrescribir_ConservaModoYGrupoDelOriginal(string modo, bool isReplace)
        {
            // Arrange: grupo secundario del usuario, distinto del primario que recibiría un archivo nuevo.
            string grupo = GrupoSecundario();
            string archivo = Ruta("modo.txt");
            await File.WriteAllTextAsync(archivo, "antes\n");
            _ = Bash($"chmod {modo} '{archivo}' && chgrp '{grupo}' '{archivo}'");
            HerramientasSsh herramientas = Herramientas();

            // Act
            ResultadoEscritura resultado = isReplace
                ? await herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "antes", "despues", 1, 1)
                : await herramientas.EscribirArchivoAsync(Destino, archivo, "despues\n", overwrite: true);

            // Assert
            _ = grupo.Should().NotBeEmpty();
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("despues\n");
            _ = Stat(archivo).Should().StartWith($"{modo} {grupo} ");
            _ = resultado.IsAtomica.Should().BeTrue();
            _ = resultado.Mensaje.Should().NotContain("no atómica");
            _ = Directory.GetFiles(_directorio).Should().ContainSingle();
        }

        [Fact]
        public async Task Sobrescribir_ArchivoConEnlacesDuros_EscribeEnElSitioYAvisa()
        {
            // Arrange
            string archivo = Ruta("duro.txt");
            string otroNombre = Ruta("duro2.txt");
            await File.WriteAllTextAsync(archivo, "antes\n");
            _ = Bash($"ln '{archivo}' '{otroNombre}'");
            string inodo = Stat(archivo).Split(' ')[2];
            HerramientasSsh herramientas = Herramientas();

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(Destino, archivo, "despues\n", overwrite: true);

            // Assert
            _ = resultado.IsAtomica.Should().BeFalse();
            _ = resultado.Mensaje.Should().Contain("no atómica");
            _ = (await File.ReadAllTextAsync(otroNombre)).Should().Be("despues\n");
            _ = Stat(archivo).Split(' ')[2].Should().Be(inodo);
            _ = Directory.GetFiles(_directorio).Should().HaveCount(2);
        }

        // Sin sudo no se puede crear un archivo de otro usuario: se fuerza el mismo camino haciendo fallar 'chown'
        // (exactamente lo que ocurre al intentar 'chown --reference' sobre un archivo ajeno sin privilegios).
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Sobrescribir_SinPoderConservarElPropietario_EscribeEnElSitioYAvisa(bool isReplace)
        {
            // Arrange
            string archivo = Ruta("ajeno.txt");
            await File.WriteAllTextAsync(archivo, "antes\n");
            _ = Bash($"chmod 640 '{archivo}'");
            string inodo = Stat(archivo).Split(' ')[2];
            HerramientasSsh herramientas = Herramientas();
            await SustituirComandoAsync(herramientas, "chown", "echo 'chown: operación no permitida' >&2; exit 1");

            // Act
            ResultadoEscritura resultado = isReplace
                ? await herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "antes", "despues", 1, 1)
                : await herramientas.EscribirArchivoAsync(Destino, archivo, "despues\n", overwrite: true);

            // Assert
            _ = resultado.IsAtomica.Should().BeFalse();
            _ = resultado.Mensaje.Should().Contain("no atómica");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("despues\n");
            string stat = Stat(archivo);
            _ = stat.Should().StartWith("640 ");
            _ = stat.Split(' ')[2].Should().Be(inodo);
            _ = Directory.GetFiles(_directorio).Should().ContainSingle();
        }

        // La carrera se fuerza sustituyendo 'dirname', que se ejecuta después de comprobar que el archivo no existe.
        [Fact]
        public async Task EscribirSinSobrescribir_ArchivoCreadoDuranteLaEscritura_NoSeMachaca()
        {
            // Arrange
            string archivo = Ruta("carrera.txt");
            HerramientasSsh herramientas = Herramientas();
            await SustituirComandoAsync(herramientas, "dirname", $"[ -e '{archivo}' ] || printf 'ajeno' > '{archivo}'\nexec /usr/bin/dirname \"$@\"");

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, archivo, "mio"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("ya existe");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("ajeno");
            _ = Directory.GetFiles(_directorio).Should().ContainSingle();
        }

        [Fact]
        public async Task EscribirSinSobrescribir_ArchivoNuevoConPermisosPorDefectoYEnlaceRotoNoSeSigue()
        {
            // Arrange
            string nuevo = Ruta("nuevo.txt");
            string enlaceRoto = Ruta("roto.txt");
            _ = File.CreateSymbolicLink(enlaceRoto, Ruta("no_existe.txt"));
            HerramientasSsh herramientas = Herramientas();
            string umask = Bash("umask");

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(Destino, nuevo, "nuevo\n");
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, enlaceRoto, "x"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = resultado.Mensaje.Should().Contain("Archivo escrito correctamente");
            _ = resultado.Bytes.Should().Be(6);
            _ = (await File.ReadAllTextAsync(nuevo)).Should().Be("nuevo\n");
            _ = Stat(nuevo).Split(' ')[0].Should().Be(Convert.ToString(0x1B6 & ~Convert.ToInt32(umask, 8), 8));
            _ = error.Message.Should().Contain("ya existe");
            _ = File.Exists(Ruta("no_existe.txt")).Should().BeFalse();
            _ = Directory.GetFileSystemEntries(_directorio).Should().HaveCount(2);
        }

        // sudo sin contraseña simulado con un espacio de nombres de usuario: 'unshare -r' ejecuta el comando como "root"
        // (uid 0 mapeado al usuario de las pruebas), que tiene CAP_DAC_OVERRIDE sobre los archivos del usuario y atraviesa
        // un directorio 0000 suyo, como root atraviesa uno ajeno con sudo real. Diferencias con sudo real: solo están
        // mapeados el uid y el gid primario (los grupos secundarios aparecen como 65534 y 'chown' a ellos falla, así que
        // se cae a la escritura en el sitio) y requiere espacios de nombres de usuario sin privilegios en el kernel.
        private async Task SimularSudoAsync(HerramientasSsh herramientas)
        {
            _ = Bash("unshare -r true 2>/dev/null && echo disponible").Should().Be("disponible",
                "la simulación de sudo necesita 'unshare -r' (espacios de nombres de usuario sin privilegios: kernel.unprivileged_userns_clone=1 y sin restricción de AppArmor)");
            await SustituirComandoAsync(herramientas, "sudo", "exec unshare -r \"$@\"");
        }

        private static string GrupoSecundario() => Bash("id -Gn | tr ' ' '\\n' | grep -vx \"$(id -gn)\" | head -n 1");

        // Caso del auditor: certificado 0600/0640 root:ssl-cert en un directorio que el usuario no puede atravesar,
        // editado a través de un enlace con sudo=true. Las comprobaciones sin sudo veían "no existe".
        [Theory]
        [InlineData("600", false)]
        [InlineData("640", false)]
        [InlineData("600", true)]
        [InlineData("640", true)]
        public async Task Sobrescribir_ConSudoEnDirectorioNoAtravesable_ConservaEnlaceModoYGrupo(string modo, bool isReplace)
        {
            // Arrange
            string grupo = GrupoSecundario();
            string privado = Ruta("priv");
            _ = Directory.CreateDirectory(privado);
            string clave = Path.Combine(privado, "clave.pem");
            string enlace = Path.Combine(privado, "actual.pem");
            await File.WriteAllTextAsync(clave, "secreto\n");
            _ = File.CreateSymbolicLink(enlace, "clave.pem");
            _ = Bash($"chmod {modo} '{clave}' && chgrp '{grupo}' '{clave}' && chmod 000 '{privado}'");
            HerramientasSsh herramientas = Herramientas();
            await SimularSudoAsync(herramientas);

            // Act
            ResultadoEscritura resultado = isReplace
                ? await herramientas.ReemplazarContenidoArchivoAsync(Destino, enlace, "secreto", "nuevo", sudo: true)
                : await herramientas.EscribirArchivoAsync(Destino, enlace, "nuevo\n", overwrite: true, sudo: true);
            _ = Bash($"chmod 700 '{privado}'");

            // Assert: con el grupo secundario sin mapear la escritura puede ser en el sitio; lo que importa es conservarlo todo.
            _ = grupo.Should().NotBeEmpty();
            _ = resultado.Reemplazos.Should().Be(isReplace ? 1 : null);
            _ = new FileInfo(enlace).LinkTarget.Should().Be("clave.pem");
            _ = (await File.ReadAllTextAsync(clave)).Should().Be("nuevo\n");
            _ = Stat(clave).Should().StartWith($"{modo} {grupo} ");
            _ = Directory.GetFileSystemEntries(privado).Should().HaveCount(2);
        }

        [Fact]
        public async Task EscribirSinSobrescribir_ConSudoEnDirectorioNoAtravesable_DetectaQueYaExiste()
        {
            // Arrange
            string privado = Ruta("priv");
            _ = Directory.CreateDirectory(privado);
            string clave = Path.Combine(privado, "clave.pem");
            await File.WriteAllTextAsync(clave, "secreto\n");
            _ = Bash($"chmod 600 '{clave}' && chmod 000 '{privado}'");
            HerramientasSsh herramientas = Herramientas();
            await SimularSudoAsync(herramientas);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, clave, "otro\n", sudo: true))
                .Should().ThrowAsync<NativeSshException>()).Which;
            _ = Bash($"chmod 700 '{privado}'");

            // Assert
            _ = error.Message.Should().Contain("ya existe");
            _ = (await File.ReadAllTextAsync(clave)).Should().Be("secreto\n");
            _ = Directory.GetFileSystemEntries(privado).Should().ContainSingle();
        }

        // $(...) quita los saltos de línea finales: un enlace a "victima\n" no debe acabar escribiendo en "victima".
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Sobrescribir_EnlaceADestinoQueTerminaEnSalto_NoTocaElArchivoSinSalto(bool isReplace)
        {
            // Arrange
            string victima = Ruta("victima");
            string conSalto = Ruta("victima\n");
            string enlace = Ruta("enlace");
            await File.WriteAllTextAsync(victima, "intacto\n");
            await File.WriteAllTextAsync(conSalto, "antes\n");
            _ = File.CreateSymbolicLink(enlace, "victima\n");
            HerramientasSsh herramientas = Herramientas();

            // Act
            _ = isReplace
                ? await herramientas.ReemplazarContenidoArchivoAsync(Destino, enlace, "antes", "nuevo")
                : await herramientas.EscribirArchivoAsync(Destino, enlace, "nuevo\n", overwrite: true);

            // Assert
            _ = (await File.ReadAllTextAsync(victima)).Should().Be("intacto\n");
            _ = (await File.ReadAllTextAsync(conSalto)).Should().Be("nuevo\n");
            _ = new FileInfo(enlace).LinkTarget.Should().Be("victima\n");
            _ = Directory.GetFileSystemEntries(_directorio).Should().HaveCount(3);
        }

        [Fact]
        public async Task ArchivoCuyoNombreTerminaEnSalto_SeCreaSobrescribeReemplazaYLeeSinPerderElSalto()
        {
            // Arrange
            string archivo = Ruta("salto_final\n");
            string enlace = Ruta("enlace_salto_final\n");
            HerramientasSsh herramientas = Herramientas();

            // Act
            _ = await herramientas.EscribirArchivoAsync(Destino, archivo, "uno\n");
            _ = await herramientas.EscribirArchivoAsync(Destino, archivo, "dos\n", overwrite: true);
            _ = await herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "dos", "tres");
            string leido = await herramientas.LeerArchivoAsync(Destino, archivo, -1);
            _ = File.CreateSymbolicLink(enlace, "salto_final\n");
            _ = await herramientas.EscribirArchivoAsync(Destino, enlace, "cuatro\n", overwrite: true);
            ResultadoMetadatos metadatos = await herramientas.ConsultarMetadatosAsync(Destino, enlace);

            // Assert
            _ = leido.Should().Be("[Líneas 1-1 de 1]\ntres\n");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("cuatro\n");
            _ = new FileInfo(enlace).LinkTarget.Should().Be("salto_final\n");
            _ = metadatos.DestinoEnlace.Should().Be("salto_final\n");
            _ = Directory.GetFileSystemEntries(_directorio).Should().BeEquivalentTo([archivo, enlace]);
        }

        // dirname de "carpeta\n/archivo" es "carpeta\n": sin preservar el salto, el temporal se buscaría en "carpeta".
        [Fact]
        public async Task Sobrescribir_ArchivoEnDirectorioQueTerminaEnSalto_UsaEseDirectorio()
        {
            // Arrange
            string carpeta = Ruta("carpeta\n");
            _ = Directory.CreateDirectory(carpeta);
            string archivo = Path.Combine(carpeta, "archivo");
            await File.WriteAllTextAsync(archivo, "antes\n");
            HerramientasSsh herramientas = Herramientas();

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(Destino, archivo, "despues\n", overwrite: true);

            // Assert
            _ = resultado.IsAtomica.Should().BeTrue();
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("despues\n");
            _ = Directory.GetFileSystemEntries(_directorio).Should().ContainSingle();
            _ = Directory.GetFileSystemEntries(carpeta).Should().ContainSingle();
        }

        [Theory]
        [InlineData("a\nb")]
        [InlineData("termina_en_salto\n")]
        [InlineData("termina_en_retorno\r")]
        [InlineData(" espacios ")]
        public async Task ConsultarMetadatos_DestinoDeEnlaceConSaltosOEspacios_SeDevuelveExacto(string destinoEnlace)
        {
            // Arrange
            string enlace = Ruta("enlace");
            _ = File.CreateSymbolicLink(enlace, destinoEnlace);
            HerramientasSsh herramientas = Herramientas();

            // Act
            ResultadoMetadatos metadatos = await herramientas.ConsultarMetadatosAsync(Destino, enlace);

            // Assert
            _ = metadatos.Existe.Should().BeTrue();
            _ = metadatos.DestinoEnlace.Should().Be(destinoEnlace);
        }

        private static string AclYAtributos(string ruta) =>
            Bash($"getfacl -c -- '{ruta}' 2>&1; getfattr --absolute-names -d -m - -- '{ruta}' 2>&1 | grep -v '^# file:'");

        // mv sustituiría el archivo por uno sin sus ACL ni atributos extendidos (y la máscara de la ACL heredada podría
        // ampliar permisos de grupo): con ACL o xattr se escribe en el sitio, avisando de que no es atómica.
        [Theory]
        [InlineData("acl", false)]
        [InlineData("acl", true)]
        [InlineData("xattr", false)]
        [InlineData("xattr", true)]
        public async Task Sobrescribir_ArchivoConAclOAtributosExtendidos_EscribeEnElSitioYLosConserva(string tipo, bool isReplace)
        {
            // Arrange
            string archivo = Ruta("con_atributos.txt");
            await File.WriteAllTextAsync(archivo, "antes\n");
            _ = Bash(tipo == "acl"
                ? $"chmod 640 '{archivo}' && setfacl -m u:nobody:r '{archivo}'"
                : $"setfattr -n user.marca -v valor '{archivo}'");
            string atributos = AclYAtributos(archivo);
            string stat = Stat(archivo);
            HerramientasSsh herramientas = Herramientas();

            // Act
            ResultadoEscritura resultado = isReplace
                ? await herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "antes", "despues")
                : await herramientas.EscribirArchivoAsync(Destino, archivo, "despues\n", overwrite: true);

            // Assert
            _ = atributos.Should().Contain(tipo == "acl" ? "user:nobody:r--" : "user.marca=\"valor\"");
            _ = resultado.IsAtomica.Should().BeFalse();
            _ = resultado.Mensaje.Should().Contain("no atómica");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("despues\n");
            _ = AclYAtributos(archivo).Should().Be(atributos);
            _ = Stat(archivo).Should().Be(stat);
            _ = Directory.GetFileSystemEntries(_directorio).Should().ContainSingle();
        }

        // El temporal hereda la ACL por defecto del directorio: renombrarlo pondría al archivo una ACL que no tenía.
        [Fact]
        public async Task Sobrescribir_EnDirectorioConAclPorDefecto_NoAnadeAclAlArchivo()
        {
            // Arrange
            string carpeta = Ruta("con_acl_defecto");
            _ = Directory.CreateDirectory(carpeta);
            string archivo = Path.Combine(carpeta, "sin_acl.txt");
            await File.WriteAllTextAsync(archivo, "antes\n");
            _ = Bash($"chmod 600 '{archivo}' && setfacl -d -m u:nobody:rw '{carpeta}'");
            string atributos = AclYAtributos(archivo);
            string stat = Stat(archivo);
            HerramientasSsh herramientas = Herramientas();

            // Act
            ResultadoEscritura resultado = await herramientas.EscribirArchivoAsync(Destino, archivo, "despues\n", overwrite: true);

            // Assert
            _ = atributos.Should().NotContain("nobody");
            _ = resultado.IsAtomica.Should().BeFalse();
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("despues\n");
            _ = AclYAtributos(archivo).Should().Be(atributos);
            _ = Stat(archivo).Should().Be(stat);
            _ = Directory.GetFileSystemEntries(carpeta).Should().ContainSingle();
        }

        // Las tres vías de detección de ACL/xattr, con un PATH que solo tiene las herramientas de cada vía.
        [Theory]
        [InlineData("getfattr", true, true)]
        [InlineData("getfacl", true, false)]
        [InlineData("ls", true, false)]
        public async Task DeteccionDeAclOAtributos_SegunLasHerramientasDisponibles(string via, bool isAclDetectada, bool isXattrDetectado)
        {
            // Arrange
            string bin = Ruta("bin_" + via);
            _ = Directory.CreateDirectory(bin);
            string[] herramientas = via switch
            {
                "getfattr" => ["bash", "grep", "ls", "getfattr", "getfacl"],
                "getfacl" => ["bash", "grep", "ls", "getfacl"],
                _ => ["bash", "grep", "ls"]
            };
            foreach (string herramienta in herramientas)
                _ = File.CreateSymbolicLink(Path.Combine(bin, herramienta), Bash($"command -v {herramienta}"));
            string script = Ruta("deteccion.sh");
            await File.WriteAllTextAsync(script, HerramientasSsh.DefinicionTieneAclOAtributos + "\nfor f in \"$@\"; do if tiene_acl_o_atributos \"$f\"; then echo si; else echo no; fi; done\n");
            string normal = Ruta("normal.txt");
            string conAcl = Ruta("acl.txt");
            string conXattr = Ruta("xattr.txt");
            await File.WriteAllTextAsync(normal, "x");
            await File.WriteAllTextAsync(conAcl, "x");
            await File.WriteAllTextAsync(conXattr, "x");
            _ = Bash($"setfacl -m u:nobody:r '{conAcl}' && setfattr -n user.marca -v 1 '{conXattr}'");

            // Act
            string[] resultado = Bash($"PATH='{bin}' '{bin}/bash' '{script}' '{normal}' '{conAcl}' '{conXattr}'").Split('\n');

            // Assert
            _ = resultado.Should().Equal("no", isAclDetectada ? "si" : "no", isXattrDetectado ? "si" : "no");
        }

        // Mutación superviviente de A4: una espera fija de 20 s en lugar del timeout de la herramienta. Un comando de
        // ≈22 s con un timeout de 40 s debe completarse, tanto por el shell PTY (escritura) como por un canal exec (búsqueda).
        [Fact]
        public async Task Herramientas_ComandoQueDuraMasDe20sSinSuperarSuTimeout_SeCompletan()
        {
            // Arrange
            string archivo = Ruta("lento.txt");
            await File.WriteAllTextAsync(archivo, "antes\n");
            string fifo = Ruta("fifo_lenta");
            _ = Bash($"mkfifo '{fifo}'");
            HerramientasSsh herramientas = Herramientas(timeout: TimeSpan.FromSeconds(40));
            // mktemp solo lo usa el script de escritura (base64 -d también decodifica cada comando enviado al shell).
            string mktemp = Bash("command -v mktemp");
            await SustituirComandoAsync(herramientas, "mktemp", $"sleep 22\nexec '{mktemp}' \"$@\"");
            Stopwatch cronometro = Stopwatch.StartNew();

            // Act: la búsqueda lee la FIFO, que no recibe datos hasta pasados 22 s (con tope por si nadie la abre).
            Task escritorFifo = Task.Run(() => Bash($"sleep 22; timeout 60 bash -c \"echo coincidencia > '{fifo}'\""));
            Task<ResultadoEscritura> escritura = herramientas.EscribirArchivoAsync(Destino, archivo, "despues\n", overwrite: true);
            Task<string> busqueda = herramientas.BuscarAsync(Destino, "coincidencia", fifo);
            await Task.WhenAll(escritura, busqueda, escritorFifo);
            TimeSpan duracion = cronometro.Elapsed;

            // Assert
            _ = duracion.Should().BeGreaterThan(TimeSpan.FromSeconds(21));
            _ = (await escritura).IsAtomica.Should().BeTrue();
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("despues\n");
            _ = (await busqueda).Should().Contain(":1:coincidencia");
        }
    }
}
