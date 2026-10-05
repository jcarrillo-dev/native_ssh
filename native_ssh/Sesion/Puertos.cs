using NativeSsh.Comun;

namespace NativeSsh.Sesion
{
    // Canal de shell interactivo. Leer bloquea hasta que hay datos y devuelve 0 al cerrarse.
    public interface ICanalShell : IDisposable
    {
        int Leer(Span<byte> destino);

        void Escribir(ReadOnlySpan<byte> datos);
    }

    // Resultado de un canal exec: stdout y stderr separados, sin PTY. CodigoSalida es null si el proceso terminó por
    // una señal (SenalSalida) o el servidor no envió código. HasExcedidoTope: stdout+stderr superaron el tope, se cerró
    // el canal y la salida no está completa (no debe usarse).
    public sealed record ResultadoCanalExec(int? CodigoSalida, string? SenalSalida, ReadOnlyMemory<byte> Salida, ReadOnlyMemory<byte> Errores, bool HasExcedidoTope);

    public interface IConexionSsh : IDisposable
    {
        bool IsConectada { get; }

        ICanalShell AbrirShell();

        // Ejecuta 'comando' tal cual en un canal exec nuevo sobre la misma conexión TCP (lo interpreta el shell de login).
        // Cancelar el token cierra el canal y lanza OperationCanceledException.
        Task<ResultadoCanalExec> EjecutarExecAsync(string comando, int maxBytesSalida, CancellationToken cancellationToken);
    }

    public interface IFabricaConexion
    {
        Task<IConexionSsh> ConectarAsync(DestinoSsh destino, CancellationToken cancellationToken);
    }
}
