using System.Text;
using NativeSsh.Comun;
using NativeSsh.Protocolo;

namespace NativeSsh.Sesion
{
    // Punto de entrada del núcleo: una SesionShell por destino, creada bajo demanda.
    public sealed class GestorSesiones(
        IFabricaConexion fabrica,
        LimitadorConexiones limitador,
        RegistroPeticiones registro,
        OpcionesNativeSsh opciones,
        TimeProvider reloj,
        ILoggerFactory loggerFactory) : IAsyncDisposable
    {
        public static readonly TimeSpan TimeoutMinimo = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan TimeoutMaximo = TimeSpan.FromHours(1);

        private readonly Lock _candado = new();
        private readonly Dictionary<string, SesionShell> _sesiones = new(StringComparer.Ordinal);
        private readonly ILogger _logger = loggerFactory.CreateLogger("NativeSsh.Sesion");
        private bool _isDispuesto;

        // isHerramienta: la petición de una herramienta de archivo o de ssh_set_cwd (Peticion.IsHerramienta).
        // isGuardarSalida: ssh_exec con guardarSalida=true (Peticion.MarcaGuardado, SalidaGuardada).
        public Peticion Ejecutar(string destinoTexto, string comando, TimeSpan timeout, bool isHerramienta = false, bool isGuardarSalida = false)
        {
            if (string.IsNullOrWhiteSpace(comando))
                throw new NativeSshException("El comando está vacío.");

            DestinoSsh destino = ParsearDestino(destinoTexto);
            SesionShell sesion = ObtenerOCrear(destino);
            Peticion peticion = new(destino, comando, Ajustar(timeout), opciones.MaxBytesSalidaPorPeticion, reloj, isGuardarSalida ? IdentidadShell.NuevoTestigo() : null) { IsHerramienta = isHerramienta };
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
                _ = _sesiones.TryGetValue(peticion.Destino.Clave, out sesion);

            return sesion is not null && sesion.EnviarEntrada(peticion, texto, isEnviarEnter, isSecreto);
        }

        public IReadOnlyList<SesionShell> ListarSesiones()
        {
            lock (_candado)
                return [.. _sesiones.Values];
        }

        public IReadOnlyList<Peticion> ListarPeticiones() => registro.ListarRecientes();

        public async Task<bool> DesconectarAsync(string destinoTexto) => await DesconectarConLimpiezaAsync(destinoTexto).ConfigureAwait(false) is not null;

        // null si no había sesión; si no, los archivos de guardarSalida que tenía la sesión y los que no se pudieron borrar.
        public async Task<LimpiezaGuardados?> DesconectarConLimpiezaAsync(string destinoTexto)
        {
            DestinoSsh destino = ParsearDestino(destinoTexto);
            SesionShell? sesion;
            lock (_candado)
            {
                if (_sesiones.Remove(destino.Clave, out sesion))
                    _logger.Desconectando(destino.Clave);
            }

            if (sesion is null)
                return null;

            int archivos = sesion.ArchivosGuardados;
            await sesion.DisposeAsync().ConfigureAwait(false);
            return new LimpiezaGuardados(archivos, sesion.ArchivosGuardadosSinBorrarAlCerrar);
        }

        public bool ReiniciarShell(string destinoTexto) => ReiniciarShellConLimpieza(destinoTexto) is not null;

        // null si no había sesión; si no, los archivos de guardarSalida de la sesión cuyo borrado se lanza.
        public LimpiezaGuardados? ReiniciarShellConLimpieza(string destinoTexto)
        {
            DestinoSsh destino = ParsearDestino(destinoTexto);
            SesionShell? sesion;
            lock (_candado)
                _ = _sesiones.TryGetValue(destino.Clave, out sesion);

            if (sesion is null)
                return null;

            int archivos = sesion.ArchivosGuardados;
            sesion.SolicitarReset();
            return new LimpiezaGuardados(archivos, 0);
        }

        public DestinoSsh ParsearDestino(string destinoTexto) => !DestinoSsh.TryParse(destinoTexto, opciones.UsuarioPorDefecto, out DestinoSsh destino)
                ? throw new NativeSshException($"Destino '{destinoTexto}' no válido. Formato: usuario@servidor[:puerto], o [ipv6]:puerto.")
                : destino;

        public async ValueTask DisposeAsync()
        {
            SesionShell[] sesiones;
            lock (_candado)
            {
                if (_isDispuesto)
                    return;

                _isDispuesto = true;
                sesiones = [.. _sesiones.Values];
                _sesiones.Clear();
            }

            foreach (SesionShell sesion in sesiones)
                await sesion.DisposeAsync().ConfigureAwait(false);
        }

        private SesionShell ObtenerOCrear(DestinoSsh destino)
        {
            lock (_candado)
            {
                ObjectDisposedException.ThrowIf(_isDispuesto, this);
                if (!_sesiones.TryGetValue(destino.Clave, out SesionShell? sesion))
                {
                    sesion = new SesionShell(destino, fabrica, limitador, opciones, _logger);
                    _sesiones[destino.Clave] = sesion;
                }

                return sesion;
            }
        }

        private static TimeSpan Ajustar(TimeSpan timeout) =>
            timeout < TimeoutMinimo ? TimeoutMinimo : timeout > TimeoutMaximo ? TimeoutMaximo : timeout;
    }

    // Archivos de guardarSalida de una sesión al hacer ssh_reset_shell o ssh_disconnect: los que había (creados en sus shells
    // y aún no borrados) y, al desconectar, los que no se pudieron borrar.
    public sealed record LimpiezaGuardados(int Archivos, int SinBorrar);
}
