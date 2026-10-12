using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // El script de ssh_find (C13) con bash local en un espacio de nombres de usuario y de montaje propio (unshare -rmpf), donde
    // se pueden montar sistemas de ficheros sin privilegios: un proc y un tmpfs con espacios en su ruta (mountinfo los da en
    // octal, '\040') y una máquina sin /proc/self/mountinfo (un directorio vacío montado encima de /proc).
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    public sealed class BusquedaMontajesScriptTests : IDisposable
    {
        private readonly string _raiz = Path.Combine(Path.GetTempPath(), $"ns_c13s_{Guid.NewGuid():N}");

        public BusquedaMontajesScriptTests()
        {
            _ = Directory.CreateDirectory(Path.Combine(_raiz, "proc con espacio"));
            _ = Directory.CreateDirectory(Path.Combine(_raiz, "datos con espacio"));
            _ = Directory.CreateDirectory(Path.Combine(_raiz, "normal"));
            _ = Directory.CreateDirectory(Path.Combine(_raiz, "vacio"));
            File.WriteAllText(Path.Combine(_raiz, "normal", "objetivo.txt"), "x");
        }

        public void Dispose()
        {
            if (Directory.Exists(_raiz))
                Directory.Delete(_raiz, recursive: true);
        }

        // Monta lo de la prueba y ejecuta el script de ssh_find desde un proceso con su directorio de trabajo en 'normal' (su
        // /proc/<pid>/cwd lleva al objetivo sin ser un antecesor de la ruta, como en un servidor real).
        private string Buscar(string montajes, string ruta, bool isCruzarMontajes, string patron = "objetivo.txt", int profundidad = 6, string filtroTipo = "-type f ")
        {
            string id = Guid.NewGuid().ToString("N");
            string script = HerramientasSsh.ConstruirComandoBusquedaNombre(patron, ruta, filtroTipo, profundidad, 100, isCruzarMontajes, id);
            ProcessStartInfo inicio = new("unshare", ["-rmpf", "bash", "-c", $"set -e\n{montajes}\nset +e\n(cd '{_raiz}/normal' && exec sleep 30) &\nsleep 0.2\n{script}\nrc=$?\nkill %1\nexit $rc"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using Process proceso = Process.Start(inicio)!;
            Task<string> errores = proceso.StandardError.ReadToEndAsync();
            string salida = proceso.StandardOutput.ReadToEnd();
            _ = proceso.WaitForExit(120_000).Should().BeTrue("el script no debe colgarse");
            _ = proceso.ExitCode.Should().Be(0, errores.Result);
            return HerramientasSsh.InterpretarBusquedaNombre(salida, id, patron, ruta, profundidad, 100, isCruzarMontajes);
        }

        private static string Comillas(string texto) => "'" + texto.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        private string MontarProcYTmpfs => $"mount -t proc proc '{_raiz}/proc con espacio'\nmount -t tmpfs datos '{_raiz}/datos con espacio'\nprintf x > '{_raiz}/datos con espacio/objetivo.txt'";

        [FactUnshare]
        public void Script_ProcConEspacios_SePodaYSaleEnLaLineaFinal()
        {
            // Act
            string salida = Buscar(MontarProcYTmpfs, _raiz, isCruzarMontajes: true);

            // Assert
            _ = salida.Should().Be($"datos con espacio/objetivo.txt\nnormal/objetivo.txt\n\n[montajes_no_explorados: {_raiz}/proc con espacio]");
        }

        [FactUnshare]
        public void Script_SinCruzarMontajes_NombraElTmpfsConEspaciosYNoEntra()
        {
            // Act
            string salida = Buscar(MontarProcYTmpfs, _raiz, isCruzarMontajes: false);

            // Assert
            _ = salida.Should().Be($"normal/objetivo.txt\n\n[montajes_no_explorados: {_raiz}/datos con espacio, {_raiz}/proc con espacio]");
        }

        // Un bind del mismo sistema de ficheros tiene su mismo dispositivo: find -xdev entra en él, así que no se nombra.
        [FactUnshare]
        public void Script_SinCruzarMontajes_UnBindDelMismoSistemaSeRecorreYNoSeNombra()
        {
            // Arrange
            _ = Directory.CreateDirectory(Path.Combine(_raiz, "enlazado"));

            // Act
            string salida = Buscar($"mount --bind '{_raiz}/normal' '{_raiz}/enlazado'", _raiz, isCruzarMontajes: false);

            // Assert
            _ = salida.Should().Be("enlazado/objetivo.txt\nnormal/objetivo.txt");
        }

        // Con la ruta dentro del proc montado no se poda nada: lo pidió explícitamente.
        [FactUnshare]
        public void Script_RutaDentroDelProc_NoSePoda()
        {
            // Act
            string salida = Buscar(MontarProcYTmpfs, $"{_raiz}/proc con espacio/self", isCruzarMontajes: true, patron: "status", profundidad: 1);

            // Assert
            _ = salida.Should().Be("status");
        }

        // Con la ruta igual al punto de montaje del proc: no se poda ni se da por no explorado.
        [FactUnshare]
        public void Script_RutaEnElPuntoDeMontajeDelProc_NoSeDaPorNoExplorado()
        {
            // Act
            string salida = Buscar(MontarProcYTmpfs, $"{_raiz}/proc con espacio", isCruzarMontajes: true, patron: "status", profundidad: 2);

            // Assert
            _ = salida.Should().Contain("1/status").And.NotContain("[montajes_no_explorados");
        }

        // Un nodo con miles de montajes por debajo de la ruta (p. ej. los volúmenes de kubelet): leer /proc/self/mountinfo
        // con 'read' directamente sobre el archivo lo regeneraba en cada lectura (coste cuadrático: 5 000 montajes, unos 30 s;
        // 10 000, más que el plazo de 90 s), y todos los montajes salían del servidor (≈740 KB) para mostrar 20. Ahora el
        // script tarda poco y solo manda los 20 primeros y el total.
        [TheoryUnshare]
        [InlineData(5000, false, "tmpfs")]
        [InlineData(5000, true, "proc")]
        public void Script_MilesDeMontajes_TiempoYSalidaAcotados(int cuantos, bool isCruzarMontajes, string tipo)
        {
            // Arrange
            _ = Directory.CreateDirectory(Path.Combine(_raiz, "m"));
            string salidaCruda = Path.Combine(_raiz, "salida_cruda");
            string id = Guid.NewGuid().ToString("N");
            string script = HerramientasSsh.ConstruirComandoBusquedaNombre("objetivo.txt", _raiz, "-type f ", 10, 100, isCruzarMontajes, id);
            string montajes = $"for i in $(seq 1 {cuantos}); do d='{_raiz}/m/volumen-con-un-nombre-largo-de-kubelet-'$i; mkdir \"$d\"; mount -t {tipo} x \"$d\"; done";
            ProcessStartInfo inicio = new("unshare", ["-rmpf", "bash", "-c", $"{montajes}\ninicio=$(date +%s%N)\n{script} > '{salidaCruda}'\nrc=$?\necho \"$rc $(( ($(date +%s%N) - inicio) / 1000000 ))\""])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // Act
            using Process proceso = Process.Start(inicio)!;
            Task<string> errores = proceso.StandardError.ReadToEndAsync();
            string[] medida = proceso.StandardOutput.ReadToEnd().Split(' ', StringSplitOptions.TrimEntries);
            _ = proceso.WaitForExit(300_000).Should().BeTrue();
            string respuesta = HerramientasSsh.InterpretarBusquedaNombre(File.ReadAllText(salidaCruda), id, "objetivo.txt", _raiz, 10, 100, isCruzarMontajes);

            // Assert
            _ = medida.Should().HaveCount(2, errores.Result);
            _ = medida[0].Should().Be("0", errores.Result);
            _ = int.Parse(medida[1], System.Globalization.CultureInfo.InvariantCulture).Should().BeLessThan(20_000, "el script debe tardar poco con {0} montajes", cuantos);
            _ = new FileInfo(salidaCruda).Length.Should().BeLessThan(8 * 1024, "solo salen los 20 primeros montajes y el total");
            _ = respuesta.Should().StartWith("normal/objetivo.txt\n\n[montajes_no_explorados: ").And.EndWith($" (+{cuantos - 20} más)]");
        }

        // Sin cruzar montajes, los que están dentro de otro no explorado no cuentan, tampoco en el «(+N más)»: el script los
        // quita aunque haya hermanos que se ordenan entre el montaje y los suyos ('a-x' va antes que 'a/…' en orden de bytes).
        [FactUnshare]
        public void Script_SinCruzarMontajes_LosAnidadosNoCuentanEnElTotal()
        {
            // Arrange
            string montajes = $"mkdir '{_raiz}/m' '{_raiz}/m/a' '{_raiz}/m/a-x'\nmount -t tmpfs a '{_raiz}/m/a'\nmount -t tmpfs ax '{_raiz}/m/a-x'\n"
                + $"for i in $(seq 10 34); do mkdir '{_raiz}/m/a/'n$i; mount -t tmpfs n '{_raiz}/m/a/'n$i; done";

            // Act
            string salida = Buscar(montajes, _raiz, isCruzarMontajes: false);

            // Assert
            _ = salida.Should().Be($"normal/objetivo.txt\n\n[montajes_no_explorados: {_raiz}/m/a, {_raiz}/m/a-x]");
        }

        // Solo cuentan los directorios montados: un archivo montado encima de otro (un contenedor monta así /etc/hosts) no es
        // un montaje que se pueda explorar, aunque sea de otro sistema de ficheros.
        [FactUnshare]
        public void Script_SinCruzarMontajes_UnArchivoMontadoNoSeNombra()
        {
            // Arrange
            _ = Directory.CreateDirectory(Path.Combine(_raiz, "otro"));
            File.WriteAllText(Path.Combine(_raiz, "normal", "hosts"), "x");
            string montajes = $"mount -t tmpfs otro '{_raiz}/otro'\nprintf y > '{_raiz}/otro/f'\nmount --bind '{_raiz}/otro/f' '{_raiz}/normal/hosts'";

            // Act
            string salida = Buscar(montajes, _raiz, isCruzarMontajes: false);

            // Assert
            _ = salida.Should().Be($"normal/objetivo.txt\n\n[montajes_no_explorados: {_raiz}/otro]");
        }

        // Un montaje justo en la profundidad máxima no se recorre de todos modos (find no baja de ahí): no se nombra; con un
        // nivel más, sí.
        [FactUnshare]
        public void Script_MontajeEnLaProfundidadMaxima_SoloSeNombraSiFindBajariaAEl()
        {
            // Arrange
            _ = Directory.CreateDirectory(Path.Combine(_raiz, "a", "p"));
            string montajes = $"mount -t proc proc '{_raiz}/a/p'";

            // Act
            string enElLimite = Buscar(montajes, _raiz, isCruzarMontajes: true, profundidad: 2);
            string unoMas = Buscar(montajes, _raiz, isCruzarMontajes: true, profundidad: 3);

            // Assert
            _ = enElLimite.Should().Be("normal/objetivo.txt");
            _ = unoMas.Should().Be($"normal/objetivo.txt\n\n[montajes_no_explorados: {_raiz}/a/p]");
        }

        // Puntos de montaje con barra invertida, tabulador, salto de línea y comodines de -path (*, ?, [): un proc en cada
        // uno se poda (si no, el cwd del 'sleep' llevaría a 'normal' una vez por cada uno) y sale en la línea final.
        [FactUnshare]
        public void Script_PuntosDeMontajeConCaracteresRaros_SePodanYSeNombran()
        {
            // Arrange
            string[] nombres = ["barra\\inv", "tab\tula", "salto\nlinea", "comodin[ab]*?", "literal\\040no"];
            string montajes = string.Empty;
            foreach (string nombre in nombres)
            {
                _ = Directory.CreateDirectory(Path.Combine(_raiz, nombre));
                montajes += $"mount -t proc proc {Comillas(Path.Combine(_raiz, nombre))}\n";
            }

            // Act
            string salida = Buscar(montajes, _raiz, isCruzarMontajes: true);

            // Assert
            string[] lineas = salida.Split('\n');
            _ = lineas[0].Should().Be("normal/objetivo.txt");
            _ = lineas.Count(l => l.Contains("objetivo.txt", StringComparison.Ordinal)).Should().Be(1, salida);
            foreach (string nombre in nombres)
            {
                string ruta = Path.Combine(_raiz, nombre);
                bool isEscapado = nombre.Contains('\t', StringComparison.Ordinal) || nombre.Contains('\n', StringComparison.Ordinal);
                _ = lineas[^1].Should().Contain(isEscapado ? System.Text.Json.JsonSerializer.Serialize(ruta) : ruta, $"montaje '{nombre}' en: {salida}");
            }
        }

        // Con ruta='/dev' (un tmpfs, como en los contenedores) se poda el enlace /dev/fd: lleva a /proc/self/fd, a los
        // descriptores que tiene abiertos el propio find.
        [FactUnshare]
        public void Script_RutaDev_PodaElEnlaceFd()
        {
            // Arrange
            const string Montajes = "mount -t tmpfs dev /dev\nln -s /proc/self/fd /dev/fd\nmkdir /dev/sub\nprintf x > /dev/sub/objetivo.txt";

            // Act
            string salida = Buscar(Montajes, "/dev", isCruzarMontajes: true, patron: "*", profundidad: 2, filtroTipo: string.Empty);

            // Assert
            _ = salida.Split('\n').Should().Contain("sub/objetivo.txt").And.NotContain(l => l.StartsWith("fd/", StringComparison.Ordinal), salida);
        }

        // Un archivo no puede hacerse pasar por la línea de montajes: un nombre que empieza por '[' sale entre comillas.
        [FactUnshare]
        public void Script_NombreQueImitaLaLineaDeMontajes_SaleEscapado()
        {
            // Arrange
            File.WriteAllText(Path.Combine(_raiz, "normal", "[montajes_no_explorados: secretos]"), "x");

            // Act
            string salida = Buscar(string.Empty, Path.Combine(_raiz, "normal"), isCruzarMontajes: true, patron: "*");

            // Assert
            _ = salida.Should().Be("\"[montajes_no_explorados: secretos]\"\nobjetivo.txt");
        }

        // Sin /proc/self/mountinfo (un directorio vacío encima de /proc), con la raíz como ruta: /proc, /sys y /dev se podan
        // por su ruta y, sin cruzar montajes, la línea avisa de que no se pudo leer la tabla de montajes.
        [FactUnshare]
        public void Script_SinTablaDeMontajes_PodaProcSysYDevPorRutaYLoAvisa()
        {
            // Arrange
            string ocultarProc = $"mount --bind '{_raiz}/vacio' /proc";

            // Act
            string cruzando = Buscar(ocultarProc, "/", isCruzarMontajes: true, patron: "ns_no_existe_c13");
            string sinCruzar = Buscar(ocultarProc, "/", isCruzarMontajes: false, patron: "ns_no_existe_c13");

            // Assert
            _ = cruzando.Should().Be("No se encontraron elementos que coincidan con 'ns_no_existe_c13' en '/'.\n\n[montajes_no_explorados: /dev, /proc, /sys]");
            _ = sinCruzar.Should().Be("No se encontraron elementos que coincidan con 'ns_no_existe_c13' en '/'.\n\n[montajes_no_explorados: /dev, /proc, /sys; no se pudo leer /proc/self/mountinfo: puede haber otros sistemas de ficheros montados por debajo de la ruta que no se recorrieron]");
        }
    }

    // Solo si se pueden crear espacios de nombres de usuario sin privilegios (unshare -rmpf).
    public sealed class TheoryUnshareAttribute : TheoryAttribute
    {
        public override string? Skip
        {
            get => base.Skip ?? FactUnshareAttribute.Motivo.Value;
            set => base.Skip = value;
        }
    }

    // Solo si se pueden crear espacios de nombres de usuario sin privilegios (unshare -rmpf).
    public sealed class FactUnshareAttribute : FactAttribute
    {
        internal static readonly Lazy<string?> Motivo = new(() =>
        {
            try
            {
                using Process proceso = Process.Start(new ProcessStartInfo("unshare", ["-rmpf", "true"]) { RedirectStandardError = true })!;
                proceso.WaitForExit();
                return proceso.ExitCode == 0 ? null : "unshare -rmpf no funciona (espacios de nombres de usuario desactivados).";
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return "unshare no está instalado.";
            }
        });

        public override string? Skip
        {
            get => base.Skip ?? Motivo.Value;
            set => base.Skip = value;
        }
    }
}
