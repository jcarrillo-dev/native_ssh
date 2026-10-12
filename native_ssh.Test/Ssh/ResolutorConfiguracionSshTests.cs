using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Ssh;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Ssh
{
    // CI4: los destinos se resuelven con 'ssh -G' sobre un fichero de configuración PROPIO de cada prueba (ssh -F), nunca
    // el ~/.ssh/config real del usuario ni /etc/ssh/ssh_config. Necesita el cliente de OpenSSH en la máquina.
    [SupportedOSPlatform("linux")]
    public sealed class ResolutorConfiguracionSshTests : IDisposable
    {
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_cfg_{Guid.NewGuid():N}")).FullName;
        private readonly LoggerPrueba _log = new();

        public void Dispose()
        {
            _log.Dispose();
            Directory.Delete(_directorio, recursive: true);
        }

        private string Configuracion(string contenido)
        {
            string ruta = Path.Combine(_directorio, $"config_{Guid.NewGuid():N}");
            File.WriteAllText(ruta, contenido);
            return ruta;
        }

        private ResolutorConfiguracionSsh Resolutor(string configuracion, bool isIdentidadesExplicitas = false, string binario = "ssh", TimeSpan? plazo = null) =>
            new(new OpcionesNativeSsh
            {
                UsuarioPorDefecto = Environment.UserName,
                ConfiguracionSsh = configuracion,
                IsRutasIdentidadExplicitas = isIdentidadesExplicitas,
                BinarioSsh = binario,
                TimeoutConfiguracionSsh = plazo ?? TimeSpan.FromSeconds(5)
            }, new LoggerDe<ResolutorConfiguracionSsh>(_log));

        private static DestinoSsh Resolver(ResolutorConfiguracionSsh resolutor, string texto)
        {
            _ = DestinoSsh.TryParse(texto, Environment.UserName, out DestinoSsh destino, out bool isUsuario, out bool isPuerto).Should().BeTrue();
            return resolutor.Resolver(destino, isUsuario, isPuerto);
        }

        [Fact]
        public void Alias_SeResuelveConHostNameUserPortEIdentityFile()
        {
            // Arrange
            string clave = Path.Combine(_directorio, "id_web");
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion($"Host web\n  HostName 127.0.0.1\n  User deploy\n  Port 2222\n  IdentityFile {clave}\n"));

            // Act
            DestinoSsh destino = Resolver(resolutor, "web");

            // Assert
            _ = destino.Clave.Should().Be("deploy@127.0.0.1:2222");
            _ = destino.Identidades.Should().Equal(clave);
        }

        [Fact]
        public void UsuarioYPuertoExplicitos_MandanSobreLaConfiguracion()
        {
            // Arrange
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion("Host web\n  HostName 127.0.0.1\n  User deploy\n  Port 2222\n"));

            // Act
            DestinoSsh destino = Resolver(resolutor, "otro@web:33");

            // Assert
            _ = destino.Clave.Should().Be("otro@127.0.0.1:33");
        }

        // El usuario explícito llega a ssh -G (-l): Match user y %r lo ven, como en ssh.
        [Fact]
        public void UsuarioExplicito_LoVeMatchUser()
        {
            // Arrange
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion("Match user otro\n  Port 4444\nHost web\n  HostName 127.0.0.1\n"));

            // Act
            DestinoSsh conOtro = Resolver(resolutor, "otro@web");
            DestinoSsh sinOtro = Resolver(resolutor, "web");

            // Assert
            _ = conOtro.Puerto.Should().Be(4444);
            _ = sinOtro.Puerto.Should().Be(22);
        }

        // Host * y Match se aplican también a una IP o a un nombre que no es un alias.
        [Fact]
        public void DestinoQueNoEsAlias_TambienRecibeLaConfiguracionGeneral()
        {
            // Arrange
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion("Host 10.1.2.*\n  User admin\n  Port 2200\n"));

            // Act
            DestinoSsh destino = Resolver(resolutor, "10.1.2.3");

            // Assert
            _ = destino.Clave.Should().Be("admin@10.1.2.3:2200");
        }

        [Fact]
        public void Ipv6EntreCorchetes_SeResuelve()
        {
            // Arrange
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion("Host ::1\n  User seis\n"));

            // Act
            DestinoSsh destino = Resolver(resolutor, "[::1]:2022");

            // Assert
            _ = destino.Clave.Should().Be("seis@::1:2022");
        }

        // [ipv6] sin puerto no es un puerto explícito: no se pasa -p 22 y vale el Port de la configuración, como en ssh.
        [Fact]
        public void Ipv6EntreCorchetesSinPuerto_UsaElPortDeLaConfiguracion()
        {
            // Arrange
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion("Host ::1\n  User seis\n  Port 2222\n"));

            // Act
            DestinoSsh destino = Resolver(resolutor, "[::1]");

            // Assert
            _ = destino.Clave.Should().Be("seis@::1:2222");
        }

        // Sin IdentityFile en la configuración, ssh -G lista sus claves por defecto (~/.ssh/id_*): se reconocen y se dejan las
        // de native_ssh (las de NATIVE_SSH_DIRECTORIO_SSH), sin leer las del ~/.ssh real.
        [Fact]
        public void SinIdentityFile_UsaLasClavesDeNativeSsh()
        {
            // Arrange
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion("Host web\n  HostName 127.0.0.1\n"));

            // Act
            DestinoSsh destino = Resolver(resolutor, "web");

            // Assert
            _ = destino.Identidades.Should().BeNull();
        }

        // NATIVE_SSH_IDENTIDADES manda sobre los IdentityFile (precedencia explícita).
        [Fact]
        public void ConIdentidadesExplicitas_SeIgnoranLosIdentityFile()
        {
            // Arrange
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion($"Host web\n  HostName 127.0.0.1\n  IdentityFile {_directorio}/id_web\n"), isIdentidadesExplicitas: true);

            // Act
            DestinoSsh destino = Resolver(resolutor, "web");

            // Assert
            _ = destino.Identidades.Should().BeNull();
        }

        [Fact]
        public void VariosIdentityFile_TodosEnOrdenYSinRepetir()
        {
            // Arrange
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion($"Host web\n  HostName 127.0.0.1\n  IdentityFile {_directorio}/b\n  IdentityFile {_directorio}/a\nHost *\n  IdentityFile {_directorio}/b\n  IdentityFile {_directorio}/c\n"));

            // Act
            DestinoSsh destino = Resolver(resolutor, "web");

            // Assert
            _ = destino.Identidades.Should().Equal($"{_directorio}/b", $"{_directorio}/a", $"{_directorio}/c");
        }

        // ssh -G ya quita las líneas repetidas literalmente; las que solo coinciden tras expandirlas las quita native_ssh.
        [Fact]
        public void IdentityFileQueCoincidenTrasExpandir_SinRepetir()
        {
            // Arrange
            string casa = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion("Host web\n  HostName 127.0.0.1\n  IdentityFile ~/clave_ns\n  IdentityFile %d/clave_ns\n"));

            // Act
            DestinoSsh destino = Resolver(resolutor, "web");

            // Assert
            _ = destino.Identidades.Should().Equal($"{casa}/clave_ns");
        }

        [Fact]
        public void IdentityFileConTildeYTokens_SeExpanden()
        {
            // Arrange
            string casa = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string usuario = Environment.UserName;
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion("Host web\n  HostName 127.0.0.1\n  User deploy\n  Port 2222\n  IdentityFile ~/k_%h_%r_%p_%n_%u_%%\n  IdentityFile %d/d\n  IdentityFile ${HOME}/v\n"));

            // Act
            DestinoSsh destino = Resolver(resolutor, "web");

            // Assert
            _ = destino.Identidades.Should().Equal($"{casa}/k_127.0.0.1_deploy_2222_web_{usuario}_%", $"{casa}/d", $"{Environment.GetEnvironmentVariable("HOME")}/v");
        }

        [Theory]
        [InlineData("%C/x", "usa '%C'")]
        [InlineData("~root/x", "'~usuario'")]
        [InlineData("${NS_VARIABLE_QUE_NO_EXISTE_9F}/x", "${NS_VARIABLE_QUE_NO_EXISTE_9F}, que no está definida")]
        [InlineData("claves/id", "es una ruta relativa")]
        public void IdentityFileQueNoSeSabeExpandir_ErrorQueLoDice(string identityFile, string motivo)
        {
            // Arrange
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion($"Host web\n  HostName 127.0.0.1\n  IdentityFile {identityFile}\n"));

            // Act
            Action resolver = () => Resolver(resolutor, "web");

            // Assert
            _ = resolver.Should().Throw<NativeSshException>().Which.Message.Should().Contain($"El IdentityFile '{identityFile}'").And.Contain(motivo).And.Contain("NATIVE_SSH_IDENTIDADES");
        }

        [Theory]
        [InlineData("ProxyJump bastion", "ProxyJump")]
        [InlineData("ProxyCommand nc %h %p", "ProxyCommand")]
        public void ProxyJumpOProxyCommand_SeRechazan(string linea, string nombre)
        {
            // Arrange
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion($"Host web\n  HostName 10.0.0.5\n  {linea}\n"));

            // Act
            Action resolver = () => Resolver(resolutor, "web");

            // Assert
            _ = resolver.Should().Throw<NativeSshException>().Which.Message.Should().Contain($"usa {nombre}, que native_ssh no admite");
        }

        [Fact]
        public void ProxyJumpNone_NoSeRechaza()
        {
            // Arrange
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion("Host web\n  HostName 127.0.0.1\n  ProxyJump none\n"));

            // Act & Assert
            _ = Resolver(resolutor, "web").Servidor.Should().Be("127.0.0.1");
        }

        // El destino lo escribe el LLM: nada que ssh -G pudiera tomar por una opción o una URI llega a ejecutarse.
        [Theory]
        [InlineData("-oProxyCommand=touch")]
        [InlineData("[ssh://h]")]
        [InlineData("-l@web")]
        [InlineData("a/b")]
        [InlineData("web%x")]
        public void DestinoConOpcionesOUris_SeRechazaSinEjecutarSsh(string texto)
        {
            // Arrange: si se ejecutara ssh, el binario que no existe dejaría el destino tal cual en vez de lanzar.
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion(""), binario: Path.Combine(_directorio, "no-existe"));

            // Act
            Action resolver = () => Resolver(resolutor, texto);

            // Assert
            _ = resolver.Should().Throw<NativeSshException>().Which.Message.Should().Contain("no válido para leerlo con la configuración de ssh");
        }

        // Plazo: un Match exec que se cuelga no bloquea la llamada, y ssh y lo que lanzó mueren.
        [Fact]
        public async Task SshQueNoTermina_ErrorTrasElPlazoYSinProcesosColgados()
        {
            // Arrange
            string marca = $"37.{Random.Shared.Next(100000, 999999)}";
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion($"Match exec \"sleep {marca}\"\n  Port 9\n"), plazo: TimeSpan.FromSeconds(1));
            Stopwatch reloj = Stopwatch.StartNew();

            // Act
            Action resolver = () => Resolver(resolutor, "web");

            // Assert
            _ = resolver.Should().Throw<NativeSshException>().Which.Message.Should().Contain("no terminó en 1 s");
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
            await EsperasPruebaSinProceso(marca);
        }

        // Un Match exec que deja un proceso en segundo plano con la salida de ssh abierta: ssh termina al momento con toda su
        // salida, así que el destino se resuelve (sin decir que ssh no terminó), y lo que dejó en segundo plano muere (ssh va
        // en su propio grupo de procesos). La espera tiene tope para que una lectura sin plazo falle en vez de colgarse.
        [Fact]
        public async Task MatchExecQueDejaUnProcesoEnSegundoPlano_SeResuelveYNoQuedanHuerfanos()
        {
            // Arrange
            string marca = $"39.{Random.Shared.Next(100000, 999999)}";
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion($"Match originalhost fondo exec \"(sleep {marca} &) ; true\"\n  Port 9\n"));
            Stopwatch reloj = Stopwatch.StartNew();

            // Act
            DestinoSsh destino = await Task.Run(() => Resolver(resolutor, "fondo")).WaitAsync(TimeSpan.FromSeconds(20));

            // Assert
            _ = destino.Puerto.Should().Be(9);
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
            await EsperasPruebaSinProceso(marca);
        }

        // Si lo que dejó en segundo plano salió del grupo (su propio setsid) y retiene la salida, el error dice que ssh sí
        // terminó y qué la retiene, no que no terminó.
        [Fact]
        public async Task MatchExecQueSaleDelGrupoYRetieneLaSalida_ErrorQueDiceQueSshTermino()
        {
            // Arrange
            string marca = $"40.{Random.Shared.Next(100000, 999999)}";
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion($"Match originalhost fondo exec \"(setsid sleep {marca} &) ; true\"\n  Port 9\n"));

            // Act
            Func<Task> resolver = () => Task.Run(() => Resolver(resolutor, "fondo")).WaitAsync(TimeSpan.FromSeconds(20));

            // Assert
            try
            {
                _ = (await resolver.Should().ThrowAsync<NativeSshException>()).Which.Message.Should().Contain("'ssh -G' terminó").And.Contain("mantiene abierta su salida").And.NotContain("no terminó");
            }
            finally
            {
                _ = BashLocal.Ejecutar($"pkill -f '[s]leep {marca}' || true");
            }
        }

        // El plazo por defecto es de 5 s: un Match exec colgado no retiene la llamada más que eso.
        [Fact]
        public async Task PlazoPorDefecto_CincoSegundos()
        {
            // Arrange
            string marca = $"38.{Random.Shared.Next(100000, 999999)}";
            ResolutorConfiguracionSsh resolutor = new(new OpcionesNativeSsh { ConfiguracionSsh = Configuracion($"Match exec \"sleep {marca}\"\n  Port 9\n") }, NullLogger<ResolutorConfiguracionSsh>.Instance);
            _ = DestinoSsh.TryParse("web", "ana", out DestinoSsh destino);
            Stopwatch reloj = Stopwatch.StartNew();

            // Act
            Task<DestinoSsh> resolucion = Task.Run(() => resolutor.Resolver(destino, false, false));
            Func<Task> esperar = () => resolucion.WaitAsync(TimeSpan.FromSeconds(20));

            // Assert
            _ = (await esperar.Should().ThrowAsync<NativeSshException>()).Which.Message.Should().Contain("no terminó en 5 s");
            _ = reloj.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(4.5)).And.BeLessThan(TimeSpan.FromSeconds(9));
            await EsperasPruebaSinProceso(marca);
        }

        // '[s]leep': el patrón no coincide con la línea de órdenes del propio bash que ejecuta pgrep.
        private static async Task EsperasPruebaSinProceso(string marca)
        {
            string orden = $"pgrep -f '[s]leep {marca}' || true";
            for (int i = 0; i < 50 && BashLocal.Ejecutar(orden).Length > 0; i++)
                await Task.Delay(100);
            _ = BashLocal.Ejecutar(orden).Should().BeEmpty();
        }

        [Fact]
        public void ConfiguracionConErrores_ErrorConElMotivoDeSsh()
        {
            // Arrange
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion("Host *\n  OpcionInventada si\n"));

            // Act
            Action resolver = () => Resolver(resolutor, "web");

            // Assert
            _ = resolver.Should().Throw<NativeSshException>().Which.Message.Should().Contain("'ssh -G' no pudo leer la configuración de ssh de 'web' (código 255)").And.Contain("Bad configuration option");
        }

        [Fact]
        public void FicheroDeConfiguracionQueNoExiste_Error()
        {
            // Arrange
            string ruta = Path.Combine(_directorio, "no-existe");
            ResolutorConfiguracionSsh resolutor = Resolutor(ruta);

            // Act
            Action resolver = () => Resolver(resolutor, "web");

            // Assert
            _ = resolver.Should().Throw<NativeSshException>().Which.Message.Should().Contain("no pudo leer").And.Contain(ruta);
        }

        // Sin el programa ssh, el destino se usa tal cual (lo de antes) y un aviso en el log, una sola vez.
        [Fact]
        public void SinProgramaSsh_DestinoTalCualYUnSoloAviso()
        {
            // Arrange
            ResolutorConfiguracionSsh resolutor = Resolutor(Configuracion("Host web\n  HostName 127.0.0.1\n"), binario: Path.Combine(_directorio, "ssh-que-no-existe"));

            // Act
            DestinoSsh primero = Resolver(resolutor, "web");
            DestinoSsh segundo = Resolver(resolutor, "ana@web:2222");

            // Assert
            _ = primero.Clave.Should().Be($"{Environment.UserName}@web:22");
            _ = segundo.Clave.Should().Be("ana@web:2222");
            _ = _log.Entradas.Where(e => e.Nivel == LogLevel.Warning).Should().ContainSingle().Which.Mensaje.Should().Contain("ssh-que-no-existe");
        }

        [Theory]
        [InlineData("none", false, null)]
        [InlineData("/ruta/config", false, "/ruta/config")]
        [InlineData(null, false, "")]
        [InlineData("", false, "")]
        [InlineData(null, true, "DIRECTORIO/config")]
        public void ElegirConfiguracionSsh_SegunLasVariables(string? variable, bool isConDirectorio, string? esperado)
        {
            // Arrange
            File.WriteAllText(Path.Combine(_directorio, "config"), "");

            // Act
            string? elegida = OpcionesNativeSsh.ElegirConfiguracionSsh(variable, isConDirectorio ? _directorio : null);

            // Assert
            _ = elegida.Should().Be(esperado?.Replace("DIRECTORIO", _directorio, StringComparison.Ordinal));
        }

        // Con NATIVE_SSH_DIRECTORIO_SSH y sin config en él, no se usa ninguna configuración (nunca el ~/.ssh/config real).
        [Fact]
        public void ElegirConfiguracionSsh_DirectorioSinConfig_Ninguna() =>
            OpcionesNativeSsh.ElegirConfiguracionSsh(null, _directorio).Should().BeNull();
    }

    // ssh hereda el entorno sin los secretos de native_ssh (un Match exec los vería). Cambia variables del proceso: sin
    // paralelismo con otras pruebas.
    [CollectionDefinition(Nombre, DisableParallelization = true)]
    public sealed class ColeccionEntornoProceso
    {
        public const string Nombre = "entorno del proceso";
    }

    [Collection(ColeccionEntornoProceso.Nombre)]
    [SupportedOSPlatform("linux")]
    public sealed class ResolutorConfiguracionSshEntornoTests : IDisposable
    {
        private static readonly string[] Variables = ["NATIVE_SSH_PASSWORD", "NATIVE_SSH_PASSPHRASE", "NATIVE_SSH_TOKEN_HTTP"];
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_cfg_env_{Guid.NewGuid():N}")).FullName;
        private readonly Dictionary<string, string?> _anteriores = Variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);

        public void Dispose()
        {
            foreach ((string variable, string? valor) in _anteriores)
                Environment.SetEnvironmentVariable(variable, valor);
            Directory.Delete(_directorio, recursive: true);
        }

        [Fact]
        public void MatchExec_NoVeLosSecretosDeNativeSsh()
        {
            // Arrange
            string volcado = Path.Combine(_directorio, "entorno");
            string configuracion = Path.Combine(_directorio, "config");
            File.WriteAllText(configuracion, $"Match exec \"env > {volcado}\"\n  Port 9\n");
            foreach (string variable in Variables)
                Environment.SetEnvironmentVariable(variable, "Canario-secreto-entorno");
            ResolutorConfiguracionSsh resolutor = new(new OpcionesNativeSsh { ConfiguracionSsh = configuracion }, NullLogger<ResolutorConfiguracionSsh>.Instance);
            _ = DestinoSsh.TryParse("web", "ana", out DestinoSsh destino);

            // Act
            DestinoSsh resuelto = resolutor.Resolver(destino, false, false);

            // Assert
            _ = resuelto.Puerto.Should().Be(9);
            string entorno = File.ReadAllText(volcado);
            _ = entorno.Should().Contain("PATH=").And.NotContain("Canario-secreto-entorno");
        }

        // ${VAR} de un IdentityFile no expande los secretos de native_ssh (ssh tampoco los tiene en su entorno): se tratan
        // como no definidas y su valor no llega ni al error ni a las rutas (que van a los mensajes de credenciales y al log).
        [Theory]
        [InlineData("NATIVE_SSH_PASSPHRASE", "${{{0}}}")]
        [InlineData("NATIVE_SSH_PASSPHRASE", "/nada/${{{0}}}")]
        [InlineData("NATIVE_SSH_PASSWORD", "/nada/${{{0}}}")]
        [InlineData("NATIVE_SSH_TOKEN_HTTP", "~/${{{0}}}/id")]
        public void IdentityFileConUnaVariableSecreta_ComoNoDefinidaYSinSuValor(string variable, string formato)
        {
            // Arrange
            const string Canario = "Canario-secreto-identityfile";
            string identityFile = string.Format(System.Globalization.CultureInfo.InvariantCulture, formato, variable);
            string configuracion = Path.Combine(_directorio, "config");
            File.WriteAllText(configuracion, $"Host web\n  HostName 127.0.0.1\n  IdentityFile {identityFile}\n");
            Environment.SetEnvironmentVariable(variable, Canario);
            using LoggerPrueba log = new();
            ResolutorConfiguracionSsh resolutor = new(new OpcionesNativeSsh { ConfiguracionSsh = configuracion }, new LoggerDe<ResolutorConfiguracionSsh>(log));
            _ = DestinoSsh.TryParse("web", "ana", out DestinoSsh destino);

            // Act
            Action resolver = () => resolutor.Resolver(destino, false, false);

            // Assert
            _ = resolver.Should().Throw<NativeSshException>().Which.Message.Should().Contain($"${{{variable}}}, que no está definida").And.NotContain(Canario);
            _ = log.Entradas.Should().NotContain(e => e.Mensaje.Contains(Canario, StringComparison.Ordinal));
        }
    }
}
