using NativeSsh.Comun;

namespace NativeSsh.Herramientas
{
    // Modo solo lectura por destino (CI6, DestinosSoloLectura): ssh_exec, ssh_input, ssh_write_file y ssh_replace_file_content
    // lo comprueban antes de nada (sin conectar ni enviar nada) y lo rechazan con un mensaje que dice qué sí se puede usar y
    // que el cliente no lo puede cambiar. Se compara el texto pedido (alias), el destino resuelto y, si la sesión ya está
    // conectada, la dirección de su conexión. Si no se pudo comprobar (un nombre que no se resolvió), también se rechaza y
    // el mensaje lo dice. La sesión repite la comparación de la dirección justo antes de enviar la escritura (SesionShell).
    public sealed partial class HerramientasSsh
    {
        private async Task ExigirEscrituraPermitidaAsync(DestinoSsh destino, string herramienta, CancellationToken cancellationToken)
        {
            if (opciones.SoloLectura.IsVacio)
                return;

            ComprobacionSoloLectura comprobacion = await opciones.SoloLectura.ComprobarAsync(destino, gestor.ExtremoDe(destino), cancellationToken).ConfigureAwait(false);
            if (DestinosSoloLectura.MensajeRechazo(comprobacion, destino.Clave, herramienta) is { } rechazo)
                throw new NativeSshException(rechazo);
        }

        internal static string MensajeSoloLecturaNoComprobado(string clave, string herramienta, string noResuelto, bool isCupoLleno = false) =>
            DestinosSoloLectura.MensajeNoComprobado(clave, herramienta, noResuelto, isCupoLleno);

        internal static string MensajeSoloLectura(string clave, string herramienta) => DestinosSoloLectura.MensajeSoloLectura(clave, herramienta);
    }
}
