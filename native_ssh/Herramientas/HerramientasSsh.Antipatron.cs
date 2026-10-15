using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;

namespace NativeSsh.Herramientas
{
    // Avisos de antipatrón de ssh_exec (C10): el comando hace con bash lo que ya hace una herramienta especializada (buscar,
    // localizar, listar, leer, consultar metadatos, editar o escribir archivos). Sin falsos positivos: solo se avisa de lo
    // que con seguridad es ese uso; las tuberías (journalctl | tail, cmd | grep), tail -f, las redirecciones a /dev/null
    // y 2>&1, --help y las transformaciones en el servidor que no devuelven el contenido (sed -n 's/…/\1/p', que saca solo
    // los nombres de las variables de un .env sin sus valores) no avisan.
    // El comando se parte en segmentos (órdenes simples separadas por ;, &, &&, ||, |, |& y saltos de línea) fuera de
    // comillas, de $(…), de `…` y de los comentarios; el cuerpo de un heredoc no se analiza (no son órdenes). De cada
    // segmento se miran el programa (tras sudo, env, time, nice, nohup, command, exec, stdbuf, timeout y las asignaciones), sus
    // argumentos y sus redirecciones, y los separadores de alrededor: una lectura cuya salida va a otra orden (|) o a un
    // archivo no devuelve el contenido al LLM.
    public sealed partial class HerramientasSsh
    {
        private static string AvisoBusquedaRg(string programa) => $"Sugerencia de rendimiento: Has ejecutado '{programa}' sobre el sistema de archivos vía ssh_exec. Utiliza la herramienta especializada 'ssh_search' (usa rg en el servidor si lo tiene) para excluir automáticamente binarios, directorios pesados (.git, node_modules, .venv) y acotar las coincidencias.";
        private static string AvisoLecturaArchivo(string programa) => $"Sugerencia de rendimiento: Has leído un archivo con '{programa}' vía ssh_exec. Utiliza la herramienta especializada 'ssh_read_file' (rangos con lineaInicio/lineaFin y las últimas N líneas con lineaInicio=-N): devuelve el contenido tal cual, sin pasar por el terminal, y avisa si no es UTF-8.";
        private static string AvisoListado(string programa) => $"Sugerencia de rendimiento: Has ejecutado '{programa}' vía ssh_exec. Utiliza la herramienta especializada 'ssh_list_dir' (con incluirArchivos=true sustituye a 'ls' y 'tree', con poda automática de dependencias) y 'ssh_stat' para los metadatos de una ruta concreta.";
        private const string AvisoMetadatos = "Sugerencia de rendimiento: Has ejecutado 'stat' vía ssh_exec. Utiliza la herramienta especializada 'ssh_stat', que devuelve tipo, tamaño, permisos, propietario, grupo y fecha en formato estructurado, igual en cualquier servidor e idioma.";
        private static string AvisoEdicionEnSitio(string orden) => $"Sugerencia de seguridad: Has editado un archivo en el sitio con '{orden}' vía ssh_exec. Utiliza la herramienta especializada 'ssh_replace_file_content' (varias ediciones en una llamada con 'ediciones', sudo=true para archivos de root): escribe de forma atómica o con copia de seguridad, con candado y comprobando que nadie cambió el archivo entretanto.";
        private static string AvisoEscrituraArchivo(string orden) => $"Sugerencia de seguridad: Has escrito un archivo con '{orden}' vía ssh_exec. Utiliza la herramienta especializada 'ssh_write_file' (contenido sin problemas de comillas ni de expansión de variables, escritura atómica con candado, append=true para anexar y sudo=true para archivos de root).";

        [GeneratedRegex(@"(?:^|\s)(?:--recursive|-[a-zA-Z]*[rR][a-zA-Z]*)\b", RegexOptions.CultureInvariant)]
        private static partial Regex RegexFlagsRecursivosGrep();

