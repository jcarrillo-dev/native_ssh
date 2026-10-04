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

        [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "--http-unsecure: MCP escuchando en todas las interfaces (puerto {Puerto}) SIN autenticación. Cualquiera que alcance el puerto puede ejecutar comandos en tus servidores.")]
        public static partial void HttpInseguro(this ILogger logger, int puerto);

        [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "MCP escuchando en http://localhost:{Puerto}/mcp (solo loopback).")]
        public static partial void HttpLoopback(this ILogger logger, int puerto);
    }
}
