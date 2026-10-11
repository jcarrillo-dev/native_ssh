namespace NativeSsh.Comun
{
    public sealed record OpcionesNativeSsh
    {
        public string UsuarioPorDefecto { get; init; } = Environment.UserName;
        public string DirectorioSsh { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        public IReadOnlyList<string> RutasIdentidad { get; init; } = [];

        // NATIVE_SSH_IDENTIDADES definida: sus claves mandan sobre los IdentityFile de la configuración de ssh.
        public bool IsRutasIdentidadExplicitas { get; init; }

        // Configuración de ssh con la que se resuelven los destinos (ssh -G, ResolutorConfiguracionSsh). null: no se usa (el
        // destino se toma tal cual; lo que hacen las pruebas y NATIVE_SSH_CONFIG_SSH=none); "": la de ssh por defecto
        // (~/.ssh/config y /etc/ssh/ssh_config); otra: ese fichero (ssh -F, que no lee /etc/ssh/ssh_config).
        public string? ConfiguracionSsh { get; init; }
        public string BinarioSsh { get; init; } = "ssh";
        public TimeSpan TimeoutConfiguracionSsh { get; init; } = TimeSpan.FromSeconds(5);
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
        /// NATIVE_SSH_PASSPHRASE y NATIVE_SSH_PASSPHRASE_FICHERO (la passphrase de las claves cifradas: FabricaConexionSsh),
        /// NATIVE_SSH_CONFIG_SSH (ruta o 'none'). Sin NATIVE_SSH_CONFIG_SSH: con NATIVE_SSH_DIRECTORIO_SSH, el fichero 'config'
        /// de ese directorio si existe al arrancar (si no, ninguno: nunca el ~/.ssh/config real cuando se ha pedido otro
        /// directorio); sin ninguna de las dos, la configuración de ssh por defecto.
        public static OpcionesNativeSsh DesdeEntorno()
        {
            OpcionesNativeSsh porDefecto = new();
            string? directorioEntorno = Environment.GetEnvironmentVariable("NATIVE_SSH_DIRECTORIO_SSH") is { Length: > 0 } d ? d : null;
            string directorio = directorioEntorno ?? porDefecto.DirectorioSsh;
            string? identidades = Environment.GetEnvironmentVariable("NATIVE_SSH_IDENTIDADES");
            bool hasIdentidades = identidades is { Length: > 0 };
            string[] rutas = hasIdentidades
                ? identidades!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [Path.Combine(directorio, "id_ed25519"), Path.Combine(directorio, "id_ecdsa"), Path.Combine(directorio, "id_rsa")];

            return porDefecto with
            {
                DirectorioSsh = directorio,
                RutasIdentidad = rutas,
                IsRutasIdentidadExplicitas = hasIdentidades,
                ConfiguracionSsh = ElegirConfiguracionSsh(Environment.GetEnvironmentVariable("NATIVE_SSH_CONFIG_SSH"), directorioEntorno),
                Contrasena = Environment.GetEnvironmentVariable("NATIVE_SSH_PASSWORD"),
                Passphrase = Environment.GetEnvironmentVariable("NATIVE_SSH_PASSPHRASE"),
                FicheroPassphrase = Environment.GetEnvironmentVariable("NATIVE_SSH_PASSPHRASE_FICHERO")
            };
        }

        internal static string? ElegirConfiguracionSsh(string? configuracion, string? directorioEntorno)
        {
            if (configuracion is { Length: > 0 })
                return string.Equals(configuracion, "none", StringComparison.Ordinal) ? null : configuracion;
            if (directorioEntorno is null)
                return "";

            string enDirectorio = Path.Combine(directorioEntorno, "config");
            return File.Exists(enDirectorio) ? enDirectorio : null;
        }
    }
}
