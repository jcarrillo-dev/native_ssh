using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Sesion.Falsos;

namespace NativeSsh.Test.Comun
{
    // La resolución en un proceso hijo de Linux (ResolutorEnProceso; solo se compila para Linux): el hijo que no termina en su
    // plazo se mata y la siguiente resolución lanza otro; el que no muere tras el SIGKILL se dice en el rechazo y en
    // ssh_status, y con MaxSinTerminar de ellos no se lanzan más. Con hijos de verdad ('sleep' en lugar del binario para los
    // que se cuelgan) y, para el que no se deja matar, un 'matar' que no hace nada.
    public sealed class ResolutorEnProcesoTests
    {
        private static Process Dormir()
        {
            ProcessStartInfo inicio = new("sleep", "30") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            return Process.Start(inicio)!;
        }

        // Por pid (el resolutor ya pudo liberar el objeto Process): SIGKILL y espera a que desaparezca de /proc.
        private static void MatarDeVerdad(IEnumerable<int> pids)
        {
            foreach (int pid in pids)
            {
                try
                {
                    using Process proceso = Process.GetProcessById(pid);
                    proceso.Kill();
                }
                catch (ArgumentException)
                {
                    // Ya no existe.
                }

                for (int i = 0; i < 100 && IsVivo(pid); i++)
                    Thread.Sleep(50);
            }
        }

        // Existe y no es un zombi (un hijo propio que .NET aún no ha recogido).
        private static bool IsVivo(int pid)
        {
            try
            {
                return !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z ", StringComparison.Ordinal);
            }
            catch (IOException)
            {
                return false;
            }
        }

        // Los que siguen sin terminar, cuando queden 'esperados' (.NET recoge al hijo muerto en segundo plano: con carga,
        // un momento después de que desaparezca de /proc), o los que haya a los 10 s.
        private static async Task<List<string>> SinTerminarCuandoQuedenAsync(ResolutorEnProceso resolutor, int esperados)
        {
            List<string> textos = [];
            for (int i = 0; i < 200; i++)
            {
                textos.Clear();
                resolutor.AnadirSinTerminar(textos);
                if (textos.Count == esperados)
                    break;
                await Task.Delay(50);
            }

            return textos;
        }

        [Fact]
        public async Task FamiliaColgada_DnsRecuperado_LaSiguienteVuelveAResolver()
        {
            // Arrange: la primera resolución se cuelga (un hijo que no contesta, como getaddrinfo con el TCP/53 colgado); las
            // siguientes, el binario de verdad, que resuelve localhost.
            int lanzados = 0;
            int colgado = 0;
            Process? Lanzar(string nombre, string familia)
            {
                if (Interlocked.Increment(ref lanzados) > 1)
                    return ResolutorEnProceso.LanzarPropio("localhost", familia);

                Process hijo = Dormir();
                colgado = hijo.Id;
                return hijo;
            }

            ResolutorEnProceso resolutor = new(Lanzar, ResolutorEnProceso.Matar, ResolutorEnProceso.RecogerAsync);
            using CancellationTokenSource plazo1 = new(TimeSpan.FromMilliseconds(300));
            ResultadoResolucion primera = await resolutor.ResolverAsync("prod.ejemplo", AddressFamily.InterNetwork, plazo1.Token).WaitAsync(TimeSpan.FromSeconds(10));

            // Act: el DNS ya responde
            using CancellationTokenSource plazo2 = new(TimeSpan.FromSeconds(10));
            ResultadoResolucion segunda = await resolutor.ResolverAsync("prod.ejemplo", AddressFamily.InterNetwork, plazo2.Token);

            // Assert
            _ = primera.Should().Be(default(ResultadoResolucion), "no terminó en su plazo y se mató y se recogió: un fallo, sin incidencia");
            _ = IsVivo(colgado).Should().BeFalse("el hijo colgado se mata al vencer el plazo y se recoge");
            _ = segunda.Direcciones.Should().Equal(IPAddress.Loopback);
            _ = lanzados.Should().Be(2);
        }

