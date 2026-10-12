using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // guardarSalida: un archivo guardado no se borra porque el shell se reabra solo (plazo agotado o cancelación con el canal
    // cerrado, caída del shell): los directorios de los shells anteriores se recuerdan y se borran solo con ssh_reset_shell,
    // ssh_disconnect, el cierre de la sesión o el barrido de 24 h, y los avisos lo dicen así. El barrido no se atribuye al
    // comando. ssh_reset_shell y ssh_disconnect dicen que borran.
    public sealed partial class GuardarSalidaTests
    {
        private const string ComandoQueIgnoraCtrlC = "bash -c 'trap \"\" INT; echo uno; sleep 60'";

        // (K04, K05) Tiempo agotado y ssh_cancel con un proceso que no atiende Ctrl+C (se cierra el canal del shell) y caída del
        // shell: el archivo sigue ahí tras el siguiente ssh_exec (que abre un shell nuevo), un ssh_set_cwd, una escritura por
        // el shell y otro guardarSalida (que barre restos de otros shells); el aviso no dice que el siguiente lo borre. Después,
        // ssh_reset_shell borra los directorios de los dos shells.
        [Theory]
        [InlineData("plazo")]
        [InlineData("cancel")]
        [InlineData("caida")]
        public async Task ShellReabiertoSolo_ElArchivoSeConservaHastaReset(string camino)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando r = camino switch
            {
                "plazo" => await GuardarAsync(h, ComandoQueIgnoraCtrlC, esperarSegundos: 60, timeoutSegundos: 3),
                "cancel" => await h.CancelarAsync((await GuardarAsync(h, ComandoQueIgnoraCtrlC, esperarSegundos: 2)).Id).ComoComando(),
                _ => await GuardarAsync(h, "echo uno; kill -9 $$")
            };
            salida.WriteLine($"{r.Estado} {r.Archivo} aviso '{r.Aviso}'");
            EstadoPeticionEnum esperado = camino switch
            {
                "plazo" => EstadoPeticionEnum.TiempoAgotado,
                "cancel" => EstadoPeticionEnum.Cancelado,
                _ => EstadoPeticionEnum.ShellPerdido
            };
            _ = r.Estado.Should().Be(esperado);
            _ = File.Exists(r.Archivo).Should().BeTrue();
            string directorioCwd = Directory.CreateDirectory(Path.Combine(_raiz, "cwd")).FullName;

            // Act
            ResultadoComando siguiente = await h.EjecutarAsync(Destino, "echo siguiente", esperarSegundos: 30).ComoComando();
            _ = await h.FijarDirectorioAsync(Destino, directorioCwd);
            _ = await h.EscribirArchivoAsync(Destino, Path.Combine(directorioCwd, "escrito.txt"), "hola\n");
            ResultadoComando otro = await GuardarAsync(h, "echo otro");
            await Task.Delay(3000);
            bool isSigue = File.Exists(r.Archivo);
            string contenido = isSigue ? await File.ReadAllTextAsync(r.Archivo!) : string.Empty;
            _ = h.ReiniciarShell(Destino);

            // Assert
            _ = siguiente.Salida.Should().Contain("siguiente");
            _ = otro.Estado.Should().Be(EstadoPeticionEnum.Completado, otro.Aviso);
            _ = isSigue.Should().BeTrue("ni el shell nuevo, ni las herramientas, ni el barrido borran el archivo de un shell anterior de la sesión");
            _ = contenido.Should().Be("uno\n");
            _ = r.Aviso.Should().Contain(r.Archivo).And.Contain("ssh_reset_shell").And.Contain("ssh_disconnect").And.NotContain("siguiente ssh_exec");
            await EsperasPrueba.EsperarHastaAsync(() => !File.Exists(r.Archivo) && !File.Exists(otro.Archivo), TimeSpan.FromSeconds(15));
        }

        // ssh_disconnect borra también los directorios de los shells anteriores de la sesión (aquí, el de antes de una caída).
        [Fact]
        public async Task Disconnect_BorraTambienLosDeShellsAnteriores()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            ResultadoComando antes = await GuardarAsync(h, "echo antes; kill -9 $$");
            ResultadoComando despues = await GuardarAsync(h, "echo despues");
            _ = (File.Exists(antes.Archivo), File.Exists(despues.Archivo)).Should().Be((true, true));

            // Act
            ResultadoAccion accion = await h.DesconectarAsync(Destino);

            // Assert
            salida.WriteLine(accion.Mensaje);
            _ = (File.Exists(antes.Archivo), File.Exists(despues.Archivo)).Should().Be((false, false));
            _ = Directory.Exists(Path.GetDirectoryName(antes.Archivo)).Should().BeFalse();
        }

        // Los avisos de cada estado dicen cuándo se borra el archivo: con ssh_reset_shell, ssh_disconnect o al cerrarse la
        // sesión, y no al abrirse un shell nuevo por sí solo.
        [Fact]
        public async Task Avisos_DicenCuandoSeBorra()
        {
            // Arrange
            HerramientasSsh h = Herramientas();

            // Act
            ResultadoComando completado = await GuardarAsync(h, "echo hola");
            ResultadoComando enCurso = await GuardarAsync(h, "sleep 3; echo fin", esperarSegundos: 1);
            _ = await h.LeerSalidaAsync(enCurso.Id, esperarSegundos: 30).ComoComando();

            // Assert
            foreach (ResultadoComando r in new[] { completado, enCurso })
            {
                salida.WriteLine($"{r.Estado}: {r.Aviso}");
                _ = r.Aviso.Should().Contain("ssh_reset_shell").And.Contain("ssh_disconnect").And.Contain("cerrarse la sesión").And.Contain("no lo borra");
            }
        }

        // (K06) El barrido de 24 h del primer guardado de otra sesión borra el directorio de un comando en curso (simulado con
        // la fecha del archivo y del directorio): el aviso no lo atribuye al comando ni dice que la salida está guardada.
        [Fact]
        public async Task Barrido_DeUnComandoEnCurso_ElAvisoNoAfirmaNadaFalso()
        {
            // Arrange
            HerramientasSsh a = Herramientas();
            GestorSesiones primero = _gestor!;
            HerramientasSsh b = Herramientas();
            ResultadoComando r = await GuardarAsync(a, "echo uno; sleep 6; echo dos", esperarSegundos: 2);
            DateTime viejo = DateTime.UtcNow.AddDays(-2);
            File.SetLastWriteTimeUtc(r.Archivo!, viejo);
            Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(r.Archivo!)!, viejo);

            // Act
            _ = await GuardarAsync(b, "echo hola");
            ResultadoComando fin = await a.LeerSalidaAsync(r.Id, esperarSegundos: 30).ComoComando();
            await primero.DisposeAsync();

            // Assert
            salida.WriteLine($"existe={File.Exists(r.Archivo)} {fin.Estado} aviso '{fin.Aviso}'");
            _ = File.Exists(r.Archivo).Should().BeFalse("precondición: el barrido lo borró");
            _ = fin.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = (fin.Lineas, fin.Bytes).Should().Be((null, null));
            _ = fin.Aviso.Should().NotContain("el comando lo movió o lo borró").And.NotContain("Salida completa guardada").And.Contain("barrido");
        }

        // ssh_reset_shell y ssh_disconnect dicen que borran las salidas guardadas y cuántas.
        [Fact]
        public async Task ResetYDisconnect_DicenQueBorranLasSalidasGuardadas()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            _ = await GuardarAsync(h, "echo uno");
            _ = await GuardarAsync(h, "echo dos");

            // Act
            ResultadoAccion reset = h.ReiniciarShell(Destino);
            _ = await GuardarAsync(h, "echo tres");
            ResultadoAccion desconexion = await h.DesconectarAsync(Destino);

            // Assert
            salida.WriteLine($"{reset.Mensaje}\n{desconexion.Mensaje}");
            _ = reset.Mensaje.Should().Contain("2 archivo(s) de salida guardados").And.Contain("guardarSalida");
            _ = desconexion.Mensaje.Should().Contain("1 archivo(s) de salida guardados").And.Contain("Se borraron");
        }
    }
}
