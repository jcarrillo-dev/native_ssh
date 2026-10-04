using FluentAssertions;
using System.Text;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Ssh;

namespace NativeSsh.Test.Integracion
{
    [Collection(ColeccionSshd.Nombre)]
    [Trait("Categoria", "Integracion")]
    public class SesionShellIntegracionTests(SshdLocal sshd)
    {
        private static readonly TimeSpan Limite = TimeSpan.FromSeconds(30);

        private async Task<Peticion> EjecutarAsync(SesionShell sesion, string comando, double timeoutSegundos = 15)
        {
            Peticion peticion = new(sshd.Destino, comando, TimeSpan.FromSeconds(timeoutSegundos), 4 << 20, TimeProvider.System);
            sesion.Encolar(peticion);
            await peticion.Finalizacion.WaitAsync(Limite);
            return peticion;
        }

        private static string Texto(Peticion peticion)
        {
            byte[] destino = new byte[(int)peticion.Salida.Longitud];
            int copiados = peticion.Salida.Leer(0, destino).Copiados;
            return Encoding.UTF8.GetString(destino, 0, copiados);
        }

        [Fact]
        public async Task Comando_SalidaLimpiaYCodigo()
        {
            // Arrange
            await using SesionShell sesion = sshd.Sesion();

            // Act
            Peticion eco = await EjecutarAsync(sesion, "echo 'hola ñandú ✓'");
            Peticion falla = await EjecutarAsync(sesion, "echo error >&2; exit_code() { return 7; }; exit_code");

            // Assert
            _ = eco.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = Texto(eco).Should().Be("hola ñandú ✓\n");
            _ = falla.CodigoSalida.Should().Be(7);
            _ = Texto(falla).Should().Be("error\n");
        }

        [Fact]
        public async Task Variables_Persisten_YLaCarpetaVuelveAHome()
        {
            // Arrange
            await using SesionShell sesion = sshd.Sesion();

            // Act
            _ = await EjecutarAsync(sesion, "export NS_PRUEBA=valor; cd /tmp");
            Peticion lectura = await EjecutarAsync(sesion, "echo \"$NS_PRUEBA|$PWD\"");

            // Assert
            _ = Texto(lectura).Should().Be($"valor|{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\n");
        }

        [Fact]
        public async Task NsCwd_DirectorioConfigurableConFallback()
        {
            // Arrange
            await using SesionShell sesion = sshd.Sesion();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // Act & Assert 1: Sin __NS_CWD, el comando parte de $HOME.
            Peticion inicio = await EjecutarAsync(sesion, "pwd");
            _ = inicio.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = inicio.CodigoSalida.Should().Be(0);
            _ = Texto(inicio).Should().Be($"{home}\n");

            // Act & Assert 2: Si se ejecuta export __NS_CWD=/tmp; pwd, el siguiente comando que ejecute pwd devuelve /tmp\n.
            Peticion cambioTmp = await EjecutarAsync(sesion, "export __NS_CWD=/tmp; pwd");
            _ = cambioTmp.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = cambioTmp.CodigoSalida.Should().Be(0);
            _ = Texto(cambioTmp).Should().Be($"{home}\n");

            Peticion siguienteTmp = await EjecutarAsync(sesion, "pwd");
            _ = siguienteTmp.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguienteTmp.CodigoSalida.Should().Be(0);
            _ = Texto(siguienteTmp).Should().Be("/tmp\n");

            // Act & Assert 3: Si __NS_CWD=/ruta/inexistente_123; pwd, el siguiente comando que ejecute pwd cae limpiamente en $HOME sin error.
            Peticion rutaInexistente = await EjecutarAsync(sesion, "__NS_CWD=/ruta/inexistente_123; pwd");
            _ = rutaInexistente.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = rutaInexistente.CodigoSalida.Should().Be(0);
            _ = Texto(rutaInexistente).Should().Be("/tmp\n");

            Peticion siguienteFallback = await EjecutarAsync(sesion, "pwd");
            _ = siguienteFallback.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = siguienteFallback.CodigoSalida.Should().Be(0);
            _ = Texto(siguienteFallback).Should().Be($"{home}\n");
        }

        [Fact]
        public async Task ComandoComplejo_HeredocComillasYLargo()
        {
            // Arrange
            await using SesionShell sesion = sshd.Sesion();
            string largo = new('x', 20_000);

            // Act
            Peticion heredoc = await EjecutarAsync(sesion, "cat <<'EOF'\n\"a\" 'b' $HOME # no\nEOF");
            Peticion grande = await EjecutarAsync(sesion, $"printf '%s' '{largo}' | wc -c");

            // Assert
            _ = Texto(heredoc).Should().Be("\"a\" 'b' $HOME # no\n");
            _ = Texto(grande).Trim().Should().Be("20000");
        }