        // Script de sed que solo imprime líneas por su número ('5p', '10,20p', '$p', '1~2p', '3,+4p'; varios con ';'): es una
        // lectura. Cualquier otro (s///, y///, direcciones con regex...) es una transformación y no avisa.
        [GeneratedRegex(@"^\s*(?:\d+|\$)(?:\s*[,~]\s*(?:\d+|\$|\+\d+|~\d+))?\s*p\s*$", RegexOptions.CultureInvariant)]
        private static partial Regex RegexImpresionPorLineas();

        internal static string? DetectarAvisoAntipatron(string comando)
        {
            if (string.IsNullOrWhiteSpace(comando))
                return null;

            using PooledListRef<string> avisos = new();
            OrdenSimple? anterior = null;
            foreach (OrdenSimple orden in SepararOrdenes(comando))
            {
                // El productor de una tubería (echo … | tee) es el segmento anterior si este lee de él.
                OrdenSimple? productor = orden.IsTuberiaDeEntrada ? anterior : null;
                // Rama de respaldo (RC6 a): lo que sigue a '||', y lo que va en tubería detrás, solo se ejecuta si falló lo
                // anterior, y desde aquí no se sabe: el aviso se condiciona en vez de afirmar que se ejecutó. Un '&&' o un ';'
                // posterior ya no es de la rama ('a || b && cat x' ejecuta cat si a o b terminan bien).
                orden.IsRespaldo = orden.SeparadorPrevio == "||" || (orden.IsTuberiaDeEntrada && anterior?.IsRespaldo == true);
                string? aviso = AvisoDeOrden(orden, productor);
                if (aviso is not null && orden.IsRespaldo)
                    aviso = PrefijoRespaldo + aviso;
                if (aviso is not null && !avisos.Span.Contains(aviso))
                    avisos.Add(aviso);
                anterior = orden;
            }

            if (avisos.Tamaño == 0)
                return null;

            // Mismo orden que antes de C10 (grep, find, cd) y después los demás, en el orden en que aparecen.
            using PooledListRef<string> ordenados = new();
            ReadOnlySpan<string> primeros = [AvisoGrepDisco, AvisoFindDisco, AvisoCdEncadenado];
            foreach (string primero in primeros)
                if (avisos.Remove(primero))
                    ordenados.Add(primero);
            ordenados.AddRange(avisos.Span);
            return string.Join(" ", ordenados.Span);
        }

        private const string PrefijoRespaldo = "Solo si llegó a ejecutarse la rama de respaldo tras '||' (desde aquí no se sabe): ";

        private static string? AvisoDeOrden(OrdenSimple orden, OrdenSimple? productor)
        {
            if (orden.Programa is null || orden.HasAyuda)
                return null;

            // La salida llega al LLM: no va a otra orden ni a un archivo (2>&1, 2>/dev/null y >&2 no la desvían).
            bool isSalidaAlLlm = !orden.IsTuberiaDeSalida && orden.DestinoSalida is null;
            string programa = orden.Programa;
            return programa switch
            {
                "cd" => orden.SeparadorSiguiente == "&&" ? AvisoCdEncadenado : null,
                "find" => orden.IsTuberiaDeEntrada || !IsFindQueHaceSshFind(orden) ? null : AvisoFindDisco,
                "grep" or "egrep" or "fgrep" or "rgrep" => programa == "rgrep" || !orden.IsTuberiaDeEntrada || RegexFlagsRecursivosGrep().IsMatch(orden.TextoArgumentos) ? AvisoGrepDisco : null,
                "rg" or "ag" or "ack" or "ack-grep" => orden.IsTuberiaDeEntrada ? null : AvisoBusquedaRg(programa),
                "ls" or "tree" or "exa" or "eza" => isSalidaAlLlm ? AvisoListado(programa) : null,
                "stat" => isSalidaAlLlm && orden.Operandos.Length > 0 ? AvisoMetadatos : null,
                "cat" => AvisoCat(orden, isSalidaAlLlm),
                "head" or "tail" => isSalidaAlLlm && IsLecturaHeadTail(orden) ? AvisoLecturaArchivo(programa) : null,
                "less" or "more" or "most" => orden.Operandos.Length > 0 ? AvisoLecturaArchivo(programa) : null,
                "sed" => AvisoSed(orden, isSalidaAlLlm),
                "perl" => IsEdicionEnSitio(orden.Argumentos, PerlOpcionesConValor) ? AvisoEdicionEnSitio("perl -i") : null,
                "tee" => IsEscrituraTee(orden, productor) ? AvisoEscrituraArchivo("tee") : null,
                _ => null
            };
        }