        [Fact]
        public async Task HijoQueNoSeDejaMatar_LoDiceYCuentaParaElTope()
        {
            // Arrange
            List<int> hijos = [];
            Process? Lanzar(string nombre, string familia)
            {
                Process hijo = Dormir();
                lock (hijos)
                    hijos.Add(hijo.Id);
                return hijo;
            }

            ResolutorEnProceso resolutor = new(Lanzar, _ => { }, (_, _) => Task.FromResult(false));
            try
            {
                // Act
                ResultadoResolucion[] sinTerminar = new ResultadoResolucion[ResolutorEnProceso.MaxSinTerminar];
                for (int i = 0; i < sinTerminar.Length; i++)
                {
                    using CancellationTokenSource plazo = new(TimeSpan.FromMilliseconds(100));
                    sinTerminar[i] = await resolutor.ResolverAsync($"n{i}.ejemplo", AddressFamily.InterNetworkV6, plazo.Token);
                }

                using CancellationTokenSource otroPlazo = new(TimeSpan.FromMilliseconds(100));
                ResultadoResolucion conTope = await resolutor.ResolverAsync("otro.ejemplo", AddressFamily.InterNetwork, otroPlazo.Token);
                List<string> enEstado = [];
                resolutor.AnadirSinTerminar(enEstado);
                MatarDeVerdad(hijos[..1]);
                List<string> trasMorirUno = await SinTerminarCuandoQuedenAsync(resolutor, ResolutorEnProceso.MaxSinTerminar - 1);

                // Assert
                for (int i = 0; i < sinTerminar.Length; i++)
                    _ = sinTerminar[i].Incidencia.Should().Be($"no se pudo terminar el proceso de resolución {hijos[i]} (n{i}.ejemplo); sigue vivo: native_ssh le mandó SIGKILL al vencer el plazo de la resolución y no terminó en 1 s (p. ej. bloqueado en el núcleo, en estado D)");
                _ = conTope.Direcciones.Should().BeNull();
                _ = conTope.Incidencia.Should().StartWith($"hay {ResolutorEnProceso.MaxSinTerminar} procesos de resolución que no se pudieron terminar con SIGKILL y siguen vivos ({hijos[0]} (n0.ejemplo), ").And.Contain("el máximo: no se lanzan más resoluciones hasta que alguno termine");
                _ = hijos.Should().HaveCount(ResolutorEnProceso.MaxSinTerminar, "con el tope no se lanza otro");
                _ = enEstado.Should().HaveCount(ResolutorEnProceso.MaxSinTerminar).And.Contain(t => t.StartsWith($"proceso {hijos[1]} (n1.ejemplo, AAAA): no terminó tras SIGKILL; vivo desde hace ", StringComparison.Ordinal));
                _ = trasMorirUno.Should().HaveCount(ResolutorEnProceso.MaxSinTerminar - 1, "el que ya terminó se quita (sin bloquear)");
            }
            finally
            {
                MatarDeVerdad(hijos);
            }
        }

