using System.Text;
using NativeSsh.Comun;
using NativeSsh.Protocolo;

namespace NativeSsh.Sesion
{
    // Punto de entrada del núcleo: una SesionShell por destino y claves (DestinoSsh.ClaveSesion), creada bajo demanda. Cada
    // sesión recuerda los alias con los que se abrió o usó (hasta MaxAliasPorSesion), para que ssh_disconnect y ssh_status
    // con un alias la encuentren aunque después cambie el HostName (o las claves) del alias en la configuración de ssh.
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

        // Alias que recuerda cada sesión (README; ssh_disconnect y ssh_status lo citan si una sesión llegó al tope sin guardar
        // el alias pedido).
        public const int MaxAliasPorSesion = 64;

        private readonly Lock _candado = new();

        private readonly Dictionary<string, EntradaSesion> _sesiones = new(StringComparer.Ordinal);
        private readonly ILogger _logger = loggerFactory.CreateLogger("NativeSsh.Sesion");

        // La configuración de ssh. Las entradas de NATIVE_SSH_SOLO_LECTURA que son alias suyos se comparan por su HostName,
        // leído con ella igual que los destinos.
        private readonly IResolutorDestino? _resolutor = UsarConfiguracionEnSoloLectura(opciones.SoloLectura, resolutor);
        private bool _isDispuesto;

