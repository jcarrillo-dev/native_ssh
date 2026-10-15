namespace NativeSsh.Comun
{
    public sealed record OpcionesNativeSsh
    {
        public string UsuarioPorDefecto { get; init; } = Environment.UserName;
        public string DirectorioSsh { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        public string[] RutasIdentidad { get; init; } = [];

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

        // guardarSalida (SalidaGuardada): el directorio base en el servidor es este prefijo + '-<usuario>'. Fijo en producción
        // (no hay variable de entorno); las pruebas lo cambian para no compartir el /tmp/native_ssh-<usuario> de la máquina.
        public string PrefijoGuardado { get; init; } = "/tmp/native_ssh";

        // guardarSalida: como mucho tantos shells de una sesión (el actual y los que se reabrieron solos tras un plazo, una
        // cancelación o una caída) con salidas guardadas sin borrar; por encima, un guardarSalida en un shell nuevo no se
        // ejecuta hasta ssh_reset_shell o ssh_disconnect (SesionShell). Fijo en producción; las pruebas lo bajan.
        public int MaxShellsConGuardado { get; init; } = 64;

        public TimeSpan VentanaConexiones { get; init; } = TimeSpan.FromSeconds(30);

        // Destinos en modo solo lectura (DestinosSoloLectura, NATIVE_SSH_SOLO_LECTURA).
        public DestinosSoloLectura SoloLectura { get; init; } = DestinosSoloLectura.Ninguno;

        /// Variables: NATIVE_SSH_DIRECTORIO_SSH, NATIVE_SSH_IDENTIDADES (separadas como el PATH: ':' en Linux y macOS, ';' en Windows), NATIVE_SSH_PASSWORD,
        /// NATIVE_SSH_PASSPHRASE y NATIVE_SSH_PASSPHRASE_FICHERO (la passphrase de las claves cifradas: FabricaConexionSsh),
        /// NATIVE_SSH_CONFIG_SSH (ruta o 'none') y NATIVE_SSH_SOLO_LECTURA (si no se entiende, native_ssh no arranca). Sin
        /// NATIVE_SSH_CONFIG_SSH: con NATIVE_SSH_DIRECTORIO_SSH, el fichero 'config' de ese directorio si existe al arrancar
        /// (si no, ninguno: nunca el ~/.ssh/config real cuando se ha pedido otro directorio); sin ninguna de las dos, la
        /// configuración de ssh por defecto.
        public static OpcionesNativeSsh DesdeEntorno()
        {
            if (!DestinosSoloLectura.TryParsear(Environment.GetEnvironmentVariable(DestinosSoloLectura.Variable), out DestinosSoloLectura soloLectura, out string? errorSoloLectura))
                throw new InvalidOperationException(errorSoloLectura);

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
                Contrasena = Environment.GetEnvironmentVariable(VariablesSecretas.Contrasena),
                Passphrase = Environment.GetEnvironmentVariable(VariablesSecretas.Passphrase),
                FicheroPassphrase = Environment.GetEnvironmentVariable("NATIVE_SSH_PASSPHRASE_FICHERO"),
                SoloLectura = soloLectura
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