        // Por la herramienta (con la sesión falsa): el rechazo de una escritura dice que el proceso de resolución del destino
        // no se pudo terminar, y ssh_status lo lista mientras siga vivo.
        [Fact]
        public async Task HijoQueNoSeDejaMatar_ElRechazoYSshStatusLoDicen()
        {
            // Arrange
            List<int> hijos = [];
            Process? Lanzar(string nombre, string familia)
            {
                Process hijo = Dormir();
                lock (hijos)
                    hijos.Add(hijo.Id);
                return hijo;
            }

            ResolutorEnProceso resolutor = new(Lanzar, _ => { }, (_, _) => Task.FromResult(false));
            List<string> SinTerminar()
            {
                List<string> textos = [];
                resolutor.AnadirSinTerminar(textos);
                return textos;
            }

            DestinosSoloLectura soloLectura = DestinosSoloLectura.ConResolutores("10.0.0.5", (nombre, plazo) => resolutor.ResolverAsync(nombre, AddressFamily.Unspecified, plazo), (nombre, plazo) => ResolucionPorFamilia.ResolverAsync(nombre, resolutor.ResolverAsync, plazo), SinTerminar);
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana", SoloLectura = soloLectura };
            FabricaFalsa fabrica = new(_ => new RespuestaFalsa("ok\n"));
            await using GestorSesiones gestor = new(fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);
            try
            {
                // Act
                NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EjecutarAsync("prod", "echo hola")).Should().ThrowAsync<NativeSshException>()).Which;
                ResultadoEstado estado = herramientas.Estado();
                MatarDeVerdad(hijos);
                _ = await SinTerminarCuandoQuedenAsync(resolutor, 0);
                ResultadoEstado despues = herramientas.Estado();

