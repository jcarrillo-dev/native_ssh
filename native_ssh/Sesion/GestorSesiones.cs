using System.Text;
using NativeSsh.Comun;

namespace NativeSsh.Sesion
{
    // Punto de entrada del núcleo: una SesionShell por destino y claves (DestinoSsh.ClaveSesion), creada bajo demanda. Cada
    // sesión recuerda los alias con los que se abrió o usó, para que ssh_disconnect y ssh_status con un alias la encuentren
    // aunque después cambie la configuración de ssh del alias.
    public sealed class GestorSesiones(
        IFabricaConexion fabrica,
        LimitadorConexiones limitador,
        RegistroPeticiones registro,
        OpcionesNativeSsh opciones,
        TimeProvider reloj,
        ILoggerFactory loggerFactory,
        IResolutorDestino? resolutor = null) : IAsyncDisposable
    {
        public static readonly TimeSpan TimeoutMinimo = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan TimeoutMaximo = TimeSpan.FromHours(1);

        private readonly Lock _candado = new();
        private const int MaxAliasPorSesion = 16;

        private readonly Dictionary<string, EntradaSesion> _sesiones = new(StringComparer.Ordinal);
        private readonly ILogger _logger = loggerFactory.CreateLogger("NativeSsh.Sesion");
        private bool _isDispuesto;

        public Peticion Ejecutar(string destinoTexto, string comando, TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(comando))
                throw new NativeSshException("El comando está vacío.");

            DestinoSsh destino = ParsearDestino(destinoTexto);
            SesionShell sesion = ObtenerOCrear(destino);
            Peticion peticion = new(destino, comando, Ajustar(timeout), opciones.MaxBytesSalidaPorPeticion, reloj);
            registro.Registrar(peticion);
            sesion.Encolar(peticion);
            return peticion;
        }

        // Herramientas de solo lectura: canal exec en la sesión del destino, sin pasar por su cola ni por el registro.
        public Task<ResultadoCanalExec> EjecutarExecAsync(string destinoTexto, string script, TimeSpan timeout, CancellationToken cancellationToken)
        {
            DestinoSsh destino = ParsearDestino(destinoTexto);
            return ObtenerOCrear(destino).EjecutarExecAsync(script, Ajustar(timeout), cancellationToken);
        }

        public void FijarDirectorioTrabajo(string destinoTexto, string? directorio)
        {
            if (directorio is not null && Encoding.UTF8.GetByteCount(directorio) > SesionShell.MaxBytesDirectorioTrabajo)
                throw new NativeSshException($"La ruta del directorio de trabajo supera {SesionShell.MaxBytesDirectorioTrabajo} bytes.");

            ObtenerOCrear(ParsearDestino(destinoTexto)).DirectorioTrabajo = directorio;
        }

        public Peticion BuscarPeticion(string id) =>
            registro.Buscar(id.Trim()) ?? throw new NativeSshException($"No existe la petición '{id}'. Puede que haya caducado; usa ssh_status para ver las recientes.");

        // isSecreto: el texto no debe quedar en ningún sitio (ssh_input con secreto=true): la salida de la petición ya lo
        // enmascara (BufferSalida.RegistrarSecreto, antes de llamar aquí) y la sesión borra su copia tras enviarlo.
        public bool EnviarEntrada(Peticion peticion, ReadOnlySpan<char> texto, bool isEnviarEnter, bool isSecreto = false)
        {
            SesionShell? sesion;
            lock (_candado)
                sesion = _sesiones.GetValueOrDefault(peticion.Destino.ClaveSesion)?.Sesion;

            return sesion is not null && sesion.EnviarEntrada(peticion, texto, isEnviarEnter, isSecreto);
        }

        public IReadOnlyList<SesionShell> ListarSesiones()
        {
            lock (_candado)
            {
                List<SesionShell> sesiones = new(_sesiones.Count);
                foreach (EntradaSesion entrada in _sesiones.Values)
                    sesiones.Add(entrada.Sesion);
                return sesiones;
            }
        }

        // Alias con los que se abrió o usó la sesión (vacío si solo se usó con el servidor real).
        public IReadOnlyList<string> AliasDe(SesionShell sesion)
        {
            lock (_candado)
                return _sesiones.TryGetValue(sesion.Destino.ClaveSesion, out EntradaSesion? entrada) && ReferenceEquals(entrada.Sesion, sesion) ? [.. entrada.Alias] : [];
        }

        // Las sesiones a las que se refiere un destino ya resuelto (ParsearDestino), para ssh_disconnect y ssh_status:
        // - un alias (Alias != null): la de su destino y claves actuales y las que se abrieron o usaron con ese alias (aunque
        //   su configuración haya cambiado después);
        // - el servidor real (usuario@servidor:puerto, o un nombre que la configuración no cambia): todas las de ese
        //   usuario@servidor:puerto, sean de las claves que sean.
        public IReadOnlyList<SesionShell> Seleccionar(DestinoSsh destino)
        {
            lock (_candado)
            {
                List<SesionShell> sesiones = [];
                foreach ((string clave, EntradaSesion entrada) in _sesiones)
                    if (IsSeleccionada(destino, clave, entrada))
                        sesiones.Add(entrada.Sesion);
                return sesiones;
            }
        }

