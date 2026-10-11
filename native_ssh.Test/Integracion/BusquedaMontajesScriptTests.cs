using System.Diagnostics;
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
        private string Buscar(string montajes, string ruta, bool isCruzarMontajes, string patron = "objetivo.txt", int profundidad = 6)
        {
            string id = Guid.NewGuid().ToString("N");
            string script = HerramientasSsh.ConstruirComandoBusquedaNombre(patron, ruta, "-type f ", profundidad, 100, isCruzarMontajes, id);
            ProcessStartInfo inicio = new("unshare", ["-rmpf", "bash", "-c", $"set -e\n{montajes}\nset +e\n(cd '{_raiz}/normal' && exec sleep 30) &\nsleep 0.2\n{script}\nrc=$?\nkill %1\nexit $rc"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using Process proceso = Process.Start(inicio)!;
            Task<string> errores = proceso.StandardError.ReadToEndAsync();
            string salida = proceso.StandardOutput.ReadToEnd();
            proceso.WaitForExit();
            _ = proceso.ExitCode.Should().Be(0, errores.Result);
            return HerramientasSsh.InterpretarBusquedaNombre(salida, id, patron, ruta, profundidad, 100, isCruzarMontajes);
        }

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

        // Con la ruta dentro del proc montado no se poda nada: lo pidió explícitamente.
        [FactUnshare]
        public void Script_RutaDentroDelProc_NoSePoda()
        {
            // Act
            string salida = Buscar(MontarProcYTmpfs, $"{_raiz}/proc con espacio/self", isCruzarMontajes: true, patron: "status", profundidad: 1);

            // Assert
            _ = salida.Should().Be("status");
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
    public sealed class FactUnshareAttribute : FactAttribute
    {
        private static readonly Lazy<string?> Motivo = new(() =>
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
