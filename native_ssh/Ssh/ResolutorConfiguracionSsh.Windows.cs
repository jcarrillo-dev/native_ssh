using System.Diagnostics;
using System.Runtime.InteropServices;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;
using Microsoft.Win32.SafeHandles;

namespace NativeSsh.Ssh
{
    // Lo de Windows de 'ssh -G' (solo se compila para Windows: ver NativeSshSistema en Directory.Build.props).
    // El ssh.exe de OpenSSH para Windows (medido con 9.5p2) se queda bloqueado en la segunda escritura en su salida de
    // errores cuando esta es una tubería, sea cual sea su entrada: un 'ssh -G' que escribía algún mensaje (una configuración
    // con errores, un -F que no existe o, con la entrada en una tubería, «Pseudo-terminal will not be allocated because
    // stdin is not a terminal.») no terminaba y agotaba el plazo. Por eso sus mensajes van a un fichero propio de cada
    // ejecución (-E), que se lee como su salida de errores y se borra; y -T (no pedir terminal), para que el mensaje del
    // terminal ni siquiera se escriba. En los demás sistemas no hace falta. Lo que lanza un Match exec no escribe en ese
    // fichero, sino en la salida de errores de ssh (la tubería), y ssh lo ejecuta mientras lee la configuración, antes de
    // terminar con su error: en el error, la salida de errores va delante y los mensajes de ssh del fichero detrás, en el
    // orden en que ocurren en el caso normal (si ssh escribe algo antes del Match exec, como sus mensajes de depuración,
    // ese orden entre los dos no se puede saber y sale detrás).
    public sealed partial class ResolutorConfiguracionSsh
    {
        // Se llama con la lista vacía: lo que añade va delante de las demás opciones.
        static partial void AnadirOpcionesDelSistema(scoped ref PooledListRef<string> argumentos, ref string? registro)
        {
            registro = Path.Combine(Path.GetTempPath(), $"native_ssh_g_{Guid.NewGuid():N}.log");
            argumentos.Add("-T");
            argumentos.Add("-E");
            argumentos.Add(registro);
        }

        static partial void LeerRegistroDelSistema(string? registro, ref string errores)
        {
            try
            {
                if (registro is null || !File.Exists(registro))
                    return;

                string mensajes = File.ReadAllText(registro);
                errores = errores.Length == 0 || errores.EndsWith('\n') ? errores + mensajes : errores + Environment.NewLine + mensajes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sin el registro, el error lleva solo lo que ssh escribió en su salida de errores.
            }
        }

        // CreateProcess, con un nombre sin ruta, busca el programa en el directorio de native_ssh y en el directorio actual
        // antes que en PATH: un ssh.exe en la carpeta desde la que se lanza native_ssh (la del proyecto que abre el cliente
        // MCP, que puede venir de un repositorio ajeno) se ejecutaría al leer la configuración. Por eso el programa se busca
        // aquí, solo en PATH, y se lanza por su ruta completa; como CreateProcess, con .exe si el nombre no lleva extensión.
        static partial void NombreDelSistema(ref string programa)
        {
            if (!Path.HasExtension(programa))
                programa += ".exe";
        }

        // Una entrada de PATH entre comillas se usa sin ellas; una que no es absoluta (vacía, '.', una relativa) se salta:
        // sería relativa al directorio actual, justo lo que se evita.
        static partial void DirectorioDePathDelSistema(ref string? directorio)
        {
            string sinComillas = directorio!.Trim().Trim('"');
            directorio = Path.IsPathFullyQualified(sinComillas) ? sinComillas : null;
        }

        // El ssh de Git for Windows (MSYS) entiende una ruta de unidad '/c/…' como 'C:\…' y conecta con un IdentityFile así
        // (medido con OpenSSH_10.5p1 de Git 2.55); el de OpenSSH para Windows la lee como '\c\…' de la unidad actual
        // («no such identity» si no existe). ssh -G la da tal cual, así que native_ssh la lee como el ssh que leyó la
        // configuración: como 'C:\…' solo si ese ssh es de MSYS. Solo la forma '/' + una letra + '/' o fin; lo demás que
        // empieza por '/' (p. ej. '/etc/…', que en MSYS cuelga de la carpeta de Git) sigue igual.
        static partial void SshDeMsysDelSistema(string binario, ref bool isDeMsys) =>
            isDeMsys = BuscarPrograma(binario) is { } programa && IsProgramaDeMsys(programa);

