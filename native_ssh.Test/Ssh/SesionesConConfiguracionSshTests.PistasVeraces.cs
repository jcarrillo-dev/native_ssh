using System.Linq;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Ssh
{
    // La pista de las sesiones viejas de un alias no promete lo que no puede cumplir: calcula con el criterio de selección de
    // ssh_disconnect qué cerraría de verdad cada destino que sugiere (usuario@alias:puerto y, si todas son del mismo destino,
    // usuario@servidor:puerto) y lo dice; si ninguno las cierra solas, nombra lo demás que cerraría (sesión, claves, alias y
    // comando en curso); si ninguno se puede escribir como destino, lo dice y no sugiere ninguno. Oráculo: lo que anuncia el
    // mensaje es lo que cierra seguirlo.
    public sealed partial class SesionesConConfiguracionSshTests
    {
        private const string SinPistaExacta = "que se cierra con";

        // 'prod' pasa a usar la clave de 'otra' (/k/b) y User root con su sesión vieja (deploy, /k/a) viva: deploy@prod:2222 y
        // deploy@127.0.0.1:2222 cierran también la de 'otra' (misma ClaveSesion, mismo usuario@servidor:puerto). La pista lo
        // dice, con el comando de 'otra', y seguirla cierra lo anunciado.
        [Fact]
        public async Task IdentityFileYUserCambiadosHastaOtraSesion_LaPistaDiceQueNingunoLaCierraSolaYQueCerraria()
        {
            // Arrange
            EscribirProdCambiado(extra: Otra);
            await Ejecutar("prod");
            ResultadoComando largoOtra = await LanzarLargoEnEjecucionAsync("otra");
            File.WriteAllText(_configuracion, "Host prod\n  HostName 127.0.0.1\n  User root\n  Port 2222\n  IdentityFile /k/b\n" + Otra);

            // Act
            ResultadoAccion pista = await Herramientas.DesconectarAsync("prod");
            ResultadoAccion siguiendo = await Herramientas.DesconectarAsync("deploy@prod:2222");

            // Assert
            _ = pista.Mensaje.Should().NotContain(SinPistaExacta)
                .And.Contain("ni deploy@prod:2222 ni deploy@127.0.0.1:2222 la cierran sola")
                .And.Contain($"la cierran sola: los dos cerrarían también deploy@127.0.0.1:2222 (IdentityFile {IdentidadB}; alias otra; comando en curso {largoOtra.Id} «largo»)");
            _ = siguiendo.Mensaje.Should().StartWith("Se cerraron 2 conexiones con deploy@127.0.0.1:2222");
            _ = _gestor.ListarSesiones().Should().BeEmpty();
            _ = _gestor.BuscarPeticion(largoOtra.Id).Estado.Should().Be(EstadoPeticionEnum.Cancelado);
        }

        // 'prod' pasa a 127.0.0.5 (donde 'otra5' tiene una sesión con /k/a) y a User root: deploy@prod:2222 cerraría también la
        // de 'otra5', pero deploy@127.0.0.1:2222 cierra solo la vieja: la pista sugiere ese, y seguirla no toca 'otra5'.
        [Fact]
        public async Task HostNameYUserCambiadosHastaOtraSesion_LaPistaSugiereElServidorReal()
        {
            // Arrange
            const string Otra5 = "Host otra5\n  HostName 127.0.0.5\n  User deploy\n  Port 2222\n  IdentityFile /k/a\n";
            EscribirProdCambiado(extra: Otra5);
            await Ejecutar("prod", "otra5");
            File.WriteAllText(_configuracion, "Host prod\n  HostName 127.0.0.5\n  User root\n  Port 2222\n  IdentityFile /k/a\n" + Otra5);

            // Act
            ResultadoAccion pista = await Herramientas.DesconectarAsync("prod");
            ResultadoAccion siguiendo = await Herramientas.DesconectarAsync("deploy@127.0.0.1:2222");

            // Assert
            _ = pista.Mensaje.Should().Contain("deploy@127.0.0.1:2222, que se cierra con deploy@127.0.0.1:2222").And.NotContain("deploy@prod:2222");
            _ = siguiendo.Mensaje.Should().Be($"Conexión cerrada: deploy@127.0.0.1:2222 (IdentityFile {IdentidadA}).");
            _ = ClavesYIdentidades().Should().ContainSingle().Which.Should().StartWith("deploy@127.0.0.5:2222");
        }

        // El User de la configuración lleva caracteres que un destino escrito no admite: la pista no sugiere un destino que se
        // rechazaría; dice que no se cierra con ellos, por qué, y cuándo la cierra el propio alias.
        [Theory]
        [InlineData("ana@corp")]
        [InlineData("ana+x")]
        public async Task UserQueUnDestinoNoAdmite_LaPistaNoSugiereDestinoYLoDice(string usuario)
        {
            // Arrange
            EscribirProdCambiado("User deploy", $"User {usuario}");
            await Ejecutar("prod");
            EscribirProd();

            // Act
            ResultadoAccion pista = await Herramientas.DesconectarAsync("prod");
            ResultadoEstado estado = Herramientas.Estado("prod");

            // Assert
            _ = pista.Mensaje.Should().NotContain(SinPistaExacta).And.Contain($"{usuario}@127.0.0.1:2222: no se cierra con {usuario}@prod:2222 ni con {usuario}@127.0.0.1:2222 ({usuario}@prod:2222 se rechaza: ")
                .And.Contain($"ssh_disconnect con 'prod' la cierra cuando su configuración vuelva a ese usuario y puerto");
            _ = estado.Aviso.Should().Contain($"no se cierra con {usuario}@prod:2222 ni con");
            _ = _gestor.ListarSesiones().Should().ContainSingle();
        }

        // Una sesión llena olvidó el alias, y el servidor real (la única vía) cierra también la de otro alias con otras claves:
        // la pista lo dice.
        [Fact]
        public async Task SesionEnElTopeCuyoServidorRealCierraOtra_LaPistaLoDice()
        {
            // Arrange
            int cantidad = TopeAlias + 1;
            const string Kb = "Host kb\n  HostName 127.0.0.1\n  User deploy\n  Port 2222\n  IdentityFile /k/b\n";
            EscribirAlias(cantidad);
            File.AppendAllText(_configuracion, Kb);
            await Ejecutar([.. Enumerable.Range(1, cantidad).Select(i => $"a{i:000}"), "kb"]);
            EscribirAlias(cantidad, isUltimoCambiado: true);
            File.AppendAllText(_configuracion, Kb);

            // Act
            ResultadoAccion pista = await Herramientas.DesconectarAsync($"a{cantidad:000}");
            ResultadoAccion siguiendo = await Herramientas.DesconectarAsync("deploy@127.0.0.1:2222");

            // Assert
            _ = pista.Mensaje.Should().Contain($"{TopeAlias} alias").And.NotContain(SinPistaExacta)
                .And.Contain($"deploy@127.0.0.1:2222 no la cierra sola: cerraría también deploy@127.0.0.1:2222 (IdentityFile {IdentidadB}; alias kb)");
            _ = siguiendo.Mensaje.Should().StartWith("Se cerraron 2 conexiones con deploy@127.0.0.1:2222");
        }

        // Con varias sesiones viejas del alias en distintos servidores, el servidor real no es candidato (no las alcanza todas):
        // solo usuario@alias:puerto, que las cierra todas.
        [Fact]
        public async Task SesionesViejasEnDosServidores_SoloSugiereElAlias()
        {
            // Arrange
            EscribirProd();
            await Ejecutar("prod");
            EscribirProd(servidor: "localhost");
            await Ejecutar("prod");
            File.WriteAllText(_configuracion, "Host prod\n  HostName localhost\n  User root\n  Port 2222\n  IdentityFile /k/a\n");

            // Act
            ResultadoAccion pista = await Herramientas.DesconectarAsync("prod");
            ResultadoAccion siguiendo = await Herramientas.DesconectarAsync("deploy@prod:2222");

            // Assert
            _ = pista.Mensaje.Should().Contain("deploy@127.0.0.1:2222, deploy@localhost:2222, que se cierra con deploy@prod:2222");
            _ = siguiendo.IsHecho.Should().BeTrue(siguiendo.Mensaje);
            _ = _gestor.ListarSesiones().Should().BeEmpty();
        }

        // Una sesión llena en un servidor IPv6 que olvidó el alias: su servidor real va entre corchetes en la pista (como
        // destino escrito; DestinoSsh.Clave no los lleva), y seguirla la cierra.
        [Fact]
        public async Task SesionEnElTopeConServidorIpv6_LaPistaLoPoneEntreCorchetes()
        {
            // Arrange
            int cantidad = TopeAlias + 1;
            string config = string.Concat(Enumerable.Range(1, cantidad).Select(i => $"Host a{i:000}\n  HostName ::1\n  User deploy\n  Port 2222\n  IdentityFile /k/a\n"));
            File.WriteAllText(_configuracion, config);
            await Ejecutar([.. Enumerable.Range(1, cantidad).Select(i => $"a{i:000}")]);
            File.WriteAllText(_configuracion, config.Replace($"Host a{cantidad:000}\n  HostName ::1", $"Host a{cantidad:000}\n  HostName 127.0.0.1", StringComparison.Ordinal));

            // Act
            ResultadoAccion pista = await Herramientas.DesconectarAsync($"a{cantidad:000}");
            ResultadoAccion siguiendo = await Herramientas.DesconectarAsync("deploy@[::1]:2222");

            // Assert
            _ = pista.Mensaje.Should().Contain("deploy@::1:2222, que se cierra con deploy@[::1]:2222");
            _ = siguiendo.IsHecho.Should().BeTrue(siguiendo.Mensaje);
            _ = _gestor.ListarSesiones().Should().BeEmpty();
        }

        // Una sesión llena olvidó el alias y su servidor real pasa a ser un Host de la configuración (127.0.0.1 → 127.0.0.9):
        // deploy@127.0.0.1:2222 ya no la alcanza. La pista no lo sugiere como si la cerrara: dice que no la alcanza.
        [Fact]
        public async Task SesionEnElTopeCuyoServidorRealAhoraEsUnAlias_LaPistaDiceQueNoLaAlcanza()
        {
            // Arrange
            int cantidad = TopeAlias + 1;
            EscribirAlias(cantidad);
            await Ejecutar([.. Enumerable.Range(1, cantidad).Select(i => $"a{i:000}")]);
            EscribirAlias(cantidad, isUltimoCambiado: true);
            File.AppendAllText(_configuracion, "Host 127.0.0.1\n  HostName 127.0.0.9\n");

            // Act
            ResultadoAccion pista = await Herramientas.DesconectarAsync($"a{cantidad:000}");

            // Assert
            _ = pista.Mensaje.Should().NotContain(SinPistaExacta)
                .And.Contain("deploy@127.0.0.1:2222: no se cierra con deploy@127.0.0.1:2222 (deploy@127.0.0.1:2222 no la alcanza)");
            _ = _gestor.ListarSesiones().Should().ContainSingle();
        }
    }
}