        // RC6 (b): find solo avisa si lo que pide lo hace ssh_find: nombre (-name), tipo f o d, profundidad (-maxdepth,
        // -mindepth 1, que es la de ssh_find), sin salir del sistema de ficheros (-xdev, -mount), tamaño (-size, RC9),
        // imprimir y las opciones de enlaces (-L, -H, -P). Con cualquier otro predicado (fechas, dueño, permisos, -iname,
        // -path, -regex, -empty), acción (-exec, -delete, -printf, -ls) u operador (-o, -a, -not, !, paréntesis), no hay
        // herramienta que lo sustituya y no se avisa (el aviso disuadía de usar 'find -newer' o 'find -size' cuando no había alternativa).
        // El tamaño solo con archivos: ssh_find rechaza tamanoMin y tamanoMax con tipo='dir', así que -size con -type d no avisa.
        private static bool IsFindQueHaceSshFind(OrdenSimple orden)
        {
            string[] argumentos = orden.Argumentos;
            bool hasTamano = false;
            bool hasTipoDirectorio = false;
            for (int i = 0; i < argumentos.Length; i++)
            {
                string argumento = argumentos[i];
                string? valor = i + 1 < argumentos.Length ? argumentos[i + 1] : null;
                bool isAdmitido = argumento switch
                {
                    "-L" or "-H" or "-P" or "-xdev" or "-mount" or "-print" or "-print0" => true,
                    "-name" or "-maxdepth" or "-size" => valor is not null,
                    "-type" => valor is "f" or "d",
                    "-mindepth" => valor is "1",
                    _ => !argumento.StartsWith('-') && argumento is not ("(" or ")" or "!" or ",")
                };
                if (!isAdmitido)
                    return false;

                hasTamano |= argumento == "-size";
                hasTipoDirectorio |= argumento == "-type" && valor == "d";
                if (argumento is "-name" or "-maxdepth" or "-size" or "-type" or "-mindepth")
                    i++;
            }

            return !(hasTamano && hasTipoDirectorio);
        }

        // cat: con heredoc o herestring y la salida a un archivo es una escritura (cat > f <<EOF); con archivos y la salida al
        // LLM, una lectura. 'cat -' (o sin archivos) lee la entrada estándar: nada.
        private static string? AvisoCat(OrdenSimple orden, bool isSalidaAlLlm)
        {
            if (orden.HasDocumentoEnLinea)
                return orden.DestinoSalida is { } destino && !IsDestinoEspecial(destino) ? AvisoEscrituraArchivo("cat > archivo <<") : null;

            bool hasArchivo = Array.Exists(orden.Operandos, static o => o != "-");
            return isSalidaAlLlm && hasArchivo ? AvisoLecturaArchivo("cat") : null;
        }

        // head/tail con algún archivo y sin seguirlo (-f, -F, también tras el número en la forma antigua '-100f', --follow,
        // --retry): -n y -c llevan un valor ('-n 20', '-n20').
        private static bool IsLecturaHeadTail(OrdenSimple orden)
        {
            bool hasArchivo = false;
            for (int i = 0; i < orden.Argumentos.Length; i++)
            {
                string argumento = orden.Argumentos[i];
                bool isOpcionCorta = argumento.Length > 1 && argumento[0] == '-' && argumento[1] != '-';
                if (argumento.StartsWith("--follow", StringComparison.Ordinal) || argumento == "--retry" || (isOpcionCorta && argumento.AsSpan(1).IndexOfAny('f', 'F') >= 0))
                    return false;

                if (argumento is "-n" or "-c" or "--lines" or "--bytes")
                    i++;
                else if (!argumento.StartsWith('-') || argumento == "-")
                    hasArchivo |= argumento != "-";
            }

            return hasArchivo;
        }

