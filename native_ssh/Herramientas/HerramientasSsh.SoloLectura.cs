using NativeSsh.Comun;

namespace NativeSsh.Herramientas
{
    // Modo solo lectura por destino (CI6, DestinosSoloLectura): ssh_exec, ssh_input, ssh_write_file y ssh_replace_file_content
    // lo comprueban antes de nada (sin conectar ni enviar nada) y lo rechazan con un mensaje que dice qué sí se puede usar y
    // que el LLM no lo puede cambiar. Si no se pudo comprobar (un nombre que no se resolvió), también se rechaza y el mensaje
    // lo dice.
    public sealed partial class HerramientasSsh
    {
        private async Task ExigirEscrituraPermitidaAsync(DestinoSsh destino, string herramienta, CancellationToken cancellationToken)
        {
            ComprobacionSoloLectura comprobacion = await opciones.SoloLectura.ComprobarAsync(destino, cancellationToken).ConfigureAwait(false);
            if (comprobacion.NoResuelto is { } noResuelto)
                throw new NativeSshException(MensajeSoloLecturaNoComprobado(destino.Clave, herramienta, noResuelto, comprobacion.IsCupoLleno));

            if (comprobacion.IsSoloLectura)
                throw new NativeSshException(MensajeSoloLectura(destino.Clave, herramienta));
        }

        // isCupoLleno: el nombre no se llegó a resolver (demasiadas resoluciones a la vez), no es que el DNS fallara.
        internal static string MensajeSoloLecturaNoComprobado(string clave, string herramienta, string noResuelto, bool isCupoLleno = false)
        {
            string causa = isCupoLleno ? $"no se llegó a resolver {noResuelto}: demasiadas resoluciones a la vez" : $"no se resolvió {noResuelto}";
            string cuando = isCupoLleno ? "vuelve a intentarlo en unos segundos, cuando terminen las resoluciones en curso; la comprobación se repite en cada llamada." : "la comprobación se repite en cada llamada y pasará cuando el nombre se resuelva (DNS o /etc/hosts).";
            return $"Rechazado: {clave}: no se pudo comprobar si es de solo lectura ({causa}); se rechaza por seguridad (configuración local de native_ssh, variable {DestinosSoloLectura.Variable}), así que {herramienta} no se ha ejecutado y no se ha enviado nada al servidor. ssh_read_file, ssh_search, ssh_find, ssh_list_dir, ssh_stat y ssh_set_cwd siguen disponibles. No busques otra vía para modificar el servidor: {cuando}";
        }

        internal static string MensajeSoloLectura(string clave, string herramienta) =>
            $"Rechazado: {clave} está en modo solo lectura (configuración local de native_ssh, variable {DestinosSoloLectura.Variable}), así que {herramienta} no se ha ejecutado y no se ha enviado nada al servidor. Para ese destino están desactivadas ssh_exec, ssh_input, ssh_write_file y ssh_replace_file_content: usa ssh_read_file, ssh_search, ssh_find, ssh_list_dir, ssh_stat y ssh_set_cwd. No reintentes ni busques otra vía para modificar el servidor: este modo solo lo puede quitar quien configura native_ssh.";
    }
}