        // isHerramienta: la petición de una herramienta de archivo o de ssh_set_cwd (Peticion.IsHerramienta).
        // isGuardarSalida: ssh_exec con guardarSalida=true (Peticion.MarcaGuardado, SalidaGuardada).
        // herramientaEscritura: la herramienta de escritura que lo lanza (Peticion.HerramientaEscritura), o null.
        public Peticion Ejecutar(string destinoTexto, string comando, TimeSpan timeout, bool isHerramienta = false, bool isGuardarSalida = false, string? herramientaEscritura = null)
        {
            if (string.IsNullOrWhiteSpace(comando))
                throw new NativeSshException("El comando está vacío.");

            DestinoSsh destino = ParsearDestino(destinoTexto);
            SesionShell sesion = ObtenerOCrear(destino);
            Peticion peticion = new(destino, comando, Ajustar(timeout), opciones.MaxBytesSalidaPorPeticion, reloj, isGuardarSalida ? IdentidadShell.NuevoTestigo() : null) { IsHerramienta = isHerramienta, HerramientaEscritura = herramientaEscritura };
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

        // La conexión de la sesión que usaría el próximo comando con ese destino (sus claves actuales), si está conectada.
        public ExtremoConectado? ExtremoDe(DestinoSsh destino)
        {
            lock (_candado)
                return _sesiones.GetValueOrDefault(destino.ClaveSesion)?.Sesion.Extremo;
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
        // - un alias (Alias != null): las de su usuario y puerto resueltos que son la de su destino y claves actuales o se
        //   abrieron o usaron con ese alias (aunque su HostName o sus claves hayan cambiado después). El alias se compara como
        //   en ssh, distinguiendo mayúsculas ('Host Web' no es 'Host web'), y el usuario y el puerto evitan que un destino
        //   que nunca se usó (root@prod, prod:2223) alcance la sesión de otro (deploy@prod);
        // - el servidor real (usuario@servidor:puerto, o un nombre que la configuración no cambia): todas las de ese
        //   usuario@servidor:puerto, sean de las claves que sean.
        public IReadOnlyList<SesionShell> Seleccionar(DestinoSsh destino)
        {
            lock (_candado)
            {
                List<SesionShell> sesiones = [];
                foreach (EntradaSesion entrada in _sesiones.Values)
                    if (IsSeleccionada(destino, entrada))
                        sesiones.Add(entrada.Sesion);
                return sesiones;
            }
        }

        // ¿La petición es de una sesión a la que se refiere el destino (mismo criterio que Seleccionar)?
        public static bool IsDelDestino(Peticion peticion, DestinoSsh destino) =>
            IsDelDestino(peticion.Destino, string.Equals(peticion.Destino.Alias, destino.Alias, StringComparison.Ordinal), destino);

        // Para ssh_disconnect y ssh_status con un alias, entre las sesiones que no selecciona (Seleccionar): las abiertas con
        // ese alias para otro usuario o puerto (su User o Port cambió con la sesión viva, o se pidió otro), una pista por
        // usuario y puerto, y las que llegaron al tope de alias sin guardar este, una por usuario@servidor:puerto. Cada pista
        // lleva lo que cerraría de verdad cada destino con que se podría cerrar (EvaluarCandidato). Vacía si no es un alias.
        public IReadOnlyList<PistaAlias> BuscarPistasAlias(DestinoSsh destino)
        {
            if (destino.Alias is not { } alias)
                return [];

            List<List<SesionShell>> otros = [];
            List<List<SesionShell>> enElTope = [];
            lock (_candado)
                foreach (EntradaSesion entrada in _sesiones.Values)
                {
                    DestinoSsh deLaSesion = entrada.Sesion.Destino;
                    if (IsSeleccionada(destino, entrada))
                        continue;

                    bool isMismoUsuarioYPuerto = IsMismoUsuarioYPuerto(deLaSesion, destino);
                    if (entrada.Alias.Contains(alias) && !isMismoUsuarioYPuerto)
                        Agrupar(otros, entrada.Sesion, v => IsMismoUsuarioYPuerto(v.Destino, deLaSesion));
                    else if (entrada.AliasOlvidados.Contains(alias) || (entrada.IsOlvidadosDesbordado && isMismoUsuarioYPuerto))
                        Agrupar(enElTope, entrada.Sesion, v => string.Equals(v.Destino.Clave, deLaSesion.Clave, StringComparison.Ordinal));
                }

            // Fuera del candado: cada candidato pasa por ParsearDestino (ssh -G, hasta su plazo).
            List<PistaAlias> pistas = new(otros.Count + enElTope.Count);
            string servidorAlias = ConCorchetes(alias);
            foreach (List<SesionShell> viejas in otros)
            {
                DestinoSsh primera = viejas[0].Destino;
                List<CandidatoCierre> candidatos = [EvaluarCandidato($"{primera.Usuario}@{servidorAlias}:{primera.Puerto}", viejas)];
                if (viejas.TrueForAll(v => string.Equals(v.Destino.Clave, primera.Clave, StringComparison.Ordinal)))
                    candidatos.Add(EvaluarCandidato(TextoServidorReal(primera), viejas));
                pistas.Add(new PistaAlias(IsEnElTope: false, viejas, candidatos));
            }

            foreach (List<SesionShell> viejas in enElTope)
                pistas.Add(new PistaAlias(IsEnElTope: true, viejas, [EvaluarCandidato(TextoServidorReal(viejas[0].Destino), viejas)]));
            return pistas;
        }

        // usuario@servidor:puerto escrito como destino (servidor IPv6 entre corchetes): DestinoSsh.Clave no lleva corchetes.
        private static string TextoServidorReal(DestinoSsh destino) => $"{destino.Usuario}@{ConCorchetes(destino.Servidor)}:{destino.Puerto}";

        private static string ConCorchetes(string servidor) => servidor.Contains(':', StringComparison.Ordinal) ? $"[{servidor}]" : servidor;

        private static void Agrupar(List<List<SesionShell>> grupos, SesionShell sesion, Predicate<SesionShell> isDelGrupo)
        {
            foreach (List<SesionShell> grupo in grupos)
                if (isDelGrupo(grupo[0]))
                {
                    grupo.Add(sesion);
                    return;
                }

            grupos.Add([sesion]);
        }

        // Lo que cerraría ssh_disconnect con ese texto, con su mismo criterio (ParsearDestino y Seleccionar): si alcanza todas
        // las viejas y qué más cierra; Error si el destino se rechaza (p. ej. un User de la configuración con '@' o '+').
        private CandidatoCierre EvaluarCandidato(string texto, List<SesionShell> viejas)
        {
            DestinoSsh candidato;
            try
            {
                candidato = ParsearDestino(texto);
            }
            catch (NativeSshException ex)
            {
                return new CandidatoCierre(texto, ex.Message, IsAlcanzaTodas: false, []);
            }

            List<SesionShell> cerraria = [.. Seleccionar(candidato)];
            List<SesionShell> ademas = [];
            foreach (SesionShell sesion in cerraria)
                if (!viejas.Contains(sesion))
                    ademas.Add(sesion);
            bool isAlcanzaTodas = viejas.TrueForAll(cerraria.Contains);
            return new CandidatoCierre(texto, null, isAlcanzaTodas, ademas);
        }

        public IReadOnlyList<Peticion> ListarPeticiones() => registro.ListarRecientes();

        // Cierra las sesiones a las que se refiere el destino (criterio de Seleccionar) y devuelve los destinos que cerró.
        public Task<IReadOnlyList<DestinoSsh>> DesconectarAsync(string destinoTexto) => DesconectarAsync(ParsearDestino(destinoTexto));

        public async Task<IReadOnlyList<DestinoSsh>> DesconectarAsync(DestinoSsh destino)
        {
            IReadOnlyList<SesionCerrada> cerradas = await DesconectarConLimpiezaAsync(destino).ConfigureAwait(false);
            List<DestinoSsh> destinos = new(cerradas.Count);
            foreach (SesionCerrada cerrada in cerradas)
                destinos.Add(cerrada.Destino);

            return destinos;
        }

        // Como DesconectarAsync, y de cada sesión cerrada, los archivos de guardarSalida creados en ella, los que contó el
        // servidor al borrarlos y si la limpieza falló.
        public async Task<IReadOnlyList<SesionCerrada>> DesconectarConLimpiezaAsync(DestinoSsh destino)
        {
            List<SesionShell> cerradas = [];
            lock (_candado)
            {
                foreach (EntradaSesion entrada in _sesiones.Values)
                    if (IsSeleccionada(destino, entrada))
                        cerradas.Add(entrada.Sesion);

                foreach (SesionShell sesion in cerradas)
                {
                    _ = _sesiones.Remove(sesion.Destino.ClaveSesion);
                    _logger.Desconectando(sesion.Destino.Clave);
                }
            }

            List<SesionCerrada> resultado = new(cerradas.Count);
            foreach (SesionShell sesion in cerradas)
            {
                await sesion.DisposeAsync().ConfigureAwait(false);
                resultado.Add(new SesionCerrada(sesion.Destino, sesion.LimpiezaAlCerrar));
            }

            return resultado;
        }

        public bool ReiniciarShell(string destinoTexto) => ReiniciarShellConLimpieza(destinoTexto) is not null;

        // Solo la sesión que usaría el próximo comando con ese destino (sus claves actuales). null si no había sesión; si no,
        // los archivos de guardarSalida creados en los shells cuyo borrado se lanza (sin recuento del servidor: el borrado
        // sigue en segundo plano) y si el de un ssh_reset_shell anterior no se completó.
        public LimpiezaGuardados? ReiniciarShellConLimpieza(string destinoTexto)
        {
            DestinoSsh destino = ParsearDestino(destinoTexto);
            SesionShell? sesion;
            lock (_candado)
                sesion = _sesiones.GetValueOrDefault(destino.ClaveSesion)?.Sesion;

            return sesion?.SolicitarResetConLimpieza();
        }

        // Con resolutor (la configuración de ssh: alias, HostName, User, Port, IdentityFile), el destino que se devuelve, y
        // con él la clave de la sesión, es el resuelto: un alias y su usuario@HostName:Port comparten sesión si resuelven a
        // las mismas claves.
        public DestinoSsh ParsearDestino(string destinoTexto) =>
            !DestinoSsh.TryParse(destinoTexto, opciones.UsuarioPorDefecto, out DestinoSsh destino, out bool isUsuarioExplicito, out bool isPuertoExplicito)
                ? throw new NativeSshException($"Destino '{destinoTexto}' no válido. Formato: usuario@servidor[:puerto], o [ipv6]:puerto.")
                : _resolutor?.Resolver(destino, isUsuarioExplicito, isPuertoExplicito) ?? destino;

        private static IResolutorDestino? UsarConfiguracionEnSoloLectura(DestinosSoloLectura soloLectura, IResolutorDestino? resolutor)
        {
            if (!soloLectura.IsVacio && resolutor is not null)
                soloLectura.UsarConfiguracionSsh(resolutor.HostNameDeEntrada);
            return resolutor;
        }

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

                if (destino.Alias is { } alias && !entrada.Alias.Contains(alias))
                {
                    if (entrada.Alias.Count < MaxAliasPorSesion)
                        _ = entrada.Alias.Add(alias);
                    else if (entrada.AliasOlvidados.Count < MaxAliasPorSesion)
                        _ = entrada.AliasOlvidados.Add(alias);
                    else
                        entrada.IsOlvidadosDesbordado = true;
                }

                return entrada.Sesion;
            }
        }

