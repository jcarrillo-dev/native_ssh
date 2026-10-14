using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using NativeSsh.Comun;
using NativeSsh.Sesion;

namespace NativeSsh.Test.Utilidades
{
    // Decorador de una fábrica real: anota los bytes (stdout + stderr) que llegan por cada canal exec.
    public sealed class FabricaMedidora(IFabricaConexion interna) : IFabricaConexion
    {
        public ConcurrentQueue<(string Comando, int Bytes)> Exec { get; } = new();

        public async Task<IConexionSsh> ConectarAsync(DestinoSsh destino, CancellationToken cancellationToken) =>
            new ConexionMedida(await interna.ConectarAsync(destino, cancellationToken), Exec);

        public int UltimosBytes(string fragmentoScript) =>
            Exec.Last(e => ScriptExec.Decodificar(e.Comando).Contains(fragmentoScript, StringComparison.Ordinal)).Bytes;

        private sealed class ConexionMedida(IConexionSsh interna, ConcurrentQueue<(string, int)> exec) : IConexionSsh
        {
            public bool IsConectada => interna.IsConectada;

            public IPAddress? DireccionRemota => interna.DireccionRemota;
            public bool IsServidorWindows => interna.IsServidorWindows;

            public ICanalShell AbrirShell(bool isSinPerfil) => interna.AbrirShell(isSinPerfil);

            public async Task<ResultadoCanalExec> EjecutarExecAsync(string comando, int maxBytesSalida, Action alConfirmarseApertura, CancellationToken cancellationToken)
            {
                ResultadoCanalExec resultado = await interna.EjecutarExecAsync(comando, maxBytesSalida, alConfirmarseApertura, cancellationToken);
                exec.Enqueue((comando, resultado.Salida.Length + resultado.Errores.Length));
                return resultado;
            }

            public void Dispose() => interna.Dispose();
        }
    }
}
