using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // guardarSalida: un borrado que falla en el servidor (aquí, el comando dejó el directorio de un shell en 500) no se da por
    // hecho. ssh_reset_shell devuelve esos shells a la lista y el aviso llega después (el siguiente ssh_reset_shell o
    // ssh_disconnect); ssh_disconnect dice cuántos quedan de verdad; el tope de shells cuenta los que no se pudieron borrar.
    // Solo se cuentan las salidas del protocolo, y si no se puede comprobar el directorio base no se dice que no quedaba nada.
    public sealed partial class GuardarSalidaTests
    {
        private static void Escribible(string directorio) =>
            File.SetUnixFileMode(directorio, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // (M02 de la auditoría) ssh_reset_shell con el borrado fallido y después ssh_disconnect: el archivo sigue y
        // ssh_disconnect lo dice, con el número que queda.
        [Fact]
        public async Task ResetConBorradoFallido_DisconnectDiceLoQueQueda()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando uno = await GuardarAsync(h, "echo uno");
            _ = await h.EjecutarAsync(Destino, $"chmod 500 -- '{Path.GetDirectoryName(uno.Archivo)}'", esperarSegundos: 30).ComoComando();
            _ = h.ReiniciarShell(Destino);
            await Task.Delay(3000);
            ResultadoComando dos = await GuardarAsync(h, "echo dos");

            // Act
            ResultadoAccion desconexion = await h.DesconectarAsync(Destino);

            // Assert
            salida.WriteLine(desconexion.Mensaje);
            _ = File.Exists(uno.Archivo).Should().BeTrue();
            _ = File.Exists(dos.Archivo).Should().BeFalse();
            _ = desconexion.Mensaje.Should().Contain("NO se pudieron borrar 1 de los 2").And.NotContain("Se borraron del servidor los");
        }

        // ssh_reset_shell con el borrado fallido y ssh_disconnect enseguida: el cierre espera el borrado en curso, así que el
        // shell que vuelve a la lista no llega después de que el cierre la haya tomado y ssh_disconnect lo dice.
        [Fact]
        public async Task ResetYDisconnectSeguidos_ConBorradoFallido_DisconnectLoDice()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando uno = await GuardarAsync(h, "echo uno");
            _ = await h.EjecutarAsync(Destino, $"chmod 500 -- '{Path.GetDirectoryName(uno.Archivo)}'", esperarSegundos: 30).ComoComando();
            _ = h.ReiniciarShell(Destino);

            // Act
            ResultadoAccion desconexion = await h.DesconectarAsync(Destino);

            // Assert
            salida.WriteLine(desconexion.Mensaje);
            _ = File.Exists(uno.Archivo).Should().BeTrue();
            _ = desconexion.Mensaje.Should().Contain("NO se pudieron borrar");
        }

        // El ssh_reset_shell siguiente a uno cuyo borrado falló lo dice (el primero no puede: borra en segundo plano).
        [Fact]
        public async Task ResetConBorradoFallido_ElSiguienteResetLoDice()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando uno = await GuardarAsync(h, "echo uno");
            _ = await h.EjecutarAsync(Destino, $"chmod 500 -- '{Path.GetDirectoryName(uno.Archivo)}'", esperarSegundos: 30).ComoComando();
            _ = h.ReiniciarShell(Destino);
            await Task.Delay(3000);

            // Act
            ResultadoAccion segundo = h.ReiniciarShell(Destino);

            // Assert
            salida.WriteLine(segundo.Mensaje);
            _ = File.Exists(uno.Archivo).Should().BeTrue();
            _ = segundo.Mensaje.Should().Contain("no se completó").And.Contain("hasta 1");
        }

        // (parcial) ssh_disconnect con dos shells, uno con su directorio en 500: borra el otro y dice que queda 1 de los 2.
        [Fact]
        public async Task DisconnectConFalloParcial_DiceCuantosQuedan()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando uno = await GuardarAsync(h, "echo uno; kill -9 $$");
            ResultadoComando dos = await GuardarAsync(h, "echo dos");
            _ = await h.EjecutarAsync(Destino, $"chmod 500 -- '{Path.GetDirectoryName(uno.Archivo)}'", esperarSegundos: 30).ComoComando();

            // Act
            ResultadoAccion desconexion = await h.DesconectarAsync(Destino);

            // Assert
            salida.WriteLine(desconexion.Mensaje);
            _ = (File.Exists(uno.Archivo), File.Exists(dos.Archivo)).Should().Be((true, false));
            _ = desconexion.Mensaje.Should().Contain("NO se pudieron borrar 1 de los 2").And.NotContain("(todos)");
        }

        // (M06 de la auditoría) Con tope 1, un ssh_reset_shell cuyo borrado falla no saca el shell de la cuenta: el guardarSalida
        // del shell nuevo no se ejecuta, y el aviso habla de shells anteriores (no del actual) y de cómo salir. Con el directorio
        // otra vez escribible, el siguiente ssh_reset_shell lo borra y vuelve a funcionar.
        [Fact]
        public async Task TopeCuentaLosShellsCuyoBorradoFallo()
        {
            // Arrange
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones() with { PrefijoGuardado = Prefijo, MaxShellsConGuardado = 1 };
            _gestor = new GestorSesiones(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh h = new(_gestor, opciones);
            ResultadoComando uno = await GuardarAsync(h, "echo uno");
            string directorio = Path.GetDirectoryName(uno.Archivo)!;
            _ = await h.EjecutarAsync(Destino, $"chmod 500 -- '{directorio}'", esperarSegundos: 30).ComoComando();
            _ = h.ReiniciarShell(Destino);
            await Task.Delay(3000);
            string testigo = Path.Combine(_raiz, "ejecutado");

            // Act
            ResultadoComando rechazado = await GuardarAsync(h, $"touch '{testigo}'; echo dos");
            Escribible(directorio);
            _ = h.ReiniciarShell(Destino);
            await EsperasPrueba.EsperarHastaAsync(() => !File.Exists(uno.Archivo), TimeSpan.FromSeconds(15));
            ResultadoComando tres = await GuardarAsync(h, "echo tres");

            // Assert
            salida.WriteLine($"{rechazado.Estado} '{rechazado.Aviso}'");
            _ = (rechazado.Estado, rechazado.Archivo).Should().Be((EstadoPeticionEnum.Error, null));
            _ = File.Exists(testigo).Should().BeFalse();
            _ = rechazado.Aviso.Should().Contain("1 shells anteriores").And.NotContain("el actual").And.Contain("no se pudo borrar");
            _ = tres.Estado.Should().Be(EstadoPeticionEnum.Completado, tres.Aviso);
        }

        // (O-b; M05 de la auditoría) ssh_disconnect cuenta solo las salidas del protocolo (<id>.out), no lo que el comando dejó
        // en el directorio del shell.
        [Fact]
        public async Task Disconnect_CuentaSoloLasSalidasDelProtocolo()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando uno = await GuardarAsync(h, "echo uno");
            string directorio = Path.GetDirectoryName(uno.Archivo)!;
            _ = await h.EjecutarAsync(Destino, $"cd '{directorio}' && touch 'con espacio' $'con\\nsalto' .oculto otro.txt && mkdir sub && touch sub/x.out && ln -s /etc/passwd enlace.out && ln -s /nada roto", esperarSegundos: 30).ComoComando();

            // Act
            ResultadoAccion desconexion = await h.DesconectarAsync(Destino);

            // Assert
            salida.WriteLine(desconexion.Mensaje);
            _ = Directory.Exists(directorio).Should().BeFalse();
            _ = desconexion.Mensaje.Should().Contain("Se borraron del servidor los 1 archivo(s)");
        }

        // (O-c) Con el directorio base movido, ssh_disconnect no dice que no quedaba nada: dice que no lo pudo comprobar.
        [Fact]
        public async Task Disconnect_ConElBaseMovido_DiceQueNoSePudoComprobar()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            _ = await GuardarAsync(h, "echo uno");
            Directory.Move(Base, Base + ".movido");

            // Act
            ResultadoAccion desconexion = await h.DesconectarAsync(Destino);

            // Assert
            salida.WriteLine(desconexion.Mensaje);
            _ = desconexion.Mensaje.Should().Contain("No se pudo comprobar").And.NotContain("No quedaba").And.NotContain("Se borraron");
        }
    }
}