        private static bool IsSeleccionada(DestinoSsh destino, EntradaSesion entrada) =>
            IsDelDestino(entrada.Sesion.Destino, destino.Alias is { } alias && entrada.Alias.Contains(alias), destino);

        // El criterio de Seleccionar para una sesión (o petición) con ese destino; isConElAlias: se abrió o usó con el alias
        // pedido. Con un alias, además, el mismo usuario y puerto resueltos.
        private static bool IsDelDestino(DestinoSsh deLaSesion, bool isConElAlias, DestinoSsh pedido) =>
            pedido.Alias is null
                ? string.Equals(deLaSesion.Clave, pedido.Clave, StringComparison.Ordinal)
                : IsMismoUsuarioYPuerto(deLaSesion, pedido) && (isConElAlias || string.Equals(deLaSesion.ClaveSesion, pedido.ClaveSesion, StringComparison.Ordinal));

        // Los usuarios distinguen mayúsculas, como en ssh.
        private static bool IsMismoUsuarioYPuerto(DestinoSsh deLaSesion, DestinoSsh pedido) =>
            deLaSesion.Puerto == pedido.Puerto && string.Equals(deLaSesion.Usuario, pedido.Usuario, StringComparison.Ordinal);

        private sealed class EntradaSesion(SesionShell sesion)
        {
            public SesionShell Sesion { get; } = sesion;

