using ModelContextProtocol;

namespace NativeSsh.Comun
{
    /// Error esperado cuyo mensaje se devuelve tal cual al cliente MCP. No debe contener secretos.
    public sealed class NativeSshException : McpException
    {
        public NativeSshException(string mensaje) : base(mensaje)
        {
        }

        public NativeSshException(string mensaje, Exception interna) : base(mensaje, interna)
        {
        }
    }
}
