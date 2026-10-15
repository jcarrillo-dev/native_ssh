namespace NativeSsh.Comun
{
    // Mensajes de log generados en compilación: sin boxing de argumentos ni plantillas parseadas en ejecución,
    // y sin coste cuando el nivel está deshabilitado (CA1848/CA1873).
    internal static partial class Registro
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Desconectando {Destino}.")]
        public static partial void Desconectando(this ILogger logger, string destino);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Conectado a {Destino}.")]
        public static partial void Conectado(this ILogger logger, string destino);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "No se pudo cargar la clave {Ruta}: {Motivo}")]
        public static partial void ClaveNoCargada(this ILogger logger, string ruta, string motivo);

        [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "El bucle de {Destino} terminó con error al cerrar.")]
        public static partial void BucleTerminadoConError(this ILogger logger, Exception ex, string destino);

        [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Comando interrumpido por el cierre de {Destino}.")]
        public static partial void ComandoInterrumpidoPorCierre(this ILogger logger, Exception ex, string destino);

        [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Error inesperado ejecutando un comando en {Destino}.")]
        public static partial void ErrorInesperado(this ILogger logger, Exception ex, string destino);

        [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "La lectura del shell de {Destino} terminó con error.")]
        public static partial void LecturaTerminadaConError(this ILogger logger, Exception ex, string destino);

        [LoggerMessage(EventId = 8, Level = LogLevel.Debug, Message = "Error cerrando el canal de {Destino}.")]
        public static partial void ErrorCerrandoCanal(this ILogger logger, Exception ex, string destino);

        [LoggerMessage(EventId = 9, Level = LogLevel.Debug, Message = "Escritura fallida en el shell de {Destino}.")]
        public static partial void EscrituraFallida(this ILogger logger, Exception ex, string destino);

        [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "--http-unsecure: MCP escuchando en todas las interfaces (puerto {Puerto}), con token pero SIN cifrar (HTTP, no HTTPS): el token y todo lo que se envía (comandos, contenido de archivos, contraseñas de ssh_input) viajan en claro por la red. Úsalo solo en una red de confianza o detrás de un proxy con TLS.")]
        public static partial void HttpInseguro(this ILogger logger, int puerto);

        [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "MCP escuchando en http://localhost:{Puerto}/mcp (solo loopback, con token).")]
        public static partial void HttpLoopback(this ILogger logger, int puerto);

        [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "Petición HTTP rechazada desde {Origen}: {Motivo}.")]
        public static partial void PeticionHttpRechazada(this ILogger logger, string origen, string motivo);

        [LoggerMessage(EventId = 14, Level = LogLevel.Warning, Message = "Se omitieron {Omitidas} avisos de peticiones HTTP rechazadas (como mucho {Maximo} por minuto).")]
        public static partial void PeticionesHttpRechazadasOmitidas(this ILogger logger, int omitidas, int maximo);

        [LoggerMessage(EventId = 12, Level = LogLevel.Debug, Message = "Error liberando una conexión caída con {Destino}.")]
        public static partial void ErrorLiberandoConexion(this ILogger logger, Exception ex, string destino);

        [LoggerMessage(EventId = 30, Level = LogLevel.Debug, Message = "Un canal exec de {Destino} abandonado al agotarse su plazo (o cancelarse) terminó de cerrarse.")]
        public static partial void CanalExecCerradoTrasAbandonarlo(this ILogger logger, Exception ex, string destino);

        [LoggerMessage(EventId = 31, Level = LogLevel.Warning, Message = "Se descarta el PATH del shell de login de {Destino}: {Motivo}. Los canales exec usarán el PATH por defecto de sshd.")]
        public static partial void PathLoginDescartado(this ILogger logger, string destino, string motivo);

        [LoggerMessage(EventId = 32, Level = LogLevel.Debug, Message = "Se descarta la conexión caída con {Destino} (sustituida por una nueva) y, si era suyo, el canal del shell.")]
        public static partial void ConexionDescartada(this ILogger logger, string destino);

        [LoggerMessage(EventId = 40, Level = LogLevel.Debug, Message = "Cerrando la sesión con {Destino}: no se admiten más comandos ni lecturas.")]
        public static partial void CerrandoSesion(this ILogger logger, string destino);

        [LoggerMessage(EventId = 41, Level = LogLevel.Debug, Message = "Terminó una apertura del shell de {Destino} que ya nadie esperaba (conexión sustituida o sesión cerrada); si abrió el canal, se cierra.")]
        public static partial void AperturaShellAbandonada(this ILogger logger, Exception? ex, string destino);

        [LoggerMessage(EventId = 42, Level = LogLevel.Debug, Message = "Al cerrar la sesión con {Destino} el bucle de comandos sigue dentro de una escritura que el servidor no acepta: se libera la conexión para desbloquearla.")]
        public static partial void BucleBloqueadoAlCerrar(this ILogger logger, string destino);

        [LoggerMessage(EventId = 43, Level = LogLevel.Debug, Message = "Se cierra el canal del shell de {Destino} (lo cerró el servidor, cayó la conexión o se descarta): no se escribe nada más en él.")]
        public static partial void CanalShellCerrado(this ILogger logger, string destino);

        [LoggerMessage(EventId = 60, Level = LogLevel.Warning, Message = "No se envió Ctrl+C al shell de {Destino}: otra escritura (una entrada) sigue dentro de un envío que el servidor no acepta. El proceso sigue en ejecución.")]
        public static partial void InterrupcionSinCandadoEscritura(this ILogger logger, string destino);

        [LoggerMessage(EventId = 61, Level = LogLevel.Warning, Message = "Se abandona el canal del shell de {Destino} con una entrada atascada dentro: no se escribe nada más en él, el siguiente comando abre un shell nuevo y el viejo se cierra cuando termine esa escritura.")]
        public static partial void CanalShellAbandonado(this ILogger logger, string destino);

        [LoggerMessage(EventId = 62, Level = LogLevel.Debug, Message = "Terminó la escritura del canal del shell abandonado de {Destino}: se cierra.")]
        public static partial void CanalShellAbandonadoCerrado(this ILogger logger, string destino);

        [LoggerMessage(EventId = 63, Level = LogLevel.Warning, Message = "Una línea del shell de {Destino} la ejecutó otro shell (uno interactivo anidado que dejó el comando: sudo -i, su -, ssh a otra máquina, bash -i): se cierra el canal del shell para que los comandos siguientes no se ejecuten dentro.")]
        public static partial void ShellAnidadoDetectado(this ILogger logger, string destino);

        [LoggerMessage(EventId = 64, Level = LogLevel.Warning, Message = "No se pudieron borrar en {Destino} los archivos de salida guardados (guardarSalida) de {Shells} shell(s); quedan en /tmp/native_ssh-<usuario> hasta el barrido de 24 h.")]
        public static partial void LimpiezaGuardadosFallida(this ILogger logger, Exception ex, string destino, int shells);

        [LoggerMessage(EventId = 65, Level = LogLevel.Warning, Message = "El borrado en {Destino} de los archivos de salida guardados (guardarSalida) de {Shells} shell(s) terminó con código {Codigo}: los que siguen vuelven a la lista y se reintentan con el siguiente ssh_reset_shell o al cerrar.")]
        public static partial void LimpiezaGuardadosIncompleta(this ILogger logger, string destino, int shells, int? codigo);

        [LoggerMessage(EventId = 66, Level = LogLevel.Warning, Message = "No se encontró el programa '{Binario}': los destinos se usan tal cual, sin la configuración de ssh (alias, HostName, User, Port e IdentityFile de ~/.ssh/config). Instala el cliente de OpenSSH y reinicia native_ssh para usarla.")]
        public static partial void SshNoEncontrado(this ILogger logger, string binario);

        [LoggerMessage(EventId = 67, Level = LogLevel.Debug, Message = "'{Servidor}' resuelto con la configuración de ssh: {Destino} ({Claves}).")]
        public static partial void DestinoResuelto(this ILogger logger, string servidor, string destino, string claves);

        [LoggerMessage(EventId = 68, Level = LogLevel.Warning, Message = "No se pudo meter 'ssh -G' de '{Servidor}' en un job object propio (error {Error} de Windows): si se agota el plazo, se mata su árbol de procesos, que no alcanza lo que lanzó un Match exec cuyo padre ya terminó.")]
        public static partial void GrupoSshNoAsignado(this ILogger logger, string servidor, int error);
    }
}