            // Bajo _candado. Ordinal: ssh distingue mayúsculas en los Host.
            public HashSet<string> Alias { get; } = new(StringComparer.Ordinal);

            // Bajo _candado: alias que se usaron con la sesión ya en MaxAliasPorSesion (no se recuerdan para seleccionarla, solo
            // para avisar), también hasta MaxAliasPorSesion; pasado ese tope, IsOlvidadosDesbordado (el aviso pasa a valer para
            // cualquier alias con el mismo usuario y puerto).
            public HashSet<string> AliasOlvidados { get; } = new(StringComparer.Ordinal);

            public bool IsOlvidadosDesbordado { get; set; }
        }

        private static TimeSpan Ajustar(TimeSpan timeout) =>
            timeout < TimeoutMinimo ? TimeoutMinimo : timeout > TimeoutMaximo ? TimeoutMaximo : timeout;
    }

    // Archivos de guardarSalida de una sesión al hacer ssh_reset_shell o ssh_disconnect: Creados, los que se crearon en los
    // shells cuyo directorio se borra (alguno pudo borrarse antes, p. ej. con 'rm'); Contados, las salidas que había en el
    // servidor al borrarlas (solo al desconectar, y null si no se sabe); IsFallida, si la limpieza al desconectar no se hizo
    // o algún directorio no se pudo borrar, y Quedan y Otros, las salidas y las demás entradas que siguen en ellos (null si
    // no se sabe; un directorio que sigue sin nada dentro es que no se pudo quitar, p. ej. el base sin permiso de escritura);
    // IsSinComprobar,
    // si al desconectar no se pudo comprobar nada (no se supo el usuario, o el base no estaba o no era un directorio propio).
    // Al hacer ssh_reset_shell: IsAnteriorFallido, si el borrado de un ssh_reset_shell anterior no se completó (sus shells
    // vuelven a estar entre los que se borran), y SinBorrarAnterior, cuántas salidas quedaron entonces (null si no se sabe).
    public sealed record LimpiezaGuardados(int Creados, int? Contados, bool IsFallida)
    {
        public int? Quedan { get; init; }

        public int? Otros { get; init; }

        public bool IsSinComprobar { get; init; }

        public bool IsAnteriorFallido { get; init; }

        public int? SinBorrarAnterior { get; init; }
    }

    // Una sesión cerrada por ssh_disconnect y lo que pasó con sus archivos de guardarSalida.
    public sealed record SesionCerrada(DestinoSsh Destino, LimpiezaGuardados Limpieza);

    // Ver GestorSesiones.BuscarPistasAlias. Viejas: las sesiones del alias que la pista nombra (no vacía); IsEnElTope: las
    // de una sesión llena que no guardó el alias (si no, abiertas con él para otro usuario o puerto).
    public sealed record PistaAlias(bool IsEnElTope, IReadOnlyList<SesionShell> Viejas, IReadOnlyList<CandidatoCierre> Candidatos)
    {
        // El primer candidato que cierra justo las viejas, si hay alguno.
        public CandidatoCierre? Exacto
        {
            get
            {
                foreach (CandidatoCierre candidato in Candidatos)
                    if (candidato.IsExacto)
                        return candidato;

                return null;
            }
        }
    }

    // Un destino con que cerrar las sesiones viejas de una pista: Error (rechazado por ParsearDestino) o, si no, si alcanza
    // todas y qué más cerraría (Ademas).
    public sealed record CandidatoCierre(string Texto, string? Error, bool IsAlcanzaTodas, IReadOnlyList<SesionShell> Ademas)
    {
        public bool IsExacto => IsAlcanzaTodas && Ademas.Count == 0;
    }
}
