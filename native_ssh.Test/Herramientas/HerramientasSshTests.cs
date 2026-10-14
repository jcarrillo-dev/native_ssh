using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Sesion.Falsos;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Herramientas
{
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "El gestor se libera en IAsyncLifetime.DisposeAsync.")]
    public sealed class HerramientasSshTests : IAsyncLifetime
    {
        private readonly FabricaFalsa _fabrica = new(Guion);
        private readonly GestorSesiones _gestor;
        private readonly HerramientasSsh _herramientas;

        public HerramientasSshTests()
        {
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana", MaxBytesPorRespuesta = 16, EsperaTrasCtrlC = TimeSpan.FromMilliseconds(300) };
            _gestor = new GestorSesiones(_fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            _herramientas = new HerramientasSsh(_gestor, opciones);
        }

        private static RespuestaFalsa Guion(string comando) =>
            comando.Contains("perdido_mudo") ? new(string.Empty, 0, ComportamientoFalsoEnum.Salir) :
            comando.Contains("colgado_") ? new("parcial\n", 0, ComportamientoFalsoEnum.Colgado) :
            comando.Contains("crlf.txt") ? new("[Líneas 1-2 de 2]\nuno\r\ndos\r\n", 0) :
            comando.Contains("latin1_lectura.txt") ? new("", 0, ComportamientoFalsoEnum.Normal, [.. "[Líneas 1-1 de 1]\na"u8, 0xF1, .. "o\n"u8]) :
            comando.Contains("enorme") ? SimularSalidaEnorme(comando) :
            comando.Contains("error.txt") ? new("Error: el archivo no existe: error.txt\n", 1) :
            comando.Contains("| tail -n +") ? SimularLectura(comando) :
            comando.Contains("base64 -d") ? SimularEscritura(comando) :
            comando.Contains("grep -rnI") ? SimularBusqueda(comando) :
            comando.Contains("-o -type d -print0") || comando.Contains("-o -print0 2>/dev/null") ? SimularListDir(comando) :
            comando.Contains("-print0 -prune") ? SimularFind(comando) :
            comando.Contains("find -L . -maxdepth") ? SimularFind(comando) :
            comando.Contains("stat -c") ? SimularStat(comando) :
            comando.Contains("pwd -P") ? SimularValidacionCwd(comando) :
            comando.Contains("unset __NS_CWD") ? new("/home/ana\n", 0) :
            comando.StartsWith("export __NS_CWD=", StringComparison.Ordinal) ? new("", 0) :
            comando.Contains("cat -- \"$ruta\" | base64", StringComparison.Ordinal) ? SimularLecturaBase64(comando) :
            comando switch
            {
                "colgado" => new("empezando\n", 0, ComportamientoFalsoEnum.Colgado),
                "ignora_ctrlc" => new("empezando\n", 0, ComportamientoFalsoEnum.IgnoraCtrlC),
                "pregunta" => new("¿Nombre? ", 0, ComportamientoFalsoEnum.PideEntrada),
                "largo" => new(string.Concat(Enumerable.Range(0, 10).Select(i => $"linea {i}\n"))),
                _ => new($"eco:{comando}\n", 2)
            };

        private static RespuestaFalsa SimularLecturaBase64(string comando) =>
            comando.Contains("archivo_latin1.txt", StringComparison.Ordinal) ? EnmarcarBase64(comando, [.. "caf"u8, 0xE9, .. "\nlinea 2\n"u8]) :
            comando.Contains("archivo_binario.bin", StringComparison.Ordinal) ? EnmarcarBase64(comando, [.. "linea 1\n"u8, 0x00, 0x01, .. "\n"u8]) :
            comando.Contains("archivo_bom.txt", StringComparison.Ordinal) ? EnmarcarBase64(comando, [0xEF, 0xBB, 0xBF, .. "linea 1\nlinea 2\n"u8]) :
            comando.Contains("archivo_windows.txt", StringComparison.Ordinal) ? EnmarcarBase64(comando, Encoding.UTF8.GetBytes(ContenidoCrlf)) :
            comando.Contains("archivo_separadores.txt", StringComparison.Ordinal) ? EnmarcarBase64(comando, Encoding.UTF8.GetBytes(ContenidoConSeparadores)) :
            comando.Contains("archivo_inexistente.txt", StringComparison.Ordinal)
                ? new("Error: el archivo no existe: archivo_inexistente.txt\n", 1)
                : comando.Contains("es_directorio/", StringComparison.Ordinal)
                    ? new("Error: la ruta es un directorio: es_directorio/\n", 1)
                    : comando.Contains("archivo_multiples.txt", StringComparison.Ordinal)
                        ? EnmarcarBase64(comando, Encoding.UTF8.GetBytes("linea 1\nrepetido\nrepetido\nlinea 4\n"))
                        : EnmarcarBase64(comando, Encoding.UTF8.GetBytes("linea 1\nlinea 2\nlinea 3\nlinea 4\nlinea 5\n"));

        private const string ContenidoCrlf = "uno\r\ndos\r\ntres\r\n";
        private const string ContenidoConSeparadores = "uno\fdos\u0085tres\u2028cuatro\u2029cinco\rseis\nsiete\n";
        private const string SeparadoresExoticos = "\f\u0085\u2028\u2029\r";

        // Responde como el script real: el base64 entre los marcadores con el id de la petición y el tamaño del archivo.
        private static RespuestaFalsa EnmarcarBase64(string comando, byte[] bytes)
        {
            int posicion = comando.IndexOf("INICIO ", StringComparison.Ordinal) + "INICIO ".Length;
            string id = comando[posicion..(posicion + 32)];
            return new($"__NS_B64_INICIO_{id}__\n{Convert.ToBase64String(bytes)}\n__NS_B64_FIN_{id}__ {bytes.Length}\n", 0);
        }

        // Más de 16 KiB por encima de MaxBytesSalidaPorPeticion de las pruebas de descarte: el principio se pierde.
        private const int BytesSalidaEnorme = 64 * 1024;
        private const int MaxBytesSalidaPequena = 20_000;

        private static RespuestaFalsa SimularSalidaEnorme(string comando)
        {
            string texto = string.Concat(Enumerable.Range(0, BytesSalidaEnorme / 32).Select(i => $"linea {i:D6} xxxxxxxxxxxxxxxxxx\n"));
            return comando.Contains("base64", StringComparison.Ordinal) && !comando.Contains("base64 -d", StringComparison.Ordinal)
                ? new(Convert.ToBase64String(Encoding.UTF8.GetBytes(texto), Base64FormattingOptions.InsertLineBreaks).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", 0)
                : new(texto, 0);
        }

        private static (FabricaFalsa Fabrica, GestorSesiones Gestor, HerramientasSsh Herramientas) CrearConSalidaPequena()
        {
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana", MaxBytesSalidaPorPeticion = MaxBytesSalidaPequena, EsperaTrasCtrlC = TimeSpan.FromMilliseconds(300) };
            FabricaFalsa fabrica = new(Guion, tamanoTrozo: 4096);
            GestorSesiones gestor = new(fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            return (fabrica, gestor, new HerramientasSsh(gestor, opciones));
        }

        private static RespuestaFalsa SimularValidacionCwd(string comando) =>
            comando.Contains("ruta_inexistente")
                ? new("Error: la ruta no existe: ruta_inexistente\n", 1)
                : comando.Contains("es_archivo.txt")
                    ? new("Error: la ruta no es un directorio: es_archivo.txt\n", 1)
                    : comando.Contains("/var/www/proyecto")
                        ? MarcarDirectorio(comando, "/var/www/proyecto")
                        : MarcarDirectorio(comando, "/tmp/trabajo");

        // Responde como el script real: la ruta resuelta en base64 tras el marcador con el id de la petición.
        private static RespuestaFalsa MarcarDirectorio(string comando, string directorio)
        {
            const string Formato = "__NS_CWD_%s__ %s\\n' ";
            int posicion = comando.IndexOf(Formato, StringComparison.Ordinal) + Formato.Length;
            string id = comando[posicion..(posicion + 32)];
            return new($"__NS_CWD_{id}__ {Convert.ToBase64String(Encoding.UTF8.GetBytes(directorio))}\n", 0);
        }

        // Un stderr de 1500 caracteres (el aviso lo acota a 1000) y otro cuyo carácter 1000 es la primera mitad de un emoji
        // (un par sustituto): el corte no debe partirlo.
        private static readonly string ErrorLargo = "grep: " + string.Concat(Enumerable.Range(0, 1494).Select(i => (char)('a' + (i % 26))));
        private static readonly string ErrorConEmojiEnElCorte = "grep: " + new string('e', 993) + "\U0001F600" + "cola";

        // El script elige el motor en el servidor y lo anuncia en la primera línea; el falso simula 'grep -P'.
        private static RespuestaFalsa SimularBusqueda(string comando)
        {
            // Recorrido con find sin grep de GNU: sale con 0 aunque no haya coincidencias.
            if (comando.Contains("'vacio_con_find'"))
                return new("__NS_MOTOR:grep -E\n", 0);

            if (!comando.Contains("-\"$sintaxis\" -e"))
                return new("grep: error falta flag -e\n", 2);

            if (comando.Contains("ruta_inexistente"))
                return new("Error: la ruta de búsqueda no existe: ruta_inexistente\n", 1);

            const string Motor = "__NS_MOTOR:grep -P\n";
            if (comando.Contains("error_fatal"))
                return new(Motor + "grep: error de sintaxis en expresión regular\n", 2);

            if (comando.Contains("sin_coincidencias"))
                return new(Motor, 1);

            if (comando.Contains("parcial_sin_error"))
                return new(Motor + "archivo.txt:1:coincidencia encontrada\n", 2);

            if (comando.Contains("vacio_codigo_dos"))
                return new(Motor, 2);

            if (comando.Contains("parcial_con_error"))
                return new(Motor + "archivo.txt:1:coincidencia encontrada\ngrep: sub/x.txt: Input/output error\n", 2);

            if (comando.Contains("parcial_error_largo"))
                return new(Motor + "archivo.txt:1:coincidencia encontrada\n" + ErrorLargo + "\n", 2);

            if (comando.Contains("parcial_error_emoji"))
                return new(Motor + "archivo.txt:1:coincidencia encontrada\n" + ErrorConEmojiEnElCorte + "\n", 2);

            if (comando.Contains("parcial_limite"))
                return new(Motor + "a.txt:1:uno\nb.txt:2:dos\nc.txt:3:tres\ngrep: sub/x.txt: Input/output error\n", 2);

            if (comando.Contains("'extremos'"))
                return new(Motor + LineasConBlancos + "\n", 0);

            if (comando.Contains("muchas_coincidencias"))
            {
                string muchas = string.Concat(Enumerable.Range(1, 5).Select(i => $"archivo.txt:{i}:coincidencia {i}\n"));
                return new(Motor + muchas, 0);
            }

            return new(Motor + "archivo.txt:1:coincidencia encontrada\n", 0);
        }

        // Los listados de ssh_list_dir y ssh_find llegan del servidor como nombres terminados en NUL.
        private static RespuestaFalsa Nombres(params string[] nombres) => new(string.Concat(nombres.Select(nombre => nombre + "\0")), 0);

        // Nombres con caracteres de control, con comillas dobles y con una barra invertida (como una unidad de systemd).
        private static readonly string[] NombresConControl = ["a\nb.txt", "\"citado\".txt", "medio\"comilla.txt", "dev-disk-by\\x2duuid.swap", "tab\there", "\u0085nel\u007f", "barra\\ y\r"];

        private static RespuestaFalsa SimularListDir(string comando) =>
            comando.Contains("ruta_inexistente") ? new("Error: la ruta de exploración no existe: 'ruta_inexistente'\n", 1) :
            comando.Contains("'extremos'") ? Nombres(NombresConBlancos.Split('\n')) :
            comando.Contains("'control'") ? Nombres(NombresConControl) :
            comando.Contains("es_archivo.txt") ? new("Error: la ruta no es un directorio: 'es_archivo.txt'\n", 1) :
            comando.Contains("vacio") ? new("", 0) :
            comando.Contains("marcadores") && comando.Contains("-name .git") ? (comando.Contains("-o -print0 2>/dev/null")
                ? Nombres(".git", "node_modules", "Program.cs", "README.md", "src", "src/models", "src/views")
                : Nombres(".git", "node_modules", "src", "src/models", "src/views")) :
            comando.Contains("-o -print0 2>/dev/null") ? Nombres("Program.cs", "README.md", "src", "src/models", "src/views") :
            Nombres("src", "src/models", "src/views");

        private static RespuestaFalsa SimularFind(string comando) =>
            comando.Contains("ruta_inexistente") ? new("Error: la ruta de búsqueda no existe: 'ruta_inexistente'\n", 1) :
            comando.Contains("'extremos'") ? Nombres(NombresConBlancos.Split('\n')) :
            comando.Contains("'control'") ? Nombres(NombresConControl) :
            comando.Contains("sin_coincidencias") ? new("", 0) :
            comando.Contains("-name '.git'") ? Nombres(".git") :
            comando.Contains("-name 'node_modules'") ? Nombres("node_modules") :
            comando.Contains("-name '__pycache__'") ? Nombres("__pycache__") :
            comando.Contains("-name '.venv'") ? Nombres(".venv") :
            comando.Contains("-name '*.cs'") ? Nombres("Program.cs", "Herramientas/HerramientasSsh.cs") :
            Nombres("app.service", "nginx.conf");

        private static RespuestaFalsa SimularStat(string comando) =>
            comando.Contains("no_existe.txt")
                ? new("NO_EXISTE\n", 0)
                : comando.Contains("fichero_invalido")
                    ? new("error en stat\n", 1)
                    : comando.Contains("enlace.txt")
                        ? new("enlace simbólico|12|777|2026-10-05 01:00:00|ana|devs\nENLACE:/etc/real.conf\n", 0)
                        : new("archivo regular|2048|644|2026-10-05 01:00:00|ana|devs\n", 0);

        // Responde como el script real: los anuncios de InformeEscritura con el id de la petición ('marca=<id>' en el script).
        private static RespuestaFalsa SimularEscritura(string comando)
        {
            const string Asignacion = "\nmarca=";
            int posicion = comando.IndexOf(Asignacion, StringComparison.Ordinal) + Asignacion.Length;
            string marca = $"__NS_W_{comando[posicion..(posicion + 32)]}__ ";
            return comando.Contains("no_atomico.txt") ? new($"{marca}fase escribiendo\n{marca}fase escrito\n{marca}limpio\n", 0) :
                comando.Contains("salida_desbordada.txt") ? new($"{marca}fase escribiendo\n{new string('r', BytesSalidaEnorme)}\nError: algo falló\n{marca}limpio\n", 1) :
                comando.Contains("ya_existe.txt") && comando.Contains("el archivo ya existe")
                    ? new("Error: el archivo ya existe: ya_existe.txt. Especifica overwrite=true para sobrescribirlo o append=true para anexar.\n", 1)
                    : comando.Contains("base64 -d >> ", StringComparison.Ordinal)
                        ? new($"{marca}fase anexando\n{marca}fase anexado\n", 0)
                        : new($"{marca}fase colocando\n{marca}fase colocado\n{marca}limpio\n", 0);
        }

        private static RespuestaFalsa SimularLectura(string comando)
        {
            int idxCabecera = comando.LastIndexOf("echo \"[Líneas ", StringComparison.Ordinal);
            if (idxCabecera >= 0)
            {
                int finCabecera = comando.IndexOf(']', idxCabecera);
                if (finCabecera > idxCabecera)
                {
                    const int total = 10;
                    int fin = total;
                    int idxFin = comando.IndexOf("total < ", StringComparison.Ordinal);
                    if (idxFin >= 0)
                    {
                        int idxEspacio = comando.IndexOf(' ', idxFin + 8);
                        if (idxEspacio > idxFin && int.TryParse(comando[(idxFin + 8)..idxEspacio], out int parsedFin))
                            fin = parsedFin;
                    }

                    int finEfectivo = Math.Min(total, fin);
                    string cabecera = comando[(idxCabecera + 6)..(finCabecera + 1)]
                        .Replace("$total", total.ToString(CultureInfo.InvariantCulture))
                        .Replace("$fin_efectivo", finEfectivo.ToString(CultureInfo.InvariantCulture));
                    return new($"{cabecera}\nlinea 1\nlinea 2\nlinea 3\n", 0);
                }
            }

            return new("linea 1\nlinea 2\nlinea 3\n", 0);
        }

        // IAsyncLifetime (no IAsyncDisposable): es lo que xUnit v2 invoca al terminar cada prueba.
        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync() => _gestor.DisposeAsync().AsTask();

        [Fact]
        public async Task Exec_EsperaYDevuelveResultado()
        {
            // Arrange
            ResultadoComando resultado = await _herramientas.EjecutarAsync("servidor", "hola").ComoComando();

            // Act & Assert
            _ = resultado.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = resultado.CodigoSalida.Should().Be(2);
            _ = resultado.Salida.Should().Be("eco:hola\n");
            _ = resultado.Destino.Should().Be("ana@servidor:22");
            _ = resultado.HasMasSalida.Should().BeFalse();
            _ = resultado.SegundosSinSalida.Should().BeNull();
        }

        [Fact]
        public async Task Exec_SinEsperar_DevuelveEjecutando_YLuegoSePuedeCancelar()
        {
            // Arrange
            ResultadoComando parcial = await _herramientas.EjecutarAsync("servidor", "colgado", esperarSegundos: 0).ComoComando();
            await EsperasPrueba.EsperarHastaAsync(() => _gestor.BuscarPeticion(parcial.Id).Salida.Longitud > 0);
            ResultadoComando enCurso = await _herramientas.LeerSalidaAsync(parcial.Id).ComoComando();

            // Act
            ResultadoComando cancelado = await _herramientas.CancelarAsync(parcial.Id).ComoComando();

            // Assert
            _ = enCurso.CodigoSalida.Should().BeNull();
            _ = enCurso.Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
            _ = enCurso.SegundosSinSalida.Should().NotBeNull();
            _ = enCurso.Salida.Should().Be("empezando\n");
            _ = cancelado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = cancelado.CodigoSalida.Should().Be(130);
        }

        [Fact]
        public async Task Output_PaginaConSiguienteByte()
        {
            // Arrange
            ResultadoComando primera = await _herramientas.EjecutarAsync("servidor", "largo").ComoComando();
            string texto = primera.Salida;
            ResultadoComando actual = primera;
            while (actual.HasMasSalida)
            {
                actual = await _herramientas.LeerSalidaAsync(primera.Id, actual.SiguienteByte).ComoComando();
                texto += actual.Salida;
            }

            // Act & Assert
            _ = primera.HasMasSalida.Should().BeTrue();
            _ = primera.SiguienteByte.Should().Be(16);
            _ = texto.Should().Be(string.Concat(Enumerable.Range(0, 10).Select(i => $"linea {i}\n")));
        }

        [Fact]
        public async Task Input_DevuelveLaSalidaPosteriorAlEnvio()
        {
            // Arrange
            ResultadoComando pregunta = await _herramientas.EjecutarAsync("servidor", "pregunta", esperarSegundos: 0).ComoComando();
            await EsperasPrueba.EsperarHastaAsync(() => _gestor.BuscarPeticion(pregunta.Id).Salida.Longitud > 0);

            // Act
            ResultadoComando respuesta = await _herramientas.EnviarEntradaAsync(pregunta.Id, "ana", esperarSegundos: 5).ComoComando();

            // Assert
            _ = respuesta.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = respuesta.Salida.Should().Be("recibido:ana\n");
        }

        // Ciclos de «ssh_exec con esperarSegundos=0 + ssh_input con una línea de más, sin leer el final», con el comando
        // siguiente siempre encolado: cada petición trae lo que la anterior no leyó y la sesión la enlaza a la anterior, cuyo
        // aviso aún no ha dado nadie. Una vez dado, el enlace no se conserva: la cadena no crece con los ciclos (antes crecía 1
        // por ciclo, retenía todas las peticiones y cada respuesta la recorría entera, de forma recursiva, hasta desbordar la
        // pila del servidor).
        [Fact]
        public async Task CiclosConAvisoSinLeerElFinal_LaCadenaDeAvisosNoCrece()
        {
            // Arrange
            const int Ciclos = 200;
            (_, GestorSesiones gestor, HerramientasSsh herramientas) = CrearConSalidaPequena();
            await using GestorSesiones gestorLiberable = gestor;
            _ = await herramientas.EjecutarAsync("servidor", "hola", esperarSegundos: 10).ComoComando();
            ResultadoComando actual = await herramientas.EjecutarAsync("servidor", "pregunta", esperarSegundos: 0).ComoComando();
            int avisos = 0;
            Peticion? ultima = null;

            // Act
            for (int k = 0; k < Ciclos; k++)
            {
                ResultadoComando siguiente = await herramientas.EjecutarAsync("servidor", "pregunta", esperarSegundos: 0).ComoComando();
                Peticion peticion = gestor.BuscarPeticion(actual.Id);
                await EsperasPrueba.EsperarHastaAsync(() => peticion.IsComandoEnviado && peticion.Salida.Longitud > 0);
                ResultadoComando entrada = await herramientas.EnviarEntradaAsync(actual.Id, "ana\nuno", esperarSegundos: 0).ComoComando();
                avisos += entrada.Aviso?.Contains("no leyó", StringComparison.Ordinal) == true ? 1 : 0;
                await peticion.Finalizacion.WaitAsync(TimeSpan.FromSeconds(10));
                ultima = gestor.BuscarPeticion(siguiente.Id);
                actual = siguiente;
            }

            int largo = 0;
            for (Peticion? p = ultima?.AnteriorConTecleadoDescartado; p is not null; p = p.AnteriorConTecleadoDescartado)
                largo++;
            Peticion ultimaPregunta = gestor.BuscarPeticion(actual.Id);
            await EsperasPrueba.EsperarHastaAsync(() => ultimaPregunta.IsComandoEnviado && ultimaPregunta.Salida.Longitud > 0);
            _ = await herramientas.EnviarEntradaAsync(actual.Id, "ana", esperarSegundos: 5).ComoComando();
            ResultadoComando final = await herramientas.EjecutarAsync("servidor", "hola", esperarSegundos: 10).ComoComando();

            // Assert
            _ = largo.Should().BeLessThanOrEqualTo(2);
            _ = avisos.Should().BeGreaterThan(Ciclos / 2, "las respuestas de ssh_input siguen dando el aviso de la petición anterior");
            _ = final.Estado.Should().Be(EstadoPeticionEnum.Completado);
        }

        // Una cadena de avisos sin dar mucho más larga que el tope: el recorrido es iterativo (no desborda la pila) y da como
        // mucho Peticion.MaxAvisosTecleadoEnlazados avisos, los de las más recientes.
        [Fact]
        public void AvisosTecleadoDeAnteriores_CadenaMuyLarga_NoDesbordaYSeQuedaEnElTope()
        {
            // Arrange
            _ = DestinoSsh.TryParse("ana@servidor", "ana", out DestinoSsh destino);
            Peticion? anterior = null;
            for (int k = 0; k < 100_000; k++)
            {
                Peticion peticion = new(destino, "x", TimeSpan.FromSeconds(1), 16, TimeProvider.System);
                peticion.FijarLineasTecleadoDescartadas(1, $"c{k}");
                peticion.FijarAnteriorConTecleadoDescartado(anterior);
                anterior = peticion;
            }

            // Act
            string? avisos = HerramientasSsh.AvisosTecleadoDeAnteriores(anterior);

            // Assert
            string texto = avisos ?? string.Empty;
            int apariciones = texto.Split("no leyó").Length - 1;
            _ = apariciones.Should().Be(Peticion.MaxAvisosTecleadoEnlazados);
            _ = texto.Should().Contain("el comando c99999 no leyó").And.NotContain($"el comando c{99_999 - Peticion.MaxAvisosTecleadoEnlazados} no leyó");
            _ = texto.IndexOf($"el comando c{100_000 - Peticion.MaxAvisosTecleadoEnlazados} ", StringComparison.Ordinal).Should().BeLessThan(texto.IndexOf("el comando c99999 ", StringComparison.Ordinal), "del más antiguo al más reciente");
        }

        // Los avisos que la compactación soltó por el tope los resume la respuesta: cuántos comandos y cuántas líneas, una
        // sola vez, para que la respuesta siga diciendo exactamente cuántas líneas se descartaron (Y01 de la auditoría del
        // tramo 1-septies: con más de 16 pendientes no decía que hubo más).
        [Fact]
        public void DescribirAvisosOmitidos_DiceCuantosComandosYLineasUnaSolaVez()
        {
            // Arrange
            AvisosTecleadoOmitidos omitidos = new();
            omitidos.Sumar(3);
            omitidos.Sumar(1);

            // Act
            string? aviso = HerramientasSsh.DescribirAvisosOmitidos(omitidos);

            // Assert
            _ = aviso.Should().Be($"Además, otros 2 comandos anteriores no leyeron 4 líneas de entrada tecleada con ssh_input: se descartaron sin ejecutarse (ninguna se ejecutó como orden). No se detallan uno por uno: cada respuesta detalla como mucho {Peticion.MaxAvisosTecleadoEnlazados}.");
            _ = HerramientasSsh.DescribirAvisosOmitidos(omitidos).Should().BeNull();
            _ = HerramientasSsh.DescribirAvisosOmitidos(null).Should().BeNull();
        }

        // Sin grep de GNU el recorrido con find sale con 0 aunque no encuentre nada: una salida vacía es «sin coincidencias»
        // (nunca un resultado en blanco ni un error).
        [Fact]
        public async Task Buscar_SalidaVaciaConCodigoCero_SinCoincidencias()
        {
            // Act
            string resultado = await _herramientas.BuscarAsync("servidor", "vacio_con_find");

            // Assert
            _ = resultado.Should().Be("No se encontraron coincidencias para el patrón: 'vacio_con_find' en '.'.\n\n[motor: grep -E]");
        }

        [Fact]
        public async Task Input_EnComandoTerminado_Lanza()
        {
            // Arrange
            ResultadoComando terminado = await _herramientas.EjecutarAsync("servidor", "hola").ComoComando();

            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.EnviarEntradaAsync(terminado.Id, "x").ComoComando()).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Be($"No se envió la entrada: el comando {terminado.Id} ya terminó (estado Completado).");
        }

        // Un comando encolado tras otro todavía no admite entrada: el error lo dice así, no que el comando no la admita.
        [Fact]
        public async Task Input_EnComandoEncolado_DiceQueSigueEncolado()
        {
            // Arrange
            _ = await _herramientas.EjecutarAsync("servidor", "colgado", esperarSegundos: 0).ComoComando();
            ResultadoComando encolado = await _herramientas.EjecutarAsync("servidor", "pregunta", esperarSegundos: 0).ComoComando();

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.EnviarEntradaAsync(encolado.Id, "x").ComoComando()).Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = encolado.Estado.Should().Be(EstadoPeticionEnum.Encolado);
            _ = error.Message.Should().Be($"No se envió la entrada: el comando {encolado.Id} sigue encolado, esperando su turno en el shell de ana@servidor:22. Envíala cuando esté en ejecución (ssh_status).");
        }

        // Mientras el shell se abre (la conexión, o la apertura del canal del shell, retenidas) la petición ya está en
        // ejecución pero el comando aún no se ha enviado y la sesión rechaza la entrada: el error dice que el shell se está
        // abriendo y que se reintente cuando ssh_status lo muestre activo, no una lista de causas que no la incluye (auditoría
        // del A-septies, Y01/Y02). Con el shell ya abierto, la misma entrada llega.
        [Theory]
        [InlineData("conexion")]
        [InlineData("apertura_shell")]
        public async Task Input_MientrasElShellSeAbre_DiceQueSeEstaAbriendo(string fase)
        {
            // Arrange
            using SemaphoreSlim bloqueo = new(0);
            if (fase == "conexion")
                _fabrica.BloqueoConexion = bloqueo;
            else
                _fabrica.BloqueoAperturaShell = bloqueo;
            ResultadoComando comando = await _herramientas.EjecutarAsync("servidor", "pregunta", esperarSegundos: 0).ComoComando();
            Peticion peticion = _gestor.BuscarPeticion(comando.Id);
            await EsperasPrueba.EsperarHastaAsync(() => fase == "conexion" ? _fabrica.ConexionesRetenidas == 1 : _fabrica.Ultima?.AperturasShellRetenidas == 1);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.EnviarEntradaAsync(comando.Id, "x", esperarSegundos: 0).ComoComando()).Should().ThrowAsync<NativeSshException>()).Which;
            ResultadoEstado estado = _herramientas.Estado("servidor");
            _ = bloqueo.Release();
            // La pregunta, ya en la salida del comando: el falso apunta el comando antes de escribirla, y la respuesta de
            // ssh_input empieza en lo que había al enviar (si llegara después, ocuparía los 16 bytes de MaxBytesPorRespuesta).
            await EsperasPrueba.EsperarHastaAsync(() => _herramientas.Estado("servidor").Sesiones[0].IsShellActivo && _fabrica.Ultima!.Comandos.Any(c => c.Contains("pregunta", StringComparison.Ordinal)) && peticion.Salida.Longitud > 0);
            ResultadoComando reintento = await _herramientas.EnviarEntradaAsync(comando.Id, "x", esperarSegundos: 5).ComoComando();

            // Assert
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = estado.Sesiones.Should().ContainSingle().Which.IsShellActivo.Should().BeFalse();
            _ = error.Message.Should().Be($"No se envió la entrada al comando {comando.Id}, que sigue en ejecución: el shell de ana@servidor:22 todavía se está abriendo (conexión, inicio de sesión o inicialización del shell) y el comando aún no se le ha enviado. Reintenta en unos segundos (ssh_status muestra isShellActivo=true en cuanto el canal está abierto, aunque la inicialización puede tardar algo más); si sigue fallando, comprueba su estado con ssh_output o ssh_status.");
            _ = reintento.Salida.Should().Contain("recibido:x");
        }

        // Con el canal del shell ya abierto pero el inicio de sesión todavía en curso (aquí tarda 1,5 s), la sesión sigue
        // rechazando la entrada: ssh_status muestra el shell activo, así que el error no dice que no lo esté, sino que aún se
        // está inicializando: con la señal de la sesión (el comando no se ha enviado) es la única causa (auditoría del
        // A-septies, sonda Y02; antes daba dos posibles, la otra un fallo de la escritura, que no rechaza sino que lanza su
        // propio error).
        [Fact]
        public async Task Input_ConElCanalAbiertoYLaInicializacionEnCurso_DiceQueAunSePuedeEstarInicializando()
        {
            // Arrange
            FabricaFalsa fabrica = new(Guion, login: LoginFalsoEnum.LentoConReadline, retrasoLogin: TimeSpan.FromSeconds(1.5));
            OpcionesNativeSsh opciones = new() { UsuarioPorDefecto = "ana" };
            await using GestorSesiones gestor = new(fabrica, new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            HerramientasSsh herramientas = new(gestor, opciones);
            ResultadoComando comando = await herramientas.EjecutarAsync("servidor", "pregunta", esperarSegundos: 0).ComoComando();
            await EsperasPrueba.EsperarHastaAsync(() => herramientas.Estado("servidor").Sesiones is [{ IsShellActivo: true }]);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EnviarEntradaAsync(comando.Id, "x", esperarSegundos: 0).ComoComando()).Should().ThrowAsync<NativeSshException>()).Which;
            await EsperasPrueba.EsperarHastaAsync(() => fabrica.Ultima!.Comandos.Any(c => c.Contains("pregunta", StringComparison.Ordinal)), TimeSpan.FromSeconds(10));
            ResultadoComando reintento = await herramientas.EnviarEntradaAsync(comando.Id, "x", esperarSegundos: 5).ComoComando();

            // Assert
            _ = error.Message.Should().Be($"No se envió la entrada al comando {comando.Id}, que sigue en ejecución: el shell de ana@servidor:22 tiene el canal abierto, pero aún se está inicializando (primer comando del destino, tras ssh_reset_shell o con un inicio de sesión lento) y el comando todavía no se le ha enviado. Reintenta en unos segundos (ssh_status muestra isShellActivo=true en cuanto el canal está abierto, aunque la inicialización puede tardar algo más); si sigue fallando, comprueba su estado con ssh_output o ssh_status.");
            _ = reintento.Salida.Should().Contain("recibido:x");
        }

        // Durante la interrupción de un comando en ejecución (aquí, uno que ignora el Ctrl+C, así que la interrupción dura
        // hasta EsperaTrasCtrlC y el cierre del canal) el shell rechaza la entrada: si se pidió cancelarlo, el error dice que
        // se está cancelando; si lo interrumpe su timeout, da las causas posibles. Nunca que el comando no admite entrada.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Input_DuranteLaInterrupcion_DiceQueSeEstaInterrumpiendo(bool isCancelado)
        {
            // Arrange
            ResultadoComando comando = await _herramientas.EjecutarAsync("servidor", "ignora_ctrlc", timeoutSegundos: isCancelado ? 300 : 1, esperarSegundos: 0).ComoComando();
            Peticion peticion = _gestor.BuscarPeticion(comando.Id);
            await EsperasPrueba.EsperarHastaAsync(() => peticion.Salida.Longitud > 0);
            if (isCancelado)
                _ = peticion.SolicitarCancelacion();

            // Act: reintenta hasta que la interrupción empiece (antes, la entrada se acepta).
            Exception? error = null;
            for (int intento = 0; intento < 100 && error is null; intento++)
            {
                error = await Record.ExceptionAsync(() => _herramientas.EnviarEntradaAsync(comando.Id, "x", enter: false, esperarSegundos: 0).ComoComando());
                if (error is null)
                    await Task.Delay(20);
            }

            // Assert
            _ = error.Should().BeOfType<NativeSshException>();
            _ = error!.Message.Should().NotContain("no admite entrada:");
            _ = error.Message.Should().StartWith(isCancelado
                ? $"No se envió la entrada: el comando {comando.Id} se está cancelando"
                : $"No se envió la entrada al comando {comando.Id}, que sigue en ejecución: el shell no la admitió en este momento (se está interrumpiendo por su timeout");
        }

        // Con la señal de la sesión (Peticion.IsComandoEnviado, leída antes de intentar el envío) el rechazo da una sola
        // causa: sin el comando enviado, el shell se está abriendo o inicializando; con él enviado y el shell inactivo, su
        // canal se cerró después; con él enviado y el canal abierto, la interrupción o el cierre en curso. Nunca un fallo de
        // la escritura, que no rechaza la entrada sino que da su propio error (SesionShell.DescribirEntradaFallida).
        [Theory]
        [InlineData(false, "Inactivo", "todavía se está abriendo (conexión, inicio de sesión o inicialización del shell) y el comando aún no se le ha enviado.")]
        [InlineData(false, "ActivoConElComando", "tiene el canal abierto, pero aún se está inicializando")]
        [InlineData(false, "InicializadoConElComando", "el shell de ana@servidor:22 ya está abierto e inicializado, pero el comando acaba de empezar y todavía no se le ha enviado")]
        [InlineData(true, "InicializadoConElComando", "el shell no la admitió en este momento (se está interrumpiendo por su timeout, la sesión se está cerrando con ssh_disconnect o ya no es el comando que ocupa el shell)")]
        [InlineData(true, "Inactivo", "se acaba de cerrar o se perdió la conexión después de enviarle el comando: el comando terminará como shell perdido")]
        [InlineData(true, "ActivoConElComando", "el shell no la admitió en este momento (se está interrumpiendo por su timeout, la sesión se está cerrando con ssh_disconnect o ya no es el comando que ocupa el shell)")]
        [InlineData(true, "Indeterminado", "el shell no la admitió en este momento (se está interrumpiendo por su timeout, la sesión se está cerrando con ssh_disconnect o ya no es el comando que ocupa el shell)")]
        public void MensajeEntradaNoEnviada_EnEjecucion_UnaSolaCausaSegunLaSenalDeLaSesion(bool isEnviado, string shell, string esperado)
        {
            // Arrange
            _ = DestinoSsh.TryParse("ana@servidor", "ana", out DestinoSsh destino);
            Peticion peticion = new(destino, "pregunta", TimeSpan.FromSeconds(30), 1024, TimeProvider.System);
            _ = peticion.TryMarcarEjecutando();
            if (isEnviado)
                peticion.MarcarComandoEnviado();

            // Act
            string mensaje = HerramientasSsh.MensajeEntradaNoEnviada(peticion, Enum.Parse<ShellEntradaEnum>(shell), isEnviado);

            // Assert
            _ = mensaje.Should().Contain(esperado).And.NotContain("falló la escritura").And.NotContain("causas posibles");
        }

        [Fact]
        public async Task Status_FiltraPorDestino()
        {
            // Arrange
            _ = await _herramientas.EjecutarAsync("uno", "a").ComoComando();
            _ = await _herramientas.EjecutarAsync("dos", "b").ComoComando();

            // Act
            ResultadoEstado todo = _herramientas.Estado();
            ResultadoEstado filtrado = _herramientas.Estado("dos");

            // Assert
            _ = todo.Sesiones.Count.Should().Be(2);
            _ = todo.Peticiones.Count.Should().Be(2);
            _ = filtrado.Sesiones.Should().ContainSingle().Subject.Destino.Should().Be("ana@dos:22");
            _ = filtrado.Peticiones.Should().ContainSingle().Subject.Comando.Should().Be("b");
        }

        [Fact]
        public async Task Status_DevuelveComoMaximoVeintePeticionesLasMasRecientes()
        {
            // Arrange
            for (int i = 0; i < 25; i++)
                _ = await _herramientas.EjecutarAsync("uno", $"cmd{i}").ComoComando();
            _ = await _herramientas.EjecutarAsync("dos", "otro").ComoComando();

            // Act
            ResultadoEstado todo = _herramientas.Estado();
            ResultadoEstado filtrado = _herramientas.Estado("uno");

            // Assert
            _ = todo.Peticiones.Count.Should().Be(20);
            _ = todo.Peticiones[0].Comando.Should().Be("otro");
            _ = filtrado.Peticiones.Count.Should().Be(20);
            _ = filtrado.Peticiones[0].Comando.Should().Be("cmd24");
            _ = filtrado.Peticiones[^1].Comando.Should().Be("cmd5");
        }

        // ST1: ssh_status muestra qué herramienta y qué ruta, nunca el script interno ni el contenido (en base64 o tal cual) de
        // un archivo escrito o reemplazado.
        [Fact]
        public async Task Status_TrasEscribirYReemplazar_MuestraHerramientaYRutaSinContenidoNiBase64()
        {
            // Arrange
            const string Contenido = "CONTENIDO_ST_UNICO=clave-que-no-debe-salir\nsegunda linea del archivo\n";
            _ = await _herramientas.EscribirArchivoAsync("servidor", "nuevo.txt", Contenido, overwrite: true);
            _ = await _herramientas.ReemplazarContenidoArchivoAsync("servidor", "app.env", "linea 2", "REEMPLAZO_ST_UNICO");
            _ = await _herramientas.EjecutarAsync("servidor", "echo hola").ComoComando();

            // Act
            ResultadoEstado estado = _herramientas.Estado();
            string json = JsonSerializer.Serialize(estado, ContextoJsonNativeSsh.OpcionesHerramientas);

            // Assert
            _ = estado.Peticiones.Select(p => p.Comando).Should().Equal("echo hola", "[ssh_replace_file_content] app.env (escritura)", "[ssh_write_file] nuevo.txt");
            string[] fugas = [Contenido[..20], Convert.ToBase64String(Encoding.UTF8.GetBytes(Contenido))[..16], "linea 1", Convert.ToBase64String(Encoding.UTF8.GetBytes("linea 1\nREEMPLAZO_ST_UNICO"))[..16], "REEMPLAZO_ST_UNICO", "base64", "printf"];
            foreach (string fuga in fugas)
                _ = json.Should().NotContain(fuga);
        }

        // ST2: la lectura y la escritura de ssh_replace_file_content, y la validación y el export de ssh_set_cwd, son una sola
        // entrada de ssh_status (la de la petición más reciente de la llamada).
        [Fact]
        public async Task Status_ReemplazoYSetCwd_UnaEntradaPorLlamada()
        {
            // Arrange
            _ = await _herramientas.ReemplazarContenidoArchivoAsync("servidor", "app.env", "linea 2", "dos");
            _ = await _herramientas.FijarDirectorioAsync("servidor", "/var/www/proyecto");

            // Act
            ResultadoEstado estado = _herramientas.Estado();

            // Assert
            _ = estado.Peticiones.Select(p => p.Comando).Should().Equal("[ssh_set_cwd] /var/www/proyecto (export)", "[ssh_replace_file_content] app.env (escritura)");
        }

        // ST2/ST3: 20 peticiones internas (10 llamadas a ssh_replace_file_content, lectura y escritura) no se comen las ranuras:
        // los ssh_exec anteriores siguen en la lista, como antes de esas llamadas.
        [Fact]
        public async Task Status_VeintePeticionesInternas_SiguenMostrandoLosExecRecientes()
        {
            // Arrange
            for (int i = 0; i < 5; i++)
                _ = await _herramientas.EjecutarAsync("servidor", $"cmd{i}").ComoComando();
            for (int i = 0; i < 10; i++)
                _ = await _herramientas.ReemplazarContenidoArchivoAsync("servidor", $"a{i}.txt", "linea 2", "dos");

            // Act
            ResultadoEstado estado = _herramientas.Estado();

            // Assert
            _ = estado.Peticiones.Count.Should().Be(15);
            _ = estado.Peticiones.Select(p => p.Comando).TakeLast(5).Should().Equal("cmd4", "cmd3", "cmd2", "cmd1", "cmd0");
        }

        // RC1: ssh_reset_shell con sinPerfil=true abre los shells siguientes con la petición 'exec' sin perfil (también sin
        // sesión previa: la crea), ssh_status lo muestra y el primer comando lo avisa; un reset sin la opción vuelve al login.
        [Fact]
        public async Task ResetSinPerfil_AbreLosShellsSinPerfilHastaUnResetNormal()
        {
            // Arrange
            ResultadoAccion sinPerfil = _herramientas.ReiniciarShell("servidor", sinPerfil: true);

            // Act
            ResultadoComando primero = await _herramientas.EjecutarAsync("servidor", "a").ComoComando();
            ResultadoEstado durante = _herramientas.Estado();
            _ = await _herramientas.EscribirArchivoAsync("servidor", "f.txt", "x", overwrite: true);
            ResultadoAccion normal = _herramientas.ReiniciarShell("servidor");
            ResultadoComando despues = await _herramientas.EjecutarAsync("servidor", "b").ComoComando();
            ResultadoEstado tras = _herramientas.Estado();

            // Assert
            _ = (sinPerfil.IsHecho, normal.IsHecho).Should().Be((true, true));
            _ = sinPerfil.Mensaje.Should().StartWith("El próximo comando usará un shell nuevo SIN PERFIL");
            _ = primero.Aviso.Should().Contain("Shell SIN PERFIL");
            _ = durante.Sesiones.Single().IsSinPerfil.Should().BeTrue();
            _ = despues.Aviso.Should().BeNull();
            _ = tras.Sesiones.Single().IsSinPerfil.Should().BeNull();
            _ = (_fabrica.Ultima!.AperturasShell, _fabrica.Ultima.AperturasSinPerfil).Should().Be((2, 1));
        }

        [Fact]
        public async Task Disconnect_YReset()
        {
            // Arrange
            _ = await _herramientas.EjecutarAsync("servidor", "a").ComoComando();

            // Act & Assert
            _ = _herramientas.ReiniciarShell("servidor").IsHecho.Should().BeTrue();
            _ = (await _herramientas.DesconectarAsync("servidor")).IsHecho.Should().BeTrue();
            _ = (await _herramientas.DesconectarAsync("servidor")).IsHecho.Should().BeFalse();
            _ = _herramientas.ReiniciarShell("servidor").IsHecho.Should().BeFalse();
        }

        [Fact]
        public async Task Json_CamelCaseEstadoComoTextoYSinNulos()
        {
            // Arrange
            ResultadoComando resultado = await _herramientas.EjecutarAsync("servidor", "hola").ComoComando();

            // Act
            string json = JsonSerializer.Serialize(resultado, ContextoJsonNativeSsh.OpcionesHerramientas);

            // Assert
            _ = json.Should().Contain("\"estado\":\"Completado\"");
            _ = json.Should().Contain("\"codigoSalida\":2");
            _ = json.Should().NotContain("aviso");
            _ = json.Should().NotContain("segundosSinSalida");
        }

        [Fact]
        public void Json_ContratosDeEscrituraYDirectorio_CamelCaseYSinNulos()
        {
            // Act
            string escritura = JsonSerializer.Serialize(new ResultadoEscritura("a.txt", 3, null, false, "m"), ContextoJsonNativeSsh.OpcionesHerramientas);
            string directorio = JsonSerializer.Serialize(new ResultadoDirectorioTrabajo("ana@s:22", null, "m"), ContextoJsonNativeSsh.OpcionesHerramientas);

            // Assert
            _ = escritura.Should().Be("{\"ruta\":\"a.txt\",\"bytes\":3,\"isAtomica\":false,\"mensaje\":\"m\"}");
            _ = directorio.Should().Be("{\"destino\":\"ana@s:22\",\"mensaje\":\"m\"}");
        }

        [Fact]
        public async Task ReadFile_ConMetadatos_DevuelveCabeceraConTotalLineas()
        {
            // Simula comando que devuelve cabecera y contenido
            string contenido = await _herramientas.LeerArchivoAsync("servidor", "fichero.txt", lineaInicio: 1, lineaFin: 2);
            _ = contenido.Should().StartWith("[Líneas 1-2 de ");
        }

        [Fact]
        public async Task ReadFile_LecturaExitosa_DevuelveTextoPlano()
        {
            // Act
            string texto = await _herramientas.LeerArchivoAsync("servidor", "archivo.txt", 1, 3);

            // Assert
            _ = texto.Should().Be("[Líneas 1-3 de 10]\nlinea 1\nlinea 2\nlinea 3\n");
        }


        private HerramientasSsh HerramientasConTimeoutCorto() =>
            new(_gestor, new OpcionesNativeSsh { UsuarioPorDefecto = "ana", MaxBytesPorRespuesta = 16, EsperaTrasCtrlC = TimeSpan.FromMilliseconds(300) }) { TimeoutForzado = TimeSpan.FromSeconds(1) };

        [Fact]
        public async Task ReadFile_TiempoAgotado_CierraElCanalExecYLanzaSinSalidaParcial()
        {
            // Arrange
            HerramientasSsh herramientas = HerramientasConTimeoutCorto();

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync("servidor", "colgado_lectura.txt"))
                .Should().ThrowAsync<NativeSshException>()).Which;
            string siguiente = await herramientas.LeerArchivoAsync("servidor", "archivo.txt", 1, 3);

            // Assert
            _ = error.Message.Should().Contain("tiempo agotado");
            _ = error.Message.Should().NotContain("parcial");
            _ = siguiente.Should().StartWith("[Líneas 1-3 de 10]");
            await EsperasPrueba.EsperarHastaAsync(() => _herramientas.Estado().Sesiones.Single().CanalesExecActivos == 0);
        }

        // La lectura previa de ssh_replace_file_content se interrumpe por el timeout: el error dice que el archivo no se tocó y
        // da el id de la petición (la sesión la finaliza como TiempoAgotado antes de que venza la espera de la herramienta).
        [Fact]
        public async Task ReplaceFileContent_LecturaPreviaConTiempoAgotado_DiceQueNoSeModificoYDaElId()
        {
            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => HerramientasConTimeoutCorto().ReemplazarContenidoArchivoAsync("servidor", "colgado_r.txt", "a", "b"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().MatchRegex("^La operación no se completó \\(estado: TiempoAgotado; petición [0-9a-f]{32}, su salida completa con ssh_output\\)\\. ")
                .And.EndWith("No se ha modificado 'colgado_r.txt'.");
            _ = _fabrica.Ultima!.Comandos.Should().NotContain(c => c.Contains("base64 -d", StringComparison.Ordinal));
        }

        // Cancelada con el token durante la lectura previa: la OperationCanceledException también dice que no se tocó.
        [Fact]
        public async Task ReplaceFileContent_LecturaPreviaCanceladaPorElToken_DiceQueNoSeModifico()
        {
            // Arrange
            using CancellationTokenSource cancelacion = new(TimeSpan.FromMilliseconds(300));

            // Act
            OperationCanceledException error = (await FluentActions.Awaiting(() => _herramientas.ReemplazarContenidoArchivoAsync("servidor", "colgado_r.txt", "a", "b", cancellationToken: cancelacion.Token))
                .Should().ThrowAsync<OperationCanceledException>()).Which;

            // Assert
            _ = error.Message.Should().MatchRegex("^Se canceló la operación \\(petición [0-9a-f]{32}\\)\\. No se ha modificado 'colgado_r\\.txt'\\.$");
        }

        // Una escritura en cola detrás de un comando colgado y cancelada con el token: no llega a ejecutarse, y la
        // El shell se pierde con el comando de la escritura ya enviado y sin ninguna salida: no es un comando que no llegó
        // al shell (pudo ejecutarse), así que el error no dice que no se ejecutó ni que el archivo no se modificó, sino que
        // no consta y cómo comprobarlo (mutación M22 de la auditoría del A-nonies; A-decies, AD3).
        [Fact]
        public async Task WriteFile_ShellPerdidoConElComandoEnviadoYSinSalida_NoDiceQueNoSeEjecuto()
        {
            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.EscribirArchivoAsync("servidor", "perdido_mudo.txt", "hola"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            Peticion peticion = _gestor.ListarPeticiones()[0];
            _ = (peticion.Estado, peticion.Salida.Longitud).Should().Be((EstadoPeticionEnum.ShellPerdido, 0));
            _ = error.Message.Should().Contain("estado: ShellPerdido").And.Contain("No consta ninguna modificación de 'perdido_mudo.txt'")
                .And.NotContain("no llegó a ejecutarse").And.NotContain("no se ha modificado");
        }

        // OperationCanceledException dice que el archivo no se modificó y da el id de la petición.
        [Fact]
        public async Task WriteFile_EncoladaYCanceladaPorElToken_DiceQueNoSeModifico()
        {
            // Arrange
            ResultadoComando colgado = await _herramientas.EjecutarAsync("servidor", "colgado", esperarSegundos: 0).ComoComando();
            await EsperasPrueba.EsperarHastaAsync(() => _gestor.BuscarPeticion(colgado.Id).Salida.Longitud > 0);
            using CancellationTokenSource cancelacion = new(TimeSpan.FromMilliseconds(300));

            // Act
            OperationCanceledException error = (await FluentActions.Awaiting(() => _herramientas.EscribirArchivoAsync("servidor", "nuevo.txt", "hola", cancellationToken: cancelacion.Token))
                .Should().ThrowAsync<OperationCanceledException>()).Which;

            // Assert
            _ = error.Message.Should().MatchRegex("^La escritura en 'nuevo.txt' no terminó \\(estado: Cancelado; petición [0-9a-f]{32}, su salida completa con ssh_output\\)\\. No se llegó a ejecutar: 'nuevo.txt' no se ha modificado\\. Cancelada antes de empezar\\.$");
            _ = _fabrica.Ultima!.Comandos.Should().NotContain(c => c.Contains("base64 -d", StringComparison.Ordinal));
            _ = (await _herramientas.CancelarAsync(colgado.Id).ComoComando()).Estado.Should().Be(EstadoPeticionEnum.Cancelado);
        }

        [Fact]
        public async Task HerramientasDeSoloLectura_VanPorCanalExecSinAbrirElShell()
        {
            // Act
            _ = await _herramientas.LeerArchivoAsync("servidor", "archivo.txt", 1, 3);
            _ = await _herramientas.ConsultarMetadatosAsync("servidor", "archivo.txt");
            _ = await _herramientas.BuscarAsync("servidor", "algo");
            _ = await _herramientas.BuscarPorNombreAsync("servidor", "*.service");
            _ = await _herramientas.ListarDirectoriosAsync("servidor", "proyecto");

            // Assert
            ConexionFalsa conexion = _fabrica.Ultima!;
            _ = conexion.ComandosExec.Count.Should().Be(5);
            _ = conexion.ShellsAbiertos.Should().Be(0);
            _ = _gestor.ListarPeticiones().Should().BeEmpty();
            _ = conexion.ComandosExec.Should().AllSatisfy(c => c.Should().StartWith("bash --noprofile --norc -c '"));
        }

        [Fact]
        public async Task EscriturasSetCwdYExec_SiguenPorElShell()
        {
            // Act
            _ = await _herramientas.EscribirArchivoAsync("servidor", "nuevo.txt", "hola");
            _ = await _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_prueba.txt", "linea 3", "linea 3b");
            _ = await _herramientas.FijarDirectorioAsync("servidor", "/var/www/proyecto");
            _ = await _herramientas.EjecutarAsync("servidor", "hola").ComoComando();

            // Assert
            _ = _fabrica.Ultima!.ComandosExec.Should().BeEmpty();
            _ = _fabrica.Ultima!.ShellsAbiertos.Should().Be(1);
        }

        [Fact]
        public async Task ReadFile_ConComandoColgadoEnElShell_NoEsperaDetras()
        {
            // Arrange
            ResultadoComando colgado = await _herramientas.EjecutarAsync("servidor", "colgado", esperarSegundos: 0).ComoComando();
            await EsperasPrueba.EsperarHastaAsync(() => _gestor.BuscarPeticion(colgado.Id).Salida.Longitud > 0);

            // Act
            string texto = await _herramientas.LeerArchivoAsync("servidor", "archivo.txt", 1, 3).WaitAsync(TimeSpan.FromSeconds(5));
            string busqueda = await _herramientas.BuscarAsync("servidor", "algo").WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            _ = texto.Should().Be("[Líneas 1-3 de 10]\nlinea 1\nlinea 2\nlinea 3\n");
            _ = busqueda.Should().StartWith("archivo.txt:1:coincidencia encontrada");
            _ = _gestor.BuscarPeticion(colgado.Id).Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
            _ = _fabrica.Conexiones.Should().Be(1);
            _ = (await _herramientas.CancelarAsync(colgado.Id).ComoComando()).Estado.Should().Be(EstadoPeticionEnum.Cancelado);
        }

        [Fact]
        public async Task ReadFile_DevuelveLaSalidaTalCualConLosRetornosDeCarro()
        {
            // Act
            string texto = await _herramientas.LeerArchivoAsync("servidor", "crlf.txt");

            // Assert
            _ = texto.Should().Be("[Líneas 1-2 de 2]\nuno\r\ndos\r\n");
        }

        [Fact]
        public async Task ReadFile_ContenidoNoUtf8_LoSustituyeYAnadeUnAvisoVisible()
        {
            // Act
            string texto = await _herramientas.LeerArchivoAsync("servidor", "latin1_lectura.txt");
            string valido = await _herramientas.LeerArchivoAsync("servidor", "crlf.txt");

            // Assert
            _ = texto.Should().Be("[Líneas 1-1 de 1]\na\uFFFDo\n[aviso: contenido no UTF-8; bytes inválidos sustituidos por U+FFFD]");
            _ = valido.Should().NotContain("[aviso:");
        }

        [Fact]
        public async Task LecturasSimultaneas_RespetanElLimiteDeCanalesExecPorConexion()
        {
            // Arrange
            using SemaphoreSlim bloqueo = new(0);
            _fabrica.BloqueoExec = bloqueo;
            Task<string>[] lecturas = [.. Enumerable.Range(0, SesionShell.MaxCanalesExecSimultaneos + 2).Select(_ => _herramientas.LeerArchivoAsync("servidor", "archivo.txt", 1, 3))];
            await EsperasPrueba.EsperarHastaAsync(() => _fabrica.Ultima?.ExecActivos == SesionShell.MaxCanalesExecSimultaneos);
            await Task.Delay(200);
            int activosEnEstado = _herramientas.Estado().Sesiones.Single().CanalesExecActivos;

            // Act
            _ = bloqueo.Release();
            string[] resultados = await Task.WhenAll(lecturas).WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            _ = activosEnEstado.Should().Be(SesionShell.MaxCanalesExecSimultaneos);
            _ = _fabrica.Ultima!.MaxExecSimultaneos.Should().Be(SesionShell.MaxCanalesExecSimultaneos);
            _ = resultados.Should().AllSatisfy(r => r.Should().StartWith("[Líneas 1-3 de 10]"));
            _ = _fabrica.Conexiones.Should().Be(1);
        }

        [Fact]
        public async Task LecturasYComandoSimultaneosSinConexion_AbrenUnaSolaConexion()
        {
            // Arrange
            _fabrica.RetrasoConexion = TimeSpan.FromMilliseconds(200);

            // Act
            Task<string>[] lecturas = [.. Enumerable.Range(0, 3).Select(_ => _herramientas.LeerArchivoAsync("servidor", "archivo.txt", 1, 3))];
            Task<ResultadoComando> comando = _herramientas.EjecutarAsync("servidor", "hola").ComoComando();
            Task<ResultadoMetadatos> metadatos = _herramientas.ConsultarMetadatosAsync("servidor", "archivo.txt");
            _ = await Task.WhenAll(lecturas).WaitAsync(TimeSpan.FromSeconds(5));
            ResultadoComando resultado = await comando.WaitAsync(TimeSpan.FromSeconds(5));
            _ = await metadatos.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            _ = resultado.Salida.Should().Be("eco:hola\n");
            _ = _fabrica.Conexiones.Should().Be(1);
            _ = _herramientas.Estado().Sesiones.Single().ConexionesAbiertas.Should().Be(1);
        }

        [Fact]
        public async Task Lectura_TrasCaidaQueNoCierraElCanal_ElSiguienteExecRecreaElShellEnLaNuevaConexion()
        {
            // Arrange: shell vivo en la primera conexión; la caída no cierra su canal (como SSH.NET con un TCP muerto).
            _ = await _herramientas.EjecutarAsync("servidor", "a").ComoComando();
            ConexionFalsa primera = _fabrica.Ultima!;
            CanalFalso canalViejo = primera.CanalActual!;
            primera.CaerSinCerrarCanal();

            // Act: la lectura reconecta por su canal exec y después llega un ssh_exec.
            string texto = await _herramientas.LeerArchivoAsync("servidor", "archivo.txt", 1, 3);
            bool isCanalViejoCerradoTrasLectura = canalViejo.IsCerrado;
            ResultadoComando comando = await _herramientas.EjecutarAsync("servidor", "b").ComoComando();

            // Assert: el canal muere con la conexión que lo abrió y el shell se recrea sobre la nueva.
            _ = texto.Should().StartWith("[Líneas 1-3 de 10]");
            _ = isCanalViejoCerradoTrasLectura.Should().BeTrue();
            _ = comando.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = comando.Salida.Should().Be("eco:b\n");
            _ = comando.Aviso.Should().Contain("Shell remoto recreado");
            _ = _fabrica.Conexiones.Should().Be(2);
            _ = _fabrica.Ultima!.ShellsAbiertos.Should().Be(1);
        }

        [Fact]
        public async Task Lectura_TrasCaerLaConexion_ReconectaYElShellReutilizaLaNueva()
        {
            // Arrange
            _ = await _herramientas.EjecutarAsync("servidor", "a").ComoComando();
            ConexionFalsa primera = _fabrica.Ultima!;
            primera.Caer();

            // Act
            string texto = await _herramientas.LeerArchivoAsync("servidor", "archivo.txt", 1, 3);
            ResultadoComando comando = await _herramientas.EjecutarAsync("servidor", "b").ComoComando();

            // Assert
            _ = texto.Should().StartWith("[Líneas 1-3 de 10]");
            _ = comando.Salida.Should().Be("eco:b\n");
            _ = _fabrica.Conexiones.Should().Be(2);
            _ = _fabrica.Ultima!.Should().NotBeSameAs(primera);
            _ = _fabrica.Ultima!.ShellsAbiertos.Should().Be(1);
            _ = _fabrica.Ultima!.ComandosExec.Should().ContainSingle();
        }

        [Fact]
        public async Task SetCwd_LoGuardaEnLaSesionYLosCanalesExecLoRecibenExplicitamente()
        {
            // Act
            _ = await _herramientas.FijarDirectorioAsync("servidor", "/var/www/proyecto");
            _ = await _herramientas.LeerArchivoAsync("servidor", "archivo.txt", 1, 3);
            string conDirectorio = ScriptExec.Decodificar(_fabrica.Ultima!.ComandosExec.Last());
            _ = await _herramientas.FijarDirectorioAsync("servidor", null);
            _ = await _herramientas.LeerArchivoAsync("servidor", "archivo.txt", 1, 3);
            string sinDirectorio = ScriptExec.Decodificar(_fabrica.Ultima!.ComandosExec.Last());

            // Assert
            _ = conDirectorio.Should().StartWith("export __NS_CWD='/var/www/proyecto'\ncd \"${__NS_CWD:-$HOME}\"");
            _ = sinDirectorio.Should().StartWith("cd \"${__NS_CWD:-$HOME}\"");
            _ = _gestor.ListarSesiones().Single().DirectorioTrabajo.Should().BeNull();
        }

        [Fact]
        public async Task CanalesExec_UsanElPathDelLoginCapturadoAlInicializarElShell()
        {
            // Arrange: el login falso exporta otro PATH (p. ej. con ~/.cargo/bin) que el de un canal exec.
            _fabrica.PathLogin = "/home/ana/.cargo/bin:/usr/local/bin:/usr/bin";

            // Act: antes de que exista el shell PTY no hay PATH capturado; después, sí.
            _ = await _herramientas.LeerArchivoAsync("servidor", "archivo.txt", 1, 3);
            string? antes = ScriptExec.DecodificarPath(_fabrica.Ultima!.ComandosExec.Last());
            _ = await _herramientas.EjecutarAsync("servidor", "hola").ComoComando();
            _ = await _herramientas.BuscarAsync("servidor", "algo");
            string? despues = ScriptExec.DecodificarPath(_fabrica.Ultima!.ComandosExec.Last());

            // Assert
            _ = antes.Should().BeNull();
            _ = despues.Should().Be("/home/ana/.cargo/bin:/usr/local/bin:/usr/bin");
            _ = _gestor.ListarSesiones().Single().PathLogin.Should().Be("/home/ana/.cargo/bin:/usr/local/bin:/usr/bin");
        }

        [Fact]
        public async Task SetCwd_AlRecrearElShell_SeReexportaElDirectorio()
        {
            // Arrange
            _ = await _herramientas.FijarDirectorioAsync("servidor", "/var/www/proyecto");
            string base64 = Convert.ToBase64String("/var/www/proyecto"u8);

            // Act
            _ = _herramientas.ReiniciarShell("servidor");
            _ = await _herramientas.EjecutarAsync("servidor", "hola").ComoComando();

            // Assert
            ConexionFalsa conexion = _fabrica.Ultima!;
            _ = conexion.ShellsAbiertos.Should().Be(2);
            _ = conexion.CanalActual!.Capturas.Should().ContainSingle().Which.Should().Contain($"export __NS_CWD=\"$(printf '%s' '{base64}' | base64 -d)\"; cd \"${{__NS_CWD:-$HOME}}\"");
        }

        [Fact]
        public async Task ReadFile_SalidaConDatosDescartados_LanzaErrorSinDevolverLaCola()
        {
            // Arrange
            (_, GestorSesiones gestor, HerramientasSsh herramientas) = CrearConSalidaPequena();
            await using GestorSesiones gestorLiberable = gestor;

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.LeerArchivoAsync("servidor", "archivo_enorme.txt", 1, 5000))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("superó");
            _ = error.Message.Should().NotContain("linea 00");
        }

        [Fact]
        public async Task ReplaceFileContent_LecturaConDatosDescartados_AbortaSinEscribir()
        {
            // Arrange
            (FabricaFalsa fabrica, GestorSesiones gestor, HerramientasSsh herramientas) = CrearConSalidaPequena();
            await using GestorSesiones gestorLiberable = gestor;

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_enorme.txt", "linea 002000", "cambiada", 1, 3000))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("superó");
            _ = error.Message.Should().Contain("No se ha escrito nada");
            _ = fabrica.Ultima!.Comandos.Should().NotContain(c => c.Contains("base64 -d", StringComparison.Ordinal));
        }

        // Una escritura cuya salida supera el máximo por petición pierde su principio, y con él anuncios (aquí, que empezó a
        // escribir en el sitio): el error no puede apoyarse en lo que queda para decir qué pasó con el archivo, lo dice, y no
        // vuelca la salida entera. El anuncio final sí llegó (es lo último): dice que el script anunció que terminó.
        [Fact]
        public async Task WriteFile_SalidaConDatosDescartados_ElErrorNoSeApoyaEnLosAnunciosQueQuedan()
        {
            // Arrange
            (_, GestorSesiones gestor, HerramientasSsh herramientas) = CrearConSalidaPequena();
            await using GestorSesiones gestorLiberable = gestor;

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync("servidor", "salida_desbordada.txt", "x", overwrite: true))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().StartWith("La escritura en 'salida_desbordada.txt' terminó con el código 1; el script anunció que terminó, pero se descartó el principio de su salida (petición ")
                .And.Contain("No consta ninguna modificación de 'salida_desbordada.txt'.")
                .And.Contain("superó el máximo de bytes por petición y se descartó su principio")
                .And.EndWith("Error: algo falló");
            _ = error.Message.Length.Should().BeLessThan(3000);
        }

        [Fact]
        public async Task SearchFindListDir_SalidaConDatosDescartados_LanzanErrorExplicito()
        {
            // Arrange
            (_, GestorSesiones gestor, HerramientasSsh herramientas) = CrearConSalidaPequena();
            await using GestorSesiones gestorLiberable = gestor;

            // Act
            NativeSshException errorSearch = (await FluentActions.Awaiting(() => herramientas.BuscarAsync("servidor", "enorme"))
                .Should().ThrowAsync<NativeSshException>()).Which;
            NativeSshException errorFind = (await FluentActions.Awaiting(() => herramientas.BuscarPorNombreAsync("servidor", "*.cs", "dir_enorme"))
                .Should().ThrowAsync<NativeSshException>()).Which;
            NativeSshException errorList = (await FluentActions.Awaiting(() => herramientas.ListarDirectoriosAsync("servidor", "dir_enorme"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = errorSearch.Message.Should().Contain("superó");
            _ = errorFind.Message.Should().Contain("superó");
            _ = errorList.Message.Should().Contain("superó");
        }

        [Theory]
        [InlineData(1024 * 1024)]
        [InlineData(64 * 1024)]
        [InlineData(20_000)]
        [InlineData(4096 + 77)]
        [InlineData(4096 + 80)]
        public void MaxBytesArchivoEditable_SuBase64ConSaltosCabeConMargenYUnByteMasNo(int maxBytesSalida)
        {
            // Arrange
            static long Base64ConSaltos(long bytes)
            {
                long caracteres = (bytes + 2) / 3 * 4;
                return caracteres + ((caracteres + 75) / 76);
            }

            long disponible = maxBytesSalida - HerramientasSsh.MargenSalidaBase64;

            // Act
            long maximo = HerramientasSsh.CalcularMaxBytesArchivoEditable(maxBytesSalida);

            // Assert
            _ = Base64ConSaltos(maximo).Should().BeLessThanOrEqualTo(disponible);
            _ = Base64ConSaltos(maximo + 1).Should().BeGreaterThan(disponible);
        }

        private const string IdMarcas = "0123456789abcdef0123456789abcdef";
        private const string MarcaInicio = "__NS_B64_INICIO_" + IdMarcas + "__";
        private const string MarcaFin = "__NS_B64_FIN_" + IdMarcas + "__";

        [Fact]
        public void ExtraerArchivoEntreMarcadores_ConAvisosAlrededor_DevuelveSoloElContenido()
        {
            // Arrange: avisos de solo letras (base64 válido) antes y después, y CRLF del terminal.
            string salida = $"Info root\r\nsudo: aviso\n{MarcaInicio}\r\naG9sYQo=\r\n{MarcaFin} 5\r\nInfo root\n";

            // Act
            byte[] bytes = HerramientasSsh.ExtraerArchivoEntreMarcadores(salida, IdMarcas, "a.txt");

            // Assert
            _ = Encoding.UTF8.GetString(bytes).Should().Be("hola\n");
        }

        [Fact]
        public void ExtraerArchivoEntreMarcadores_ArchivoVacio_DevuelveCeroBytes()
        {
            // Act
            byte[] bytes = HerramientasSsh.ExtraerArchivoEntreMarcadores($"{MarcaInicio}\n\n{MarcaFin} 0\n", IdMarcas, "vacio.txt");

            // Assert
            _ = bytes.Should().BeEmpty();
        }

        public static TheoryData<string, string> SalidasDeLecturaInvalidas => new()
        {
            { "aG9sYQo=\n", "marcadores esperados (inicio: 0, fin: 0)" },
            { $"{MarcaInicio}\naG9sYQo=\n", "marcadores esperados (inicio: 1, fin: 0)" },
            { $"{MarcaFin} 5\n{MarcaInicio}\naG9sYQo=\n", "marcadores esperados (inicio: 1, fin: 1)" },
            { $"{MarcaInicio}\n{MarcaInicio}\naG9sYQo=\n{MarcaFin} 5\n", "marcadores esperados (inicio: 2, fin: 1)" },
            { $"{MarcaInicio}\naG9sYQo=\n{MarcaFin} 5\n{MarcaFin} 5\n", "marcadores esperados (inicio: 1, fin: 2)" },
            { "__NS_B64_INICIO_ffffffffffffffffffffffffffffffff__\naG9sYQo=\n__NS_B64_FIN_ffffffffffffffffffffffffffffffff__ 5\n", "marcadores esperados (inicio: 0, fin: 0)" },
            { $"Aviso{MarcaInicio}\naG9sYQo=\n{MarcaFin} 5\n", "marcadores esperados (inicio: 0, fin: 1)" },
            { $"{MarcaInicio}\naG9sYQo=\n{MarcaFin} cinco\n", "no indicó el tamaño" },
            { $"{MarcaInicio}\nsudo: aviso\naG9sYQo=\n{MarcaFin} 5\n", "texto ajeno al base64" },
            { $"{MarcaInicio}\nInfo root\naG9sYQo=\n{MarcaFin} 5\n", "texto ajeno al base64" },
            { $"{MarcaInicio}\nSGVs\naG9sYQo=\n{MarcaFin} 5\n", "Se leyeron 8 bytes" },
            { $"{MarcaInicio}\naG9sYQo\n{MarcaFin} 5\n", "no devolvió base64 válido" },
            { $"{MarcaInicio}\naG9sYQo=\n{MarcaFin} 9\n", "pero stat indicó 9" },
        };

        [Theory]
        [MemberData(nameof(SalidasDeLecturaInvalidas))]
        public void ExtraerArchivoEntreMarcadores_SalidaMezcladaOIncompleta_LanzaSinDevolverContenido(string salida, string mensajeEsperado)
        {
            // Act
            NativeSshException error = FluentActions.Invoking(() => HerramientasSsh.ExtraerArchivoEntreMarcadores(salida, IdMarcas, "a.txt"))
                .Should().Throw<NativeSshException>().Which;

            // Assert
            _ = error.Message.Should().Contain(mensajeEsperado).And.Contain("No se ha escrito nada en 'a.txt'.");
        }

        [Fact]
        public void ExtraerDirectorioMarcado_ConRuidoAlrededor_DevuelveLaRutaExacta()
        {
            // Arrange: CRLF, LS y espacio dentro y al final del nombre; el terminal añade sus propios CRLF.
            string directorio = "/tmp/d\r\ne\u2028 ";
            string salida = $"aviso\r\n__NS_CWD_{IdMarcas}__ {Convert.ToBase64String(Encoding.UTF8.GetBytes(directorio))}\r\notro\n";

            // Act & Assert
            _ = HerramientasSsh.ExtraerDirectorioMarcado(salida, IdMarcas).Should().Be(directorio);
        }

        public static TheoryData<string, string> SalidasDeDirectorioInvalidas => new()
        {
            { "/tmp/trabajo\n", "(marcas: 0)" },
            { "__NS_CWD_ffffffffffffffffffffffffffffffff__ L3RtcA==\n", "(marcas: 0)" },
            { $"__NS_CWD_{IdMarcas}__ L3RtcA==\n__NS_CWD_{IdMarcas}__ L3RtcA==\n", "(marcas: 2)" },
            { $"__NS_CWD_{IdMarcas}__ L3RtcA\n", "no es base64" },
            { $"__NS_CWD_{IdMarcas}__ {Convert.ToBase64String([0x2F, 0xFF])}\n", "UTF-8" },
            { $"__NS_CWD_{IdMarcas}__ {Convert.ToBase64String("tmp"u8)}\n", "no es absoluta" },
        };

        [Theory]
        [MemberData(nameof(SalidasDeDirectorioInvalidas))]
        public void ExtraerDirectorioMarcado_SalidaSinRutaValida_LanzaSinFijarNada(string salida, string mensajeEsperado)
        {
            // Act
            NativeSshException error = FluentActions.Invoking(() => HerramientasSsh.ExtraerDirectorioMarcado(salida, IdMarcas))
                .Should().Throw<NativeSshException>().Which;

            // Assert
            _ = error.Message.Should().Contain(mensajeEsperado).And.Contain("No se ha cambiado el directorio de trabajo.");
        }

        private static string DescripcionDe(string metodo) =>
            typeof(HerramientasSsh).GetMethod(metodo)!.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

        [Fact]
        public void DescripcionesDeEscritura_DocumentanSoloLecturaYLosAtributosQueSeConservan()
        {
            // Act
            string escribir = DescripcionDe(nameof(HerramientasSsh.EscribirArchivoAsync));
            string reemplazar = DescripcionDe(nameof(HerramientasSsh.ReemplazarContenidoArchivoAsync));

            // Assert
            _ = escribir.Should().Contain("archivos de solo lectura propios").And.Contain("conservando su modo");
            _ = escribir.Should().Contain("ACL, propietario, grupo, atributos extendidos y contexto SELinux").And.Contain("enlaces duros");
            _ = reemplazar.Should().Contain("ACL, propietario, grupo, atributos extendidos y contexto SELinux").And.Contain("enlaces duros");
            _ = (escribir + reemplazar).Should().NotContain("el contexto SELinux no cuenta");

            // En el sitio no se afirma que se conserva todo: el núcleo quita las capacidades y, salvo a root, los bits setuid
            // y setgid, que se devuelven y se comprueban (las capacidades, solo con sudo); lo que no se pueda devolver o
            // comprobar lo dice la respuesta.
            _ = (escribir + reemplazar).Should().NotContain("conserva inodo, enlaces duros y todos los atributos");
            _ = escribir.Should().Contain("bits setuid y setgid").And.Contain("salvo si escribe root").And.Contain("security.capability").And.Contain("lo que no se pueda devolver o comprobar lo dice la respuesta");
            _ = reemplazar.Should().Contain("bits setuid y setgid").And.Contain("salvo si escribe root").And.Contain("security.capability").And.Contain("lo que no se pueda devolver o comprobar lo dice la respuesta");

            // Si no termina (también por ssh_disconnect o una conexión perdida) o el script muere a mitad, el error dice qué pasó
            // con el archivo y da el id de la petición; interrumpida en el sitio, el script restaura el original por su cuenta.
            _ = new[] { escribir, reemplazar }.Should().AllSatisfy(descripcion => descripcion.Should()
                .Contain("ssh_disconnect o conexión perdida").And.Contain("o el script muere a mitad (p. ej. por SIGKILL)").And.Contain("el script restaura el original por su cuenta"));
        }

        [Fact]
        public void MaxBytesArchivoEditable_ValorDocumentadoConElMaximoPorDefecto()
        {
            // Act & Assert: 1 MiB de salida por petición => 773 184 bytes (≈755 KiB) de archivo editable.
            _ = HerramientasSsh.CalcularMaxBytesArchivoEditable(new OpcionesNativeSsh().MaxBytesSalidaPorPeticion).Should().Be(773_184);
            _ = HerramientasSsh.CalcularMaxBytesArchivoEditable(HerramientasSsh.MargenSalidaBase64).Should().Be(0);
        }

        [Fact]
        public async Task ReplaceFileContent_ComandoDeLectura_CompruebaElTamanoAntesDeLeer()
        {
            // Act
            _ = await _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_prueba.txt", "linea 3", "linea 3b", 1, 5);

            // Assert
            string lectura = _fabrica.Ultima!.Comandos.First(c => c.Contains("base64", StringComparison.Ordinal));
            _ = lectura.Should().Contain("773184");
            _ = lectura.Should().Contain("supera el máximo editable");
        }

        // ssh_search corta con head; ssh_find y ssh_list_dir, con el bucle que lee sus nombres terminados en NUL.
        [Fact]
        public async Task SearchFindListDir_LimitanLaSalidaEnElServidor()
        {
            // Act
            _ = await _herramientas.BuscarAsync("servidor", "patron", maxResultados: 7);
            _ = await _herramientas.BuscarPorNombreAsync("servidor", "*.conf", maxResultados: 8);
            _ = await _herramientas.ListarDirectoriosAsync("servidor", maxResultados: 9);

            // Assert
            string[] comandos = [.. _fabrica.Ultima!.Comandos];
            _ = comandos.Should().Contain(c => c.Contains("grep -rnI", StringComparison.Ordinal) && c.Contains("| head -n 8", StringComparison.Ordinal));
            _ = comandos.Should().Contain(c => c.Contains("-name '*.conf'", StringComparison.Ordinal) && c.Contains("sort -z | { n=0; while [ \"$n\" -lt 9 ]", StringComparison.Ordinal));
            _ = comandos.Should().Contain(c => c.Contains("-type d -print0", StringComparison.Ordinal) && c.Contains("sort -z | { n=0; while [ \"$n\" -lt 10 ]", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ReadFile_InicioNegativoConLineaFin_LanzaErrorClaro()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.LeerArchivoAsync("servidor", "archivo.txt", -5, 10))
                .Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("lineaFin");
            _ = error.Message.Should().Contain("negativo");
        }

        [Fact]
        public async Task ReadFile_InicioNegativo_LeeLasUltimasLineasConTail()
        {
            // Act
            _ = await FluentActions.Awaiting(() => _herramientas.LeerArchivoAsync("servidor", "archivo.txt", -3)).Should().ThrowAsync<NativeSshException>();

            // Assert: el guion falso no simula tail (responde con código 2); basta con el comando enviado.
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("tail -n 3 -- 'archivo.txt'", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("")]
        public async Task ReadFile_RutaVacia_LanzaExcepcion(string ruta)
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.LeerArchivoAsync("servidor", ruta)).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("vacía");
        }

        [Fact]
        public async Task ReadFile_FinMenorQueInicio_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.LeerArchivoAsync("servidor", "archivo.txt", lineaInicio: 10, lineaFin: 5)).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("no puede ser menor");
        }

        [Fact]
        public async Task ReadFile_FalloCodigoDistintoDeCero_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.LeerArchivoAsync("servidor", "error.txt")).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Be("Error: el archivo no existe: error.txt");
        }

        [Theory]
        [InlineData("")]
        public async Task WriteFile_RutaVacia_LanzaExcepcion(string ruta)
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.EscribirArchivoAsync("servidor", ruta, "contenido")).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("vacía");
        }

        [Fact]
        public async Task WriteFile_ArchivoExisteSinOverwriteNiAppend_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.EscribirArchivoAsync("servidor", "ya_existe.txt", "nuevo contenido", overwrite: false, append: false)).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("ya existe");
        }

        [Fact]
        public async Task WriteFile_SinOverwrite_CreacionExclusivaConLnSinMvForzado()
        {
            // Act
            _ = await _herramientas.EscribirArchivoAsync("servidor", "nuevo.txt", "hola");

            // Assert
            string comando = _fabrica.Ultima!.Comandos.Last(c => c.Contains("base64 -d", StringComparison.Ordinal));
            _ = comando.Should().Contain("ln -T -- \"$tmp\" \"$destino\"").And.Contain("ln -- \"$tmp\" \"$destino\"");
            _ = comando.Should().Contain("set -C");
            _ = comando.Should().NotContain("mv -f");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task WriteFileOverwriteYReplace_ResuelvenEnlacesYCopianModoYPropietarioAlTemporal(bool isReplace)
        {
            // Act
            _ = isReplace
                ? await _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_prueba.txt", "linea 3", "linea 3b", 1, 5, sudo: true)
                : await _herramientas.EscribirArchivoAsync("servidor", "archivo.txt", "hola", overwrite: true, sudo: true);

            // Assert: todo el script, comprobaciones incluidas, en un único 'sudo bash'; el contenido llega por stdin.
            string comando = _fabrica.Ultima!.Comandos.Last(c => c.Contains("base64 -d", StringComparison.Ordinal));
            _ = comando.Should().Contain("| sudo bash -c '");
            _ = comando.Split("sudo ").Should().HaveCount(2);
            _ = comando.Should().Contain("readlink -f -- \"$ruta\"");
            _ = comando.Should().Contain("mktemp -- \"$dir/.$corto.tmp.XXXXXX\"").And.Contain("mktemp -- \"$dir/.$corto.orig.XXXXXX\"");
            _ = comando.IndexOf("\nacortar_nombre\n", StringComparison.Ordinal).Should().BePositive().And.BeLessThan(comando.IndexOf("mktemp -- \"$dir/.$corto.tmp.XXXXXX\"", StringComparison.Ordinal));
            _ = comando.Should().Contain("cp --attributes-only --preserve=mode,ownership,xattr,context -- \"$1\" \"$2\"");
            _ = comando.Should().Contain("conservar_atributos \"$destino\" \"$tmp\"");
            _ = comando.Should().Contain("mv -f -T -- \"$tmp\" \"$destino\"").And.Contain("mv -f -- \"$tmp\" \"$destino\"");
            _ = comando.Should().Contain("base64 -d > \"$tmp\"");
        }

        // La escritura de ssh_replace_file_content recibe la huella de lo que se leyó (sha256 y "suma bytes" de cksum) para
        // comprobar en el servidor que el archivo no cambió; ssh_write_file no compara nada.
        [Fact]
        public async Task ReplaceFileContent_LaEscrituraRecibeLaHuellaDeLoLeido()
        {
            // Act
            _ = await _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_prueba.txt", "linea 3", "linea 3b");
            _ = await _herramientas.EscribirArchivoAsync("servidor", "otro.txt", "x", overwrite: true);

            // Assert
            byte[] leidos = Encoding.UTF8.GetBytes("linea 1\nlinea 2\nlinea 3\nlinea 4\nlinea 5\n");
            string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(leidos));
            string[] escrituras = [.. _fabrica.Ultima!.Comandos.Where(c => c.Contains("base64 -d > \"$tmp\"", StringComparison.Ordinal))];
            _ = escrituras.Should().HaveCount(2);
            _ = escrituras[0].Should().Contain($" {sha} '{CksumPosix.Calcular(leidos)} {leidos.Length}'\n)");
            _ = escrituras[1].Should().Contain(" '' ''\n)");
            _ = escrituras[0].Should().Contain("sin_cambios || exit 1");
        }

        [Fact]
        public async Task ReplaceFileContent_ConSudo_LaLecturaPreviaCompruebaTodoConSudo()
        {
            // Act
            _ = await _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_prueba.txt", "linea 3", "linea 3b", sudo: true);

            // Assert: existencia, tipo, tamaño y lectura dentro del mismo 'sudo bash'.
            string lectura = _fabrica.Ultima!.Comandos.First(c => c.Contains("| base64", StringComparison.Ordinal));
            _ = lectura.Should().Contain("sudo bash -c '");
            _ = lectura.Split("sudo ").Should().HaveCount(2);
            _ = lectura.IndexOf("sudo bash", StringComparison.Ordinal).Should().BeLessThan(lectura.IndexOf("[ ! -e \"$ruta\" ]", StringComparison.Ordinal));
        }

        [Fact]
        public async Task WriteFile_EscrituraEnElSitio_LoIndicaEnElMensaje()
        {
            // Act
            ResultadoEscritura resultado = await _herramientas.EscribirArchivoAsync("servidor", "no_atomico.txt", "hola", overwrite: true);

            // Assert
            _ = resultado.IsAtomica.Should().BeFalse();
            _ = resultado.Mensaje.Should().Contain("Archivo escrito correctamente: no_atomico.txt");
            _ = resultado.Mensaje.Should().Contain("no atómica");
            _ = resultado.Mensaje.Should().NotContain("__NS_");
        }

        [Fact]
        public async Task WriteFile_OverwriteYAppendSimultaneos_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.EscribirArchivoAsync("servidor", "archivo.txt", "contenido", overwrite: true, append: true)).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("No se puede especificar 'overwrite=true' y 'append=true' simultáneamente.");
        }

        [Fact]
        public async Task WriteFile_EscrituraExitosa_DevuelveMensajeConfirmacion()
        {
            // Act
            ResultadoEscritura resultado = await _herramientas.EscribirArchivoAsync("servidor", "nuevo.txt", "hola mundo");

            // Assert
            _ = resultado.Should().BeEquivalentTo(new ResultadoEscritura("nuevo.txt", 10, null, true, "Archivo escrito correctamente: nuevo.txt (10 bytes)."));
        }

        [Fact]
        public async Task WriteFile_AnexadoExitoso_DevuelveMensajeConfirmacion()
        {
            // Act
            ResultadoEscritura resultado = await _herramientas.EscribirArchivoAsync("servidor", "nuevo.txt", "hola", append: true);

            // Assert: anexar con '>>' no promete atomicidad.
            _ = resultado.Should().BeEquivalentTo(new ResultadoEscritura("nuevo.txt", 4, null, false, "Contenido anexado correctamente a: nuevo.txt (4 bytes)."));
        }

        [Theory]
        [InlineData("")]
        public async Task Search_PatronVacio_LanzaExcepcion(string patron)
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.BuscarAsync("servidor", patron)).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("patrón de búsqueda no puede estar vacío");
        }

        [Fact]
        public async Task Search_SinCoincidencias_DevuelveMensajeInformativoSinError()
        {
            // Act
            string resultado = await _herramientas.BuscarAsync("servidor", "sin_coincidencias");

            // Assert
            _ = resultado.Should().Be("No se encontraron coincidencias para el patrón: 'sin_coincidencias' en '.'.\n\n[motor: grep -P]");
        }

        [Fact]
        public async Task Search_ConCoincidencias_DevuelveTextoFiltrado()
        {
            // Act
            string resultado = await _herramientas.BuscarAsync("servidor", "algo");

            // Assert
            _ = resultado.Should().Be("archivo.txt:1:coincidencia encontrada\n\n[motor: grep -P]");
        }

        [Fact]
        public async Task Search_LimiteAlcanzado_AnexaAviso()
        {
            // Act
            string resultado = await _herramientas.BuscarAsync("servidor", "muchas_coincidencias", maxResultados: 3);

            // Assert
            _ = resultado.Should().Contain("[Límite alcanzado: se muestran 3 coincidencias.");
            _ = resultado.Should().Contain("coincidencia 3");
            _ = resultado.Should().NotContain("coincidencia 4");
            _ = resultado.Should().EndWith("Refina el patrón o subdirectorio para acotar la búsqueda.]\n[motor: grep -P]");
        }

        [Fact]
        public async Task Search_CompruebaQueLaRutaPedidaSeLeeAntesDeBuscar()
        {
            // Act
            _ = await _herramientas.BuscarAsync("servidor", "patron", "src");

            // Assert
            string comando = _fabrica.Ultima!.Comandos.Last(c => c.Contains("grep -rnI", StringComparison.Ordinal));
            _ = comando.Should().Contain("if [ ! -r 'src' ] || { [ -d 'src' ] && [ ! -x 'src' ]; }; then");
            _ = comando.IndexOf("sin permiso de lectura", StringComparison.Ordinal).Should().BeLessThan(comando.IndexOf("command -v rg", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Search_EligeRgLuegoGrepPYComoUltimoRecursoGrepE()
        {
            // Act
            _ = await _herramientas.BuscarAsync("servidor", "patron", extension: "py", maxResultados: 7);

            // Assert
            string comando = _fabrica.Ultima!.Comandos.Last(c => c.Contains("grep -rnI", StringComparison.Ordinal));
            int rg = comando.IndexOf("command -v rg", StringComparison.Ordinal);
            int grepP = comando.IndexOf("grep -qP", StringComparison.Ordinal);
            int grepE = comando.IndexOf("-E -e 'patron'", StringComparison.Ordinal);
            _ = rg.Should().BeGreaterThan(0);
            _ = grepP.Should().BeGreaterThan(rg);
            _ = grepE.Should().BeGreaterThan(grepP);
            _ = comando.Should().Contain("rg --no-config");
            _ = comando.Should().Contain("-g '*.py'");
            _ = comando.Should().Contain("-g '!{.git,node_modules,");
            _ = comando.Should().Contain("sintaxis=P").And.Contain("-\"$sintaxis\" -e 'patron'");
            _ = comando.Should().Contain("__NS_MOTOR:rg");
        }

        [Fact]
        public async Task Search_PatronConGuion_UsaFlagEParaProtegerPatron()
        {
            // Act
            string resultado = await _herramientas.BuscarAsync("servidor", "--version");

            // Assert
            _ = resultado.Should().StartWith("archivo.txt:1:coincidencia encontrada");
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("-\"$sintaxis\" -e '--version'") && c.Contains("-E -e '--version'") && c.Contains("rg --no-config") && c.Contains("-e '--version'"));
        }

        [Fact]
        public async Task Search_RutaInexistente_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.BuscarAsync("servidor", "algo", "ruta_inexistente")).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("la ruta de búsqueda no existe");
        }

        [Fact]
        public async Task Search_FalloGrep_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.BuscarAsync("servidor", "error_fatal")).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("error de sintaxis");
            _ = error.Message.Should().Contain("[motor: grep -P]");
            _ = error.Message.Should().NotContain("__NS_");
        }

        // Código 2 con resultados y con stderr: el motor falló en parte del árbol. Se dan los resultados con un aviso de que
        // pueden faltar coincidencias y el error, en vez de descartarlo (A-decies, AD3).
        [Fact]
        public async Task Search_ErrorConResultados_AvisaDeQueElResultadoPuedeSerParcial()
        {
            // Act
            string salida = await _herramientas.BuscarAsync("servidor", "algo", "parcial_con_error");

            // Assert
            _ = salida.Should().StartWith("archivo.txt:1:coincidencia encontrada\n")
                .And.Contain("pueden faltar coincidencias")
                .And.Contain("grep: sub/x.txt: Input/output error")
                .And.EndWith("[motor: grep -P]");
        }

        // Un 2 sin stderr (los errores que -s/--no-messages callan, como archivos ilegibles) también avisa, sin detalle: algo
        // no se pudo leer y pueden faltar coincidencias (A-undecies, AU3).
        [Fact]
        public async Task Search_CodigoDosSinErroresConResultados_AvisaSinDetalle()
        {
            // Act
            string salida = await _herramientas.BuscarAsync("servidor", "algo", "parcial_sin_error");

            // Assert
            _ = salida.Should().Be($"archivo.txt:1:coincidencia encontrada\n\n{AvisoCodigoDosSinDetalle}\n[motor: grep -P]");
        }

        [Fact]
        public async Task Search_CodigoDosSinErroresNiResultados_DiceQueNoHayCoincidenciasYAvisaSinDetalle()
        {
            // Act
            string salida = await _herramientas.BuscarAsync("servidor", "algo", "vacio_codigo_dos");

            // Assert
            _ = salida.Should().Be($"No se encontraron coincidencias para el patrón: 'algo' en 'vacio_codigo_dos'.\n\n{AvisoCodigoDosSinDetalle}\n[motor: grep -P]");
        }

        private const string AvisoCodigoDosSinDetalle = "[Aviso: el motor terminó con código 2 sin mostrar el error (calla así los archivos o directorios que no puede leer, p. ej. por permisos), así que pueden faltar coincidencias.]";

        // El stderr del aviso se acota a sus primeros 1000 caracteres, con «…» (A-undecies, AU3; mutación V01).
        [Fact]
        public async Task Search_ErrorLargoConResultados_AcotaElErrorA1000Caracteres()
        {
            // Act
            string salida = await _herramientas.BuscarAsync("servidor", "algo", "parcial_error_largo");

            // Assert
            _ = salida.Should().Be($"archivo.txt:1:coincidencia encontrada\n\n[Aviso: la búsqueda terminó con errores, así que pueden faltar coincidencias: {ErrorLargo[..1000]}…]\n[motor: grep -P]");
        }

        // Si el carácter 1000 es la primera mitad de un par sustituto, el corte se queda en 999 (no deja medio emoji).
        [Fact]
        public async Task Search_ErrorConUnEmojiEnElCorte_NoParteElParSustituto()
        {
            // Act
            string salida = await _herramientas.BuscarAsync("servidor", "algo", "parcial_error_emoji");

            // Assert
            _ = salida.Should().Be($"archivo.txt:1:coincidencia encontrada\n\n[Aviso: la búsqueda terminó con errores, así que pueden faltar coincidencias: {ErrorConEmojiEnElCorte[..999]}…]\n[motor: grep -P]");
            _ = salida.EnumerateRunes().Should().NotContain(Rune.ReplacementChar);
        }

        // Con el límite alcanzado y errores, salen los dos avisos (mutación V02).
        [Fact]
        public async Task Search_ErrorConElLimiteAlcanzado_DaLosDosAvisos()
        {
            // Act
            string salida = await _herramientas.BuscarAsync("servidor", "algo", "parcial_limite", maxResultados: 2);

            // Assert
            _ = salida.Should().Be("a.txt:1:uno\nb.txt:2:dos\n\n[Límite alcanzado: se muestran 2 coincidencias. Refina el patrón o subdirectorio para acotar la búsqueda.]\n[Aviso: la búsqueda terminó con errores, así que pueden faltar coincidencias: grep: sub/x.txt: Input/output error]\n[motor: grep -P]");
        }

        [Theory]
        [InlineData("py", "--include='*.py'")]
        [InlineData(".ts", "--include='*.ts'")]
        [InlineData("*.json", "--include='*.json'")]
        public async Task Search_ConExtension_IncluyeFlagInclude(string extension, string esperado)
        {
            // Act
            _ = await _herramientas.BuscarAsync("servidor", "patron", extension: extension);

            // Assert
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains(esperado));
        }

        [Fact]
        public async Task ListDir_RutaInexistente_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                _herramientas.ListarDirectoriosAsync("servidor", "ruta_inexistente")).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("la ruta de exploración no existe");
        }

        [Fact]
        public async Task ListDir_SinSubdirectorios_DevuelveMensajeInformativo()
        {
            // Act
            string resultado = await _herramientas.ListarDirectoriosAsync("servidor", "vacio");

            // Assert
            _ = resultado.Should().Be("No se encontraron subdirectorios en 'vacio'.");
        }

        [Fact]
        public async Task ListDir_ConDirectorios_DevuelveListadoOrdenado()
        {
            // Act
            string resultado = await _herramientas.ListarDirectoriosAsync("servidor", "proyecto");

            // Assert
            _ = resultado.Should().Be("src\nsrc/models\nsrc/views");
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("find -L . -maxdepth 3 -mindepth 1"));
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains(@"\( -name __pycache__ -o -name .cache \) -prune"));
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains(@"-o \( -name .git -o -name node_modules"));
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("-o -type d -print"));
        }

        [Fact]
        public async Task ListDir_ConIncluirArchivos_DevuelveArchivosYDirectorios()
        {
            // Act
            string resultado = await _herramientas.ListarDirectoriosAsync("servidor", "proyecto", incluirArchivos: true);

            // Assert
            _ = resultado.Should().Contain("Program.cs");
            _ = resultado.Should().Contain("src");
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains(@"\( -name __pycache__ -o -name .cache \) -prune"));
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains(@"-o \( -name .git -o -name node_modules"));
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("-o -print0 2>/dev/null"));
            _ = _fabrica.Ultima!.Comandos.Should().NotContain("-type d -print");
        }

        [Fact]
        public async Task ListDir_ConMarcadores_MuestraMarcadoresYPrune()
        {
            // Act
            string resultado = await _herramientas.ListarDirectoriosAsync("servidor", "marcadores");

            // Assert
            _ = resultado.Should().Contain(".git");
            _ = resultado.Should().Contain("node_modules");
            _ = resultado.Should().Contain("src");
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains(@"-o \( -name .git -o -name node_modules -o -name .venv"));
        }

        [Fact]
        public async Task ListDir_ConIncluirArchivos_VacioDevuelveElementos()
        {
            // Act
            string resultado = await _herramientas.ListarDirectoriosAsync("servidor", "vacio", incluirArchivos: true);

            // Assert
            _ = resultado.Should().Be("No se encontraron elementos en 'vacio'.");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task Find_PatronVacio_LanzaExcepcion(string? patron)
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                _herramientas.BuscarPorNombreAsync("servidor", patron!)).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("El patrón de búsqueda no puede estar vacío");
        }

        [Fact]
        public async Task Find_SinResultados_DevuelveMensajeInformativo()
        {
            // Act
            string resultado = await _herramientas.BuscarPorNombreAsync("servidor", "sin_coincidencias");

            // Assert
            _ = resultado.Should().Be("No se encontraron elementos que coincidan con 'sin_coincidencias' en '.'.");
        }

        [Fact]
        public async Task Find_ConResultados_DevuelveRutasOrdenadas()
        {
            // Act
            string resultado = await _herramientas.BuscarPorNombreAsync("servidor", "*.service");

            // Assert
            _ = resultado.Should().Be("app.service\nnginx.conf");
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("-name '*.service'"));
        }

        [Theory]
        [InlineData(".git", ".git")]
        [InlineData("node_modules", "node_modules")]
        [InlineData("__pycache__", "__pycache__")]
        [InlineData(".venv", ".venv")]
        public async Task Find_CarpetasExcluidas_DevuelveCarpetaYComandoContienePrintPrune(string patron, string esperado)
        {
            // Act
            string resultado = await _herramientas.BuscarPorNombreAsync("servidor", patron, tipo: "dir");

            // Assert
            _ = resultado.Should().Be(esperado);
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("-print0 -prune"));
        }

        [Fact]
        public async Task Find_ArchivosNormales_ExcluyeCarpetasPodadas()
        {
            // Act
            string resultado = await _herramientas.BuscarPorNombreAsync("servidor", "*.cs", tipo: "file");

            // Assert
            _ = resultado.Should().Contain("Program.cs");
            _ = resultado.Should().NotContain("node_modules");
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("-print0 -prune"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task Stat_RutaVacia_LanzaExcepcion(string? ruta)
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                _herramientas.ConsultarMetadatosAsync("servidor", ruta!)).Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("La ruta no puede estar vacía");
        }

        [Fact]
        public async Task Stat_ArchivoInexistente_DevuelveExisteFalse()
        {
            // Act
            ResultadoMetadatos resultado = await _herramientas.ConsultarMetadatosAsync("servidor", "no_existe.txt");

            // Assert
            _ = resultado.Existe.Should().BeFalse();
            _ = resultado.Ruta.Should().Be("no_existe.txt");
            _ = resultado.Mensaje.Should().Contain("no existe");
        }

        [Fact]
        public async Task Stat_ArchivoExistente_DevuelveMetadatosJson()
        {
            // Act
            ResultadoMetadatos resultado = await _herramientas.ConsultarMetadatosAsync("servidor", "archivo.txt");

            // Assert: contrato estructurado (no un JSON serializado dentro de un string).
            _ = resultado.Should().Be(new ResultadoMetadatos("archivo.txt", true, null, "archivo regular", 2048, "2 KiB", "644", "2026-10-05 01:00:00", "ana", "devs", null));
            string json = JsonSerializer.Serialize(resultado, ContextoJsonNativeSsh.OpcionesHerramientas);
            _ = json.Should().Contain("\"tamanoBytes\":2048").And.Contain("\"propietario\":\"ana\"").And.NotContain("destinoEnlace");

            // El tipo lo calcula el script con test (valores fijos), no sale del texto de stat (%F, %HT), que depende del idioma.
            string comando = _fabrica.Ultima!.Comandos.Single(c => c.Contains("stat -c", StringComparison.Ordinal));
            _ = comando.Should().Contain("elif [ -f 'archivo.txt' ]; then tipo='archivo regular'").And.Contain("printf '%s|' \"$tipo\"");
            _ = comando.Should().NotContain("%F").And.NotContain("%HT");
        }

        [Fact]
        public async Task Stat_EnlaceSimbolico_DevuelveDestinoEnlace()
        {
            // Act
            ResultadoMetadatos resultado = await _herramientas.ConsultarMetadatosAsync("servidor", "enlace.txt");

            // Assert
            _ = resultado.Tipo.Should().Be("enlace simbólico");
            _ = resultado.DestinoEnlace.Should().Be("/etc/real.conf");
            _ = resultado.Propietario.Should().Be("ana");
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("readlink --", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task SetCwd_RutaVaciaONull_RestableceHome(string? ruta)
        {
            // Act
            ResultadoDirectorioTrabajo resultado = await _herramientas.FijarDirectorioAsync("servidor", ruta);

            // Assert
            _ = resultado.Should().Be(new ResultadoDirectorioTrabajo("ana@servidor:22", null, "Directorio de trabajo persistente restablecido a $HOME (~)."));
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("unset __NS_CWD"));
        }

        [Fact]
        public async Task SetCwd_RutaInexistente_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                _herramientas.FijarDirectorioAsync("servidor", "ruta_inexistente"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            _ = error.Message.Should().Contain("la ruta no existe: ruta_inexistente");
        }

        [Fact]
        public async Task SetCwd_RutaValida_FijaVariable()
        {
            // Act
            ResultadoDirectorioTrabajo resultado = await _herramientas.FijarDirectorioAsync("servidor", "/var/www/proyecto");

            // Assert
            _ = resultado.Should().Be(new ResultadoDirectorioTrabajo("ana@servidor:22", "/var/www/proyecto", "Directorio de trabajo persistente fijado a: /var/www/proyecto"));
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("export __NS_CWD='/var/www/proyecto'"));
        }

        [Theory]
        [InlineData("", "target", 1, 5, "vacía")]
        [InlineData("archivo.txt", "", 1, 5, "targetContent")]
        [InlineData("archivo.txt", "target", 0, 5, "mayor o igual a 1")]
        [InlineData("archivo.txt", "target", 5, 2, "no puede ser menor")]
        public async Task ReplaceFileContent_ParametrosInvalidos_LanzaExcepcion(string ruta, string target, int inicio, int fin, string mensajeEsperado)
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                _herramientas.ReemplazarContenidoArchivoAsync("servidor", ruta, target, "replacement", inicio, fin))
                .Should().ThrowAsync<NativeSshException>()).Which;

            _ = error.Message.Should().Contain(mensajeEsperado);
        }

        [Fact]
        public async Task ReplaceFileContent_TargetNoEncontradoEnRango_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_prueba.txt", "inexistente", "nuevo", 1, 3))
                .Should().ThrowAsync<NativeSshException>()).Which;

            _ = error.Message.Should().Contain("No se encontró 'targetContent' en el rango de líneas 1-3");
        }

        [Fact]
        public async Task ReplaceFileContent_MultiplesAparicionesSinAllowMultiple_LanzaExcepcion()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() =>
                _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_multiples.txt", "repetido", "nuevo", 1, 4, allowMultiple: false))
                .Should().ThrowAsync<NativeSshException>()).Which;

            _ = error.Message.Should().Contain("Se encontraron 2 apariciones de 'targetContent'");
        }

        [Fact]
        public async Task ReplaceFileContent_ReemplazoExitoso_DevuelveMensajeConfirmacion()
        {
            // Act
            ResultadoEscritura resultado = await _herramientas.ReemplazarContenidoArchivoAsync(
                "servidor",
                "archivo_prueba.txt",
                "linea 3",
                "linea 3 modificada",
                lineaInicio: 2,
                lineaFin: 4);

            // Assert: 'bytes' es el tamaño final del archivo ("linea 1\n..linea 5\n" + " modificada").
            _ = resultado.Should().Be(new ResultadoEscritura("archivo_prueba.txt", 51, 1, true, "Archivo modificado quirúrgicamente con éxito: archivo_prueba.txt (1 reemplazo(s) realizado(s))."));
            _ = _fabrica.Ultima!.Comandos.Should().Contain(c => c.Contains("base64 -d"));
        }

        [Theory]
        [InlineData("archivo_latin1.txt", "UTF-8")]
        [InlineData("archivo_binario.bin", "binario")]
        public async Task ReplaceFileContent_ArchivoNoUtf8OBinario_SeRechazaSinEscribir(string ruta, string mensajeEsperado)
        {
            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.ReemplazarContenidoArchivoAsync("servidor", ruta, "linea", "otra", 1, 2))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain(mensajeEsperado);
            _ = error.Message.Should().Contain("No se ha escrito nada");
            _ = _fabrica.Ultima!.Comandos.Should().NotContain(c => c.Contains("base64 -d", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ReplaceFileContent_ArchivoConBom_ConservaElBom()
        {
            // Act
            _ = await _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_bom.txt", "linea 2", "linea dos", 1, 2);

            // Assert
            string escritura = _fabrica.Ultima!.Comandos.Last(c => c.Contains("base64 -d", StringComparison.Ordinal));
            int inicio = escritura.IndexOf("printf '%s' '", StringComparison.Ordinal) + 13;
            string payload = escritura[inicio..escritura.IndexOf('\'', inicio)];
            _ = Convert.FromBase64String(payload).Should().Equal([0xEF, 0xBB, 0xBF, .. "linea 1\nlinea dos\n"u8]);
        }

        [Fact]
        public async Task ReplaceFileContent_ConAllowMultiple_ReemplazaTodas()
        {
            // Act
            ResultadoEscritura resultado = await _herramientas.ReemplazarContenidoArchivoAsync(
                "servidor",
                "archivo_multiples.txt",
                "repetido",
                "cambiado",
                lineaInicio: 1,
                lineaFin: 4,
                allowMultiple: true);

            // Assert
            _ = resultado.Reemplazos.Should().Be(2);
            _ = resultado.IsAtomica.Should().BeTrue();
            _ = resultado.Mensaje.Should().Be("Archivo modificado quirúrgicamente con éxito: archivo_multiples.txt (2 reemplazo(s) realizado(s)).");
        }

        private string PayloadUltimaEscritura()
        {
            string escritura = _fabrica.Ultima!.Comandos.Last(c => c.Contains("base64 -d", StringComparison.Ordinal));
            int inicio = escritura.IndexOf("printf '%s' '", StringComparison.Ordinal) + 13;
            return Encoding.UTF8.GetString(Convert.FromBase64String(escritura[inicio..escritura.IndexOf('\'', inicio)]));
        }

        [Fact]
        public async Task ReplaceFileContent_SinRango_BuscaEnTodoElArchivo()
        {
            // Act
            _ = await _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_prueba.txt", "linea 5", "linea cinco");

            // Assert
            _ = PayloadUltimaEscritura().Should().Be("linea 1\nlinea 2\nlinea 3\nlinea 4\nlinea cinco\n");
        }

        [Fact]
        public async Task ReplaceFileContent_SinRango_VariasAparicionesSinAllowMultiple_LanzaSinEscribir()
        {
            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_multiples.txt", "repetido", "nuevo"))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Contain("Se encontraron 2 apariciones de 'targetContent' en todo el archivo");
            _ = _fabrica.Ultima!.Comandos.Should().NotContain(c => c.Contains("base64 -d", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ReplaceFileContent_SinRango_ConAllowMultiple_ReemplazaTodas()
        {
            // Act
            _ = await _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_multiples.txt", "repetido", "cambiado", allowMultiple: true);

            // Assert
            _ = PayloadUltimaEscritura().Should().Be("linea 1\ncambiado\ncambiado\nlinea 4\n");
        }

        [Fact]
        public async Task ReplaceFileContent_SinRango_TargetNoEncontrado_LanzaMencionandoTodoElArchivo()
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_prueba.txt", "inexistente", "nuevo"))
                .Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("No se encontró 'targetContent' en todo el archivo");
        }

        // Solo '\r\n' y '\n' se adaptan al separador del archivo; FF, NEL, LS, PS y un '\r' suelto son contenido.
        [Fact]
        public async Task ReplaceFileContent_ArchivoLf_SoloAdaptaLosSaltosCrlfYConservaLosDemasSeparadores()
        {
            // Act
            ResultadoEscritura resultado = await _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_separadores.txt", "dos\u0085tres\u2028cuatro\u2029cinco\rseis\r\nsiete", $"a{SeparadoresExoticos}b\r\nc\nd");

            // Assert
            _ = resultado.Reemplazos.Should().Be(1);
            _ = PayloadUltimaEscritura().Should().Be($"uno\fa{SeparadoresExoticos}b\nc\nd\n");
        }

        [Fact]
        public async Task ReplaceFileContent_ArchivoCrlf_AdaptaLosSaltosLfYConservaLosDemasSeparadores()
        {
            // Act: el objetivo con '\n' encuentra el texto con CRLF del archivo.
            ResultadoEscritura resultado = await _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_windows.txt", "uno\ndos", $"x{SeparadoresExoticos}y\nz\r\nw");

            // Assert
            _ = resultado.Reemplazos.Should().Be(1);
            _ = PayloadUltimaEscritura().Should().Be($"x{SeparadoresExoticos}y\r\nz\r\nw\r\ntres\r\n");
        }

        [Theory]
        [InlineData(2, null)]
        [InlineData(null, 4)]
        public async Task ReplaceFileContent_SoloUnLimiteDelRango_LanzaErrorClaro(int? inicio, int? fin)
        {
            // Act & Assert
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_prueba.txt", "linea 3", "otra", inicio, fin))
                .Should().ThrowAsync<NativeSshException>()).Which;
            _ = error.Message.Should().Contain("lineaInicio y lineaFin");
            _ = _fabrica.Ultima.Should().BeNull();
        }

        // Los valores del usuario se copian en el script tal cual: ningún separador de línea (CR, CRLF, FF, NEL, LS, PS) se
        // convierte en '\n', ni por canal exec ni por el shell PTY (con y sin sudo).
        [Theory]
        [InlineData("\r")]
        [InlineData("\r\n")]
        [InlineData("\f")]
        [InlineData("\u0085")]
        [InlineData("\u2028")]
        [InlineData("\u2029")]
        public async Task Herramientas_ValoresConSeparadoresDeLinea_LleganAlScriptTalCual(string separador)
        {
            // Arrange
            string ruta = $"x{separador}y";
            string patron = $"p{separador}q";

            // Act
            _ = await _herramientas.LeerArchivoAsync("servidor", ruta, 1, 3);
            _ = await _herramientas.ConsultarMetadatosAsync("servidor", ruta);
            _ = await _herramientas.BuscarAsync("servidor", patron, ruta, $"t{separador}xt");
            _ = await _herramientas.BuscarPorNombreAsync("servidor", patron, ruta);
            _ = await _herramientas.ListarDirectoriosAsync("servidor", ruta);
            _ = await _herramientas.EscribirArchivoAsync("servidor", ruta, "c", overwrite: true);
            _ = await _herramientas.EscribirArchivoAsync("servidor", ruta, "c", append: true, sudo: true);
            _ = await _herramientas.ReemplazarContenidoArchivoAsync("servidor", ruta, "linea 3", "otra");
            _ = await _herramientas.FijarDirectorioAsync("servidor", ruta);

            // Assert
            string[] comandos = [.. _fabrica.Ultima!.Comandos];
            _ = comandos.Count(c => c.Contains($"'{ruta}'", StringComparison.Ordinal)).Should().BeGreaterThanOrEqualTo(10);
            _ = comandos.Should().Contain(c => c.Contains($"-e '{patron}'", StringComparison.Ordinal) && c.Contains($"'*.t{separador}xt'", StringComparison.Ordinal));
            _ = comandos.Should().Contain(c => c.Contains($"-name '{patron}'", StringComparison.Ordinal));
            _ = comandos.Should().NotContain(c => c.Contains("x\ny", StringComparison.Ordinal) || c.Contains("p\nq", StringComparison.Ordinal) || c.Contains("t\nxt", StringComparison.Ordinal));
        }

        // Solo null o la cadena vacía son «sin valor»: una ruta o un patrón de solo blancos es un valor literal que llega al
        // servidor tal cual, nunca un error de «vacío» ni el directorio actual o $HOME en su lugar.
        [Fact]
        public async Task Herramientas_ValoresDeSoloBlancos_SonLiteralesYNoSeSustituyen()
        {
            // Act
            _ = await _herramientas.LeerArchivoAsync("servidor", " ", 1, 3);
            _ = await _herramientas.ConsultarMetadatosAsync("servidor", " ");
            _ = await _herramientas.BuscarAsync("servidor", "  ", " ");
            _ = await _herramientas.BuscarPorNombreAsync("servidor", "  ", " ");
            _ = await _herramientas.ListarDirectoriosAsync("servidor", " ");
            _ = await _herramientas.EscribirArchivoAsync("servidor", " ", "c", overwrite: true);
            _ = await _herramientas.ReemplazarContenidoArchivoAsync("servidor", " ", "linea 3", "otra");
            ResultadoDirectorioTrabajo directorio = await _herramientas.FijarDirectorioAsync("servidor", " ");

            // Assert
            string[] comandos = [.. _fabrica.Ultima!.Comandos];
            _ = comandos.Should().Contain(c => c.Contains("head -n 3 -- ' ' | tail -n +1", StringComparison.Ordinal));
            _ = comandos.Should().Contain(c => c.Contains("stat -c", StringComparison.Ordinal) && c.Contains("[ ! -e ' ' ] && [ ! -L ' ' ]", StringComparison.Ordinal));
            _ = comandos.Should().Contain(c => c.Contains("-e '  ' -- ' '", StringComparison.Ordinal));
            _ = comandos.Where(c => c.Contains("find -L . -maxdepth", StringComparison.Ordinal)).Should().HaveCount(2)
                .And.AllSatisfy(c => c.Should().Contain("cd -- ' '"));
            _ = comandos.Should().Contain(c => c.Contains("-name '  '", StringComparison.Ordinal));
            _ = comandos.Where(c => c.Contains("base64 -d > \"$tmp\"", StringComparison.Ordinal) || c.Contains("cat -- \"$ruta\" | base64", StringComparison.Ordinal)).Should().HaveCount(3)
                .And.AllSatisfy(c => c.Should().Contain(" ns ' '"));
            _ = comandos.Should().Contain(c => c.Contains("ruta=' '", StringComparison.Ordinal) && c.Contains("pwd -P", StringComparison.Ordinal));
            _ = comandos.Should().NotContain(c => c.Contains("unset __NS_CWD", StringComparison.Ordinal) || c.Contains("'.'", StringComparison.Ordinal));
            _ = directorio.Directorio.Should().Be("/tmp/trabajo");
        }

        // La extensión no se recorta: un CR, NEL, LS, tabulador o espacio en un extremo forma parte del filtro, y una
        // extensión de solo blancos también es un filtro.
        [Theory]
        [InlineData("txt\r")]
        [InlineData("txt ")]
        [InlineData(" txt")]
        [InlineData("txt\t")]
        [InlineData("txt\u2028")]
        [InlineData("\u0085txt")]
        [InlineData(" ")]
        public void Search_ExtensionConBlancosOSeparadoresEnLosExtremos_LlegaTalCualAlFiltro(string extension)
        {
            // Act
            string comando = HerramientasSsh.ConstruirComandoBusqueda("patron", ".", extension, 50);

            // Assert
            _ = comando.Should().Contain($"-g '*.{extension}' ").And.Contain($"--include='*.{extension}' ");
        }

        private const string NombresConBlancos = " uno\n\u2028dos\ntres \u2029";
        private const string LineasConBlancos = " /a.txt:1:  sangría\nb.txt:2:fin \u2029 \r";

        // Los listados solo pierden el salto de línea final: los blancos y separadores al principio del primer nombre (o
        // de la primera línea encontrada) y al final del último son datos. En ssh_find y ssh_list_dir, un nombre con un
        // separador de línea o de párrafo de Unicode (U+2028, U+2029) sale escapado como una cadena JSON, entero.
        [Fact]
        public async Task SearchFindListDir_BlancosEnLosExtremosDelListado_SeConservan()
        {
            // Act
            string busqueda = await _herramientas.BuscarAsync("servidor", "patron", "extremos");
            string encontrados = await _herramientas.BuscarPorNombreAsync("servidor", "*", "extremos");
            string listado = await _herramientas.ListarDirectoriosAsync("servidor", "extremos", incluirArchivos: true);

            // Assert
            _ = busqueda.Should().Be($"{LineasConBlancos}\n\n[motor: grep -P]");
            const string NombresEscapados = " uno\n\"\\u2028dos\"\n\"tres \\u2029\"";
            _ = encontrados.Should().Be(NombresEscapados);
            _ = listado.Should().Be(NombresEscapados);
        }

        // Un nombre con caracteres de control (categoría Cc: salto de línea, retorno de carro, tabulador, NEL, DEL...) o que
        // empieza por comillas dobles sale entre comillas dobles y escapado como una cadena JSON, en una sola línea; los
        // demás salen tal cual (también una barra invertida o unas comillas en medio del nombre). Las descripciones lo dicen.
        [Fact]
        public async Task FindYListDir_NombresConCaracteresDeControl_SalenEscapadosEnUnaSolaLinea()
        {
            // Act
            string encontrados = await _herramientas.BuscarPorNombreAsync("servidor", "*", "control");
            string listado = await _herramientas.ListarDirectoriosAsync("servidor", "control", incluirArchivos: true);

            // Assert
            string esperado = """
                "a\nb.txt"
                "\"citado\".txt"
                medio"comilla.txt
                dev-disk-by\x2duuid.swap
                "tab\there"
                "\u0085nel\u007F"
                "barra\\ y\r"
                """.ReplaceLineEndings("\n");
            _ = encontrados.Should().Be(esperado);
            _ = listado.Should().Be(esperado);
            _ = new[] { DescripcionDe(nameof(HerramientasSsh.BuscarPorNombreAsync)), DescripcionDe(nameof(HerramientasSsh.ListarDirectoriosAsync)) }
                .Should().AllSatisfy(descripcion => descripcion.Should().Contain("caracteres de control").And.Contain("U+2028, U+2029").And.Contain("entre comillas dobles y escapado como una cadena JSON"));
        }

        // Un tipo que no es 'file', 'dir' (o 'directory') ni 'all' es un error, no una búsqueda de archivos.
        [Theory]
        [InlineData("directorio")]
        [InlineData("f")]
        [InlineData(" dir")]
        public async Task Find_TipoDesconocido_LanzaErrorSinBuscar(string tipo)
        {
            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => _herramientas.BuscarPorNombreAsync("servidor", "*.txt", tipo: tipo))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().Be($"Tipo '{tipo}' no válido: usa 'file', 'dir' (o 'directory') o 'all', sin distinguir mayúsculas.");
            _ = _fabrica.Ultima.Should().BeNull();
        }

        // El tipo no distingue mayúsculas y admite 'directory' como sinónimo de 'dir': la descripción del parámetro lo dice,
        // igual que el valor por defecto y que cualquier otro valor da error.
        [Theory]
        [InlineData("FILE", "-type f ")]
        [InlineData("Dir", "-type d ")]
        [InlineData("directory", "-type d ")]
        [InlineData("ALL", null)]
        public async Task Find_TipoEnMayusculasOSinonimo_SeAceptaComoDiceSuDescripcion(string tipo, string? filtro)
        {
            // Act
            _ = await _herramientas.BuscarPorNombreAsync("servidor", "*.service", tipo: tipo);
            string descripcion = typeof(HerramientasSsh).GetMethod(nameof(HerramientasSsh.BuscarPorNombreAsync))!.GetParameters().Single(p => p.Name == "tipo")
                .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

            // Assert
            string comando = _fabrica.Ultima!.Comandos.Single(c => c.Contains("-name '*.service'", StringComparison.Ordinal));
            _ = filtro is null
                ? comando.Should().NotContain("-type ")
                : comando.Should().Contain(filtro);
            _ = descripcion.Should().Contain("'directory'").And.Contain("sin distinguir mayúsculas").And.Contain("Por defecto (o vacío), 'file'").And.Contain("Otro valor").And.Contain("da error");
        }

        // Un NUL no puede viajar en un argumento ni sobrevive a '$(... | base64 -d)' (bash lo descarta): la ruta llegaría
        // sin él, a otro archivo. Se rechaza antes de enviar nada.
        [Fact]
        public async Task Herramientas_ValorConNul_LanzanSinEnviarNada()
        {
            // Arrange
            Func<Task>[] llamadas =
            [
                () => _herramientas.LeerArchivoAsync("servidor", "a\0b"),
                () => _herramientas.ConsultarMetadatosAsync("servidor", "a\0b"),
                () => _herramientas.BuscarAsync("servidor", "patron", "a\0b"),
                () => _herramientas.BuscarAsync("servidor", "pa\0tron"),
                () => _herramientas.BuscarAsync("servidor", "patron", extension: "t\0xt"),
                () => _herramientas.BuscarPorNombreAsync("servidor", "*.txt", "a\0b"),
                () => _herramientas.BuscarPorNombreAsync("servidor", "*\0.txt"),
                () => _herramientas.ListarDirectoriosAsync("servidor", "a\0b"),
                () => _herramientas.EscribirArchivoAsync("servidor", "a\0b", "c", overwrite: true),
                () => _herramientas.EscribirArchivoAsync("servidor", "a\0b", "c", append: true),
                () => _herramientas.ReemplazarContenidoArchivoAsync("servidor", "a\0b", "linea 3", "otra"),
                () => _herramientas.FijarDirectorioAsync("servidor", "a\0b"),
            ];

            // Act & Assert
            foreach (Func<Task> llamada in llamadas)
                _ = (await FluentActions.Awaiting(llamada).Should().ThrowAsync<NativeSshException>()).Which.Message.Should().Contain("NUL");
            _ = _fabrica.Ultima.Should().BeNull();
        }

        // Un sustituto UTF-16 suelto no tiene representación en UTF-8: codificarlo lo cambiaría por U+FFFD y la ruta, el
        // patrón o el contenido llegarían cambiados. Se rechaza antes de enviar nada.
        [Fact]
        public async Task Herramientas_ValorConSustitutoUtf16Suelto_LanzanSinEnviarNada()
        {
            // Arrange
            const string Suelto = "a\ud800b";
            Func<Task>[] llamadas =
            [
                () => _herramientas.LeerArchivoAsync("servidor", Suelto),
                () => _herramientas.ConsultarMetadatosAsync("servidor", Suelto),
                () => _herramientas.BuscarAsync("servidor", Suelto),
                () => _herramientas.BuscarAsync("servidor", "patron", Suelto),
                () => _herramientas.BuscarAsync("servidor", "patron", extension: Suelto),
                () => _herramientas.BuscarPorNombreAsync("servidor", Suelto),
                () => _herramientas.ListarDirectoriosAsync("servidor", Suelto),
                () => _herramientas.EscribirArchivoAsync("servidor", Suelto, "c", overwrite: true),
                () => _herramientas.EscribirArchivoAsync("servidor", "a.txt", Suelto, overwrite: true),
                () => _herramientas.ReemplazarContenidoArchivoAsync("servidor", Suelto, "linea 3", "otra"),
                () => _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_prueba.txt", Suelto, "otra"),
                () => _herramientas.ReemplazarContenidoArchivoAsync("servidor", "archivo_prueba.txt", "linea 3", Suelto),
                () => _herramientas.FijarDirectorioAsync("servidor", Suelto),
            ];

            // Act & Assert
            foreach (Func<Task> llamada in llamadas)
                _ = (await FluentActions.Awaiting(llamada).Should().ThrowAsync<NativeSshException>()).Which.Message.Should().Contain("UTF-16");
            _ = _fabrica.Ultima.Should().BeNull();
        }

        [Theory]
        [InlineData("pm2 list | grep online")]
        [InlineData("systemctl status mi-app | grep Active")]
        [InlineData("npm test |& grep FAILED")]
        [InlineData("cat app.log |grep error")]
        [InlineData("pm2 list | egrep online")]
        [InlineData("cat app.log | fgrep error")]
        public async Task Exec_ConTuberiasGrep_NoGeneraAviso(string comando)
        {
            // Act
            ResultadoComando resultado = await _herramientas.EjecutarAsync("servidor", comando).ComoComando();

            // Assert
            _ = resultado.Aviso.Should().BeNull();
        }

        [Theory]
        [InlineData("grep -rn 'list_team_names' src/")]
        [InlineData("rgrep patron .")]
        [InlineData("egrep -rn patron src/")]
        [InlineData("fgrep 'texto' archivo.txt")]
        public async Task Exec_ConGrepDisco_GeneraAvisoSearch(string comando)
        {
            // Act
            ResultadoComando resultado = await _herramientas.EjecutarAsync("servidor", comando).ComoComando();

            // Assert
            _ = resultado.Aviso.Should().NotBeNull();
            _ = resultado.Aviso.Should().Contain("ssh_search");
        }

        [Theory]
        [InlineData("false || grep 'error' app.log")]
        [InlineData("false ||grep 'error' app.log")]
        [InlineData("false || egrep 'error' app.log")]
        [InlineData("false ||egrep 'error' app.log")]
        public async Task Exec_ConOrLogicoGrep_GeneraAvisoSearch(string comando)
        {
            // Act
            ResultadoComando resultado = await _herramientas.EjecutarAsync("servidor", comando).ComoComando();

            // Assert
            _ = resultado.Aviso.Should().NotBeNull();
            _ = resultado.Aviso.Should().Contain("ssh_search");
        }

        [Fact]
        public async Task Exec_ConFindDisco_GeneraAvisoFind()
        {
            // Act
            ResultadoComando resultado = await _herramientas.EjecutarAsync("servidor", "find . -name '*.py'").ComoComando();

            // Assert
            _ = resultado.Aviso.Should().NotBeNull();
            _ = resultado.Aviso.Should().Contain("ssh_find");
        }

        [Fact]
        public async Task Exec_ConCdEncadenado_GeneraAvisoSetCwd()
        {
            // Act
            ResultadoComando resultado = await _herramientas.EjecutarAsync("servidor", "cd /root/DiscordBots && npm test").ComoComando();

            // Assert
            _ = resultado.Aviso.Should().NotBeNull();
            _ = resultado.Aviso.Should().Contain("ssh_set_cwd");
        }

        [Fact]
        public async Task Exec_ConCombinacionCdYGrep_GeneraAvisoCombinado()
        {
            // Act
            ResultadoComando resultado = await _herramientas.EjecutarAsync("servidor", "cd /root/DiscordBots && grep -rn 'algo' src/").ComoComando();

            // Assert
            _ = resultado.Aviso.Should().NotBeNull();
            _ = resultado.Aviso.Should().Contain("ssh_set_cwd");
            _ = resultado.Aviso.Should().Contain("ssh_search");
        }
    }
}
