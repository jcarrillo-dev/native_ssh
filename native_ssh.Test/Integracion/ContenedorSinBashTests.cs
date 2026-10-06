using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using Xunit.Abstractions;

namespace NativeSsh.Test.Integracion
{
    // Servidor SIN bash (Bloque S, S4): Alpine con el ash de busybox como shell de login y bash sin instalar
    // (pruebas/contenedor/Containerfile.sinbash). native_ssh necesita bash para todo: el shell persistente hace
    // 'exec bash ...' al inicializarse (ConstructorComando.EscribirInicializacion) y los canales exec de las herramientas de
    // lectura ejecutan su script con 'bash -c' (ConstructorComandoExec), así que en este servidor ninguna herramienta puede
    // funcionar. Lo que se exige es que fallen bien:
    // - ninguna llamada se cuelga: el ash de login sale en cuanto falla el 'exec bash' (cierra el canal) y el 'bash -c' de
    //   los canales exec termina con 127, así que todas vuelven en segundos, sin esperar al plazo de inicialización (15 s);
    // - una sola conexión TCP para todas las llamadas (y otra tras ssh_disconnect), y un solo canal por llamada: sin bucles
    //   de reconexión ni reintentos (se cuentan en el log VERBOSE del sshd del contenedor);
    // - el error dice la causa real («el servidor no tiene bash; native_ssh lo necesita...»): hoy no (FalloConocido).
    // Con la herramienta (HerramientasSsh en el proceso de la prueba) y con el binario (MCP por stdio, en
    // ContenedorSinBashTests.Binario.cs).
    [Collection(ColeccionContenedores.Nombre)]
    [Trait("Categoria", "Contenedor")]
    [SupportedOSPlatform("linux")]
    public sealed partial class ContenedorSinBashTests(ContenedorSudo contenedores, ITestOutputHelper salida)
    {
        // Muy por debajo de TimeoutInicializacionShell (15 s): una llamada que tarde más esperó a un plazo en lugar de ver
        // que el shell se cerró o que el script terminó con 127.
        private static readonly TimeSpan PlazoLlamada = TimeSpan.FromSeconds(10);

        // Límite de seguridad para que una llamada colgada no cuelgue la suite (falla la prueba con TimeoutException).
        private static readonly TimeSpan LimiteSeguridad = TimeSpan.FromSeconds(60);

        private const string MotivoMensaje = "producto: sin bash en el servidor, el error no dice la causa ni que native_ssh necesita bash: " +
            "ssh_exec y las herramientas que van por el shell dicen «El shell de … se cerró durante la inicialización» (se descarta lo que " +
            "el ash de login escribió: «-ash: exec: line 1: bash: not found») y las de canal exec devuelven el stderr crudo «ash: bash: not found»";

