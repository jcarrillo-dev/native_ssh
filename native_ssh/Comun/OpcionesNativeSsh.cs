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
        public TimeSpan VentanaConexiones { get; init; } = TimeSpan.FromSeconds(30);

        // Destinos en modo solo lectura (DestinosSoloLectura, NATIVE_SSH_SOLO_LECTURA).
        public DestinosSoloLectura SoloLectura { get; init; } = DestinosSoloLectura.Ninguno;

        /// Variables: NATIVE_SSH_DIRECTORIO_SSH, NATIVE_SSH_IDENTIDADES (separadas por ':'), NATIVE_SSH_PASSWORD y
        /// NATIVE_SSH_SOLO_LECTURA (si no se entiende, native_ssh no arranca).
        public static OpcionesNativeSsh DesdeEntorno()
        {
            if (!DestinosSoloLectura.TryParsear(Environment.GetEnvironmentVariable(DestinosSoloLectura.Variable), out DestinosSoloLectura soloLectura, out string? errorSoloLectura))
                throw new InvalidOperationException(errorSoloLectura);

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
                Contrasena = Environment.GetEnvironmentVariable("NATIVE_SSH_PASSWORD"),
                SoloLectura = soloLectura
            };
        }
    }
}
