using FluentAssertions;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
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

        [Fact]
        public async Task LeerArchivo_RangoLineasYArchivoInexistente()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string archivoTemporal = Path.GetTempFileName();
            try
            {
                string[] lineas = ["Línea 1", "Línea 2", "Línea 3", "Línea 4", "Línea 5"];
                await File.WriteAllLinesAsync(archivoTemporal, lineas);

                // Act 1: Leer líneas 2 a 4
                string contenido = await herramientas.LeerArchivoAsync(sshd.Destino.Clave, archivoTemporal, lineaInicio: 2, lineaFin: 4);

                // Assert 1: Devuelve la cabecera de metadatos y exactamente las líneas 2 a 4 con saltos de línea reales
                _ = contenido.Should().Be("[Líneas 2-4 de 5]\nLínea 2\nLínea 3\nLínea 4\n");

                // Act 2: Pedir un archivo que no existe
                string rutaInexistente = Path.Combine(Path.GetTempPath(), $"no_existe_{Guid.NewGuid():N}.txt");
                NativeSshException error = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync(sshd.Destino.Clave, rutaInexistente)).Should().ThrowAsync<NativeSshException>()).Which;

                // Assert 2: Lanza la excepción con el mensaje de error del archivo inexistente
                _ = error.Message.Should().Contain($"Error: el archivo no existe: {rutaInexistente}");

                // Act 3: Pedir un directorio
                NativeSshException errorDir = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync(sshd.Destino.Clave, "/tmp")).Should().ThrowAsync<NativeSshException>()).Which;

                // Assert 3: Lanza la excepción indicando que es un directorio
                _ = errorDir.Message.Should().Contain("Error: la ruta es un directorio: /tmp");
            }
            finally
            {
                if (File.Exists(archivoTemporal))
                    File.Delete(archivoTemporal);
            }
        }

        [Fact]
        public async Task LeerArchivo_SinEspecificarLineaFin_DevuelveFinEfectivo()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string archivoTemporal = Path.GetTempFileName();
            try
            {
                string[] lineas = ["Línea 1", "Línea 2", "Línea 3", "Línea 4", "Línea 5"];
                await File.WriteAllLinesAsync(archivoTemporal, lineas);

                // Act: lectura de archivo completo sin especificar lineaFin
                string contenido = await herramientas.LeerArchivoAsync(sshd.Destino.Clave, archivoTemporal);

                // Assert: devuelve [Líneas 1-5 de 5] y no [Líneas 1-800 de 5]
                _ = contenido.Should().StartWith("[Líneas 1-5 de 5]\n");
                _ = contenido.Should().NotContain("[Líneas 1-800 de 5]");
                _ = contenido.Should().Be("[Líneas 1-5 de 5]\nLínea 1\nLínea 2\nLínea 3\nLínea 4\nLínea 5\n");
            }
            finally
            {
                if (File.Exists(archivoTemporal))
                    File.Delete(archivoTemporal);
            }
        }

        [Fact]
        public async Task LeerArchivo_RutaConCaracteresEspeciales_EsSeguraYPrevieneInyeccion()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            // Archivo cuyo nombre empieza por guion y contiene comillas, espacios y signos $
            string nombreEspecial = "--archivo 'con comillas' y $HOME.txt";
            string rutaCompleta = Path.Combine(Path.GetTempPath(), nombreEspecial);
            try
            {
                await File.WriteAllLinesAsync(rutaCompleta, ["Contenido 1", "Contenido 2"]);

                // Act 1: Leer archivo existente con nombre especial y guiones iniciales
                string contenido = await herramientas.LeerArchivoAsync(sshd.Destino.Clave, rutaCompleta);

                // Assert 1: sed procesa el archivo gracias al flag '--' y devuelve el contenido
                _ = contenido.Should().Be("[Líneas 1-2 de 2]\nContenido 1\nContenido 2\n");

                // Act 2: Ruta inexistente con caracteres de inyección y comillas
                string rutaMaliciosa = "/tmp/-n $(echo inyeccion) ' \"";
                NativeSshException error = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync(sshd.Destino.Clave, rutaMaliciosa)).Should().ThrowAsync<NativeSshException>()).Which;

                // Assert 2: printf imprime exactamente la ruta literal sin evaluar el subshell $(echo inyeccion)
                _ = error.Message.Should().Contain($"Error: el archivo no existe: {rutaMaliciosa}");
                _ = error.Message.Should().Contain("$(echo inyeccion)");

                // Act 3: Ruta con flag tipo -n
                NativeSshException errorFlag = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync(sshd.Destino.Clave, "-n")).Should().ThrowAsync<NativeSshException>()).Which;
                _ = errorFlag.Message.Should().Contain("Error: el archivo no existe: -n");
            }
            finally
            {
                if (File.Exists(rutaCompleta))
                    File.Delete(rutaCompleta);
            }
        }

        [Fact]
        public async Task EscribirArchivo_CicloCompleto_EscrituraDuplicadoSobrescrituraYAnexado()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string archivoTemporal = Path.Combine(Path.GetTempPath(), $"test_write_{Guid.NewGuid():N}.txt");
            try
            {
                // Act 1: Escritura de archivo nuevo
                string res1 = await herramientas.EscribirArchivoAsync(sshd.Destino.Clave, archivoTemporal, "Primera línea\n");

                // Assert 1: Mensaje de confirmación y contenido verificado con LeerArchivoAsync
                _ = res1.Should().Contain("Archivo escrito correctamente");
                string leido1 = await herramientas.LeerArchivoAsync(sshd.Destino.Clave, archivoTemporal);
                _ = leido1.Should().Contain("Primera línea\n");

                // Act 2: Intento de reescribir sin overwrite ni append debe fallar
                NativeSshException errorDuplicado = (await FluentActions.Awaiting(() =>
                    herramientas.EscribirArchivoAsync(sshd.Destino.Clave, archivoTemporal, "Intento sin overwrite", overwrite: false, append: false))
                    .Should().ThrowAsync<NativeSshException>()).Which;

                // Assert 2: Mensaje de error indica que el archivo ya existe y sugiere overwrite/append
                _ = errorDuplicado.Message.Should().Contain("el archivo ya existe");
                _ = errorDuplicado.Message.Should().Contain("overwrite=true");

                // Act 3: Sobrescritura con overwrite = true
                string res3 = await herramientas.EscribirArchivoAsync(sshd.Destino.Clave, archivoTemporal, "Contenido sobrescrito\n", overwrite: true);

                // Assert 3: Archivo contiene únicamente el contenido sobrescrito
                _ = res3.Should().Contain("Archivo escrito correctamente");
                string leido3 = await herramientas.LeerArchivoAsync(sshd.Destino.Clave, archivoTemporal);
                _ = leido3.Should().Contain("Contenido sobrescrito\n");
                _ = leido3.Should().NotContain("Primera línea");

                // Act 4: Anexado con append = true
                string res4 = await herramientas.EscribirArchivoAsync(sshd.Destino.Clave, archivoTemporal, "Segunda línea anexada\n", append: true);

                // Assert 4: Archivo contiene el contenido concatenado
                _ = res4.Should().Contain("Contenido anexado correctamente");
                string leido4 = await herramientas.LeerArchivoAsync(sshd.Destino.Clave, archivoTemporal);
                _ = leido4.Should().Contain("Contenido sobrescrito\nSegunda línea anexada\n");
            }
            finally
            {
                if (File.Exists(archivoTemporal))
                    File.Delete(archivoTemporal);
            }
        }

        [Fact]
        public async Task EscribirArchivo_RutaConCaracteresEspecialesYEspacios_EsSegura()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string nombreEspecial = $"--write 'comillas' $HOME {Guid.NewGuid():N}.txt";
            string rutaCompleta = Path.Combine(Path.GetTempPath(), nombreEspecial);
            try
            {
                // Act: Escribir archivo con caracteres especiales
                string res = await herramientas.EscribirArchivoAsync(sshd.Destino.Clave, rutaCompleta, "Contenido seguro");

                // Assert: Escrito correctamente y leíble sin evaluar subshells
                _ = res.Should().Contain("Archivo escrito correctamente");
                string leido = await herramientas.LeerArchivoAsync(sshd.Destino.Clave, rutaCompleta);
                _ = leido.Should().Contain("Contenido seguro");
            }
            finally
            {
                if (File.Exists(rutaCompleta))
                    File.Delete(rutaCompleta);
            }
        }

        [Fact]
        public async Task EscribirArchivo_OverwriteYAppendSimultaneos_LanzaExcepcion()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                herramientas.EscribirArchivoAsync(sshd.Destino.Clave, "archivo.txt", "contenido", overwrite: true, append: true))
                .Should().ThrowAsync<NativeSshException>()).Which;

            _ = error.Message.Should().Contain("No se puede especificar 'overwrite=true' y 'append=true' simultáneamente.");
        }

        [Fact]
        public async Task EscribirArchivo_RutaEsDirectorioExistente_LanzaExcepcion()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                herramientas.EscribirArchivoAsync(sshd.Destino.Clave, "/tmp", "contenido"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            _ = error.Message.Should().Be("Error: la ruta es un directorio: /tmp");
        }

        [Fact]
        public async Task EscribirArchivo_AppendEnArchivoInexistente_CreaYEscribeCorrectamente()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string archivoTemporal = Path.Combine(Path.GetTempPath(), $"test_append_nuevo_{Guid.NewGuid():N}.txt");
            try
            {
                // Act: append = true sobre un archivo que no existía previamente
                string res = await herramientas.EscribirArchivoAsync(sshd.Destino.Clave, archivoTemporal, "Contenido inicial append\n", append: true);

                // Assert: Se crea y escribe correctamente
                _ = res.Should().Contain("Contenido anexado correctamente");
                string leido = await herramientas.LeerArchivoAsync(sshd.Destino.Clave, archivoTemporal);
                _ = leido.Should().Contain("Contenido inicial append\n");
            }
            finally
            {
                if (File.Exists(archivoTemporal))
                    File.Delete(archivoTemporal);
            }
        }

        [Fact]
        public async Task Buscar_ExcluyeDirectoriosRuidososComoNodeModules()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string dirTemporal = Path.Combine(Path.GetTempPath(), $"test_search_excl_{Guid.NewGuid():N}");
            string subNodeModules = Path.Combine(dirTemporal, "node_modules");
            string archivoRaiz = Path.Combine(dirTemporal, "archivo_raiz.js");
            string archivoOculto = Path.Combine(subNodeModules, "modulo.js");

            try
            {
                _ = Directory.CreateDirectory(subNodeModules);
                await File.WriteAllTextAsync(archivoRaiz, "const CLAVE_SECRETA = \"val1\";\n");
                await File.WriteAllTextAsync(archivoOculto, "const CLAVE_SECRETA = \"val2\";\n");

                // Act
                string res = await herramientas.BuscarAsync(sshd.Destino.Clave, "CLAVE_SECRETA", dirTemporal);

                // Assert
                _ = res.Should().Contain("archivo_raiz.js");
                _ = res.Should().NotContain("node_modules");
                _ = res.Should().NotContain("modulo.js");
            }
            finally
            {
                if (Directory.Exists(dirTemporal))
                    Directory.Delete(dirTemporal, recursive: true);
            }
        }

        [Fact]
        public async Task Buscar_SinCoincidencias_DevuelveMensajeInformativoSinError()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string dirTemporal = Path.Combine(Path.GetTempPath(), $"test_search_vacio_{Guid.NewGuid():N}");
            try
            {
                _ = Directory.CreateDirectory(dirTemporal);
                string archivo = Path.Combine(dirTemporal, "archivo.txt");
                await File.WriteAllTextAsync(archivo, "hola mundo\n");

                // Act
                string res = await herramientas.BuscarAsync(sshd.Destino.Clave, "patron_inexistente_xyz_123", dirTemporal);

                // Assert
                _ = res.Should().Be($"No se encontraron coincidencias para el patrón: 'patron_inexistente_xyz_123' en '{dirTemporal}'.");
            }
            finally
            {
                if (Directory.Exists(dirTemporal))
                    Directory.Delete(dirTemporal, recursive: true);
            }
        }

        [Fact]
        public async Task Buscar_RutaInexistente_LanzaExcepcion()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string rutaInexistente = $"/tmp/ruta_inexistente_{Guid.NewGuid():N}";

            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                herramientas.BuscarAsync(sshd.Destino.Clave, "patron", rutaInexistente))
                .Should().ThrowAsync<NativeSshException>()).Which;

            _ = error.Message.Should().Contain("la ruta de búsqueda no existe");
        }

        [Fact]
        public async Task Buscar_SuperaLimite_AnexaAviso()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string dirTemporal = Path.Combine(Path.GetTempPath(), $"test_search_limite_{Guid.NewGuid():N}");
            try
            {
                _ = Directory.CreateDirectory(dirTemporal);
                string contenido = "coincidencia 1\ncoincidencia 2\ncoincidencia 3\n";
                await File.WriteAllTextAsync(Path.Combine(dirTemporal, "archivo.txt"), contenido);

                // Act
                string res = await herramientas.BuscarAsync(sshd.Destino.Clave, "coincidencia", dirTemporal, maxResultados: 2);

                // Assert
                _ = res.Should().Contain("[Límite alcanzado: se muestran 2 coincidencias.");
                _ = res.Should().Contain("coincidencia 2");
                _ = res.Should().NotContain("coincidencia 3");
            }
            finally
            {
                if (Directory.Exists(dirTemporal))
                    Directory.Delete(dirTemporal, recursive: true);
            }
        }

        [Fact]
        public async Task Buscar_PatronQueIniciaConGuion_NoInterpretaComoFlagYEncuentraCoincidencias()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string dirTemporal = Path.Combine(Path.GetTempPath(), $"test_search_guion_{Guid.NewGuid():N}");
            try
            {
                _ = Directory.CreateDirectory(dirTemporal);
                string contenido = "ejecutar --version para ver version\nconfigurar --config=prod.json ahora\n";
                await File.WriteAllTextAsync(Path.Combine(dirTemporal, "archivo.txt"), contenido);

                // Act
                string res = await herramientas.BuscarAsync(sshd.Destino.Clave, "--version", dirTemporal);

                // Assert
                _ = res.Should().Contain("ejecutar --version para ver version");
                _ = res.Should().NotContain("GNU grep");
            }
            finally
            {
                if (Directory.Exists(dirTemporal))
                    Directory.Delete(dirTemporal, recursive: true);
            }
        }

        [Fact]
        public async Task Buscar_MultiplesArchivos_TruncadoEnCSharpEntregaExactamenteLimiteLineas()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string dirTemporal = Path.Combine(Path.GetTempPath(), $"test_search_truncado_{Guid.NewGuid():N}");
            try
            {
                _ = Directory.CreateDirectory(dirTemporal);
                // 3 archivos, cada uno con 3 coincidencias = 9 coincidencias en total
                await File.WriteAllTextAsync(Path.Combine(dirTemporal, "a1.txt"), "marca 1\nmarca 2\nmarca 3\n");
                await File.WriteAllTextAsync(Path.Combine(dirTemporal, "a2.txt"), "marca 4\nmarca 5\nmarca 6\n");
                await File.WriteAllTextAsync(Path.Combine(dirTemporal, "a3.txt"), "marca 7\nmarca 8\nmarca 9\n");

                // Act: pedimos límite de 4 resultados
                const int limite = 4;
                string res = await herramientas.BuscarAsync(sshd.Destino.Clave, "marca", dirTemporal, maxResultados: limite);

                // Assert
                _ = res.Should().Contain($"[Límite alcanzado: se muestran {limite} coincidencias.");
                string textoCoincidencias = res.Split("\n\n[Límite alcanzado")[0];
                string[] lineas = textoCoincidencias.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                _ = lineas.Length.Should().Be(limite);
            }
            finally
            {
                if (Directory.Exists(dirTemporal))
                    Directory.Delete(dirTemporal, recursive: true);
            }
        }

        [Fact]
        public async Task Buscar_RegexInvalido_LanzaExcepcionConDiagnosticoDeGrep()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string dirTemporal = Path.Combine(Path.GetTempPath(), $"test_search_regex_invalido_{Guid.NewGuid():N}");
            try
            {
                _ = Directory.CreateDirectory(dirTemporal);
                await File.WriteAllTextAsync(Path.Combine(dirTemporal, "archivo.txt"), "texto de prueba\n");

                // Act & Assert
                NativeSshException error = (await FluentActions.Awaiting(() =>
                    herramientas.BuscarAsync(sshd.Destino.Clave, "[unclosed", dirTemporal))
                    .Should().ThrowAsync<NativeSshException>()).Which;

                _ = error.Message.Should().Contain("grep:");
                _ = error.Message.Should().Contain("[");
            }
            finally
            {
                if (Directory.Exists(dirTemporal))
                    Directory.Delete(dirTemporal, recursive: true);
            }
        }

        [Fact]
        public async Task FijarDirectorio_CicloCompleto_ValidacionPersistenciaYReset()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string dirTemporal = Path.Combine(Path.GetTempPath(), $"ns_test_setcwd_dir_{Guid.NewGuid():N}");
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            try
            {
                _ = Directory.CreateDirectory(dirTemporal);

                // Act 1: Fijar directorio a dirTemporal
                string resFijar = await herramientas.FijarDirectorioAsync(sshd.Destino.Clave, dirTemporal);

                // Assert 1: Confirmación y pwd contiene dirTemporal
                _ = resFijar.Should().Contain(dirTemporal);
                ResultadoComando pwd1 = await herramientas.EjecutarAsync(sshd.Destino.Clave, "pwd");
                _ = pwd1.Salida.Trim().Should().Be(dirTemporal);

                // Act 2: Restablecer a HOME con null
                string resReset = await herramientas.FijarDirectorioAsync(sshd.Destino.Clave, null);

                // Assert 2: Mensaje de confirmación y pwd devuelve HOME
                _ = resReset.Should().Be("Directorio de trabajo persistente restablecido a $HOME (~).");
                ResultadoComando pwd2 = await herramientas.EjecutarAsync(sshd.Destino.Clave, "pwd");
                _ = pwd2.Salida.Trim().Should().Be(home);
                _ = pwd2.Salida.Should().NotContain(dirTemporal);

                // Act 3: Intentar fijar una ruta inexistente
                string rutaInexistente = $"/ruta/inexistente/test_{Guid.NewGuid():N}";
                NativeSshException error = (await FluentActions.Awaiting(() =>
                    herramientas.FijarDirectorioAsync(sshd.Destino.Clave, rutaInexistente))
                    .Should().ThrowAsync<NativeSshException>()).Which;

                // Assert 3: Error indica que la ruta no existe
                _ = error.Message.Should().Contain("la ruta no existe");
            }
            finally
            {
                if (Directory.Exists(dirTemporal))
                    Directory.Delete(dirTemporal, recursive: true);
            }
        }

        [Fact]
        public async Task ReemplazarContenidoArchivo_CicloCompleto_ModificacionQuirurgicaYSwapAtomico()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string archivoTemporal = Path.Combine(Path.GetTempPath(), $"ns_test_replace_{Guid.NewGuid():N}.txt");
            try
            {
                // Act 1: Escribir archivo de 10 líneas
                string contenidoInicial = string.Concat(Enumerable.Range(1, 10).Select(i => $"linea {i}\n"));
                _ = await herramientas.EscribirArchivoAsync(sshd.Destino.Clave, archivoTemporal, contenidoInicial);

                // Act 2: Reemplazo quirúrgico en líneas 4 a 6
                string target = "linea 4\nlinea 5\nlinea 6";
                string replacement = "linea 4 mod\nlinea 5 mod\nlinea 6 mod";
                string resMod = await herramientas.ReemplazarContenidoArchivoAsync(
                    sshd.Destino.Clave,
                    archivoTemporal,
                    target,
                    replacement,
                    lineaInicio: 4,
                    lineaFin: 6);

                // Assert 2: Mensaje de confirmación
                _ = resMod.Should().Contain("Archivo modificado quirúrgicamente con éxito");
                _ = resMod.Should().Contain("1 reemplazo(s) realizado(s)");

                // Assert 3: Leer y comprobar que líneas 1-3 y 7-10 no sufrieron alteración
                string leido = await herramientas.LeerArchivoAsync(sshd.Destino.Clave, archivoTemporal);
                _ = leido.Should().Contain("linea 1\nlinea 2\nlinea 3\n");
                _ = leido.Should().Contain("linea 4 mod\nlinea 5 mod\nlinea 6 mod\n");
                _ = leido.Should().Contain("linea 7\nlinea 8\nlinea 9\nlinea 10\n");

                // Act 4 & Assert 4: Probar targetContent inexistente
                NativeSshException errorNoExiste = (await FluentActions.Awaiting(() =>
                    herramientas.ReemplazarContenidoArchivoAsync(
                        sshd.Destino.Clave,
                        archivoTemporal,
                        "texto inexistente",
                        "nuevo",
                        lineaInicio: 1,
                        lineaFin: 5))
                    .Should().ThrowAsync<NativeSshException>()).Which;

                _ = errorNoExiste.Message.Should().Contain("No se encontró 'targetContent'");

                // Act 5 & Assert 5: Probar allowMultiple = true
                _ = await herramientas.EscribirArchivoAsync(sshd.Destino.Clave, archivoTemporal, "item_dup\nitem_dup\n", append: true);
                string resDup = await herramientas.ReemplazarContenidoArchivoAsync(
                    sshd.Destino.Clave,
                    archivoTemporal,
                    "item_dup",
                    "item_ok",
                    lineaInicio: 10,
                    lineaFin: 13,
                    allowMultiple: true);

                _ = resDup.Should().Contain("2 reemplazo(s) realizado(s)");
                string leidoDup = await herramientas.LeerArchivoAsync(sshd.Destino.Clave, archivoTemporal);
                _ = leidoDup.Should().NotContain("item_dup");
                _ = leidoDup.Should().Contain("item_ok\nitem_ok\n");
            }
            finally
            {
                if (File.Exists(archivoTemporal))
                    File.Delete(archivoTemporal);
            }
        }

        [Fact]
        public async Task ListarDirectorios_ExcluyeDirectoriosRuidososYDevuelveRutasRelativas()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string dirTemporal = Path.Combine(Path.GetTempPath(), $"ns_tree_test_{Guid.NewGuid():N}");
            try
            {
                _ = Directory.CreateDirectory(Path.Combine(dirTemporal, "src", "models"));
                _ = Directory.CreateDirectory(Path.Combine(dirTemporal, "src", "views"));
                _ = Directory.CreateDirectory(Path.Combine(dirTemporal, "node_modules", "pkg"));
                _ = Directory.CreateDirectory(Path.Combine(dirTemporal, ".git", "objects"));
                _ = Directory.CreateDirectory(Path.Combine(dirTemporal, "__pycache__"));
                _ = Directory.CreateDirectory(Path.Combine(dirTemporal, "src", "__pycache__"));

                // Act
                string res = await herramientas.ListarDirectoriosAsync(sshd.Destino.Clave, dirTemporal, 3, 100);

                // Assert
                _ = res.Should().Contain(".git");
                _ = res.Should().NotContain(".git/objects");
                _ = res.Should().Contain("node_modules");
                _ = res.Should().NotContain("node_modules/pkg");
                _ = res.Should().NotContain("__pycache__");
                _ = res.Should().Contain("src");
                _ = res.Should().Contain("src/models");
                _ = res.Should().Contain("src/views");
            }
            finally
            {
                if (Directory.Exists(dirTemporal))
                    Directory.Delete(dirTemporal, recursive: true);
            }
        }

        [Fact]
        public async Task Buscar_ConExtension_FiltraPorExtensionCorrecta()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string dirTemporal = Path.Combine(Path.GetTempPath(), $"ns_ext_test_{Guid.NewGuid():N}");
            try
            {
                _ = Directory.CreateDirectory(dirTemporal);
                string f1 = Path.Combine(dirTemporal, "f1.py");
                string f2 = Path.Combine(dirTemporal, "f2.txt");
                await File.WriteAllTextAsync(f1, "CLAVE_MAGICA\n");
                await File.WriteAllTextAsync(f2, "CLAVE_MAGICA\n");

                // Act
                string res = await herramientas.BuscarAsync(sshd.Destino.Clave, "CLAVE_MAGICA", dirTemporal, extension: "py");

                // Assert
                _ = res.Should().Contain("f1.py");
                _ = res.Should().NotContain("f2.txt");
            }
            finally
            {
                if (Directory.Exists(dirTemporal))
                    Directory.Delete(dirTemporal, recursive: true);
            }
        }

        [Fact]
        public async Task BuscarPorNombre_EncuentraArchivosPorPatronGlob()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string dirTemporal = Path.Combine(Path.GetTempPath(), $"ns_find_test_{Guid.NewGuid():N}");
            try
            {
                _ = Directory.CreateDirectory(dirTemporal);
                _ = Directory.CreateDirectory(Path.Combine(dirTemporal, "node_modules"));
                await File.WriteAllTextAsync(Path.Combine(dirTemporal, "docker-compose.yml"), "version: '3'\n");
                await File.WriteAllTextAsync(Path.Combine(dirTemporal, "app.service"), "[Unit]\n");
                await File.WriteAllTextAsync(Path.Combine(dirTemporal, "node_modules", "bad.service"), "[Unit]\n");

                // Act
                string res = await herramientas.BuscarPorNombreAsync(sshd.Destino.Clave, "*.service", dirTemporal, tipo: "file");

                // Assert
                _ = res.Should().Contain("app.service");
                _ = res.Should().NotContain("bad.service");
                _ = res.Should().NotContain("docker-compose.yml");
            }
            finally
            {
                if (Directory.Exists(dirTemporal))
                    Directory.Delete(dirTemporal, recursive: true);
            }
        }

        [Fact]
        public async Task ConsultarMetadatos_ArchivoExistenteEInexistente()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string archivoTemporal = Path.Combine(Path.GetTempPath(), $"ns_stat_test_{Guid.NewGuid():N}.txt");
            string rutaInexistente = Path.Combine(Path.GetTempPath(), $"ns_no_existe_{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(archivoTemporal, "123456789012345"); // 15 bytes

                // Act 1: Archivo existente
                string resExistente = await herramientas.ConsultarMetadatosAsync(sshd.Destino.Clave, archivoTemporal);
                using JsonDocument docExistente = JsonDocument.Parse(resExistente);
                _ = docExistente.RootElement.GetProperty("existe").GetBoolean().Should().BeTrue();
                _ = docExistente.RootElement.GetProperty("tamanoBytes").GetInt64().Should().Be(15);
                _ = docExistente.RootElement.GetProperty("tamanoHumano").GetString().Should().Be("15 B");

                // Act 2: Archivo inexistente
                string resInexistente = await herramientas.ConsultarMetadatosAsync(sshd.Destino.Clave, rutaInexistente);
                using JsonDocument docInexistente = JsonDocument.Parse(resInexistente);
                _ = docInexistente.RootElement.GetProperty("existe").GetBoolean().Should().BeFalse();
                _ = docInexistente.RootElement.GetProperty("mensaje").GetString().Should().Contain("no existe");
            }
            finally
            {
                if (File.Exists(archivoTemporal))
                    File.Delete(archivoTemporal);
            }
        }

        [Fact]
        public async Task BuscarPorNombre_CarpetasExcluidas_LocalizaConPrintPrune()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string dirTemporal = Path.Combine(Path.GetTempPath(), $"ns_find_prune_{Guid.NewGuid():N}");
            try
            {
                _ = Directory.CreateDirectory(dirTemporal);
                _ = Directory.CreateDirectory(Path.Combine(dirTemporal, ".git"));
                _ = Directory.CreateDirectory(Path.Combine(dirTemporal, "node_modules"));
                _ = Directory.CreateDirectory(Path.Combine(dirTemporal, ".venv"));
                _ = Directory.CreateDirectory(Path.Combine(dirTemporal, "__pycache__"));
                _ = Directory.CreateDirectory(Path.Combine(dirTemporal, "src"));

                await File.WriteAllTextAsync(Path.Combine(dirTemporal, ".git", "config"), "test");
                await File.WriteAllTextAsync(Path.Combine(dirTemporal, "node_modules", "package.json"), "{}");
                await File.WriteAllTextAsync(Path.Combine(dirTemporal, "src", "app.cs"), "class App {}");

                // Act 1: Buscar .git como directorio debe devolver .git pero no su contenido
                string resGit = await herramientas.BuscarPorNombreAsync(sshd.Destino.Clave, ".git", dirTemporal, tipo: "dir");
                _ = resGit.Should().Be(".git");

                // Act 2: Buscar node_modules como directorio
                string resNodeModules = await herramientas.BuscarPorNombreAsync(sshd.Destino.Clave, "node_modules", dirTemporal, tipo: "dir");
                _ = resNodeModules.Should().Be("node_modules");

                // Act 3: Buscar .venv como directorio
                string resVenv = await herramientas.BuscarPorNombreAsync(sshd.Destino.Clave, ".venv", dirTemporal, tipo: "dir");
                _ = resVenv.Should().Be(".venv");

                // Act 4: Buscar __pycache__ como directorio
                string resPycache = await herramientas.BuscarPorNombreAsync(sshd.Destino.Clave, "__pycache__", dirTemporal, tipo: "dir");
                _ = resPycache.Should().Be("__pycache__");

                // Act 5: Buscar archivos normales excluyendo el contenido de carpetas podadas
                string resCs = await herramientas.BuscarPorNombreAsync(sshd.Destino.Clave, "*.cs", dirTemporal, tipo: "file");
                _ = resCs.Should().Be("src/app.cs");
                _ = resCs.Should().NotContain(".git");
                _ = resCs.Should().NotContain("node_modules");
            }
            finally
            {
                if (Directory.Exists(dirTemporal))
                    Directory.Delete(dirTemporal, recursive: true);
            }
        }

        [Fact]
        public async Task ListarDirectorios_ConIncluirArchivos_DevuelveArchivosYCarpetas()
        {
            // Arrange
            OpcionesNativeSsh opciones = sshd.Opciones();
            FabricaConexionSsh fabrica = sshd.Fabrica(opciones);
            await using GestorSesiones gestor = new(
                fabrica,
                new LimitadorConexiones(opciones, TimeProvider.System),
                new RegistroPeticiones(opciones, TimeProvider.System),
                opciones,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);

            string dirTemporal = Path.Combine(Path.GetTempPath(), $"ns_list_dir_{Guid.NewGuid():N}");
            try
            {
                _ = Directory.CreateDirectory(dirTemporal);
                _ = Directory.CreateDirectory(Path.Combine(dirTemporal, "src"));
                _ = Directory.CreateDirectory(Path.Combine(dirTemporal, ".git"));
                _ = Directory.CreateDirectory(Path.Combine(dirTemporal, "node_modules"));
                _ = Directory.CreateDirectory(Path.Combine(dirTemporal, "__pycache__"));

                await File.WriteAllTextAsync(Path.Combine(dirTemporal, "README.md"), "# Test");
                await File.WriteAllTextAsync(Path.Combine(dirTemporal, "src", "app.cs"), "class App {}");
                await File.WriteAllTextAsync(Path.Combine(dirTemporal, ".git", "config"), "test");
                await File.WriteAllTextAsync(Path.Combine(dirTemporal, "node_modules", "dep.js"), "test");
                await File.WriteAllTextAsync(Path.Combine(dirTemporal, "__pycache__", "module.cpython-312.pyc"), "bytecode");

                // Act 1: Por defecto (solo directorios, excluyendo podados)
                string resDirs = await herramientas.ListarDirectoriosAsync(sshd.Destino.Clave, dirTemporal, incluirArchivos: false);
                _ = resDirs.Should().Be(".git\nnode_modules\nsrc");
                _ = resDirs.Should().NotContain(".git/config");
                _ = resDirs.Should().NotContain("node_modules/dep.js");
                _ = resDirs.Should().NotContain("__pycache__");
                _ = resDirs.Should().NotContain("README.md");

                // Act 2: Con incluirArchivos = true
                string resTodos = await herramientas.ListarDirectoriosAsync(sshd.Destino.Clave, dirTemporal, incluirArchivos: true);
                _ = resTodos.Should().Be(".git\nnode_modules\nREADME.md\nsrc\nsrc/app.cs");
                _ = resTodos.Should().Contain(".git");
                _ = resTodos.Should().Contain("node_modules");
                _ = resTodos.Should().Contain("README.md");
                _ = resTodos.Should().Contain("src");
                _ = resTodos.Should().Contain("src/app.cs");
                _ = resTodos.Should().NotContain(".git/config");
                _ = resTodos.Should().NotContain("node_modules/dep.js");
                _ = resTodos.Should().NotContain("__pycache__");
            }
            finally
            {
                if (Directory.Exists(dirTemporal))
                    Directory.Delete(dirTemporal, recursive: true);
            }
        }
    }
}
