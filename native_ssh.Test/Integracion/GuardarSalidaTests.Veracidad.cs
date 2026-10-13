using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // guardarSalida: lo que dicen ssh_disconnect y ssh_reset_shell de lo que borran es cierto aunque se haya borrado algún
    // archivo antes (el flujo que recomienda el aviso del cupo); un comando no puede leer el nonce de las marcas ni, con él,
    // cambiar el archivo, el recuento o el estado; la lista de shells con salidas guardadas tiene tope.
    public sealed partial class GuardarSalidaTests
    {
        // ssh_disconnect cuenta en el servidor los archivos que quedan antes de borrarlos: tras borrar uno de dos con rm, dice
        // 1; tras borrar los dos, no dice que borró ninguno.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task Disconnect_TrasRm_CuentaLosQueQuedaban(int borrados)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando uno = await GuardarAsync(h, "echo uno");
            ResultadoComando dos = await GuardarAsync(h, "echo dos");
            string rm = borrados == 1 ? $"rm -- '{uno.Archivo}'" : $"rm -- '{uno.Archivo}' '{dos.Archivo}'";
            _ = await h.EjecutarAsync(Destino, rm, esperarSegundos: 30).ComoComando();

            // Act
            ResultadoAccion desconexion = await h.DesconectarAsync(Destino);

            // Assert
            salida.WriteLine(desconexion.Mensaje);
            _ = File.Exists(dos.Archivo).Should().BeFalse();
            _ = desconexion.Mensaje.Should().NotContain("2 archivo(s)");
            string esperado = borrados == 1 ? "Se borraron del servidor los 1 archivo(s)" : "No quedaba";
            _ = desconexion.Mensaje.Should().Contain(esperado);
        }

        // ssh_reset_shell borra en segundo plano: no afirma cuántos archivos borra, solo hasta cuántos se crearon.
        [Fact]
        public async Task Reset_TrasRm_NoAfirmaUnNumeroQueNoConoce()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando uno = await GuardarAsync(h, "echo uno");
            ResultadoComando dos = await GuardarAsync(h, "echo dos");
            _ = await h.EjecutarAsync(Destino, $"rm -- '{uno.Archivo}'", esperarSegundos: 30).ComoComando();

            // Act
            ResultadoAccion reset = h.ReiniciarShell(Destino);

            // Assert
            salida.WriteLine(reset.Mensaje);
            _ = reset.Mensaje.Should().NotContain("los 2 archivo(s)").And.Contain("hasta 2");
            await EsperasPrueba.EsperarHastaAsync(() => !File.Exists(dos.Archivo), TimeSpan.FromSeconds(15));
        }

        // El comando del usuario no ve las variables del protocolo que llevan el texto del envoltorio (y con él, el nonce de las
        // marcas), ni en el shell ni en un hijo con 'set -a'.
        [Fact]
        public async Task ElComandoNoVeLasVariablesDelEnvoltorio()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            _ = await h.EjecutarAsync(Destino, "set -a", esperarSegundos: 30).ComoComando();

            // Act
            ResultadoComando r = await GuardarAsync(h, "printf '[%s][%s]' \"${__ns_k-x}\" \"${__ns_b-x}\"; bash -c 'printf \"[%s][%s]\" \"${__ns_k-x}\" \"${__ns_b-x}\"'");
            _ = await h.EjecutarAsync(Destino, "set +a", esperarSegundos: 30).ComoComando();

            // Assert
            salida.WriteLine($"{r.Estado} {r.Archivo} '{r.Salida}' '{r.Aviso}'");
            _ = r.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = (await File.ReadAllTextAsync(r.Archivo!)).Should().Be("[x][x][x][x]");
        }

        // (O1 de la auditoría) Un comando que intenta sacar el nonce de $__ns_k e imprimir marcas E, A o F por el terminal: se
        // ejecuta, termina Completado y el archivo y el recuento son los suyos.
        [Theory]
        [InlineData("E")]
        [InlineData("A")]
        [InlineData("F")]
        public async Task MarcaFalsificadaPorElComando_NoCambiaNada(string tipo)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string testigo = Path.Combine(_raiz, $"testigo_{tipo}");
            const string Sacar = "n=$(printf '%s' \"${__ns_k-}\" | grep -o \"'__NSG' '[0-9a-f]\\{32\\}'\" | head -n 1 | grep -o '[0-9a-f]\\{32\\}'); ";
            string marca = tipo switch
            {
                "E" => "printf '__NSG_%s_E_archivo_%s_0_0__\\n' \"$n\" \"$(printf /p | base64)\" > /dev/tty",
                "A" => "printf '__NSG_%s_A_%s__\\n' \"$n\" \"$(printf /etc/passwd | base64)\" > /dev/tty",
                _ => "printf '__NSG_%s_F_7_77_0__\\n' \"$n\" > /dev/tty"
            };

            // Act
            ResultadoComando r = await GuardarAsync(h, $"echo real; touch '{testigo}'; {Sacar}{marca}");

            // Assert
            salida.WriteLine($"{r.Estado} {r.Archivo} {r.Lineas} {r.Bytes} '{r.Salida}' '{r.Aviso}'");
            _ = File.Exists(testigo).Should().BeTrue();
            _ = (r.Estado, r.CodigoSalida, r.Lineas, r.Bytes).Should().Be((EstadoPeticionEnum.Completado, 0, 1L, 5L));
            _ = RegexArchivo().IsMatch(r.Archivo!).Should().BeTrue(r.Archivo);
            _ = r.Archivo.Should().StartWith(Base);
        }

        // Con más shells con salidas guardadas en la sesión que el tope (aquí 2: cada caída abre otro), el comando no se ejecuta
        // (tampoco al repetirlo en el mismo shell) y el aviso dice cómo seguir; tras ssh_reset_shell (que los borra) vuelve a
        // funcionar. Los archivos de los shells anteriores siguen ahí hasta entonces.
        [Fact]
        public async Task TopeDeShellsConGuardado_NoSeEjecutaHastaElReset()
        {
            // Arrange
            OpcionesNativeSsh opciones = _sshd.Sshd.Opciones() with { PrefijoGuardado = Prefijo, MaxShellsConGuardado = 2 };
            _gestor = new GestorSesiones(_sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh h = new(_gestor, opciones);
            ResultadoComando uno = await GuardarAsync(h, "echo uno; kill -9 $$");
            ResultadoComando dos = await GuardarAsync(h, "echo dos; kill -9 $$");
            string testigo = Path.Combine(_raiz, "ejecutado");

            // Act
            ResultadoComando tres = await GuardarAsync(h, $"touch '{testigo}'; echo tres");
            ResultadoComando otraVez = await GuardarAsync(h, $"touch '{testigo}'; echo otra");
            bool isSiguen = File.Exists(uno.Archivo) && File.Exists(dos.Archivo);
            _ = h.ReiniciarShell(Destino);
            ResultadoComando cuatro = await GuardarAsync(h, "echo cuatro");

            // Assert
            salida.WriteLine($"{tres.Estado} '{tres.Aviso}'");
            _ = (uno.Estado, dos.Estado).Should().Be((EstadoPeticionEnum.ShellPerdido, EstadoPeticionEnum.ShellPerdido));
            _ = (tres.Estado, tres.Archivo).Should().Be((EstadoPeticionEnum.Error, null));
            _ = tres.Aviso.Should().Contain("El comando NO se ejecutó").And.Contain("2 shells").And.Contain("ssh_reset_shell");
            _ = (otraVez.Estado, otraVez.Archivo).Should().Be((EstadoPeticionEnum.Error, null), "el shell rechazado no entra en la lista");
            _ = File.Exists(testigo).Should().BeFalse();
            _ = isSiguen.Should().BeTrue();
            _ = cuatro.Estado.Should().Be(EstadoPeticionEnum.Completado, cuatro.Aviso);
        }
    }
}