        // El texto que debe ver el LLM: que falta bash y que native_ssh lo necesita.
        [GeneratedRegex(@"\bbash\b[\s\S]*\b(necesita|requiere)\b|\b(necesita|requiere)\b[\s\S]*\bbash\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex MensajeSinBash();

        private string Destino => Servidor.Destino;

        private ServidorContenedor Servidor => contenedores.Servidor(ContenedorSudo.SinBash);

        // Precondición del contenedor, comprobada en cada prueba: el usuario de prueba tiene /bin/ash como shell de login y
        // bash no existe (ni en su PATH ni como /bin/bash).
        private ServidorContenedor Preparar()
        {
            ServidorContenedor servidor = Servidor;
            (int codigo, string texto, string errores) = ServidorContenedor.Ejecutar("podman",
                ["exec", "--user", servidor.Usuario, servidor.Contenedor, "sh", "-c", "getent passwd \"$(id -un)\" | cut -d: -f7; command -v bash; echo \"rc=$?\"; ls /bin/bash /usr/bin/bash 2>/dev/null; true"],
                TimeSpan.FromMinutes(1));
            _ = codigo.Should().Be(0, errores);
            _ = texto.Should().Be("/bin/ash\nrc=127\n", "el contenedor sinbash no debe tener bash");
            return servidor;
        }

        // Precondición del contenedor (también la comprueba cada prueba): sin bash y con ash de login, también por SSH.
        [FactContenedor]
        public void SinBash_ElContenedorNoTieneBashYElShellDeLoginEsAsh()
        {
            // Arrange
            ServidorContenedor servidor = Preparar();

            // Act
            (int codigo, string texto, string errores) = ServidorContenedor.Ejecutar("ssh",
                ["-i", servidor.RutaClave, "-o", $"UserKnownHostsFile={servidor.RutaKnownHosts}", "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes",
                 "-p", servidor.Puerto.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{servidor.Usuario}@127.0.0.1",
                 "echo \"$0\"; command -v bash; echo \"rc=$?\""],
                TimeSpan.FromMinutes(1));

            // Assert
            _ = codigo.Should().Be(0, errores);
            _ = texto.Should().Be("ash\nrc=127\n");
        }

        // ssh_exec (el primero y los siguientes), ssh_write_file, ssh_replace_file_content y ssh_set_cwd van por el shell:
        // cada llamada abre un canal de shell (el ash sale al fallar 'exec bash'), vuelve en segundos con el comando en Error
        // y sin tocar nada, todas sobre la misma conexión. ssh_status lo refleja (conectada, sin shell activo) y
        // ssh_reset_shell solo pide un shell nuevo para el siguiente comando, que falla igual.
        [FactContenedor]
        public async Task SinBash_HerramientasDelShell_VuelvenEnSegundosConUnaConexionYUnCanalPorLlamada()
        {
            // Arrange
            ServidorContenedor servidor = Preparar();
            string directorio = $"/home/{servidor.Usuario}/ns_{Guid.NewGuid():N}";
            _ = servidor.ComoUsuario("mkdir \"$1\" && printf 'uno\\ndos\\n' > \"$1/a.txt\"", directorio);
            await using GestorSesiones gestor = servidor.Gestor();
            HerramientasSsh herramientas = servidor.Herramientas(gestor);
            RegistroSshd antes = await LeerRegistroAsync(servidor);

            // Act
            Llamada<ResultadoComando> primero = await MedirAsync(() => herramientas.EjecutarAsync(Destino, "echo primero"));
            Llamada<ResultadoComando> siguiente = await MedirAsync(() => herramientas.EjecutarAsync(Destino, "echo siguiente"));
            Llamada<ResultadoEscritura> escritura = await MedirAsync(() => herramientas.EscribirArchivoAsync(Destino, $"{directorio}/b.txt", "nuevo\n"));
            Llamada<ResultadoEscritura> reemplazo = await MedirAsync(() => herramientas.ReemplazarContenidoArchivoAsync(Destino, $"{directorio}/a.txt", "uno", "UNO"));
            Llamada<ResultadoDirectorioTrabajo> cwd = await MedirAsync(() => herramientas.FijarDirectorioAsync(Destino, directorio));
            ResultadoEstado estado = herramientas.Estado(Destino);
            ResultadoAccion reinicio = herramientas.ReiniciarShell(Destino);
            Llamada<ResultadoComando> trasReinicio = await MedirAsync(() => herramientas.EjecutarAsync(Destino, "echo tras_reinicio"));
            ResultadoEstado estadoFinal = herramientas.Estado(Destino);
            RegistroSshd despues = await LeerRegistroAsync(servidor);
            string contenido = servidor.ComoUsuario("ls -A \"$1\"; cat \"$1/a.txt\"", directorio);
            _ = servidor.RootConCodigo("rm -rf -- \"$1\"", directorio);

            // Assert
            Registrar(primero, siguiente, escritura, reemplazo, cwd, trasReinicio);
            foreach (Llamada<ResultadoComando> llamada in new[] { primero, siguiente, trasReinicio })
            {
                _ = llamada.Duracion.Should().BeLessThan(PlazoLlamada);
                _ = llamada.Resultado.Should().NotBeNull(llamada.Excepcion?.Message);
                _ = llamada.Resultado!.Estado.Should().Be(EstadoPeticionEnum.Error);
            }

            _ = escritura.Duracion.Should().BeLessThan(PlazoLlamada);
            // El comando de la escritura no llegó al shell (Peticion.IsComandoEnviado): el error lo afirma, no lo deja en duda.
            _ = escritura.Excepcion.Should().BeOfType<NativeSshException>().Which.Message.Should().Contain("no se ha modificado").And.NotContain("No consta");
            _ = reemplazo.Duracion.Should().BeLessThan(PlazoLlamada);
            _ = reemplazo.Excepcion.Should().BeOfType<NativeSshException>().Which.Message.Should().Contain("No se ha modificado");
            _ = cwd.Duracion.Should().BeLessThan(PlazoLlamada);
            _ = cwd.Excepcion.Should().BeOfType<NativeSshException>();
            _ = contenido.Should().Be("a.txt\nuno\ndos\n", "ninguna escritura llegó a ejecutarse");

            EstadoSesion sesion = estado.Sesiones.Should().ContainSingle().Subject;
            _ = sesion.IsConectada.Should().BeTrue();
            _ = sesion.IsShellActivo.Should().BeFalse();
            _ = sesion.ConexionesAbiertas.Should().Be(1);
            _ = sesion.ComandosEnCola.Should().Be(0);
            _ = estado.Peticiones.Should().HaveCount(5).And.OnlyContain(p => p.Estado == EstadoPeticionEnum.Error);
            _ = reinicio.IsHecho.Should().BeTrue();
            _ = estadoFinal.Sesiones.Should().ContainSingle().Which.ConexionesAbiertas.Should().Be(1);

            // Seis llamadas por el shell: seis canales de shell, ninguno de más, y ningún canal exec.
            _ = (despues - antes).Should().Be(new RegistroSshd(Conexiones: 1, Shells: 6, Exec: 0));
        }

        // Las herramientas de lectura van por canales exec y su script se ejecuta con 'bash -c': sin bash, el ash de login
        // responde 127 («bash: not found»). Ninguna funciona; todas vuelven en segundos, con un canal exec cada una sobre una
        // sola conexión y sin abrir el shell.
        [FactContenedor]
        public async Task SinBash_HerramientasDeLectura_VuelvenEnSegundosConUnaConexionYUnCanalExecPorLlamada()
        {
            // Arrange
            ServidorContenedor servidor = Preparar();
            string directorio = $"/home/{servidor.Usuario}/ns_{Guid.NewGuid():N}";
            _ = servidor.ComoUsuario("mkdir \"$1\" && printf 'uno\\ndos\\n' > \"$1/a.txt\"", directorio);
            await using GestorSesiones gestor = servidor.Gestor();
            HerramientasSsh herramientas = servidor.Herramientas(gestor);
            RegistroSshd antes = await LeerRegistroAsync(servidor);

            // Act
            Llamada<string> lectura = await MedirAsync(() => herramientas.LeerArchivoAsync(Destino, $"{directorio}/a.txt"));
            Llamada<string> listado = await MedirAsync(() => herramientas.ListarDirectoriosAsync(Destino, directorio));
            Llamada<string> porNombre = await MedirAsync(() => herramientas.BuscarPorNombreAsync(Destino, "*.txt", directorio));
            Llamada<string> busqueda = await MedirAsync(() => herramientas.BuscarAsync(Destino, "uno", directorio));
            Llamada<ResultadoMetadatos> metadatos = await MedirAsync(() => herramientas.ConsultarMetadatosAsync(Destino, $"{directorio}/a.txt"));
            ResultadoEstado estado = herramientas.Estado(Destino);
            RegistroSshd despues = await LeerRegistroAsync(servidor);
            _ = servidor.RootConCodigo("rm -rf -- \"$1\"", directorio);

            // Assert
            Registrar(lectura, listado, porNombre, busqueda, metadatos);
            foreach (ILlamada llamada in new ILlamada[] { lectura, listado, porNombre, busqueda, metadatos })
            {
                _ = llamada.Duracion.Should().BeLessThan(PlazoLlamada);
                _ = llamada.Excepcion.Should().BeOfType<NativeSshException>("sin bash ninguna herramienta de lectura funciona");
            }

            EstadoSesion sesion = estado.Sesiones.Should().ContainSingle().Subject;
            _ = sesion.IsConectada.Should().BeTrue();
            _ = sesion.IsShellActivo.Should().BeFalse();
            _ = sesion.ConexionesAbiertas.Should().Be(1);
            _ = sesion.CanalesExecActivos.Should().Be(0);
            _ = (despues - antes).Should().Be(new RegistroSshd(Conexiones: 1, Shells: 0, Exec: 5));
        }

        // ssh_disconnect cierra la conexión (ssh_status ya no la lista) y el comando siguiente abre una sola conexión nueva.
        [FactContenedor]
        public async Task SinBash_Desconectar_CierraLaConexionYElComandoSiguienteAbreSoloUna()
        {
            // Arrange
            ServidorContenedor servidor = Preparar();
            await using GestorSesiones gestor = servidor.Gestor();
            HerramientasSsh herramientas = servidor.Herramientas(gestor);
            RegistroSshd antes = await LeerRegistroAsync(servidor);
            Llamada<ResultadoComando> primero = await MedirAsync(() => herramientas.EjecutarAsync(Destino, "echo primero"));

            // Act
            Llamada<ResultadoAccion> desconexion = await MedirAsync(() => herramientas.DesconectarAsync(Destino));
            ResultadoEstado estado = herramientas.Estado(Destino);
            Llamada<ResultadoComando> siguiente = await MedirAsync(() => herramientas.EjecutarAsync(Destino, "echo siguiente"));
            ResultadoEstado estadoFinal = herramientas.Estado(Destino);
            RegistroSshd despues = await LeerRegistroAsync(servidor);

            // Assert
            Registrar(primero, desconexion, siguiente);
            _ = primero.Resultado!.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = desconexion.Duracion.Should().BeLessThan(PlazoLlamada);
            _ = desconexion.Resultado.Should().NotBeNull(desconexion.Excepcion?.Message);
            _ = desconexion.Resultado!.IsHecho.Should().BeTrue();
            _ = estado.Sesiones.Should().BeEmpty();
            _ = siguiente.Duracion.Should().BeLessThan(PlazoLlamada);
            _ = siguiente.Resultado!.Estado.Should().Be(EstadoPeticionEnum.Error);
            _ = estadoFinal.Sesiones.Should().ContainSingle().Which.ConexionesAbiertas.Should().Be(1);
            _ = (despues - antes).Should().Be(new RegistroSshd(Conexiones: 2, Shells: 2, Exec: 0));
        }

        // FALLO CONOCIDO (producto, Bloque S, S4): el error no dice que falta bash. Por el shell, SesionShell.AbrirShellAsync
        // ve el canal cerrado durante la inicialización y responde «El shell de … se cerró durante la inicialización»,
        // descartando lo que escribió el ash («-ash: exec: line 1: bash: not found»); el texto que sí nombra bash solo sale si
        // vence el plazo de inicialización. Por los canales exec, EjecutarLecturaAsync devuelve el stderr tal cual
        // («ash: bash: not found», código 127). Además, el error de ssh_write_file remite a ssh_stat/ssh_read_file, que aquí
        // tampoco funcionan.
        // Reproducción: ssh -tt al contenedor sinbash y 'exec bash' → «-ash: exec: line 1: bash: not found» y se cierra la
        // conexión; ssh al contenedor 'bash -c true; echo $?' → «ash: bash: not found», 127.
        [TheoryContenedor(FalloConocido = MotivoMensaje)]
        [Trait("Categoria", "FalloConocido")]
        [InlineData("ssh_exec")]
        [InlineData("ssh_exec siguiente")]
        [InlineData("ssh_exec tras ssh_reset_shell")]
        [InlineData("ssh_write_file")]
        [InlineData("ssh_replace_file_content")]
        [InlineData("ssh_set_cwd")]
        [InlineData("ssh_read_file")]
        [InlineData("ssh_list_dir")]
        [InlineData("ssh_find")]
        [InlineData("ssh_search")]
        [InlineData("ssh_stat")]
        public async Task FalloConocido_SinBash_ElErrorDiceQueFaltaBashYQueNativeSshLoNecesita(string herramienta)
        {
            // Arrange
            ServidorContenedor servidor = Preparar();
            string archivo = $"/home/{servidor.Usuario}/ns_{Guid.NewGuid():N}.txt";
            _ = servidor.ComoUsuario("printf 'uno\\n' > \"$1\"", archivo);
            await using GestorSesiones gestor = servidor.Gestor();
            HerramientasSsh herramientas = servidor.Herramientas(gestor);

            // Act
            string mensaje;
            try
            {
                mensaje = await MensajeAsync(herramientas, herramienta, archivo);
            }
            finally
            {
                _ = servidor.RootConCodigo("rm -f -- \"$1\"", archivo);
            }

            // Assert
            salida.WriteLine($"{herramienta}: {mensaje}");
            _ = mensaje.Should().MatchRegex(MensajeSinBash(), "el LLM debe saber que el servidor no tiene bash y que native_ssh lo necesita");
            _ = mensaje.Should().NotContain("ssh_stat").And.NotContain("ssh_read_file", "sin bash esas herramientas tampoco funcionan");
        }

        // El texto que recibe el LLM de cada herramienta: el aviso de un comando en Error o el mensaje de la excepción.
        private async Task<string> MensajeAsync(HerramientasSsh herramientas, string herramienta, string archivo)
        {
            switch (herramienta)
            {
                case "ssh_exec":
                    return await TextoAsync(() => herramientas.EjecutarAsync(Destino, "echo primero"));
                case "ssh_exec siguiente":
                    _ = await herramientas.EjecutarAsync(Destino, "echo primero");
                    return await TextoAsync(() => herramientas.EjecutarAsync(Destino, "echo siguiente"));
                case "ssh_exec tras ssh_reset_shell":
                    _ = await herramientas.EjecutarAsync(Destino, "echo primero");
                    _ = herramientas.ReiniciarShell(Destino);
                    return await TextoAsync(() => herramientas.EjecutarAsync(Destino, "echo tras_reinicio"));
                case "ssh_write_file":
                    return await TextoAsync(() => herramientas.EscribirArchivoAsync(Destino, archivo, "nuevo\n", overwrite: true));
                case "ssh_replace_file_content":
                    return await TextoAsync(() => herramientas.ReemplazarContenidoArchivoAsync(Destino, archivo, "uno", "UNO"));
                case "ssh_set_cwd":
                    return await TextoAsync(() => herramientas.FijarDirectorioAsync(Destino, "/tmp"));
                case "ssh_read_file":
                    return await TextoAsync(() => herramientas.LeerArchivoAsync(Destino, archivo));
                case "ssh_list_dir":
                    return await TextoAsync(() => herramientas.ListarDirectoriosAsync(Destino, "/tmp"));
                case "ssh_find":
                    return await TextoAsync(() => herramientas.BuscarPorNombreAsync(Destino, "*.txt", "/tmp"));
                case "ssh_search":
                    return await TextoAsync(() => herramientas.BuscarAsync(Destino, "uno", archivo));
                case "ssh_stat":
                    return await TextoAsync(() => herramientas.ConsultarMetadatosAsync(Destino, archivo));
                default:
                    throw new ArgumentOutOfRangeException(nameof(herramienta), herramienta, "Herramienta sin caso.");
            }
        }

        private static async Task<string> TextoAsync<T>(Func<Task<T>> llamada)
        {
            Llamada<T> resultado = await MedirAsync(llamada);
            return resultado.Excepcion is { } excepcion ? excepcion.Message
                : resultado.Resultado is ResultadoComando comando ? $"{comando.Aviso} {comando.Salida}".Trim()
                : $"(sin error) {resultado.Resultado}";
        }

        private interface ILlamada
        {
            TimeSpan Duracion { get; }
            Exception? Excepcion { get; }
        }

        // Una llamada a una herramienta: lo que devolvió o la excepción que lanzó, y cuánto tardó.
        private sealed record Llamada<T>(T? Resultado, Exception? Excepcion, TimeSpan Duracion) : ILlamada;

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "La prueba registra cualquier error de la herramienta para comprobarlo después.")]
        private static async Task<Llamada<T>> MedirAsync<T>(Func<Task<T>> llamada)
        {
            Stopwatch reloj = Stopwatch.StartNew();
            try
            {
                T resultado = await llamada().WaitAsync(LimiteSeguridad);
                return new Llamada<T>(resultado, null, reloj.Elapsed);
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new Llamada<T>(default, ex, reloj.Elapsed);
            }
        }

