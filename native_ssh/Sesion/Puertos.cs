using NativeSsh.Comun;

namespace NativeSsh.Sesion
{
    // Canal de shell interactivo. Leer bloquea hasta que hay datos y devuelve 0 al cerrarse.
    public interface ICanalShell : IDisposable
    {
        int Leer(Span<byte> destino);

        void Escribir(ReadOnlySpan<byte> datos);
    }

    public interface IConexionSsh : IDisposable
    {
        bool IsConectada { get; }

        ICanalShell AbrirShell();
    }

    public interface IFabricaConexion
    {
        Task<IConexionSsh> ConectarAsync(DestinoSsh destino, CancellationToken cancellationToken);
    }
}
