using NativeSsh.Comun;

namespace NativeSsh.Herramientas
{
    // Modo solo lectura por destino (CI6, DestinosSoloLectura): ssh_exec, ssh_input, ssh_write_file y ssh_replace_file_content
    // lo comprueban antes de nada (sin conectar ni enviar nada) y lo rechazan con un mensaje que dice qué sí se puede usar y
    // que el LLM no lo puede cambiar.
    public sealed partial class HerramientasSsh
    {
        private async Task ExigirEscrituraPermitidaAsync(DestinoSsh destino, string herramienta, CancellationToken cancellationToken)
        {
            if (await opciones.SoloLectura.IsSoloLecturaAsync(destino, cancellationToken).ConfigureAwait(false))
                throw new NativeSshException(MensajeSoloLectura(destino.Clave, herramienta));
        }

        internal static string MensajeSoloLectura(string clave, string herramienta) =>
            $"Rechazado: {clave} está en modo solo lectura (configuración local de native_ssh, variable {DestinosSoloLectura.Variable}), así que {herramienta} no se ha ejecutado y no se ha enviado nada al servidor. Para ese destino están desactivadas ssh_exec, ssh_input, ssh_write_file y ssh_replace_file_content: usa ssh_read_file, ssh_search, ssh_find, ssh_list_dir, ssh_stat y ssh_set_cwd. No reintentes ni busques otra vía para modificar el servidor: este modo solo lo puede quitar quien configura native_ssh.";
    }
}