                // Assert
                string pid = hijos[0].ToString(CultureInfo.InvariantCulture);
                _ = error.Message.Should().StartWith($"Rechazado: ana@prod:22: no se pudo comprobar si es de solo lectura (no se resolvió prod: no se pudo terminar el proceso de resolución {pid} (prod); sigue vivo")
                    .And.Contain("ssh_status muestra los procesos de resolución que siguen vivos");
                _ = estado.ResolucionesSinTerminar.Should().ContainSingle().Which.Should().StartWith($"proceso {pid} (prod, A y AAAA): no terminó tras SIGKILL");
                _ = despues.ResolucionesSinTerminar.Should().BeNull();
                _ = fabrica.Conexiones.Should().Be(0);
            }
            finally
            {
                MatarDeVerdad(hijos);
            }
        }

        // Un hijo que escribe mucho por su salida de errores (más que lo que cabe en la tubería) no se queda bloqueado: se lee
        // y se descarta, y su resultado vale.
        [Fact]
        public async Task HijoQueEscribeMuchoPorErrores_NoSeBloquea()
        {
            // Arrange
            static Process? Lanzar(string nombre, string familia)
            {
                ProcessStartInfo inicio = new("sh") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                inicio.ArgumentList.Add("-c");
                inicio.ArgumentList.Add("head -c 1000000 /dev/zero >&2; printf 'ok\\n10.0.0.5\\n'");
                return Process.Start(inicio);
            }

            ResolutorEnProceso resolutor = new(Lanzar, ResolutorEnProceso.Matar, ResolutorEnProceso.RecogerAsync);
            using CancellationTokenSource plazo = new(TimeSpan.FromSeconds(3));

            // Act
            ResultadoResolucion resultado = await resolutor.ResolverAsync("prod.ejemplo", AddressFamily.InterNetwork, plazo.Token);

            // Assert
            _ = resultado.Direcciones.Should().Equal(IPAddress.Parse("10.0.0.5"));
        }

        [Theory]
        [InlineData("ok\n127.0.0.1\n::1\n", 0, new[] { "127.0.0.1", "::1" })]
        [InlineData("nodata\n", 0, new string[0])]
        public void Interpretar_SalidaDelHijo(string salida, int codigo, string[] esperadas) =>
            _ = ResolutorEnProceso.Interpretar(salida, codigo).Should().Equal(Array.ConvertAll(esperadas, IPAddress.Parse));

        [Theory]
        [InlineData("fallo -2\n", 0)]
        [InlineData("ok\n", 0)]
        [InlineData("ok\nno-es-ip\n", 0)]
        [InlineData("ok\n127.0.0.1\n", 1)]
        [InlineData("", 0)]
        [InlineData("cualquier cosa", 0)]
        [InlineData("ok\n10.0.0\n", 0)]
        [InlineData("ok\n010.0.0.1\n", 0)]
        [InlineData("ok\n0:0::1\n", 0)]
        [InlineData("ok\nfe80::1%2\n", 0)]
        [InlineData("ok\n 10.0.0.1\n", 0)]
        public void Interpretar_LoDemasEsFallo(string salida, int codigo) =>
            _ = ResolutorEnProceso.Interpretar(salida, codigo).Should().BeNull();

        // native_ssh como pid 1 de su espacio de pids (un contenedor sin init: 'podman run -i imagen NativeSsh'): el hijo
        // recibe '--resolver-interno 1 ...' y su padre es el 1; tiene que resolver igual. Aquí un sh es el pid 1 de un espacio
        // de pids nuevo y lanza el modo interno con su pid, como haría native_ssh (necesita unshare y espacios de nombres de
        // usuario).
        [Fact]
        public async Task ModoInterno_ConNativeSshComoPid1_Resuelve()
        {
            // Arrange
            ProcessStartInfo inicio = new("unshare") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argumento in new[] { "-Urpf", "--mount-proc", "sh", "-c", "\"$0\" --resolver-interno $$ 4 localhost; echo \"rc=$?\"", ResolutorEnProceso.ProgramaPropio! })
                inicio.ArgumentList.Add(argumento);

            // Act
            using Process proceso = Process.Start(inicio)!;
            string salida = await proceso.StandardOutput.ReadToEndAsync();
            string errores = await proceso.StandardError.ReadToEndAsync();
            await proceso.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            // Assert
            _ = salida.Should().Be("ok\n127.0.0.1\nrc=0\n", errores);
        }

        // El binario propio: /proc/self/exe si existe (el mismo aunque se sustituya el fichero); sin /proc (montado aparte
        // o tapado), la ruta del proceso; sin ninguna de las dos, null (entonces se resuelve sin proceso hijo). Desde dotnet
        // (las pruebas), el NativeSsh compilado junto a ellas.
        [Theory]
        [InlineData("/opt/ns/NativeSsh", true, "/proc/self/exe")]
        [InlineData("/opt/ns/NativeSsh", false, "/opt/ns/NativeSsh")]
        [InlineData(null, false, null)]
        [InlineData("/usr/bin/dotnet", true, "PRUEBAS")]
        public void ProgramaPropio_SinProcUsaLaRutaDelProceso(string? rutaProceso, bool hasProcSelfExe, string? esperado) =>
            _ = ResolutorEnProceso.ElegirPrograma(rutaProceso, hasProcSelfExe).Should().Be(esperado == "PRUEBAS" ? Path.Combine(AppContext.BaseDirectory, "NativeSsh") : esperado);

        // El modo interno a mano: sin el pid de su padre de verdad, con otra familia o con un nombre no válido sale con 2 sin
        // resolver nada; con todo bien, resuelve.
        [Theory]
        [InlineData("1", "4", "localhost", 2)]
        [InlineData("padre", "5", "localhost", 2)]
        [InlineData("padre", "4", "-x", 2)]
        [InlineData("padre", "4", "a;b", 2)]
        [InlineData("0padre", "4", "localhost", 2)]
        [InlineData("padre", "4", "localhost", 0)]
        public async Task ModoInterno_ValidaSusArgumentos(string padre, string familia, string nombre, int codigoEsperado)
        {
            // Arrange
            ProcessStartInfo inicio = new(ResolutorEnProceso.ProgramaPropio!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            string pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            foreach (string argumento in new[] { ResolutorEnProceso.ModoInterno, padre.Replace("padre", pid, StringComparison.Ordinal), familia, nombre })
                inicio.ArgumentList.Add(argumento);

            // Act
            using Process proceso = Process.Start(inicio)!;
            string salida = await proceso.StandardOutput.ReadToEndAsync();
            string errores = await proceso.StandardError.ReadToEndAsync();
            await proceso.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            // Assert
            _ = proceso.ExitCode.Should().Be(codigoEsperado, errores);
            _ = codigoEsperado == 0 ? salida.Should().Be("ok\n127.0.0.1\n") : salida.Should().BeEmpty();
        }
    }
}
