using System.Globalization;
using System.Linq;
using FluentAssertions;
using NativeSsh.Protocolo;

namespace NativeSsh.Test.Utilidades
{
    // Prueba de propiedad de la integridad del comando exec (WS2): un servidor que trunca la línea de órdenes (el sshd de
    // Windows la corta hacia los 8191 caracteres y ejecuta el principio) no debe ejecutar nada del script. El comando se
    // corta en CADA posición y cada principio se ejecuta con '<shell> -c', como lo haría el shell de login del servidor.
    // El script deja un efecto observable en su primera línea (crea el archivo 'efecto'), así que si un corte llegara a
    // ejecutar parte del script, el archivo existiría.
    public static class CortesComandoExec
    {
        // Lo ejecuta bash ("$1" = comando completo, "$2" = archivo del efecto, "$3"... = shells, cada uno como "ruta:paso": se
        // prueban los cortes de longitud 1, 1+paso, 1+2·paso... y siempre el comando entero). Una línea por corte y shell:
        // "<shell> <longitud del corte> <código> <efecto 0/1> <stdout no vacío 0/1> <marca de inicio 0/1> <marca de truncado 0/1>".
        // Sin procesos por corte salvo el propio shell (mapfile y [[ ]] son de bash): son miles de cortes.
        public const string Conductor = """
            c=$1; f=$2; shift 2
            n=${#c}; o=$f.out; e=$f.err
            rm -f "$f"
            for item in "$@"; do
              sh=${item%:*}; paso=${item##*:}
              k=1
              while :; do
                [ "$k" -gt "$n" ] && k=$n
                "$sh" -c "${c:0:k}" </dev/null >"$o" 2>"$e"; rc=$?
                ef=0; if [ -e "$f" ]; then ef=1; rm -f "$f"; fi
                so=0; [ -s "$o" ] && so=1
                mapfile -t lineas <"$e"; err="${lineas[*]}"
                mi=0; [[ $err == *__NS_EXEC__* ]] && mi=1
                mt=0; [[ $err == *__NS_TRUNCADO__* ]] && mt=1
                printf '%s %d %d %d %d %d %d\n' "${sh##*/}" "$k" "$rc" "$ef" "$so" "$mi" "$mt"
                [ "$k" -eq "$n" ] && break
                k=$((k + paso))
              done
            done
            rm -f "$o" "$e"
            """;

        // Un shell de login y cada cuántas posiciones se corta (1 = todas; un paso primo con 4 recorre igualmente todos los
        // restos de las posiciones dentro del base64).
        public sealed record ShellCortes(string Ruta, int Paso)
        {
            public string Argumento => $"{Ruta}:{Paso}";

            public string Nombre => Path.GetFileName(Ruta);
        }

        // El script de la prueba: la primera línea crea 'efecto'; las demás alargan el base64 (cortes dentro de él en todas
        // las posiciones, también en múltiplos de 4, donde lo que queda sigue siendo base64 válido).
        public static string Script(string efecto) =>
            $"echo efecto > '{efecto}'\n" + string.Join("\n", Enumerable.Range(0, 12).Select(i => $"echo relleno {i} para alargar el base64 del script"));

        public sealed record Corte(string Shell, int Longitud, int Codigo, bool IsEfecto, bool HasSalida, bool HasMarcaInicio, bool HasMarcaTruncado);

        public static List<Corte> Leer(string salida) =>
            [.. salida.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static linea => linea.Split(' ')).Select(static c => new Corte(
                c[0], int.Parse(c[1], CultureInfo.InvariantCulture), int.Parse(c[2], CultureInfo.InvariantCulture), c[3] == "1", c[4] == "1", c[5] == "1", c[6] == "1"))];

        // Ningún corte ejecuta nada del script: o el programa llega a comprobar sus argumentos y sale con
        // CodigoComandoTruncado y su marca, o el shell no llega a arrancar bash (comillas sin cerrar, opción incompleta...) y
        // no escribe nada en stdout. Ninguno escribe la marca de inicio, tampoco en un error que cite la línea cortada. Solo el comando entero (o el que pierde parte de la redirección final, que ya tiene todos
        // los argumentos) ejecuta el script, y entero: con la marca de inicio.
        public static void Comprobar(string comando, IReadOnlyCollection<Corte> cortes, IReadOnlyCollection<ShellCortes> shells)
        {
            int finControl = comando.LastIndexOf(' ' + ConstructorComandoExec.ArgumentoControl, StringComparison.Ordinal) + 1 + ConstructorComandoExec.ArgumentoControl.Length;
            _ = finControl.Should().BeGreaterThan(ConstructorComandoExec.ArgumentoControl.Length, "el comando debe llevar el argumento de control");
            foreach (ShellCortes shell in shells)
                _ = cortes.Count(c => c.Shell == shell.Nombre).Should().Be(((comando.Length - 1) / shell.Paso) + 1 + ((comando.Length - 1) % shell.Paso == 0 ? 0 : 1), shell.Nombre);
            foreach (Corte corte in cortes)
            {
                string contexto = $"{corte.Shell} -c con los {corte.Longitud} primeros caracteres: «…{comando[Math.Max(0, corte.Longitud - 30)..corte.Longitud]}»";
                if (corte.Longitud == comando.Length)
                {
                    _ = corte.IsEfecto.Should().BeTrue(contexto);
                    _ = corte.Codigo.Should().Be(0, contexto);
                    _ = corte.HasMarcaInicio.Should().BeTrue(contexto);
                    continue;
                }

                if (corte.Longitud >= finControl)
                {
                    _ = (!corte.IsEfecto || corte.HasMarcaInicio).Should().BeTrue(contexto);
                    continue;
                }

                _ = corte.IsEfecto.Should().BeFalse(contexto);
                _ = corte.HasMarcaInicio.Should().BeFalse(contexto);
                _ = corte.HasSalida.Should().BeFalse(contexto);
            }

            // Los cortes que dejan el programa entero (tras su comilla de cierre) llegan a comprobar los argumentos.
            int finPrograma = comando.IndexOf("' ns ", StringComparison.Ordinal) + 1;
            _ = cortes.Where(c => c.Longitud >= finPrograma && c.Longitud < finControl).Should().OnlyContain(c => c.HasMarcaTruncado && c.Codigo == ConstructorComandoExec.CodigoComandoTruncado);
        }
    }
}