        static partial void RutaDelSistema(ref string ruta, bool isSshDeMsys)
        {
            bool isUnidadMsys = isSshDeMsys && ruta.Length >= 2 && ruta[0] == '/' && char.IsAsciiLetter(ruta[1]) && (ruta.Length == 2 || ruta[2] == '/');
            if (isUnidadMsys)
                ruta = string.Concat(char.ToUpperInvariant(ruta[1]).ToString(), @":\", ruta.Length > 3 ? ruta[3..].Replace('/', '\\') : "");
        }

        // Un programa de MSYS (el ssh de Git for Windows, los de MSYS2) lleva su runtime, msys-2.0.dll, en su misma carpeta
        // (Git\usr\bin; System32\OpenSSH no la tiene).
        internal static bool IsProgramaDeMsys(string programa) =>
            Path.GetDirectoryName(programa) is { Length: > 0 } carpeta && File.Exists(Path.Combine(carpeta, "msys-2.0.dll"));

        // Un Match exec que lanza el ssh de Git for Windows no cuelga de ssh en el árbol de procesos: el sh de MSYS que lo
        // ejecuta sale en cuanto arranca el programa (medido: con 'sleep 120', a los 2 s el padre de sleep.exe ya no existía),
        // así que Kill(entireProcessTree) no lo alcanzaba y seguía vivo tras el plazo (y tras «mantiene abierta su salida»,
        // con 'sleep 90 &'). Por eso ssh entra al arrancar en un job object propio: lo que lanza entra en él al crearse, y
        // Matar termina el job entero. Sin JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, como en Linux: si ssh -G termina bien, lo que
        // dejó en segundo plano sin retener su salida sigue vivo. Límites: lo que ssh lanzara entre su arranque y la
        // asignación (unos microsegundos, antes de leer la configuración) quedaría fuera; si no se puede crear o asignar el
        // job (ssh ya terminado), Matar hace lo de antes (el árbol de procesos) y queda en el registro.
        // Sin JOB_OBJECT_LIMIT_BREAKAWAY_OK: con él, lo que lanza el ssh de Git for Windows (MSYS) no muere con el job (medido: con
        // ese ssh primero en PATH, el bucle de las pruebas de plazo agotado seguía vivo; con el del sistema, no), justo lo que
        // el job debe alcanzar. Un programa que pida CREATE_BREAKAWAY_FROM_JOB falla con acceso denegado.
        static partial void AgruparDelSistema(Process proceso, ref SafeHandle? grupo, ref int error)
        {
            TrabajoWindows trabajo = TrabajoWindows.Crear();
            if (trabajo.IsInvalid)
                error = Marshal.GetLastPInvokeError();
            else if (!TrabajoWindows.Asignar(trabajo, proceso.SafeHandle))
                error = Marshal.GetLastPInvokeError();
            else
            {
                grupo = trabajo;
                return;
            }

            trabajo.Dispose();
        }

        static partial void MatarGrupoDelSistema(SafeHandle? grupo)
        {
            if (grupo is TrabajoWindows trabajo)
                _ = TrabajoWindows.Terminar(trabajo, 1);
        }

        static partial void BorrarRegistroDelSistema(string? registro)
        {
            try
            {
                if (registro is not null)
                    File.Delete(registro);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Queda en la carpeta temporal del usuario.
            }
        }
    }

    // Job object de kernel32 (LibraryImport, compatible con AOT) para ssh -G y lo que lance.
    internal sealed partial class TrabajoWindows : SafeHandleZeroOrMinusOneIsInvalid
    {
        public TrabajoWindows()
            : base(ownsHandle: true)
        {
        }

        public static TrabajoWindows Crear() => CreateJobObject(IntPtr.Zero, IntPtr.Zero);

        protected override bool ReleaseHandle() => CloseHandle(handle);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial TrabajoWindows CreateJobObject(IntPtr atributos, IntPtr nombre);

        [LibraryImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool Asignar(TrabajoWindows trabajo, SafeProcessHandle proceso);

        [LibraryImport("kernel32.dll", EntryPoint = "TerminateJobObject")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool Terminar(TrabajoWindows trabajo, uint codigo);

        [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr manejador);
    }
}
