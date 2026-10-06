using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Z06 de la auditoría del E-septies (B1, preexistente): Ctrl+C (ssh_cancel o el plazo de ssh_exec) a un comando que
    // abrió un shell interactivo anidado (sudo -i, su -, ssh a otra máquina, bash -i). El anidado no muere, la línea de
    // recuperación se ejecutaba dentro de él y su marcador daba la cancelación por buena: los comandos siguientes corrían
    // dentro (como root o en otra máquina) y volvían Completado sin aviso. Ahora la línea comprueba $$ y $PPID contra los
    // del shell de la sesión y, si no coinciden, se cierra el canal y se dice.
    // También lo que no debe detectarse como anidado: subshells, bash -c, tuberías, funciones, source, exec (mismo pid),
    // cancelaciones normales y ssh_reset_shell.
    [Collection(ColeccionSshd.Nombre)]
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El gestor se libera en IAsyncLifetime.DisposeAsync.")]
    public sealed class ShellAnidadoIntegracionTests : IAsyncLifetime
    {
        // Nombres únicos para poder matar los procesos de la prueba sin tocar otros de la máquina.
        private const string NombreAnidado = "ns_anidado_eo2";
        private const string Proceso = "sleep 119.41";
        private const string Cancelado = "Cancelado: se envió Ctrl+C al proceso.";

        private readonly SshdLocal _sshd;
        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;

        public ShellAnidadoIntegracionTests(SshdLocal sshd)
        {
            _sshd = sshd;
            OpcionesNativeSsh opciones = sshd.Opciones(maxConexiones: 1000);
            _gestor = new GestorSesiones(sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            _herramientas = new HerramientasSsh(_gestor, opciones);
        }

        private string Destino => _sshd.Destino.Clave;

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            await _gestor.DisposeAsync();
            _ = BashLocal.Ejecutar($"pkill -f '^({NombreAnidado}|{Proceso})' || true");
        }

        private async Task EsperarSalidaAsync(string id, string texto)
        {
            Stopwatch reloj = Stopwatch.StartNew();
            ResultadoComando actual;
            while (!(actual = await _herramientas.LeerSalidaAsync(id)).Salida.Contains(texto, StringComparison.Ordinal))
            {
                if (reloj.Elapsed > TimeSpan.FromSeconds(15))
                    throw new TimeoutException($"La salida del comando {id} no llegó a contener '{texto}': {actual.Estado} '{actual.Salida}' {actual.Aviso}");
                await Task.Delay(100);
            }
        }

        private static int ProcesosAnidados() => int.Parse(BashLocal.Ejecutar($"pgrep -fc '^{NombreAnidado}' || true").Trim(), System.Globalization.CultureInfo.InvariantCulture);

        // isRcEnganoso: el rc del shell anidado redefine '[' y 'test' como funciones que siempre dan verdadero (la comprobación
        // no puede depender de ellos).
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task CtrlCAUnShellInteractivoAnidado_CierraElCanalYElSiguienteVaAUnShellNuevoDeLaSesion(bool isPlazo, bool isRcEnganoso)
        {
            // Arrange
            string rc = Path.Combine(_sshd.DirectorioEstado, $"rc_{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(rc, "[ () { return 0; }\ntest () { return 0; }\n");
            string opciones = isRcEnganoso ? $"--noprofile --rcfile {rc}" : "--norc --noprofile";
            ResultadoComando enLaSesion = await _herramientas.EjecutarAsync(Destino, "export NS_EO2=si; echo PADRE=$(ps -o comm= -p $PPID)", esperarSegundos: 20);
            ResultadoComando comando = await _herramientas.EjecutarAsync(Destino, $"env -i PS1='anidado$ ' TERM=dumb bash -c 'exec -a {NombreAnidado} bash {opciones} -i'", timeoutSegundos: isPlazo ? 5 : 300, esperarSegundos: 0);
            await EsperasPrueba.EsperarHastaAsync(() => ProcesosAnidados() == 1, TimeSpan.FromSeconds(10));
            await Task.Delay(500);

            // Act
            ResultadoComando interrumpido = isPlazo
                ? await _herramientas.LeerSalidaAsync(comando.Id, esperarSegundos: 30)
                : await _herramientas.CancelarAsync(comando.Id);
            ResultadoComando siguiente = await _herramientas.EjecutarAsync(Destino, "echo PADRE=$(ps -o comm= -p $PPID) Q=$NS_EO2", esperarSegundos: 20);
            await EsperasPrueba.EsperarHastaAsync(() => ProcesosAnidados() == 0, TimeSpan.FromSeconds(10));
            File.Delete(rc);

            // Assert: el siguiente se ejecuta en un shell nuevo de la sesión (su padre es el sshd, no el bash anidado), y el
            // anidado recibió SIGHUP al cerrarse el canal.
            _ = interrumpido.Estado.Should().Be(isPlazo ? EstadoPeticionEnum.TiempoAgotado : EstadoPeticionEnum.Cancelado);
            _ = interrumpido.Aviso.Should().Contain("shell interactivo anidado").And.Contain("se cerró el canal del shell").And.Contain("shell nuevo");
            _ = siguiente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguiente.Salida.Should().Be($"{enLaSesion.Salida.TrimEnd('\n')} Q=\n");
            _ = siguiente.Aviso.Should().Contain("recreado");
        }

        // El uso normal de un shell anidado (abrirlo, usarlo con ssh_input y salir con exit) no se toma por uno que quedó
        // vivo: la cola del comando la ejecuta el shell de la sesión cuando el anidado termina.
        [Fact]
        public async Task ShellAnidadoUsadoConSshInputYCerradoConExit_TerminaCompletadoSinAvisoYConservaElShell()
        {
            // Arrange
            ResultadoComando inicio = await _herramientas.EjecutarAsync(Destino, "export NS_EO2=si; echo $$", esperarSegundos: 20);
            string pid = inicio.Salida.Trim();
            ResultadoComando comando = await _herramientas.EjecutarAsync(Destino, $"env -i PS1='anidado$ ' TERM=dumb bash -c 'exec -a {NombreAnidado} bash --norc --noprofile -i'; echo salio=$?", timeoutSegundos: 120, esperarSegundos: 0);
            await EsperarSalidaAsync(comando.Id, "$ ");

            // Act
            ResultadoComando dentro = await _herramientas.EnviarEntradaAsync(comando.Id, "echo dentro=$$", esperarSegundos: 2);
            ResultadoComando fuera = await _herramientas.EnviarEntradaAsync(comando.Id, "exit 3", esperarSegundos: 20);
            ResultadoComando siguiente = await _herramientas.EjecutarAsync(Destino, "echo pid=$$ v=$NS_EO2", esperarSegundos: 20);

            // Assert
            _ = dentro.Salida.Should().Contain("dentro=").And.NotContain($"dentro={pid}");
            _ = fuera.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = fuera.CodigoSalida.Should().Be(0);
            _ = fuera.Aviso.Should().BeNull();
            _ = fuera.Salida.Should().EndWith("salio=3\n");
            _ = siguiente.Salida.Should().Be($"pid={pid} v=si\n");
            _ = siguiente.Aviso.Should().BeNull();
        }

        [Fact]
        public async Task ComandosQueNoDejanUnShellAnidado_NoSeConfundenConUno()
        {
            // Arrange
            string fuente = Path.Combine(_sshd.DirectorioEstado, $"fuente_{Guid.NewGuid():N}.sh");
            ResultadoComando inicio = await _herramientas.EjecutarAsync(Destino, "export NS_FP=si; echo $$", esperarSegundos: 20);
            string pid = inicio.Salida.Trim();
            (string Comando, string Salida)[] completos =
            [
                // '[' y 'test' redefinidos en el shell de la sesión: la comprobación no los usa.
                ("[ () { return 1; }; test () { return 1; }; echo redefinidos", "redefinidos\n"),
                ("( echo sub; exit 3 ); echo rc=$?", "sub\nrc=3\n"),
                ("bash -c 'echo hijo=$NS_FP; exit 4'; echo rc=$?", "hijo=si\nrc=4\n"),
                ("sh -c 'echo sh'", "sh\n"),
                ("echo a | cat | tr a b", "b\n"),
                ("f() { echo fn=$NS_FP; }; f", "fn=si\n"),
                ($"printf 'echo fuente=$NS_FP\\n' > '{fuente}'; source '{fuente}'; rm -f '{fuente}'", "fuente=si\n"),
                ("eval 'echo ev'", "ev\n"),
                ("exec 3>/dev/null; echo redir", "redir\n"),
                ("echo pid=$$", $"pid={pid}\n")
            ];
            string[] cancelables = [Proceso, $"( {Proceso} )", $"{Proceso} | cat", $"bash -c '{Proceso}'", $"sh -c '{Proceso}'"];
            List<ResultadoComando> resultados = [];
            List<ResultadoComando> cancelados = [];

            // Act
            foreach ((string comando, _) in completos)
                resultados.Add(await _herramientas.EjecutarAsync(Destino, comando, esperarSegundos: 20));
            foreach (string comando in cancelables)
            {
                ResultadoComando enCurso = await _herramientas.EjecutarAsync(Destino, comando, timeoutSegundos: 300, esperarSegundos: 1);
                cancelados.Add(await _herramientas.CancelarAsync(enCurso.Id));
            }

            ResultadoComando trasCancelar = await _herramientas.EjecutarAsync(Destino, "echo pid=$$ v=$NS_FP", esperarSegundos: 20);

            // exec conserva el pid y el padre: el bash nuevo es el mismo proceso (no es otro usuario ni otra máquina). No
            // termina la línea, así que vence su plazo y el Ctrl+C lo recibe ese bash interactivo, que sigue.
            ResultadoComando conExec = await _herramientas.EjecutarAsync(Destino, "exec bash --noediting --norc --noprofile", timeoutSegundos: 2, esperarSegundos: 20);
            ResultadoComando trasExec = await _herramientas.EjecutarAsync(Destino, "echo pid=$$ v=$NS_FP", esperarSegundos: 20);
            ResultadoAccion reinicio = _herramientas.ReiniciarShell(Destino);
            ResultadoComando trasReinicio = await _herramientas.EjecutarAsync(Destino, "echo v=$NS_FP", esperarSegundos: 20);
            ResultadoComando enCursoTrasReinicio = await _herramientas.EjecutarAsync(Destino, Proceso, timeoutSegundos: 300, esperarSegundos: 1);
            ResultadoComando canceladoTrasReinicio = await _herramientas.CancelarAsync(enCursoTrasReinicio.Id);
            ResultadoComando final = await _herramientas.EjecutarAsync(Destino, "echo fin", esperarSegundos: 20);

            // Assert
            for (int i = 0; i < completos.Length; i++)
            {
                _ = resultados[i].Estado.Should().Be(EstadoPeticionEnum.Completado, completos[i].Comando);
                _ = resultados[i].Salida.Should().Be(completos[i].Salida, completos[i].Comando);
                _ = resultados[i].Aviso.Should().BeNull(completos[i].Comando);
            }

            _ = cancelados.Should().OnlyContain(c => c.Estado == EstadoPeticionEnum.Cancelado && c.Aviso == Cancelado);
            _ = trasCancelar.Salida.Should().Be($"pid={pid} v=si\n");
            _ = trasCancelar.Aviso.Should().BeNull();
            _ = conExec.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            _ = conExec.Aviso.Should().Be("Tiempo agotado (2 s): se envió Ctrl+C al proceso.");
            _ = trasExec.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = trasExec.Salida.Should().Contain($"pid={pid} v=si\n");
            _ = trasExec.Aviso.Should().BeNull();
            _ = reinicio.IsHecho.Should().BeTrue();
            _ = trasReinicio.Salida.Should().Be("v=\n");
            _ = trasReinicio.Aviso.Should().BeNull();
            _ = canceladoTrasReinicio.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = canceladoTrasReinicio.Aviso.Should().Be(Cancelado);
            _ = final.Salida.Should().Be("fin\n");
            _ = final.Aviso.Should().BeNull();
        }
    }
}