        // sed: -i (o --in-place) es una edición en el sitio; -n con un script que solo imprime líneas por su número y algún
        // archivo, con la salida al LLM, es una lectura. Cualquier otro script (s///…) es una transformación: nada.
        private static string? AvisoSed(OrdenSimple orden, bool isSalidaAlLlm)
        {
            if (IsEdicionEnSitio(orden.Argumentos, 'e', 'f', 'l') || Array.Exists(orden.Argumentos, static a => a.StartsWith("--in-place", StringComparison.Ordinal)))
                return AvisoEdicionEnSitio("sed -i");

            if (!isSalidaAlLlm)
                return null;

            bool isSilencioso = false;
            using PooledListRef<string> scripts = new();
            bool hasArchivos = false;
            for (int i = 0; i < orden.Argumentos.Length; i++)
            {
                string argumento = orden.Argumentos[i];
                if (argumento is "-n" or "--quiet" or "--silent")
                    isSilencioso = true;
                else if (argumento is "-e" or "--expression" && i + 1 < orden.Argumentos.Length)
                    scripts.Add(orden.Argumentos[++i]);
                else if (argumento is "-f" or "--file" or "-l" or "--line-length")
                    return null;
                else if (argumento.Length > 1 && argumento[0] == '-' && argumento[1] != '-')
                {
                    // Opciones cortas juntas (-nE): con -e o -f dentro, el resto es su valor; no se analiza.
                    if (argumento.AsSpan(1).IndexOfAny('e', 'f') >= 0)
                        return null;
                    isSilencioso |= argumento.Contains('n', StringComparison.Ordinal);
                }
                else if (!argumento.StartsWith('-'))
                    if (scripts.Tamaño == 0)
                        scripts.Add(argumento);
                    else
                        hasArchivos = true;
            }

            bool isSoloImpresionPorLineas = scripts.Tamaño > 0 && IsSoloImpresionPorLineas(scripts.Span);
            return isSilencioso && isSoloImpresionPorLineas && hasArchivos ? AvisoLecturaArchivo("sed -n") : null;
        }