        // ¿La petición es de una sesión a la que se refiere el destino (mismo criterio que Seleccionar)?
        public static bool IsDelDestino(Peticion peticion, DestinoSsh destino) =>
            destino.Alias is null
                ? string.Equals(peticion.Destino.Clave, destino.Clave, StringComparison.Ordinal)
                : string.Equals(peticion.Destino.ClaveSesion, destino.ClaveSesion, StringComparison.Ordinal) || string.Equals(peticion.Destino.Alias, destino.Alias, StringComparison.OrdinalIgnoreCase);

        public IReadOnlyList<Peticion> ListarPeticiones() => registro.ListarRecientes();

        // Cierra las sesiones a las que se refiere el destino (criterio de Seleccionar) y devuelve los destinos que cerró.
        public Task<IReadOnlyList<DestinoSsh>> DesconectarAsync(string destinoTexto) => DesconectarAsync(ParsearDestino(destinoTexto));

        public async Task<IReadOnlyList<DestinoSsh>> DesconectarAsync(DestinoSsh destino)
        {
            List<SesionShell> cerradas = [];
            lock (_candado)
            {
                foreach ((string clave, EntradaSesion entrada) in _sesiones)
                    if (IsSeleccionada(destino, clave, entrada))
                        cerradas.Add(entrada.Sesion);

                foreach (SesionShell sesion in cerradas)
                {
                    _ = _sesiones.Remove(sesion.Destino.ClaveSesion);
                    _logger.Desconectando(sesion.Destino.Clave);
                }
            }

            List<DestinoSsh> destinos = new(cerradas.Count);
            foreach (SesionShell sesion in cerradas)
            {
                await sesion.DisposeAsync().ConfigureAwait(false);
                destinos.Add(sesion.Destino);
            }

            return destinos;
        }

        // Solo la sesión que usaría el próximo comando con ese destino (sus claves actuales).
        public bool ReiniciarShell(string destinoTexto)
        {
            DestinoSsh destino = ParsearDestino(destinoTexto);
            SesionShell? sesion;
            lock (_candado)
                sesion = _sesiones.GetValueOrDefault(destino.ClaveSesion)?.Sesion;

            sesion?.SolicitarReset();
            return sesion is not null;
        }

        // Con resolutor (la configuración de ssh: alias, HostName, User, Port, IdentityFile), el destino que se devuelve, y
        // con él la clave de la sesión, es el resuelto: un alias y su usuario@HostName:Port comparten sesión si resuelven a
        // las mismas claves.
        public DestinoSsh ParsearDestino(string destinoTexto) =>
            !DestinoSsh.TryParse(destinoTexto, opciones.UsuarioPorDefecto, out DestinoSsh destino, out bool isUsuarioExplicito, out bool isPuertoExplicito)
                ? throw new NativeSshException($"Destino '{destinoTexto}' no válido. Formato: usuario@servidor[:puerto], o [ipv6]:puerto.")
                : resolutor?.Resolver(destino, isUsuarioExplicito, isPuertoExplicito) ?? destino;

        public async ValueTask DisposeAsync()
        {
            SesionShell[] sesiones;
            lock (_candado)
            {
                if (_isDispuesto)
                    return;

                _isDispuesto = true;
                sesiones = new SesionShell[_sesiones.Count];
                int i = 0;
                foreach (EntradaSesion entrada in _sesiones.Values)
                    sesiones[i++] = entrada.Sesion;
                _sesiones.Clear();
            }

            foreach (SesionShell sesion in sesiones)
                await sesion.DisposeAsync().ConfigureAwait(false);
        }

        private SesionShell ObtenerOCrear(DestinoSsh destino)
        {
            string clave = destino.ClaveSesion;
            lock (_candado)
            {
                ObjectDisposedException.ThrowIf(_isDispuesto, this);
                if (!_sesiones.TryGetValue(clave, out EntradaSesion? entrada))
                {
                    entrada = new EntradaSesion(new SesionShell(destino, fabrica, limitador, opciones, _logger));
                    _sesiones[clave] = entrada;
                }

                if (destino.Alias is { } alias && entrada.Alias.Count < MaxAliasPorSesion)
                    _ = entrada.Alias.Add(alias);
                return entrada.Sesion;
            }
        }

        private static bool IsSeleccionada(DestinoSsh destino, string clave, EntradaSesion entrada) =>
            destino.Alias is null
                ? string.Equals(entrada.Sesion.Destino.Clave, destino.Clave, StringComparison.Ordinal)
                : string.Equals(clave, destino.ClaveSesion, StringComparison.Ordinal) || entrada.Alias.Contains(destino.Alias);

        private sealed class EntradaSesion(SesionShell sesion)
        {
            public SesionShell Sesion { get; } = sesion;

            // Bajo _candado.
            public HashSet<string> Alias { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private static TimeSpan Ajustar(TimeSpan timeout) =>
            timeout < TimeoutMinimo ? TimeoutMinimo : timeout > TimeoutMaximo ? TimeoutMaximo : timeout;
    }
}