        [Fact]
        public async Task SalidaGrande_LlegaEntera()
        {
            // Arrange
            await using SesionShell sesion = sshd.Sesion();

            // Act
            Peticion peticion = await EjecutarAsync(sesion, "seq 1 100000");

            // Assert
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Completado);
            string texto = Texto(peticion);
            _ = texto.Should().StartWith("1\n2\n");
            _ = texto.Should().EndWith("\n100000\n");
        }

        [Fact]
        public async Task TiempoAgotado_CtrlCRecuperaElMismoShell()
        {
            // Arrange
            await using SesionShell sesion = sshd.Sesion();
            _ = await EjecutarAsync(sesion, "export NS_ANTES=1");

            // Act
            Peticion lento = await EjecutarAsync(sesion, "echo empiezo; sleep 30", timeoutSegundos: 1);
            Peticion despues = await EjecutarAsync(sesion, "echo \"sigo:$NS_ANTES\"");

            // Assert
            _ = lento.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            _ = Texto(lento).Should().Contain("empiezo");
            _ = Texto(despues).Should().Be("sigo:1\n");
            _ = despues.Aviso.Should().BeNull();
        }

        [Fact]
        public async Task EntradaInteractiva()
        {
            // Arrange
            await using SesionShell sesion = sshd.Sesion();
            Peticion peticion = new(sshd.Destino, "read -r -p 'Nombre? ' n; echo \"hola $n\"", TimeSpan.FromSeconds(15), 1 << 20, TimeProvider.System);
            sesion.Encolar(peticion);

            // Act
            await Utilidades.EsperasPrueba.EsperarHastaAsync(() => Texto(peticion).Contains("Nombre?"), Limite);
            _ = sesion.EnviarEntrada(peticion, "ana", isEnviarEnter: true).Should().BeTrue();
            await peticion.Finalizacion.WaitAsync(Limite);

            // Assert
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = Texto(peticion).Should().EndWith("hola ana\n");
        }

        [Fact]
        public async Task IgnoraSigint_SeCierraElCanalYSeRecrea()
        {
            // Arrange
            await using SesionShell sesion = sshd.Sesion();

            // Act
            Peticion terco = await EjecutarAsync(sesion, "trap '' INT; sleep 30", timeoutSegundos: 1);
            Peticion despues = await EjecutarAsync(sesion, "echo ok");

            // Assert
            _ = terco.Estado.Should().Be(EstadoPeticionEnum.TiempoAgotado);
            _ = terco.Aviso.Should().Contain("se cerró el canal");
            _ = Texto(despues).Should().Be("ok\n");
            _ = despues.Aviso.Should().Contain("recreado");
            _ = sesion.ConexionesAbiertas.Should().Be(1);
        }

        [Fact]
        public async Task Exit_ShellPerdidoYSeRecreaSinReconectar()
        {
            // Arrange
            await using SesionShell sesion = sshd.Sesion();

            // Act
            Peticion salir = await EjecutarAsync(sesion, "exit 3");
            Peticion despues = await EjecutarAsync(sesion, "echo ok");

            // Assert
            _ = salir.Estado.Should().Be(EstadoPeticionEnum.ShellPerdido);
            _ = Texto(despues).Should().Be("ok\n");
            _ = sesion.ConexionesAbiertas.Should().Be(1);
        }

        [Fact]
        public async Task HostDesconocido_SeRechazaConInstrucciones()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            string vacio = Path.Combine(sshd.DirectorioPruebas, ".estado", "known_hosts_vacio");
            File.WriteAllText(vacio, "");
            using FabricaConexionSsh fabrica = sshd.Fabrica(opciones, vacio);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => fabrica.ConectarAsync(sshd.Destino, CancellationToken.None)).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("ssh-keyscan -p");
            _ = error.Message.Should().Contain("SHA256:");
        }

        [Fact]
        public async Task ClaveDeHostDistinta_SeRechaza()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            string falso = Path.Combine(sshd.DirectorioPruebas, ".estado", "known_hosts_falso");
            File.WriteAllText(falso, $"[127.0.0.1]:{sshd.Puerto} ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n");
            using FabricaConexionSsh fabrica = sshd.Fabrica(opciones, falso);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => fabrica.ConectarAsync(sshd.Destino, CancellationToken.None)).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("NO coincide");
        }
    }
}