        // Cada script de sed (con sus órdenes separadas por ';') solo imprime líneas por su número.
        private static bool IsSoloImpresionPorLineas(ReadOnlySpan<string> scripts)
        {
            foreach (string script in scripts)
                foreach (string parte in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    if (!RegexImpresionPorLineas().IsMatch(parte))
                        return false;

            return true;
        }

        // Opciones de perl con el valor pegado detrás: el código (-e, -E), un módulo (-M, -m: '-Mstrict' no es -i), un
        // directorio (-I, -x) o el patrón de -F.
        private static readonly char[] PerlOpcionesConValor = ['e', 'E', 'M', 'm', 'I', 'x', 'F'];

        // Opción -i en un grupo de opciones cortas (-i, -pi, -i.bak, -Ei): al llegar a una de 'conValor' (su valor va
        // detrás, p. ej. el código de perl -e) o a un carácter que no es una letra (el sufijo de -i.bak) se deja de mirar.
        private static bool IsEdicionEnSitio(string[] argumentos, params char[] conValor)
        {
            foreach (string argumento in argumentos)
            {
                if (argumento.Length < 2 || argumento[0] != '-' || argumento[1] == '-')
                    continue;

                for (int i = 1; i < argumento.Length; i++)
                {
                    char c = argumento[i];
                    if (c == 'i')
                        return true;
                    if (!char.IsAsciiLetter(c) || Array.IndexOf(conValor, c) >= 0)
                        break;
                }
            }

            return false;
        }

        // tee escribe en un archivo lo que le llega de echo, printf o cat (contenido fijo): es una escritura. Con otro
        // productor (npm run build | tee build.log) es un registro de la salida: nada. /dev/null, /dev/std*, /dev/fd/* y los
        // archivos de /proc, /sys y /dev no son archivos que ssh_write_file pueda o deba escribir.
        private static bool IsEscrituraTee(OrdenSimple orden, OrdenSimple? productor) =>
            productor?.Programa is "echo" or "printf" or "cat" && Array.Exists(orden.Operandos, static o => !IsDestinoEspecial(o));

        private static bool IsDestinoEspecial(string ruta) =>
            ruta.StartsWith("/dev/", StringComparison.Ordinal) || ruta.StartsWith("/proc/", StringComparison.Ordinal) || ruta.StartsWith("/sys/", StringComparison.Ordinal);

        // Una orden simple del comando: el programa (sin prefijos), sus argumentos sin las redirecciones, los operandos
        // (argumentos que no son opciones), adónde va su salida estándar si se redirige y los separadores de alrededor.
        private sealed class OrdenSimple
        {
            public string? Programa { get; set; }
            public string[] Argumentos { get; set; } = [];
            public string[] Operandos { get; set; } = [];
            public string TextoArgumentos { get; set; } = string.Empty;
            public string? DestinoSalida { get; set; }
            public bool HasDocumentoEnLinea { get; set; }
            public bool HasAyuda { get; set; }

            // En una rama de respaldo ('a || cat log'): ver DetectarAvisoAntipatron.
            public bool IsRespaldo { get; set; }
            public string SeparadorPrevio { get; set; } = string.Empty;
            public string SeparadorSiguiente { get; set; } = string.Empty;
            public bool IsTuberiaDeEntrada => SeparadorPrevio is "|" or "|&";
            public bool IsTuberiaDeSalida => SeparadorSiguiente is "|" or "|&";
        }

        // Palabra de una orden: su valor sin comillas y si es un operador de redirección (>, >>, 2>, &>, >&, <, <<, <<-, <<<…).
        private readonly record struct Palabra(string Texto, bool IsRedireccion);

        // Analizador léxico mínimo de bash para los avisos: comillas simples y dobles, barra invertida, $(…) y `…` opacos
        // (su contenido no cuenta como órdenes), comentarios, continuación de línea, redirecciones y heredocs (su cuerpo se
        // salta hasta la línea del delimitador). No pretende ser bash: ante algo que no entiende, no avisa.
        // Las órdenes, las palabras y los heredocs pendientes van en List: las funciones locales CerrarPalabra y CerrarOrden
        // los capturan, y una lista rentada (ref struct) no se puede capturar.
        private static List<OrdenSimple> SepararOrdenes(string comando)
        {
            List<OrdenSimple> ordenes = [];
            List<Palabra> palabras = [];
            List<(string Delimitador, bool IsQuitarTabuladores)> heredocsPendientes = [];
            StringBuilder actual = new();
            bool hasPalabra = false;
            bool isEsperandoDelimitador = false;
            bool isDelimitadorSinTabuladores = false;
            string separadorPrevio = string.Empty;
            int i = 0;
            int longitud = comando.Length;

            void CerrarPalabra()
            {
                if (!hasPalabra)
                    return;

                string texto = actual.ToString();
                if (isEsperandoDelimitador)
                {
                    heredocsPendientes.Add((texto, isDelimitadorSinTabuladores));
                    isEsperandoDelimitador = false;
                }

                palabras.Add(new Palabra(texto, false));
                _ = actual.Clear();
                hasPalabra = false;
            }

            void CerrarOrden(string separador)
            {
                CerrarPalabra();
                if (palabras.Count > 0)
                    ordenes.Add(ConstruirOrden(CollectionsMarshal.AsSpan(palabras), separadorPrevio, separador));
                palabras.Clear();
                separadorPrevio = separador;
            }

            while (i < longitud)
            {
                char c = comando[i];
                char siguiente = i + 1 < longitud ? comando[i + 1] : '\0';

                if (c == '\\')
                {
                    // Continuación de línea: espacio; si no, el carácter escapado es literal.
                    if (siguiente == '\n')
                    {
                        CerrarPalabra();
                        i += 2;
                        continue;
                    }

                    if (i + 1 < longitud)
                        _ = actual.Append(siguiente);
                    hasPalabra = true;
                    i += 2;
                    continue;
                }

                if (c == '\'')
                {
                    int cierre = comando.IndexOf('\'', i + 1);
                    int fin = cierre < 0 ? longitud : cierre;
                    _ = actual.Append(comando, i + 1, fin - i - 1);
                    hasPalabra = true;
                    i = fin + 1;
                    continue;
                }

                if (c == '"')
                {
                    i++;
                    while (i < longitud && comando[i] != '"')
                    {
                        if (comando[i] == '\\' && i + 1 < longitud)
                            i++;
                        _ = actual.Append(comando[i]);
                        i++;
                    }

                    hasPalabra = true;
                    i++;
                    continue;
                }

                if ((c is '$' or '<' or '>' && siguiente == '(') || c == '`')
                {
                    // Sustitución de órdenes: forma parte de la palabra, sin analizar su contenido.
                    // <(…) y >(…) (sustitución de procesos) también: su contenido no son órdenes de este comando, y la
                    // palabra es la salida o la entrada de otra orden, no un archivo (ConstruirOrden la descarta).
                    i = SaltarSustitucion(comando, i);
                    _ = actual.Append(c is '<' or '>' ? MarcaSustitucionProcesos : "$");
                    hasPalabra = true;
                    continue;
                }

                if (c == '#' && !hasPalabra)
                {
                    while (i < longitud && comando[i] != '\n')
                        i++;
                    continue;
                }

                if (c is ' ' or '\t')
                {
                    CerrarPalabra();
                    i++;
                    continue;
                }

                if (c is '\n' or '\r')
                {
                    CerrarOrden("\n");
                    i += c == '\r' && siguiente == '\n' ? 2 : 1;
                    if (heredocsPendientes.Count > 0)
                    {
                        i = SaltarCuerpos(comando, i, CollectionsMarshal.AsSpan(heredocsPendientes));
                        heredocsPendientes.Clear();
                    }

                    continue;
                }

                if (c == '&' && siguiente == '&')
                {
                    CerrarOrden("&&");
                    i += 2;
                    continue;
                }

                if (c == '|')
                {
                    string separador = siguiente == '|' ? "||" : siguiente == '&' ? "|&" : "|";
                    CerrarOrden(separador);
                    i += separador.Length;
                    continue;
                }

                if (c == ';')
                {
                    CerrarOrden(";");
                    i += siguiente == ';' ? 2 : 1;
                    continue;
                }

                if (c is '>' or '<' || (c == '&' && siguiente == '>'))
                {
                    // Un número justo delante es el descriptor (2>): forma parte del operador.
                    string descriptor = hasPalabra && IsSoloDigitos(actual) ? actual.ToString() : string.Empty;
                    if (descriptor.Length > 0)
                    {
                        _ = actual.Clear();
                        hasPalabra = false;
                    }
                    else
                        CerrarPalabra();

                    int inicio = i;
                    if (c == '&')
                        i++;
                    char operador = comando[i];
                    i++;
                    while (i < longitud && (comando[i] == operador || comando[i] is '&' or '|' or '-') && i - inicio < 3)
                        i++;
                    string operadorTexto = comando[inicio..i];
                    palabras.Add(new Palabra(descriptor + operadorTexto, true));
                    if (operadorTexto is "<<" or "<<-")
                    {
                        isEsperandoDelimitador = true;
                        isDelimitadorSinTabuladores = operadorTexto == "<<-";
                    }

                    continue;
                }

                if (c == '&')
                {
                    CerrarOrden("&");
                    i++;
                    continue;
                }

                bool isFinDePalabra = siguiente is '\0' or ' ' or '\t' or '\n' or '\r' or ';' or '&' or '|' or ')';
                bool isAgrupacion = !hasPalabra && (c is '(' or ')' || (c == '{' && siguiente is ' ' or '\t' or '\n') || (c == '}' && isFinDePalabra));
                if (isAgrupacion)
                {
                    // Agrupación: separa órdenes como un ';' sin cambiar el separador previo de la primera.
                    CerrarPalabra();
                    if (palabras.Count > 0)
                        CerrarOrden(";");
                    i++;
                    continue;
                }

                _ = actual.Append(c);
                hasPalabra = true;
                i++;
            }

            CerrarOrden(string.Empty);
            return ordenes;
        }

        private static bool IsSoloDigitos(StringBuilder texto)
        {
            for (int i = 0; i < texto.Length; i++)
                if (!char.IsAsciiDigit(texto[i]))
                    return false;

            return texto.Length > 0;
        }

        // Fin de una sustitución $(…) o `…` que empieza en 'inicio' (anidadas y con comillas dentro); sin cierre, el final.
        private static int SaltarSustitucion(string comando, int inicio)
        {
            if (comando[inicio] == '`')
            {
                int i = inicio + 1;
                while (i < comando.Length && comando[i] != '`')
                    i += comando[i] == '\\' ? 2 : 1;
                return Math.Min(i + 1, comando.Length);
            }

            int profundidad = 0;
            int j = inicio + 1;
            while (j < comando.Length)
            {
                char c = comando[j];
                if (c == '\\')
                {
                    j += 2;
                    continue;
                }

                if (c is '\'' or '"')
                {
                    j = FinDeComillas(comando, j);
                    continue;
                }

                if (c == '(')
                    profundidad++;
                else if (c == ')' && --profundidad == 0)
                    return j + 1;
                j++;
            }

            return comando.Length;
        }

        // Posición tras las comillas (simples o dobles, con escapes en las dobles) que empiezan en 'inicio'.
        private static int FinDeComillas(string comando, int inicio)
        {
            char comilla = comando[inicio];
            int i = inicio + 1;
            while (i < comando.Length && comando[i] != comilla)
                i += comilla == '"' && comando[i] == '\\' ? 2 : 1;
            return Math.Min(i + 1, comando.Length);
        }

        // Salta los cuerpos de los heredocs de la línea anterior, en orden: cada uno hasta la línea igual a su delimitador
        // (con <<- se le quitan antes los tabuladores iniciales). Sin delimitador de cierre, hasta el final.
        private static int SaltarCuerpos(string comando, int inicio, ReadOnlySpan<(string Delimitador, bool IsQuitarTabuladores)> heredocs)
        {
            int i = inicio;
            foreach ((string delimitador, bool isQuitarTabuladores) in heredocs)
                while (i < comando.Length)
                {
                    int finLinea = comando.IndexOf('\n', i);
                    bool isUltimaLinea = finLinea < 0;
                    int fin = isUltimaLinea ? comando.Length : finLinea;
                    ReadOnlySpan<char> linea = comando.AsSpan(i, fin - i).TrimEnd('\r');
                    if (isQuitarTabuladores)
                        linea = linea.TrimStart('\t');
                    i = isUltimaLinea ? comando.Length : finLinea + 1;
                    if (linea.SequenceEqual(delimitador))
                        break;
                }

            return i;
        }

        // Lo que queda de una sustitución de procesos (<(…) o >(…)) en la palabra: un nombre de /dev/fd que da o recibe la
        // salida de otra orden, no un archivo que lea o escriba una herramienta.
        private const string MarcaSustitucionProcesos = "<()";

        // Prefijos que ejecutan otra orden (con sus opciones): la orden es la palabra que sigue. Las sustituciones de procesos
        // no cuentan como argumentos (cat <(…) no lee un archivo; tee >(…) no lo escribe).
        private static OrdenSimple ConstruirOrden(ReadOnlySpan<Palabra> palabras, string separadorPrevio, string separadorSiguiente)
        {
            OrdenSimple orden = new() { SeparadorPrevio = separadorPrevio, SeparadorSiguiente = separadorSiguiente };
            using PooledListRef<string> argumentos = new();
            using PooledListRef<string> operandos = new();
            StringBuilder texto = new();
            bool isEnPrefijo = true;
            string? prefijo = null;
            for (int i = 0; i < palabras.Length; i++)
            {
                Palabra palabra = palabras[i];
                if (palabra.IsRedireccion)
                {
                    string objetivo = i + 1 < palabras.Length && !palabras[i + 1].IsRedireccion ? palabras[++i].Texto : string.Empty;
                    AnotarRedireccion(orden, palabra.Texto, objetivo);
                    continue;
                }

                string valor = palabra.Texto;
                if (isEnPrefijo)
                {
                    // 'command -v ls' solo pregunta por la orden: no la ejecuta.
                    if (prefijo == "command" && valor is "-v" or "-V")
                        break;

                    if (prefijo is not null && valor.StartsWith('-'))
                    {
                        // Opciones del prefijo; las que llevan valor se lo saltan.
                        if ((prefijo == "sudo" && valor is "-u" or "-g" or "-C" or "-D" or "-p" or "-r" or "-t" or "-U") || (prefijo == "nice" && valor == "-n") || (prefijo == "timeout" && valor is "-s" or "-k") || (prefijo == "env" && valor is "-u" or "-C" or "-S"))
                            i++;
                        continue;
                    }

                    if (prefijo is "nice" or "timeout" && valor.Length > 0 && (char.IsAsciiDigit(valor[0]) || valor[0] == '+'))
                        continue;

                    if (IsAsignacion(valor))
                        continue;

                    string nombre = valor.Contains('/', StringComparison.Ordinal) ? valor[(valor.LastIndexOf('/') + 1)..] : valor;
                    if (nombre is "sudo" or "env" or "time" or "nice" or "nohup" or "command" or "exec" or "stdbuf" or "timeout" or "!")
                    {
                        prefijo = nombre;
                        continue;
                    }

                    orden.Programa = nombre;
                    isEnPrefijo = false;
                    continue;
                }

                if (valor == MarcaSustitucionProcesos)
                    continue;

                argumentos.Add(valor);
                if (valor is "--help" or "--version")
                    orden.HasAyuda = true;
                if (!valor.StartsWith('-') || valor == "-")
                    operandos.Add(valor);
                _ = texto.Append(' ').Append(valor);
            }

            orden.Argumentos = argumentos.Span.ToArray();
            orden.Operandos = operandos.Span.ToArray();
            orden.TextoArgumentos = texto.ToString();
            return orden;
        }

        private static bool IsAsignacion(string palabra)
        {
            int igual = palabra.IndexOf('=', StringComparison.Ordinal);
            if (igual <= 0)
                return false;

            for (int i = 0; i < igual; i++)
                if (!(char.IsAsciiLetterOrDigit(palabra[i]) || palabra[i] == '_') || (i == 0 && char.IsAsciiDigit(palabra[i])))
                    return false;

            return true;
        }

        // > y >> (sin descriptor o con 1), &> y &>> desvían la salida estándar a 'objetivo'; 2>, 2>&1, >&2 y las entradas no.
        // << y <<- son heredocs; <<< es un herestring: los dos dan contenido fijo a la orden.
        private static void AnotarRedireccion(OrdenSimple orden, string operador, string objetivo)
        {
            if (operador.StartsWith("<<", StringComparison.Ordinal))
            {
                orden.HasDocumentoEnLinea = true;
                return;
            }

            if (operador.StartsWith('<') || (operador.Contains(">&", StringComparison.Ordinal) && !operador.StartsWith('&')))
                return;

            bool isSalidaEstandar = operador[0] is '>' or '&' || operador.StartsWith("1>", StringComparison.Ordinal);
            if (isSalidaEstandar)
                orden.DestinoSalida = objetivo;
        }
    }
}
