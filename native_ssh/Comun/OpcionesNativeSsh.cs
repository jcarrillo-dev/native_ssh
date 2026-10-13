namespace NativeSsh.Comun
{
    public sealed record OpcionesNativeSsh
    {
        public string UsuarioPorDefecto { get; init; } = Environment.UserName;
        public string DirectorioSsh { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        public IReadOnlyList<string> RutasIdentidad { get; init; } = [];
        public string? Contrasena { get; init; }
        public int MaxBytesSalidaPorPeticion { get; init; } = 1024 * 1024;
        public int MaxBytesPorRespuesta { get; init; } = 64 * 1024;
        public int MaxPeticionesRetenidas { get; init; } = 64;
        public TimeSpan RetencionPeticionesFinalizadas { get; init; } = TimeSpan.FromMinutes(30);
        public TimeSpan IntervaloKeepAlive { get; init; } = TimeSpan.FromSeconds(30);
        public TimeSpan TimeoutConexion { get; init; } = TimeSpan.FromSeconds(15);
        public TimeSpan TimeoutInicializacionShell { get; init; } = TimeSpan.FromSeconds(15);
        public TimeSpan EsperaTrasCtrlC { get; init; } = TimeSpan.FromSeconds(3);
        public int MaxConexionesPorVentana { get; init; } = 4;

        // guardarSalida (SalidaGuardada): el directorio base en el servidor es este prefijo + '-<usuario>'. Fijo en producción
        // (no hay variable de entorno); las pruebas lo cambian para no compartir el /tmp/native_ssh-<usuario> de la máquina.
        public string PrefijoGuardado { get; init; } = "/tmp/native_ssh";

        // guardarSalida: como mucho tantos shells de una sesión (el actual y los que se reabrieron solos tras un plazo, una
        // cancelación o una caída) con salidas guardadas sin borrar; por encima, un guardarSalida en un shell nuevo no se
        // ejecuta hasta ssh_reset_shell o ssh_disconnect (SesionShell). Fijo en producción; las pruebas lo bajan.
        public int MaxShellsConGuardado { get; init; } = 64;

        public TimeSpan VentanaConexiones { get; init; } = TimeSpan.FromSeconds(30);

        /// Variables: NATIVE_SSH_DIRECTORIO_SSH, NATIVE_SSH_IDENTIDADES (separadas por ':'), NATIVE_SSH_PASSWORD.
        public static OpcionesNativeSsh DesdeEntorno()
        {
            OpcionesNativeSsh porDefecto = new();
            string directorio = Environment.GetEnvironmentVariable("NATIVE_SSH_DIRECTORIO_SSH") is { Length: > 0 } valor ? valor : porDefecto.DirectorioSsh;
            string? identidades = Environment.GetEnvironmentVariable("NATIVE_SSH_IDENTIDADES");
            string[] rutas = identidades is { Length: > 0 }
                ? identidades.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [Path.Combine(directorio, "id_ed25519"), Path.Combine(directorio, "id_ecdsa"), Path.Combine(directorio, "id_rsa")];

            return porDefecto with
            {
                DirectorioSsh = directorio,
                RutasIdentidad = rutas,
                Contrasena = Environment.GetEnvironmentVariable("NATIVE_SSH_PASSWORD")
            };
        }
    }
}
