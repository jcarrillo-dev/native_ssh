using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Ssh;
using NativeSsh.Test.Sesion.Falsos;

namespace NativeSsh.Test.Herramientas
{
    // Modo solo lectura con la configuración de ssh y con la sesión ya conectada (CI6 × CI4): un destino es de solo lectura si
    // coincide con una entrada el texto pedido (alias), el destino resuelto por ssh -G o la dirección a la que está
    // conectada de verdad la sesión. Configuración de ssh propia (ssh -F), DNS falso que se puede cambiar a mitad de prueba y
    // conexiones falsas cuya dirección remota es la que daba el DNS falso al conectar.
    [SupportedOSPlatform("linux")]
    public sealed class SoloLecturaConexionTests : IAsyncDisposable
    {
        private static readonly TimeSpan Limite = TimeSpan.FromSeconds(10);
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_sl_con_{Guid.NewGuid():N}")).FullName;
        private readonly ConcurrentDictionary<string, IPAddress> _dns = new(StringComparer.Ordinal);
        private readonly FabricaFalsa _fabrica = new(Guion);
        private GestorSesiones? _gestor;

        public SoloLecturaConexionTests()
        {
            File.WriteAllText(Path.Combine(_directorio, "config"), "Host prod\n  HostName 10.0.0.5\nHost web\n  HostName 10.0.0.7\nHost fantasma\n  HostName noexiste.ejemplo\nHost *\n  User ana\n");
            _fabrica.DireccionRemota = destino => IPAddress.TryParse(destino.Servidor, out IPAddress? ip) ? ip : _dns.GetValueOrDefault(destino.Servidor);
        }

        public async ValueTask DisposeAsync()
        {
            if (_gestor is not null)
                await _gestor.DisposeAsync();
            Directory.Delete(_directorio, recursive: true);
        }

        private static RespuestaFalsa Guion(string comando) =>
            comando.Contains("| tail -n +", StringComparison.Ordinal) ? new("[Líneas 1-1 de 1]\nhola\n") :
            comando.Contains("pwd -P", StringComparison.Ordinal) ? MarcarDirectorio(comando) :
            comando == "pregunta" ? new("¿Seguro? ", 0, ComportamientoFalsoEnum.PideEntrada) :
            new("ok\n");

        // Como el script de ssh_set_cwd: la ruta resuelta en base64 tras el marcador con el id de la petición.
        private static RespuestaFalsa MarcarDirectorio(string comando)
        {
            const string Formato = "__NS_CWD_%s__ %s\\n' ";
            int posicion = comando.IndexOf(Formato, StringComparison.Ordinal) + Formato.Length;
            return new($"__NS_CWD_{comando[posicion..(posicion + 32)]}__ {Convert.ToBase64String("/srv"u8)}\n");
        }

