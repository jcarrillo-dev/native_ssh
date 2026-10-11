namespace NativeSsh.Comun
{
    public sealed record OpcionesNativeSsh
    {
        public string UsuarioPorDefecto { get; init; } = Environment.UserName;
        public string DirectorioSsh { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        public IReadOnlyList<string> RutasIdentidad { get; init; } = [];
        public string? Contrasena { get; init; }
        public string? Passphrase { get; init; }
        public string? FicheroPassphrase { get; init; }
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

        /// Variables: NATIVE_SSH_DIRECTORIO_SSH, NATIVE_SSH_IDENTIDADES (separadas por ':'), NATIVE_SSH_PASSWORD,
        /// NATIVE_SSH_PASSPHRASE y NATIVE_SSH_PASSPHRASE_FICHERO (la passphrase de las claves cifradas: FabricaConexionSsh).
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
                Contrasena = Environment.GetEnvironmentVariable("NATIVE_SSH_PASSWORD"),
                Passphrase = Environment.GetEnvironmentVariable("NATIVE_SSH_PASSPHRASE"),
                FicheroPassphrase = Environment.GetEnvironmentVariable("NATIVE_SSH_PASSPHRASE_FICHERO")
            };
        }
    }
}
