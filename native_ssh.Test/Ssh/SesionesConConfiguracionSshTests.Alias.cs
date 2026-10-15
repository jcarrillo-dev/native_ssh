using System.Linq;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Ssh
{
    // ssh_disconnect y ssh_status con un alias: solo las sesiones de ese alias (comparado como ssh, distinguiendo mayúsculas)
    // con el mismo usuario y puerto resueltos. Un destino que nunca se usó (otro usuario u otro puerto sobre el alias) no
    // cierra ni muestra la sesión de otro. El mensaje nombra lo que se cerró de verdad.
    public sealed partial class SesionesConConfiguracionSshTests
    {
        // El tope documentado en el README y en el mensaje (no GestorSesiones.MaxAliasPorSesion: la prueba fija el valor).
        private const int TopeAlias = 64;

        private void EscribirProd(string servidor = "127.0.0.1") =>
            File.WriteAllText(_configuracion, $"Host prod\n  HostName {servidor}\n  User deploy\n  Port 2222\n  IdentityFile /k/a\n");

        // Muchos alias a la misma sesión (mismo destino y claves); el último puede cambiar de HostName.
        private void EscribirAlias(int cantidad, bool isUltimoCambiado = false) =>
            File.WriteAllText(_configuracion, string.Concat(Enumerable.Range(1, cantidad).Select(i =>
                $"Host a{i:000}\n  HostName {(i == cantidad && isUltimoCambiado ? "localhost" : "127.0.0.1")}\n  User deploy\n  Port 2222\n  IdentityFile /k/a\n")));

        [Theory]
        [InlineData("root@prod")]
        [InlineData("prod:2223")]
        public async Task DesconectarOtroUsuarioOPuertoSobreElAlias_NoCierraLaSesionDelAlias(string otro)
        {
            // Arrange
            EscribirProd();
            ResultadoComando largo = await Herramientas.EjecutarAsync("prod", "largo", esperarSegundos: 0).ComoComando();

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync(otro);

            // Assert
            _ = resultado.IsHecho.Should().BeFalse(resultado.Mensaje);
            _ = _gestor.ListarSesiones().Should().ContainSingle().Which.Destino.Clave.Should().Be("deploy@127.0.0.1:2222");
            _ = _gestor.BuscarPeticion(largo.Id).Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
        }

        // La de deploy (la del alias con su User) sigue abierta: la respuesta la nombra con el destino que la cierra.
        [Fact]
        public async Task DesconectarUnUsuarioDelAlias_CierraSoloLaDeEseUsuarioYLaNombra()
        {
            // Arrange
            EscribirProd();
            await Ejecutar("prod", "root@prod");

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync("root@prod");

            // Assert
            _ = resultado.Destino.Should().Be("root@127.0.0.1:2222");
            _ = resultado.Mensaje.Should().StartWith($"Conexión cerrada: root@127.0.0.1:2222 (IdentityFile {IdentidadA}). ")
                .And.Contain("deploy@127.0.0.1:2222, que se cierra con deploy@prod:2222");
            _ = _gestor.ListarSesiones().Should().ContainSingle().Which.Destino.Clave.Should().Be("deploy@127.0.0.1:2222");
        }

        [Fact]
        public async Task EstadoDeOtroUsuarioDelAlias_NoMuestraLaSesionNiLasPeticionesDelAlias()
        {
            // Arrange
            EscribirProd();
            await Ejecutar("prod");

            // Act
            ResultadoEstado estado = Herramientas.Estado("root@prod");

            // Assert
            _ = estado.Sesiones.Should().BeEmpty();
            _ = estado.Peticiones.Should().BeEmpty();
        }

        // Como ssh, 'Host web' y 'Host Web' son entradas distintas.
        [Fact]
        public async Task AliasQueSoloDifierenEnMayusculas_SonDistintos()
        {
            // Arrange
            File.WriteAllText(_configuracion,
                "Host web\n  HostName 127.0.0.1\n  User deploy\n  Port 2222\n  IdentityFile /k/a\n" +
                "Host Web\n  HostName localhost\n  User deploy\n  Port 2222\n  IdentityFile /k/a\n");
            await Ejecutar("web", "Web");

            // Act
            ResultadoEstado estado = Herramientas.Estado("Web");
            ResultadoAccion resultado = await Herramientas.DesconectarAsync("Web");

            // Assert
            _ = estado.Sesiones.Should().ContainSingle().Which.Destino.Should().Be("deploy@localhost:2222");
            _ = estado.Peticiones.Should().ContainSingle().Which.Destino.Should().Be("deploy@localhost:2222");
            _ = resultado.Mensaje.Should().Be($"Conexión cerrada: deploy@localhost:2222 (IdentityFile {IdentidadA}).");
            _ = _gestor.ListarSesiones().Should().ContainSingle().Which.Destino.Clave.Should().Be("deploy@127.0.0.1:2222");
        }

        // El orden de los IdentityFile cuenta (ssh los prueba en orden): a,b y b,a son sesiones distintas.
        [Fact]
        public async Task MismasClavesEnOtroOrden_SonOtraSesion()
        {
            // Arrange
            File.WriteAllText(_configuracion,
                "Host ab\n  HostName 127.0.0.1\n  User deploy\n  Port 2222\n  IdentityFile /k/a\n  IdentityFile /k/b\n" +
                "Host ba\n  HostName 127.0.0.1\n  User deploy\n  Port 2222\n  IdentityFile /k/b\n  IdentityFile /k/a\n");

            // Act
            await Ejecutar("ab", "ba");

            // Assert
            _ = _fabrica.Conexiones.Should().Be(2);
            _ = _gestor.ListarSesiones().Select(s => string.Join(",", s.Destino.Identidades!)).Should().BeEquivalentTo([$"{IdentidadA},{IdentidadB}", $"{IdentidadB},{IdentidadA}"]);
        }

        // ssh_status con un alias cuya configuración cambió con la sesión viva muestra también la sesión y las peticiones de
        // antes del cambio.
        [Fact]
        public async Task EstadoDeUnAliasCuyaConfiguracionCambio_MuestraLasPeticionesDeAntes()
        {
            // Arrange
            EscribirProd();
            await Ejecutar("prod");
            EscribirProd(servidor: "localhost");

            // Act
            ResultadoEstado estado = Herramientas.Estado("prod");

            // Assert
            _ = estado.Sesiones.Should().ContainSingle().Which.Destino.Should().Be("deploy@127.0.0.1:2222");
            _ = estado.Peticiones.Should().ContainSingle().Which.Destino.Should().Be("deploy@127.0.0.1:2222");
        }

        // El destino de la respuesta es el de la sesión que se cerró, no el que resuelve ahora el alias.
        [Fact]
        public async Task DesconectarUnAliasCuyoHostNameCambio_LaRespuestaDaElDestinoCerrado()
        {
            // Arrange
            EscribirProd();
            await Ejecutar("prod");
            EscribirProd(servidor: "localhost");

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync("prod");

            // Assert
            _ = resultado.Destino.Should().Be("deploy@127.0.0.1:2222");
            _ = resultado.Mensaje.Should().Be($"Conexión cerrada: deploy@127.0.0.1:2222 (IdentityFile {IdentidadA}).");
        }

        // Dos sesiones del mismo alias con las mismas claves (antes y después de cambiar su HostName): el mensaje nombra las
        // dos y no dice «una por cada juego de claves».
        [Fact]
        public async Task DesconectarUnAliasConDosSesionesDeLasMismasClaves_NombraLasDos()
        {
            // Arrange
            EscribirProd();
            await Ejecutar("prod");
            EscribirProd(servidor: "localhost");
            await Ejecutar("prod");

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync("prod");

            // Assert
            _ = resultado.IsHecho.Should().BeTrue();
            _ = resultado.Mensaje.Should().NotContain("juego de claves").And.Contain("Se cerraron 2 conexiones")
                .And.Contain($"deploy@127.0.0.1:2222 (IdentityFile {IdentidadA})").And.Contain($"deploy@localhost:2222 (IdentityFile {IdentidadA})");
            _ = _gestor.ListarSesiones().Should().BeEmpty();
        }

        // Una sesión recuerda hasta 64 alias: el 64.º, con su HostName cambiado, la sigue encontrando.
        [Fact]
        public async Task AliasSexagesimoCuartoCuyaConfiguracionCambio_CierraLaSesion()
        {
            // Arrange
            EscribirAlias(TopeAlias);
            await Ejecutar([.. Enumerable.Range(1, TopeAlias).Select(i => $"a{i:000}")]);
            EscribirAlias(TopeAlias, isUltimoCambiado: true);

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync($"a{TopeAlias:000}");

            // Assert
            _ = resultado.IsHecho.Should().BeTrue(resultado.Mensaje);
            _ = _gestor.ListarSesiones().Should().BeEmpty();
        }

        // Pasado el tope, el alias ya no se recuerda: ssh_disconnect no dice solo «No había conexión», sino que remite a
        // ssh_status y al destino real que la cierra (aquí solo a ella).
        [Fact]
        public async Task AliasPasadoElTopeCuyaConfiguracionCambio_ElMensajeRemiteASshStatus()
        {
            // Arrange
            int cantidad = TopeAlias + 1;
            EscribirAlias(cantidad);
            await Ejecutar([.. Enumerable.Range(1, cantidad).Select(i => $"a{i:000}")]);
            EscribirAlias(cantidad, isUltimoCambiado: true);

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync($"a{cantidad:000}");

            // Assert
            _ = resultado.IsHecho.Should().BeFalse();
            _ = resultado.Mensaje.Should().Contain("ssh_status").And.Contain($"{TopeAlias} alias").And.Contain("deploy@127.0.0.1:2222, que se cierra con deploy@127.0.0.1:2222");
            _ = _gestor.ListarSesiones().Should().ContainSingle();
        }

        // Volver a usar un alias que la sesión ya recuerda, con la sesión en el tope, no cuenta como alias olvidado: el mensaje
        // de un alias que no cierra nada no habla del tope.
        [Fact]
        public async Task AliasYaRecordadoConLaSesionEnElTope_NoCuentaComoOlvidado()
        {
            // Arrange
            EscribirAlias(TopeAlias);
            File.AppendAllText(_configuracion, "Host otro\n  HostName 127.0.0.1\n  User nadie\n  Port 2222\n");
            await Ejecutar([.. Enumerable.Range(1, TopeAlias).Select(i => $"a{i:000}"), "a001"]);

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync("otro");

            // Assert
            _ = resultado.IsHecho.Should().BeFalse();
            _ = resultado.Mensaje.Should().NotContain("alias y no guarda más");
        }

        // Si el alias cambió de usuario o de puerto con la sesión viva, ssh_disconnect no cierra la anterior (es otro destino)
        // pero lo dice y da su destino real.
        [Fact]
        public async Task AliasCuyoUsuarioCambio_ElMensajeNombraLaSesionAnterior()
        {
            // Arrange
            EscribirProd();
            await Ejecutar("prod");
            File.WriteAllText(_configuracion, "Host prod\n  HostName 127.0.0.1\n  User otro\n  Port 2222\n  IdentityFile /k/a\n");

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync("prod");

            // Assert
            _ = resultado.IsHecho.Should().BeFalse();
            _ = resultado.Mensaje.Should().Contain("deploy@127.0.0.1:2222").And.Contain("ssh_status");
            _ = _gestor.ListarSesiones().Should().ContainSingle();
        }
    }
}