        private void Registrar(params ILlamada[] llamadas)
        {
            foreach (ILlamada llamada in llamadas)
                salida.WriteLine($"{llamada.Duracion.TotalSeconds:0.00} s: {llamada}");
        }

        // Contadores del log VERBOSE del sshd del contenedor (sshd -e, en 'podman logs'): conexiones autenticadas (la sonda
        // del banner de contenedor-sudo.sh no se autentica, así que no cuenta) y canales de sesión abiertos (shell o exec).
        private sealed record RegistroSshd(int Conexiones, int Shells, int Exec)
        {
            public static RegistroSshd operator -(RegistroSshd a, RegistroSshd b) => new(a.Conexiones - b.Conexiones, a.Shells - b.Shells, a.Exec - b.Exec);
        }

        // sshd escribe el log de forma asíncrona respecto a la respuesta de la herramienta: se lee hasta que dos lecturas
        // seguidas, separadas 300 ms, coinciden.
        private static async Task<RegistroSshd> LeerRegistroAsync(ServidorContenedor servidor)
        {
            RegistroSshd anterior = LeerRegistro(servidor);
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(300);
                RegistroSshd actual = LeerRegistro(servidor);
                if (actual == anterior)
                    return actual;

                anterior = actual;
            }

            return anterior;
        }

        private static RegistroSshd LeerRegistro(ServidorContenedor servidor)
        {
            (int codigo, string texto, string errores) = ServidorContenedor.Ejecutar("podman", ["logs", servidor.Contenedor], TimeSpan.FromMinutes(1));
            if (codigo != 0)
                throw new InvalidOperationException($"podman logs {servidor.Contenedor} falló ({codigo}): {errores}");

            string log = texto + errores;
            return new RegistroSshd(LineaConexion().Count(log), LineaShell().Count(log), LineaExec().Count(log));
        }

        [GeneratedRegex("^Accepted publickey for ", RegexOptions.Multiline)]
        private static partial Regex LineaConexion();

        [GeneratedRegex("^Starting session: shell ", RegexOptions.Multiline)]
        private static partial Regex LineaShell();

        [GeneratedRegex("^Starting session: command ", RegexOptions.Multiline)]
        private static partial Regex LineaExec();
    }
}
