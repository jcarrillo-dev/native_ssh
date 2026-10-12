using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using NativeSsh.Protocolo;

namespace NativeSsh.Test.Protocolo
{
    // La comprobación de identidad de la cola (ConstructorComando + IdentidadShell) ejecutada por un bash real de la máquina:
    // la línea de captura guarda el testigo y da la identidad, y la cola solo da el código del comando si la ejecuta ese
    // mismo bash con su testigo; cualquier diferencia ($$, $PPID o el testigo) da MarcadorFin.CodigoShellAnidado. Es el
    // comportamiento, no el texto: un shell con el mismo $$ y $PPID pero sin el testigo (un gemelo en otra máquina, o el
    // programa que deja un exec) se detecta.
    [SupportedOSPlatform("linux")]
    public sealed class ComprobacionIdentidadBashTests : IDisposable
    {
        private static readonly TimeSpan Limite = TimeSpan.FromSeconds(15);

        private readonly Process _bash;
        private readonly StringBuilder _errores = new();
        private readonly Guid _testigo = IdentidadShell.NuevoTestigo();
        private readonly Guid _centinela = IdentidadShell.NuevoTestigo();

        public ComprobacionIdentidadBashTests()
        {
            // Interactivo, como el de la sesión (un error de asignación no corta la línea), sin indicador.
            ProcessStartInfo inicio = new("bash", ["--noediting", "--norc", "--noprofile", "-i"])
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            _ = inicio.Environment.Remove("__NS_TESTIGO");
            inicio.Environment["PS1"] = "";
            inicio.Environment["PS2"] = "";
            _bash = Process.Start(inicio)!;
            _bash.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    lock (_errores)
                        _ = _errores.AppendLine(e.Data);
            };
            _bash.BeginErrorReadLine();
        }