        private Task<IPAddress[]> ResolverFalso(string nombre, CancellationToken _) =>
            _dns.TryGetValue(nombre, out IPAddress? ip) ? Task.FromResult(new[] { ip }) : Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));

        private HerramientasSsh Crear(string soloLectura, bool isConConfiguracion = true)
        {
            _ = DestinosSoloLectura.TryParsear(soloLectura, out DestinosSoloLectura destinos, out string? error, ResolverFalso).Should().BeTrue(error);
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana", SoloLectura = destinos, ConfiguracionSsh = isConConfiguracion ? Path.Combine(_directorio, "config") : null };
            IResolutorDestino? resolutor = isConConfiguracion ? new ResolutorConfiguracionSsh(opciones, NullLogger<ResolutorConfiguracionSsh>.Instance) : null;
            _gestor = new GestorSesiones(_fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance, resolutor);
            return new HerramientasSsh(_gestor, opciones);
        }

        private async Task<ResultadoComando> EjecutarHastaTerminar(HerramientasSsh herramientas, string destino)
        {
            ResultadoComando resultado = await herramientas.EjecutarAsync(destino, "touch marca", esperarSegundos: 10);
            await _gestor!.BuscarPeticion(resultado.Id).Finalizacion.WaitAsync(Limite);
            return resultado.Estado.IsFinal() ? resultado : await herramientas.LeerSalidaAsync(resultado.Id);
        }

        private int ComandosEnviados(string fragmento) => _fabrica.Ultima?.Comandos.Count(c => c.Contains(fragmento, StringComparison.Ordinal)) ?? 0;

        // La entrada es el alias de la configuración de ssh: el texto pedido cuenta aunque ssh -G lo cambie por su HostName y
        // el DNS diga que ese nombre es otra máquina.
        [Theory]
        [InlineData("prod")]
        [InlineData("deploy@prod")]
        [InlineData("prod:22")]
        public async Task EntradaConElAlias_SeRechazaAunqueElHostNameNoEsteEnLaLista(string destino)
        {
            // Arrange
            _dns["prod"] = IPAddress.Parse("10.0.0.99");
            HerramientasSsh herramientas = Crear("prod");

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EjecutarAsync(destino, "touch marca")).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be(HerramientasSsh.MensajeSoloLectura(destino.StartsWith("deploy@", StringComparison.Ordinal) ? "deploy@10.0.0.5:22" : "ana@10.0.0.5:22", "ssh_exec"));
            _ = _fabrica.Conexiones.Should().Be(0);
        }

        // La entrada es un alias de la configuración que no está en el DNS (el ejemplo del README): vale por su HostName, con
        // cualquier usuario y puerto (la entrada no los lleva), y no hace fallar a los demás destinos. Antes «no se resolvió
        // prod» rechazaba toda escritura con su usuario y puerto.
        [Theory]
        [InlineData("web", true)]
        [InlineData("ana@10.0.0.7", true)]
        [InlineData("ana@10.0.0.5", false)]
        [InlineData("root@10.0.0.5:2222", false)]
        public async Task EntradaConElAlias_ProtegeSuHostNameYNoHaceFallarALosDemas(string destino, bool isEscribe)
        {
            // Arrange
            HerramientasSsh herramientas = Crear("prod");

            // Act
            Func<Task<ResultadoComando>> escribir = () => EjecutarHastaTerminar(herramientas, destino);

            // Assert
            if (isEscribe)
                _ = (await escribir()).Estado.Should().Be(EstadoPeticionEnum.Completado);
            else
                _ = (await escribir.Should().ThrowAsync<NativeSshException>()).Which.Message.Should().Be(HerramientasSsh.MensajeSoloLectura(_gestor!.ParsearDestino(destino).Clave, "ssh_exec"));
        }

        // Un alias cuyo HostName no existe en el DNS protege solo su texto: las demás escrituras pasan.
        [Fact]
        public async Task EntradaConUnAliasCuyoHostNameNoExiste_SoloElTexto()
        {
            // Arrange
            HerramientasSsh herramientas = Crear("fantasma");

            // Act
            ResultadoComando otro = await EjecutarHastaTerminar(herramientas, "web");
            NativeSshException alias = (await FluentActions.Awaiting(() => herramientas.EjecutarAsync("fantasma", "touch marca")).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = otro.Estado.Should().Be(EstadoPeticionEnum.Completado, otro.Aviso);
            _ = alias.Message.Should().Be(HerramientasSsh.MensajeSoloLectura("ana@noexiste.ejemplo:22", "ssh_exec"));
        }

        // La entrada es el HostName: el alias que lo resuelve se rechaza (destino resuelto).
        [Fact]
        public async Task EntradaConElHostName_ElAliasSeRechaza()
        {
            // Arrange
            HerramientasSsh herramientas = Crear("10.0.0.5");

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync("prod", "a.txt", "x", overwrite: true)).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be(HerramientasSsh.MensajeSoloLectura("ana@10.0.0.5:22", "ssh_write_file"));
        }

        // Un alias se compara solo como texto: que no sea un nombre del DNS no lo hace fallar cerrado.
        [Fact]
        public async Task AliasQueNoEsUnNombreDelDns_NoFallaCerrado()
        {
            // Arrange
            _dns["otra.ejemplo"] = IPAddress.Parse("10.0.0.9");
            HerramientasSsh herramientas = Crear("otra.ejemplo");

            // Act
            ResultadoComando resultado = await EjecutarHastaTerminar(herramientas, "web");

            // Assert
            _ = resultado.Estado.Should().Be(EstadoPeticionEnum.Completado, resultado.Aviso);
        }

        // La ventana del DNS cambiado: la sesión se abrió (con una lectura) cuando el nombre apuntaba a la máquina protegida;
        // ahora apunta a otra, pero la sesión sigue conectada a la protegida: las escrituras se rechazan, sin enviar nada.
        [Fact]
        public async Task SesionConectadaALaProtegida_TrasCambiarElDns_RechazaLasEscrituras()
        {
            // Arrange
            _dns["ventana.ejemplo"] = IPAddress.Parse("10.0.0.5");
            HerramientasSsh herramientas = Crear("10.0.0.5", isConConfiguracion: false);
            _ = await herramientas.LeerArchivoAsync("ventana.ejemplo", "a.txt");
            _dns["ventana.ejemplo"] = IPAddress.Parse("10.0.0.9");
            int comandosAntes = _fabrica.Ultima!.Comandos.Count;

            // Act
            NativeSshException exec = (await FluentActions.Awaiting(() => herramientas.EjecutarAsync("ventana.ejemplo", "touch marca")).Should().ThrowAsync<NativeSshException>()).Which;
            NativeSshException escritura = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync("ventana.ejemplo", "a.txt", "x", overwrite: true)).Should().ThrowAsync<NativeSshException>()).Which;
            NativeSshException reemplazo = (await FluentActions.Awaiting(() => herramientas.ReemplazarContenidoArchivoAsync("ventana.ejemplo", "a.txt", "a", "b")).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = exec.Message.Should().Be(DestinosSoloLectura.MensajeSoloLectura("ana@ventana.ejemplo:22", "ssh_exec", IPAddress.Parse("10.0.0.5")));
            _ = exec.Message.Should().Contain("está conectada a 10.0.0.5");
            _ = escritura.Message.Should().Be(DestinosSoloLectura.MensajeSoloLectura("ana@ventana.ejemplo:22", "ssh_write_file", IPAddress.Parse("10.0.0.5")));
            _ = reemplazo.Message.Should().Be(DestinosSoloLectura.MensajeSoloLectura("ana@ventana.ejemplo:22", "ssh_replace_file_content", IPAddress.Parse("10.0.0.5")));
            _ = _fabrica.Ultima!.Comandos.Count.Should().Be(comandosAntes, "no se envió nada al shell");
        }

        // Al revés: la sesión está conectada a una máquina no protegida y el nombre pasa a apuntar a la protegida: también se
        // rechaza (lo más restrictivo: el destino resuelto ahora).
        [Fact]
        public async Task SesionConectadaALaNoProtegida_TrasCambiarElDnsALaProtegida_RechazaLasEscrituras()
        {
            // Arrange
            _dns["ventana.ejemplo"] = IPAddress.Parse("10.0.0.9");
            HerramientasSsh herramientas = Crear("10.0.0.5", isConConfiguracion: false);
            _ = await herramientas.LeerArchivoAsync("ventana.ejemplo", "a.txt");
            _dns["ventana.ejemplo"] = IPAddress.Parse("10.0.0.5");

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EjecutarAsync("ventana.ejemplo", "touch marca")).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be(HerramientasSsh.MensajeSoloLectura("ana@ventana.ejemplo:22", "ssh_exec"));
        }

        // La sesión se abre después de la comprobación de la herramienta: el nombre resolvía a una máquina no protegida al
        // comprobar y a la protegida al conectar. La sesión vuelve a comparar la dirección de su conexión antes de enviar la
        // escritura: no la envía y la petición termina en Error con el rechazo.
        [Fact]
        public async Task ConexionHechaTrasLaComprobacion_ALaProtegida_NoEnviaLaEscritura()
        {
            // Arrange
            _dns["ventana.ejemplo"] = IPAddress.Parse("10.0.0.9");
            _fabrica.DireccionRemota = _ => IPAddress.Parse("10.0.0.5");
            HerramientasSsh herramientas = Crear("10.0.0.5", isConConfiguracion: false);

            // Act
            ResultadoComando resultado = await EjecutarHastaTerminar(herramientas, "ventana.ejemplo");
            NativeSshException escritura = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync("ventana.ejemplo", "b.txt", "x", overwrite: true)).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = resultado.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = resultado.Aviso.Should().Contain(DestinosSoloLectura.MensajeSoloLectura("ana@ventana.ejemplo:22", "ssh_exec", IPAddress.Parse("10.0.0.5")));
            _ = escritura.Message.Should().Contain("está conectada a 10.0.0.5");
            _ = ComandosEnviados("touch marca").Should().Be(0);
            _ = ComandosEnviados("b.txt").Should().Be(0);
        }

        // Lo mismo con ssh_write_file como primera llamada: la sesión no la envía y el error es el rechazo (no «no se pudo
        // abrir»).
        [Fact]
        public async Task EscrituraConConexionHechaTrasLaComprobacion_ALaProtegida_NoSeEnvia()
        {
            // Arrange
            _dns["ventana.ejemplo"] = IPAddress.Parse("10.0.0.9");
            _fabrica.DireccionRemota = _ => IPAddress.Parse("10.0.0.5");
            HerramientasSsh herramientas = Crear("10.0.0.5", isConConfiguracion: false);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync("ventana.ejemplo", "b.txt", "x", overwrite: true)).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be($"La escritura en 'b.txt' no se hizo y 'b.txt' no se ha modificado. {DestinosSoloLectura.MensajeSoloLectura("ana@ventana.ejemplo:22", "ssh_write_file", IPAddress.Parse("10.0.0.5"))}");
            _ = ComandosEnviados("b.txt").Should().Be(0);
        }

        // La entrada es un nombre que resuelve a la dirección conectada: la sesión sigue protegida aunque el destino apunte ya
        // a otra máquina.
        [Fact]
        public async Task EntradaPorNombre_QueResuelveALaDireccionConectada_RechazaTrasCambiarElDns()
        {
            // Arrange
            _dns["prod.ejemplo"] = IPAddress.Parse("10.0.0.5");
            _dns["ventana.ejemplo"] = IPAddress.Parse("10.0.0.5");
            HerramientasSsh herramientas = Crear("prod.ejemplo", isConConfiguracion: false);
            _ = await herramientas.LeerArchivoAsync("ventana.ejemplo", "a.txt");
            _dns["ventana.ejemplo"] = IPAddress.Parse("10.0.0.9");

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EjecutarAsync("ventana.ejemplo", "touch marca")).Should().ThrowAsync<NativeSshException>()).Which;
            ResultadoEstado estado = herramientas.Estado();

            // Assert
            _ = error.Message.Should().Be(DestinosSoloLectura.MensajeSoloLectura("ana@ventana.ejemplo:22", "ssh_exec", IPAddress.Parse("10.0.0.5")));
            _ = estado.Sesiones.Should().ContainSingle().Which.IsSoloLectura.Should().BeTrue();
        }

        // ssh_set_cwd no es una escritura: la sesión no la rechaza aunque la conexión vaya a la protegida.
        [Fact]
        public async Task SetCwd_ConLaConexionEnLaProtegida_Funciona()
        {
            // Arrange
            _dns["ventana.ejemplo"] = IPAddress.Parse("10.0.0.5");
            HerramientasSsh herramientas = Crear("10.0.0.5", isConConfiguracion: false);
            _ = await herramientas.LeerArchivoAsync("ventana.ejemplo", "a.txt");

            // Act
            ResultadoDirectorioTrabajo cwd = await herramientas.FijarDirectorioAsync("ventana.ejemplo", "/srv");

            // Assert
            _ = cwd.Directorio.Should().Be("/srv");
        }

        // ssh_input al comando en curso de una sesión conectada a la protegida (lanzado antes de configurarla, por el gestor):
        // tras cambiar el DNS, se rechaza por la dirección de la conexión.
        [Fact]
        public async Task Input_SesionConectadaALaProtegida_TrasCambiarElDns_SeRechaza()
        {
            // Arrange
            _dns["ventana.ejemplo"] = IPAddress.Parse("10.0.0.5");
            HerramientasSsh herramientas = Crear("10.0.0.5", isConConfiguracion: false);
            Peticion peticion = _gestor!.Ejecutar("ventana.ejemplo", "pregunta", TimeSpan.FromSeconds(30));
            await Task.Delay(300);
            _dns["ventana.ejemplo"] = IPAddress.Parse("10.0.0.9");

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EnviarEntradaAsync(peticion.IdTexto, "si")).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be(DestinosSoloLectura.MensajeSoloLectura("ana@ventana.ejemplo:22", "ssh_input", IPAddress.Parse("10.0.0.5")));
        }

        // Conectada sin dirección conocida y con entradas que habría que comparar: falla cerrado, y el mensaje lo dice.
        [Fact]
        public async Task SesionConectadaSinDireccionConocida_FallaCerrado()
        {
            // Arrange
            _dns["ventana.ejemplo"] = IPAddress.Parse("10.0.0.9");
            _fabrica.DireccionRemota = _ => null;
            HerramientasSsh herramientas = Crear("10.0.0.5", isConConfiguracion: false);
            _ = await herramientas.LeerArchivoAsync("ventana.ejemplo", "a.txt");

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EjecutarAsync("ventana.ejemplo", "touch marca")).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be(DestinosSoloLectura.MensajeNoComprobado("ana@ventana.ejemplo:22", "ssh_exec", null));
            _ = error.Message.Should().Contain("no se sabe a qué dirección está conectada su sesión");
        }

        // Una sesión conectada a una máquina no protegida, con el DNS igual: las escrituras pasan (sin falsos rechazos).
        [Fact]
        public async Task SesionConectadaALaNoProtegida_Escribe()
        {
            // Arrange
            _dns["ventana.ejemplo"] = IPAddress.Parse("10.0.0.9");
            HerramientasSsh herramientas = Crear("10.0.0.5", isConConfiguracion: false);
            _ = await herramientas.LeerArchivoAsync("ventana.ejemplo", "a.txt");

            // Act
            ResultadoComando resultado = await EjecutarHastaTerminar(herramientas, "ventana.ejemplo");

            // Assert
            _ = resultado.Estado.Should().Be(EstadoPeticionEnum.Completado, resultado.Aviso);
            _ = ComandosEnviados("touch marca").Should().Be(1);
        }

        // ssh_status con el mismo criterio: la sesión conectada a la protegida se marca aunque el nombre apunte ya a otra; con
        // el alias en el filtro, el alias cuenta como texto pedido.
        [Fact]
        public async Task Estado_MarcaPorLaDireccionConectadaYPorElAlias()
        {
            // Arrange
            _dns["ventana.ejemplo"] = IPAddress.Parse("10.0.0.5");
            _dns["prod"] = IPAddress.Parse("10.0.0.99");
            HerramientasSsh herramientas = Crear("10.0.0.5, prod");
            _ = await herramientas.LeerArchivoAsync("ventana.ejemplo", "a.txt");
            _dns["ventana.ejemplo"] = IPAddress.Parse("10.0.0.9");

            // Act
            ResultadoEstado ventana = herramientas.Estado("ventana.ejemplo");
            ResultadoEstado alias = herramientas.Estado("prod");

            // Assert
            _ = ventana.Sesiones.Should().ContainSingle().Which.IsSoloLectura.Should().BeTrue();
            _ = alias.Sesiones.Should().BeEmpty("no hay sesión de prod");
            _ = _gestor!.ListarSesiones().Should().ContainSingle();
        }

        // ssh_status con filtro usuario@servidor:puerto calcula isSoloLectura como una escritura a ese mismo texto en esa sesión
        // (texto, destino resuelto y dirección conectada), sin los alias con que se usó la sesión; con un alias en el filtro,
        // con ese alias; sin filtro, con todos. La sesión se abrió con prod (entonces 10.0.0.5); después la configuración
        // lleva prod a 10.0.0.9 y staging a 10.0.0.5, y la sesión se usa también con staging.
        [Fact]
        public async Task Estado_FiltroPorServidorReal_DiceLoQueHaceLaEscritura()
        {
            // Arrange
            HerramientasSsh herramientas = Crear("prod");
            _ = await herramientas.LeerArchivoAsync("prod", "a.txt");
            File.WriteAllText(Path.Combine(_directorio, "config"), "Host prod\n  HostName 10.0.0.9\nHost staging\n  HostName 10.0.0.5\nHost *\n  User ana\n");
            _ = await herramientas.LeerArchivoAsync("staging", "a.txt");

            // Act
            ResultadoEstado porServidor = herramientas.Estado("ana@10.0.0.5:22");
            ResultadoEstado porStaging = herramientas.Estado("staging");
            ResultadoEstado sinFiltro = herramientas.Estado();
            ResultadoComando escritura = await EjecutarHastaTerminar(herramientas, "ana@10.0.0.5:22");
            ResultadoComando escrituraStaging = await EjecutarHastaTerminar(herramientas, "staging");

            // Assert
            _ = porServidor.Sesiones.Should().ContainSingle().Which.IsSoloLectura.Should().BeFalse();
            _ = porStaging.Sesiones.Should().ContainSingle().Which.IsSoloLectura.Should().BeFalse();
            EstadoSesion sesion = sinFiltro.Sesiones.Should().ContainSingle().Subject;
            _ = sesion.Alias.Should().Equal("prod", "staging");
            _ = sesion.IsSoloLectura.Should().BeTrue("sin filtro cuentan los alias con que se usó la sesión: prod es de solo lectura por su texto");
            _ = escritura.Estado.Should().Be(EstadoPeticionEnum.Completado, escritura.Aviso);
            _ = escrituraStaging.Estado.Should().Be(EstadoPeticionEnum.Completado, escrituraStaging.Aviso);
        }

        // Al revés (la sonda de la auditoría con la garantía nueva): filtrado por el HostName de la entrada, ssh_status dice
        // true y la escritura a ese texto se rechaza.
        [Fact]
        public async Task Estado_FiltroPorElHostNameDeLaEntrada_DiceLoQueHaceLaEscritura()
        {
            // Arrange
            _dns["prod"] = IPAddress.Parse("10.0.0.99");
            HerramientasSsh herramientas = Crear("prod");
            _ = await herramientas.FijarDirectorioAsync("prod", "/tmp");

            // Act
            ResultadoEstado estado = herramientas.Estado("ana@10.0.0.5:22");
            Func<Task> escribir = () => herramientas.EjecutarAsync("ana@10.0.0.5:22", "touch marca");

            // Assert
            _ = estado.Sesiones.Should().ContainSingle().Which.IsSoloLectura.Should().BeTrue();
            _ = (await escribir.Should().ThrowAsync<NativeSshException>()).Which.Message.Should().Be(HerramientasSsh.MensajeSoloLectura("ana@10.0.0.5:22", "ssh_exec"));
        }

        // ssh_status del alias con sesión: true por el texto pedido, aunque su HostName no esté en la lista.
        [Fact]
        public async Task Estado_AliasDeLaLista_SuSesionSeMarca()
        {
            // Arrange
            _dns["prod"] = IPAddress.Parse("10.0.0.99");
            HerramientasSsh herramientas = Crear("prod");
            _ = await herramientas.LeerArchivoAsync("prod", "a.txt");

            // Act
            ResultadoEstado conFiltro = herramientas.Estado("prod");
            ResultadoEstado sinFiltro = herramientas.Estado();

            // Assert
            EstadoSesion sesion = conFiltro.Sesiones.Should().ContainSingle().Subject;
            _ = sesion.IsSoloLectura.Should().BeTrue();
            _ = sesion.Destino.Should().Be("ana@10.0.0.5:22");
            _ = sesion.Alias.Should().Equal("prod");
            _ = conFiltro.SoloLectura.Should().Equal("prod");
            _ = conFiltro.Aviso.Should().BeNull();
            _ = sinFiltro.Sesiones.Should().ContainSingle().Which.IsSoloLectura.Should().BeTrue();
        }
    }
}
