using System.Net;
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

        // Dirección a la que está conectado el socket (su extremo remoto), null si el adaptador no la sabe. El modo solo
        // lectura (DestinosSoloLectura) la compara con sus entradas: así el DNS de ahora no decide por una sesión ya abierta.
        IPAddress? DireccionRemota { get; }

        // CanalRechazadoException si el servidor rechaza la apertura con la conexión intacta.
        ICanalShell AbrirShell();

        // Ejecuta 'comando' tal cual en un canal exec nuevo sobre la misma conexión TCP (lo interpreta el shell de login).
        // Cancelar el token cierra el canal y lanza OperationCanceledException. La tarea termina solo cuando el canal ya
        // está cerrado: si la apertura no atiende el token (la de SSH.NET es síncrona), cuando termine y se cierre.
        // 'alConfirmarseApertura' se invoca cuando el servidor ha confirmado la apertura del canal y aceptado el comando
        // (desde entonces el comando corre en el servidor y lo acota su plazo remoto); no se invoca si no llega a pasar.
        // CanalRechazadoException si el servidor rechaza la apertura con la conexión intacta.
        Task<ResultadoCanalExec> EjecutarExecAsync(string comando, int maxBytesSalida, Action alConfirmarseApertura, CancellationToken cancellationToken);
    }

    // El servidor rechazó abrir un canal de sesión (el del shell o uno exec) con la conexión intacta: casi siempre porque
    // se alcanzó su MaxSessions. El adaptador no sabe qué ocupa esas sesiones; SesionShell la traduce a NativeSshException
    // diciendo lo que sí sabe (lecturas en curso, shells abandonados, shells cuyo cierre sigue pendiente).
    public sealed class CanalRechazadoException : Exception
    {
        public CanalRechazadoException(string mensaje) : base(mensaje)
        {
        }

        public CanalRechazadoException(string mensaje, Exception interna) : base(mensaje, interna)
        {
        }
    }

    public interface IFabricaConexion
    {
        Task<IConexionSsh> ConectarAsync(DestinoSsh destino, CancellationToken cancellationToken);
    }

    // Resolución de un destino con la configuración de ssh (alias, HostName, User, Port, IdentityFile). Lo explícito del
    // destino (usuario@, :puerto) manda sobre la configuración. Lanza NativeSshException si no se puede resolver.
    public interface IResolutorDestino
    {
        DestinoSsh Resolver(DestinoSsh destino, bool isUsuarioExplicito, bool isPuertoExplicito);

        // El HostName al que lleva una entrada de NATIVE_SSH_SOLO_LECTURA (DestinosSoloLectura.UsarConfiguracionSsh), con su
        // usuario y su puerto si los lleva; null si la configuración no la cambia o no se usa configuración. Lanza
        // NativeSshException si no se pudo leer.
        string? HostNameDeEntrada(string? usuario, string servidor, int? puerto);
    }
}
