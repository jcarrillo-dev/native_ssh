using System.Text.RegularExpressions;

namespace NativeSsh.Protocolo
{
    // Reconoce un servidor Windows cuyo shell de login no es bash (cmd o PowerShell) por lo que responde, sin depender del
    // idioma de Windows (WS1). Windows como destino no está soportado: lo que se busca es fallar en seguida y decir por qué,
    // en lugar de esperar el plazo de inicialización (15 s) y culpar al perfil. Señales medidas con Windows Server 2025 y
    // OpenSSH for Windows 9.5 (DefaultShell cmd, PowerShell 5.1 y Git Bash):
    // - la versión que anuncia el servidor al conectar ("SSH-2.0-OpenSSH_for_Windows_9.5"), en todos los casos;
    // - en el terminal, lo primero que llega es el prefijo de ConPTY (PrefijoConPty, a los ~0,25 s de abrir el canal), también
    //   con Git Bash: dice que es Windows, no que falte bash;
    // - con cmd y PowerShell, en la petición 'shell', su cabecera ("Microsoft Windows [Version …]", "Windows PowerShell") a
    //   los ~0,3–0,45 s y su indicador con la ruta de Windows ("C:\Users\nombre>"); ninguno de los dos ejecuta lo enviado,
    //   porque bajo ConPTY un '\n' no es Intro (lo muestran como eco, sin ejecutarlo), así que el marcador no llega nunca.
    //   Git Bash pone su propio título y su indicador ("usuario@EQUIPO UCRT64 ~ $") y responde al marcador;
    // - en un canal exec, el shell de login responde con su error y CRLF (cmd: «3>& was unexpected at this time.», «The
    //   command line is too long.»; PowerShell: un ParserError) sin que el programa de ConstructorComandoExec llegue a
    //   escribir su marca de inicio.
    // Los textos de los errores sí dependen del idioma: no se usan para decidir, solo se citan en el mensaje.
    public static partial class RespuestaWindows
    {
        private const int MaxCaracteresEvidencia = 120;

        // La versión del servidor de OpenSSH for Windows (el port de Microsoft, el de Windows Server y Windows 10/11).
        public const string MarcaVersionServidor = "OpenSSH_for_Windows";

        public static bool IsVersionWindows(string? versionServidor) => versionServidor?.Contains(MarcaVersionServidor, StringComparison.Ordinal) == true;

        // Modo de entrada win32 y avisos de foco: lo que envía ConPTY al empezar.
        private static ReadOnlySpan<byte> PrefijoConPty => "\e[?9001h\e[?1004h"u8;

        public static bool HasPrefijoConPty(ReadOnlySpan<byte> salida) => salida.IndexOf(PrefijoConPty) >= 0;

        // Cabeceras de cmd y PowerShell (nombres de producto, que no se traducen) o un indicador con ruta de Windows.
        [GeneratedRegex(@"Microsoft Windows \[[^\]\r\n]*\]|Windows PowerShell|PowerShell \d+\.\d+(?:\.\d+)?|(?:PS )?[A-Za-z]:\\[^\x00-\x1f>]*>", RegexOptions.CultureInvariant)]
        private static partial Regex PatronShellWindows();

        // Secuencias de escape del terminal: OSC (título de la ventana), CSI (cursor, colores) y las de dos caracteres.
        [GeneratedRegex(@"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)?|\x1b\[[0-?]*[ -/]*[@-~]|\x1b[@-Z\\-_]", RegexOptions.CultureInvariant)]
        private static partial Regex PatronSecuenciaEscape();

        // Lo que delata a cmd o a PowerShell en lo que escribió el terminal (sin sus secuencias de escape), acotado; null si
        // no hay nada.
        public static string? BuscarShell(string salida)
        {
            Match coincidencia = PatronShellWindows().Match(PatronSecuenciaEscape().Replace(salida, ""));
            return coincidencia.Success ? Acotar(coincidencia.Value) : null;
        }

        // Respuesta de un canal exec del shell de login de Windows: nada en stdout, el error en stderr y líneas con CRLF (los
        // errores de bash, también los del de Git Bash, terminan en LF). Solo se llama si el programa no escribió su marca.
        public static bool IsRespuestaExec(int? codigoSalida, ReadOnlySpan<byte> salida, ReadOnlySpan<byte> errores) =>
            codigoSalida is not (null or 0) && salida.IsEmpty && errores.IndexOf("\r\n"u8) >= 0;

        // Lo que se cita de la respuesta de un canal exec: de PowerShell, la línea de su identificador de error (no se traduce;
        // la primera solo dice la posición: «At line:1 char:3288»); si no, la primera línea no vacía. Sin caracteres de
        // control y acotada.
        public static string LineaRespuesta(string errores)
        {
            string? primera = null;
            foreach (string linea in errores.Split('\n'))
            {
                string limpia = RespuestaSinBash.Limpiar(linea);
                if (limpia.Contains("FullyQualifiedErrorId", StringComparison.Ordinal))
                    return Acotar(limpia.TrimStart('+', ' '));
                if (primera is null && limpia.Length > 0)
                    primera = limpia;
            }

            return Acotar(primera ?? "");
        }

        private static string Acotar(string texto) => texto.Length <= MaxCaracteresEvidencia ? texto : string.Concat(texto.AsSpan(0, MaxCaracteresEvidencia), "…");

        // Texto fijo de los mensajes, para reconocerlos donde solo llega el texto.
        public const string MarcaMensaje = "Windows como destino no está soportado";

        public static bool IsMensajeWindows(string? mensaje) => mensaje?.Contains(MarcaMensaje, StringComparison.Ordinal) == true;

        private const string Final = $"native_ssh necesita bash y {MarcaMensaje}. No se ejecutó nada.";

        // Shell PTY (petición 'shell') en el que se vio cmd o PowerShell.
        public static string DescribirShell(string destino, string evidencia) =>
            $"El shell de login del servidor {destino} es cmd/PowerShell (Windows; respondió: «{evidencia}»); {Final}";

        // Canal exec que contestó el shell de login de Windows sin arrancar bash.
        public static string DescribirExec(string destino, string linea) =>
            $"El shell de login del servidor {destino} es cmd/PowerShell (Windows): no arrancó el bash del comando de native_ssh y respondió «{linea}»; {Final}";

        // Comando exec que no se envía porque el sshd de Windows lo rechazaría sin cerrar el canal (SesionShell).
        public static string DescribirComandoLargo(string destino, int longitud) =>
            $"El servidor {destino} es Windows (OpenSSH for Windows) y el comando de native_ssh mide {longitud} caracteres: no cabe en la línea de órdenes de Windows (unos 32 700) y su sshd lo rechazaría sin cerrar el canal, así que no se envía; {Final}";

        // Shell PTY de un servidor Windows (ConPTY) sin señal de cmd ni de PowerShell: el shell sin perfil (que se decide con
        // el prefijo de ConPTY: ni con Git Bash arranca) o uno que no terminó la inicialización o se cerró en ella.
        public static string DescribirConPty(string destino, string situacion) =>
            $"El servidor {destino} es Windows (OpenSSH for Windows, terminal ConPTY) y {situacion}; {Final}";
    }
}