        public void Dispose()
        {
            try
            {
                _bash.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            _bash.Dispose();
        }

        private string Errores
        {
            get
            {
                lock (_errores)
                    return _errores.ToString();
            }
        }

        private async Task EscribirAsync(Action<ArrayBufferWriter<byte>> accion)
        {
            ArrayBufferWriter<byte> escritor = new();
            accion(escritor);
            await _bash.StandardInput.BaseStream.WriteAsync(escritor.WrittenMemory);
            await _bash.StandardInput.BaseStream.FlushAsync();
        }

        // Lee hasta el marcador de 'id' y devuelve lo anterior y su código (sin el recuento opcional). La cola espera después el
        // centinela, que es la primera línea del envío siguiente.
        private async Task<(string Salida, int Codigo)> LeerHastaMarcadorAsync(Guid id)
        {
            string cabecera = $"__NSX_{id:N}_";
            StringBuilder salida = new();
            while (true)
            {
                string linea = await _bash.StandardOutput.ReadLineAsync().WaitAsync(Limite)
                    ?? throw new InvalidOperationException($"bash terminó sin dar el marcador. Salida: {salida} Errores: {Errores}");

                int posicion = linea.IndexOf(cabecera, StringComparison.Ordinal);
                if (posicion < 0)
                {
                    _ = salida.AppendLine(linea);
                    continue;
                }

                _ = salida.Append(linea.AsSpan(0, posicion));
                string codigo = linea[(posicion + cabecera.Length)..^2].Split('_')[0];
                return (salida.ToString(), int.Parse(codigo, CultureInfo.InvariantCulture));
            }
        }

        // Captura como en un canal nuevo: guarda el testigo y devuelve la identidad que imprime el bash.
        private async Task<IdentidadShell> CapturarAsync()
        {
            Guid id = Guid.NewGuid();
            await EscribirAsync(w => ConstructorComando.EscribirCapturaPath(id, _testigo, _centinela, w));
            (string salida, int codigo) = await LeerHastaMarcadorAsync(id);
            _ = codigo.Should().Be(0);
            _ = IdentidadShell.TryLeer(Encoding.UTF8.GetBytes(salida), id, _testigo, out IdentidadShell identidad).Should().BeTrue(salida);
            // Lo anterior (avisos del control de trabajos de un bash interactivo sin terminal) no es de la comprobación.
            lock (_errores)
                _ = _errores.Clear();
            return identidad;
        }

        private async Task<(string Salida, int Codigo)> EjecutarAsync(string comando, IdentidadShell? identidad)
        {
            Guid id = Guid.NewGuid();
            await EscribirAsync(w => ConstructorComando.EscribirComando(comando, id, _centinela, w, identidad));
            return await LeerHastaMarcadorAsync(id);
        }

        private async Task<(string Salida, int Codigo)> RecuperarAsync(IdentidadShell identidad)
        {
            Guid id = Guid.NewGuid();
            await EscribirAsync(w => ConstructorComando.EscribirRecuperacion(id, _centinela, w, identidad));
            return await LeerHastaMarcadorAsync(id);
        }

        [Fact]
        public async Task Captura_DaElPidYElPadreDelBash()
        {
            // Arrange
            (string esperada, _) = await EjecutarAsync("echo \"$$ $PPID\"", null);

            // Act
            IdentidadShell identidad = await CapturarAsync();

            // Assert
            _ = $"{identidad.Pid} {identidad.PidPadre}".Should().Be(esperada.Trim());
            _ = identidad.Testigo.Should().Be(_testigo);
            _ = Errores.Should().BeEmpty();
        }

        [Fact]
        public async Task Cola_SoloConLaIdentidadExactaDaElCodigoDelComando()
        {
            // Arrange
            IdentidadShell real = await CapturarAsync();
            IdentidadShell[] ajenas =
            [
                // Mismo $$ y $PPID sin el testigo de este canal: un gemelo en otra máquina o el programa que deja un exec.
                real with { Testigo = IdentidadShell.NuevoTestigo() },
                // Solo cambia $PPID (una comprobación que solo mirara $$ no lo vería) y solo cambia $$ (ídem con $PPID).
                real with { PidPadre = real.PidPadre + 1 },
                real with { Pid = real.Pid + 1 },
                // Prefijos del valor real: el literal tiene que coincidir entero.
                real with { Pid = real.Pid / 10 is var corto and > 0 ? corto : real.Pid + 10 }
            ];

            // Act
            (string salida, int codigo) = await EjecutarAsync("echo hola; exit_code() { return 7; }; exit_code", real);
            (_, int recuperacion) = await RecuperarAsync(real);
            List<int> codigosAjenos = [];
            List<int> recuperacionesAjenas = [];
            foreach (IdentidadShell ajena in ajenas)
            {
                codigosAjenos.Add((await EjecutarAsync("true", ajena)).Codigo);
                recuperacionesAjenas.Add((await RecuperarAsync(ajena)).Codigo);
            }

            // Assert
            _ = salida.Should().Be("hola\n");
            _ = codigo.Should().Be(7);
            _ = recuperacion.Should().Be(130);
            _ = codigosAjenos.Should().AllBeEquivalentTo(MarcadorFin.CodigoShellAnidado);
            _ = recuperacionesAjenas.Should().AllBeEquivalentTo(MarcadorFin.CodigoShellAnidado);
            _ = Errores.Should().BeEmpty();
        }

        // Fallo cerrado: si el bash no puede guardar el testigo del canal (ya hay una variable de solo lectura con ese nombre),
        // la captura no da la identidad y la inicialización falla.
        [Fact]
        public async Task Captura_SinPoderGuardarElTestigo_NoDaLaIdentidad()
        {
            // Arrange
            _ = await EjecutarAsync("declare -r -a __NS_TESTIGO=(otro)", null);
            Guid id = Guid.NewGuid();

            // Act
            await EscribirAsync(w => ConstructorComando.EscribirCapturaPath(id, _testigo, _centinela, w));
            (string salida, _) = await LeerHastaMarcadorAsync(id);

            // Assert
            _ = IdentidadShell.TryLeer(Encoding.UTF8.GetBytes(salida), id, _testigo, out _).Should().BeFalse(salida);
        }

        // El testigo no pasa a ningún otro proceso, ni exportándolo: un bash hijo o el programa de un exec (mismo $$ y $PPID)
        // no lo tienen. Tampoco se puede cambiar ni borrar.
        [Fact]
        public async Task Testigo_NoLoHeredaNingunProcesoNiSePuedeCambiar()
        {
            // Arrange
            IdentidadShell real = await CapturarAsync();

            // Act
            (string hijo, _) = await EjecutarAsync("export __NS_TESTIGO; declare -x __NS_TESTIGO; set -a; bash --norc --noprofile -c 'echo \"hijo=[${__NS_TESTIGO-}] $(env | grep -c __NS_TESTIGO)\"'; set +a", real);
            List<(string Salida, int Codigo)> cambios = [];
            foreach (string cambio in (string[])["__NS_TESTIGO=otro", "__NS_TESTIGO[0]=otro", "unset __NS_TESTIGO", "f() { local __NS_TESTIGO=otro; true; }; f", "declare +r __NS_TESTIGO"])
                cambios.Add(await EjecutarAsync(cambio, real));
            (_, int codigoTrasCambios) = await EjecutarAsync("true", real);
            // La cola de la línea del exec no llega a ejecutarse (el proceso ya es otro programa): como tras el plazo de
            // ssh_exec, la siguiente línea es la de recuperación, y la ejecuta el bash nuevo.
            await EscribirAsync(w => ConstructorComando.EscribirComando("exec bash --norc --noprofile", Guid.NewGuid(), _centinela, w, real));
            await Task.Delay(300);
            (_, int codigoExec) = await RecuperarAsync(real);

            // Assert: el exec deja otro programa en el mismo proceso, que ya no tiene el testigo.
            _ = hijo.Should().Be("hijo=[] 0\n");
            _ = cambios.Should().OnlyContain(c => c.Salida.Length == 0 && c.Codigo != MarcadorFin.CodigoShellAnidado);
            _ = codigoTrasCambios.Should().Be(0, "el testigo sigue siendo el del canal: {0}", Errores);
            _ = Errores.Should().Contain("__NS_TESTIGO");
            _ = codigoExec.Should().Be(MarcadorFin.CodigoShellAnidado);
        }

        // Con lo que un usuario puede haber dejado en el shell de la sesión, la comprobación ni falla ni escribe nada: ni con
        // 'set -u' o 'set -e', ni con un IFS raro, ni con alias de las palabras reservadas, ni con funciones con nombre de
        // orden, ni con opciones de patrones.
        [Fact]
        public async Task Cola_ConOpcionesAliasYFuncionesDelUsuario_NiFallaNiEscribeNada()
        {
            // Arrange
            IdentidadShell real = await CapturarAsync();
            string preparacion = "set -u -e -o pipefail -f -a; IFS='0123456789abcdef '; shopt -s nocasematch extglob nullglob failglob dotglob expand_aliases; "
                + "alias case='echo ALIAS' esac='echo ALIAS' in='echo ALIAS' __ns_t='echo ALIAS'; [() { echo FUNCION; }; test() { echo FUNCION; }";

            // Act
            (string salidaPreparacion, int codigoPreparacion) = await EjecutarAsync(preparacion, real);
            (string salida, int codigo) = await EjecutarAsync("echo uno; false || echo dos", real);
            (string salidaRecuperacion, int recuperacion) = await RecuperarAsync(real);
            (_, int ajeno) = await EjecutarAsync("true", real with { Testigo = IdentidadShell.NuevoTestigo() });

            // Assert
            _ = salidaPreparacion.Should().BeEmpty();
            _ = codigoPreparacion.Should().Be(0);
            _ = salida.Should().Be("uno\ndos\n");
            _ = codigo.Should().Be(0);
            _ = salidaRecuperacion.Should().BeEmpty();
            _ = recuperacion.Should().Be(130);
            _ = ajeno.Should().Be(MarcadorFin.CodigoShellAnidado);
            _ = Errores.Should().BeEmpty();
        }
    }
}
