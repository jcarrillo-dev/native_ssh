using System.Linq;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Ssh
{
    // La pista de las sesiones de un alias con otro usuario o puerto (su User o Port cambió con la sesión viva, o se abrieron
    // con usuario@alias o alias:puerto): ssh_disconnect y ssh_status con el alias la dan siempre que existan, aunque se cierre
    // otra, sin repetir destinos, y sugieren usuario@alias:puerto cuando cierra solo esas (usuario@servidor:puerto cerraría
    // también las de otros alias; los casos en que tampoco el alias basta, en .PistasVeraces). El aviso del tope de alias solo
    // sale si alguna sesión olvidó ese alias.
    public sealed partial class SesionesConConfiguracionSshTests
    {
        private const string Otra = "Host otra\n  HostName 127.0.0.1\n  User deploy\n  Port 2222\n  IdentityFile /k/b\n";

        // 'prod' (como EscribirProd) con un cambio opcional y otras entradas detrás.
        private void EscribirProdCambiado(string? antes = null, string? despues = null, string extra = "")
        {
            string prod = "Host prod\n  HostName 127.0.0.1\n  User deploy\n  Port 2222\n  IdentityFile /k/a\n";
            File.WriteAllText(_configuracion, (antes is null ? prod : prod.Replace(antes, despues, StringComparison.Ordinal)) + extra);
        }

        private IEnumerable<string> ClavesYIdentidades() =>
            _gestor.ListarSesiones().Select(s => $"{s.Destino.Clave}|{string.Join(",", s.Destino.Identidades ?? [])}");

        // Tras cambiar el User de 'prod' con su sesión viva, el cliente vuelve a usar 'prod' (sesión nueva de root): ssh_disconnect
        // prod cierra la de root y dice que sigue la de deploy, con su comando.
        [Fact]
        public async Task CambioDeUserYSesionNueva_DisconnectDelAliasNombraLaAnterior()
        {
            // Arrange
            EscribirProd();
            ResultadoComando largo = await LanzarLargoEnEjecucionAsync("prod");
            EscribirProdCambiado("User deploy", "User root");
            await Ejecutar("prod");

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync("prod");

            // Assert
            _ = resultado.IsHecho.Should().BeTrue(resultado.Mensaje);
            _ = resultado.Mensaje.Should().StartWith($"Conexión cerrada: root@127.0.0.1:2222 (IdentityFile {IdentidadA}).")
                .And.Contain($"deploy@127.0.0.1:2222 (comando en curso {largo.Id} «largo»), que se cierra con deploy@prod:2222");
            _ = _gestor.BuscarPeticion(largo.Id).Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
            _ = ClavesYIdentidades().Should().ContainSingle().Which.Should().Be($"deploy@127.0.0.1:2222|{IdentidadA}");
        }

        // La pista sugiere usuario@alias:puerto y no el servidor real: seguirla cierra solo la sesión vieja de 'prod', no la de
        // 'otra' (mismo usuario@servidor:puerto, otras claves).
        [Theory]
        [InlineData("User deploy", "User root")]
        [InlineData("Port 2222", "Port 2223")]
        public async Task SeguirLaPistaDelMensaje_CierraSoloLaSesionViejaDelAlias(string antes, string despues)
        {
            // Arrange
            EscribirProdCambiado(extra: Otra);
            await Ejecutar("prod", "otra");
            EscribirProdCambiado(antes, despues, Otra);
            ResultadoAccion pista = await Herramientas.DesconectarAsync("prod");

            // Act
            ResultadoAccion siguiendo = await Herramientas.DesconectarAsync("deploy@prod:2222");

            // Assert
            _ = pista.IsHecho.Should().BeFalse();
            _ = pista.Mensaje.Should().Contain("deploy@127.0.0.1:2222").And.Contain("deploy@prod:2222").And.NotContain("usuario@servidor:puerto");
            _ = siguiendo.Mensaje.Should().Be($"Conexión cerrada: deploy@127.0.0.1:2222 (IdentityFile {IdentidadA}).");
            _ = ClavesYIdentidades().Should().ContainSingle().Which.Should().EndWith(IdentidadB);
        }

        // ssh_status con el alias también nombra las sesiones del alias para otro usuario o puerto, con cómo cerrarlas.
        [Fact]
        public async Task EstadoDelAliasConSesionDeOtroUsuario_LaNombraEnElAviso()
        {
            // Arrange
            EscribirProd();
            await Ejecutar("prod");
            EscribirProdCambiado("User deploy", "User root");
            await Ejecutar("prod");

            // Act
            ResultadoEstado estado = Herramientas.Estado("prod");

            // Assert
            _ = estado.Sesiones.Should().ContainSingle().Which.Destino.Should().Be("root@127.0.0.1:2222");
            _ = estado.Aviso.Should().Contain("deploy@127.0.0.1:2222").And.Contain("deploy@prod:2222");
        }

        // Sin sesiones del alias para otro usuario o puerto, ssh_status no lleva aviso.
        [Fact]
        public async Task EstadoDelAliasSinOtrasSesiones_NoLlevaAviso()
        {
            // Arrange
            EscribirProd();
            await Ejecutar("prod");

            // Act
            ResultadoEstado estado = Herramientas.Estado("prod");

            // Assert
            _ = estado.Sesiones.Should().ContainSingle();
            _ = estado.Aviso.Should().BeNull();
        }

        // Alias en mayúsculas sin Host propio: ssh lo toma como el servidor real (en minúsculas), que no es 127.0.0.1. Los
        // usuarios distinguen mayúsculas: Deploy@prod no es deploy@prod.
        [Theory]
        [InlineData("PROD")]
        [InlineData("Prod")]
        [InlineData("Deploy@prod")]
        public async Task MayusculasFrenteAHostEnMinusculas_NoAlcanzanLaSesion(string destino)
        {
            // Arrange
            EscribirProd();
            await Ejecutar("prod");

            // Act
            ResultadoEstado estado = Herramientas.Estado(destino);
            ResultadoAccion resultado = await Herramientas.DesconectarAsync(destino);

            // Assert
            _ = estado.Sesiones.Should().BeEmpty();
            _ = estado.Peticiones.Should().BeEmpty();
            _ = resultado.IsHecho.Should().BeFalse();
            _ = _gestor.ListarSesiones().Should().ContainSingle();
        }

        // Un servidor real escrito en mayúsculas sigue siendo el servidor real (ssh devuelve el HostName en minúsculas): cierra
        // todas las sesiones de ese destino, sean de las claves que sean.
        [Fact]
        public async Task ServidorRealEnMayusculas_CierraTodasLasSesionesDeEseDestino()
        {
            // Arrange
            File.WriteAllText(_configuracion,
                "Host ka\n  HostName localhost\n  User deploy\n  Port 2222\n  IdentityFile /k/a\n" +
                "Host kb\n  HostName localhost\n  User deploy\n  Port 2222\n  IdentityFile /k/b\n");
            await Ejecutar("ka", "kb");

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync("deploy@LOCALHOST:2222");

            // Assert
            _ = resultado.Mensaje.Should().StartWith("Se cerraron 2 conexiones con deploy@localhost:2222");
            _ = _gestor.ListarSesiones().Should().BeEmpty();
        }

        // Una sesión llegó al tope de alias (y olvidó uno) y el cliente pide ssh_disconnect de un alias que nunca usó, a otro
        // servidor: el mensaje no le habla del tope.
        [Fact]
        public async Task TopeDeOtraSesion_NoSeMencionaParaUnAliasNuncaUsado()
        {
            // Arrange
            int cantidad = TopeAlias + 1;
            EscribirAlias(cantidad);
            File.AppendAllText(_configuracion, "Host nuevo\n  HostName 127.0.0.2\n  User deploy\n  Port 2222\n");
            await Ejecutar([.. Enumerable.Range(1, cantidad).Select(i => $"a{i:000}")]);

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync("nuevo");

            // Assert
            _ = resultado.IsHecho.Should().BeFalse();
            _ = resultado.Mensaje.Should().NotContain($"{TopeAlias} alias");
        }

        // Un alias que la sesión llena no guardó pero que resuelve a su destino y claves la alcanza: ssh_status la muestra y
        // no avisa del tope (la sesión no se ha perdido).
        [Fact]
        public async Task AliasOlvidadoQueAunResuelveALaSesion_EstadoLaMuestraSinAvisoDelTope()
        {
            // Arrange
            int cantidad = TopeAlias + 1;
            EscribirAlias(cantidad);
            await Ejecutar([.. Enumerable.Range(1, cantidad).Select(i => $"a{i:000}")]);

            // Act
            ResultadoEstado estado = Herramientas.Estado($"a{cantidad:000}");

            // Assert
            _ = estado.Sesiones.Should().ContainSingle();
            _ = estado.Aviso.Should().BeNull();
        }

        // Pasadas también las 64 anotaciones de alias olvidados, el aviso del tope vale para cualquier alias con el mismo
        // usuario y puerto que la sesión llena (no para uno con otro usuario).
        [Fact]
        public async Task AnotacionesDeAliasOlvidadosDesbordadas_ElAvisoDelTopeSigueSaliendo()
        {
            // Arrange
            int cantidad = (2 * TopeAlias) + 1;
            EscribirAlias(cantidad);
            await Ejecutar([.. Enumerable.Range(1, cantidad).Select(i => $"a{i:000}")]);
            EscribirAlias(cantidad, isUltimoCambiado: true);
            File.AppendAllText(_configuracion, "Host otro\n  HostName 127.0.0.3\n  User nadie\n  Port 2222\n");

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync($"a{cantidad:000}");
            ResultadoAccion deOtroUsuario = await Herramientas.DesconectarAsync("otro");

            // Assert
            _ = resultado.IsHecho.Should().BeFalse();
            _ = resultado.Mensaje.Should().Contain($"{TopeAlias} alias").And.Contain("deploy@127.0.0.1:2222");
            _ = deOtroUsuario.Mensaje.Should().NotContain($"{TopeAlias} alias");
        }

        // Un alias IPv6 ('Host ::1'): la pista lo pone entre corchetes, y seguirla cierra la sesión vieja.
        [Fact]
        public async Task AliasIpv6_LaPistaLoPoneEntreCorchetes()
        {
            // Arrange
            File.WriteAllText(_configuracion, "Host ::1\n  HostName 127.0.0.1\n  User deploy\n  Port 2222\n  IdentityFile /k/a\n");
            await Ejecutar("[::1]");
            File.WriteAllText(_configuracion, "Host ::1\n  HostName 127.0.0.1\n  User root\n  Port 2222\n  IdentityFile /k/a\n");
            ResultadoAccion pista = await Herramientas.DesconectarAsync("[::1]");

            // Act
            ResultadoAccion siguiendo = await Herramientas.DesconectarAsync("deploy@[::1]:2222");

            // Assert
            _ = pista.Mensaje.Should().Contain("que se cierra con deploy@[::1]:2222");
            _ = siguiendo.IsHecho.Should().BeTrue(siguiendo.Mensaje);
            _ = _gestor.ListarSesiones().Should().BeEmpty();
        }

        // Dos sesiones viejas del alias del mismo destino (cambió su IdentityFile y luego su User): la pista no repite el
        // destino.
        [Fact]
        public async Task PistaConDosSesionesDelMismoDestino_NoRepiteElDestino()
        {
            // Arrange
            EscribirProd();
            await Ejecutar("prod");
            EscribirProdCambiado("/k/a", "/k/b");
            await Ejecutar("prod");
            EscribirProdCambiado("User deploy", "User root");

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync("prod");

            // Assert
            _ = (resultado.Mensaje.Split("deploy@127.0.0.1:2222").Length - 1).Should().Be(1, resultado.Mensaje);
            _ = (resultado.Mensaje.Split("deploy@prod:2222").Length - 1).Should().Be(1, resultado.Mensaje);
        }

        // Sesión abierta con el servidor real y las mismas claves que el alias (sin IdentityFile: las de native_ssh): es la
        // sesión del alias (comparten sesión), aunque nunca se usara con él; ssh_disconnect del alias la cierra.
        [Fact]
        public async Task SesionDelServidorRealConLasMismasClaves_LaCierraElAlias()
        {
            // Arrange
            File.WriteAllText(_configuracion, "Host sinclave\n  HostName 127.0.0.1\n  User deploy\n  Port 2222\n");
            await Ejecutar("deploy@127.0.0.1:2222");

            // Act
            ResultadoEstado estado = Herramientas.Estado("sinclave");
            ResultadoAccion resultado = await Herramientas.DesconectarAsync("sinclave");

            // Assert
            _ = estado.Sesiones.Should().ContainSingle();
            _ = resultado.IsHecho.Should().BeTrue(resultado.Mensaje);
            _ = _gestor.ListarSesiones().Should().BeEmpty();
        }

        // La pista solo nombra sesiones abiertas con ese alias: la de 'otra' no aparece.
        [Fact]
        public async Task LaPistaNoNombraSesionesDeOtrosAlias()
        {
            // Arrange
            EscribirProdCambiado(extra: Otra.Replace("127.0.0.1", "127.0.0.5", StringComparison.Ordinal));
            await Ejecutar("otra");

            // Act
            ResultadoAccion resultado = await Herramientas.DesconectarAsync("root@prod");
            ResultadoEstado estado = Herramientas.Estado("root@prod");

            // Assert
            _ = resultado.Mensaje.Should().NotContain("127.0.0.5");
            _ = estado.Aviso.Should().BeNull();
        }
    }
}
