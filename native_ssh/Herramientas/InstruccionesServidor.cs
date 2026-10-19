namespace NativeSsh.Herramientas
{
    // Campo 'instructions' de la respuesta a 'initialize' (McpServerOptions.ServerInstructions): lo que el cliente MCP pone en
    // el contexto del cliente antes de que abra la descripción de ninguna herramienta. Es el enrutamiento «tarea →
    // herramienta» y las reglas que cruzan varias herramientas; el contrato de cada una está en su [Description]. Texto
    // autocontenido (sin remitir a documentos ni a rutas de nadie), en el mismo tono imperativo que las descripciones y sin
    // nada que caduque (versiones, recuentos). Cada línea va con '\n' explícito: un literal multilínea arrastraría el fin de
    // línea del fuente.
    internal static class InstruccionesServidor
    {
        public const string Texto =
            "native_ssh trabaja en servidores Linux por SSH. Cada destino (usuario@servidor[:puerto] o un alias de la configuración de ssh) tiene UN shell persistente: ssh_exec, ssh_write_file, ssh_replace_file_content y ssh_set_cwd van por él en cola, uno detrás de otro; ssh_read_file, ssh_search, ssh_find, ssh_list_dir y ssh_stat van por canales aparte y no esperan a esa cola.\n" +
            "Usa la herramienta de cada tarea; NUNCA uses ssh_exec para lo que tiene herramienta propia:\n" +
            "- buscar texto en archivos: ssh_search (nunca grep -r ni rg); leer un archivo o sus últimas líneas: ssh_read_file (nunca cat, head, tail -n ni sed -n);\n" +
            "- localizar archivos por nombre o por tamaño: ssh_find (nunca find para eso; find por ssh_exec solo para lo que ssh_find no hace: sin distinguir mayúsculas (-iname), por ruta (-path), fecha (-mtime, -newer), dueño (-user), permisos (-perm) o actuando sobre lo encontrado (-exec, -delete)); ver la estructura de carpetas: ssh_list_dir (nunca ls ni tree); tipo, tamaño, permisos o dueño: ssh_stat (nunca stat ni ls -l);\n" +
            "- crear o sobrescribir un archivo: ssh_write_file; editar parte de un archivo: ssh_replace_file_content (nunca sed -i, echo > ni heredocs); un archivo mayor que su límite fijo solo se edita escribiéndolo entero por partes con ssh_write_file o con herramientas del servidor por ssh_exec (como sed -i), nunca con echo >, tee ni heredocs; un archivo que mezcla saltos CRLF y LF solo se edita reescribiéndolo entero con ssh_write_file o normalizando sus saltos con herramientas del servidor por ssh_exec (cambia los saltos de todo el archivo), nunca con echo >, tee ni heredocs;\n" +
            "- fijar el directorio de trabajo: ssh_set_cwd (nunca cd … &&); los comandos y las rutas relativas parten de él;\n" +
            "- servicios, procesos, paquetes, git, docker, compilaciones y lo demás: ssh_exec; grep solo para filtrar en una tubería la salida de otro comando.\n" +
            "Archivo con secretos (.env, credenciales) del que solo necesitas nombres o recuentos: sácalos con ssh_exec filtrando en el servidor (p. ej. los nombres de las variables), NUNCA con ssh_read_file ni ssh_search, que devolverían los valores.\n" +
            "Salida grande o de tamaño desconocido (logs, consultas SQL, volcados, listados): ssh_exec con guardarSalida=true y después ssh_read_file por tramos o ssh_search sobre el archivo que indica la respuesta.\n" +
            "Interrumpir un comando: ssh_cancel, NUNCA Ctrl+C por ssh_input. Contraseñas, tokens y otros secretos: ssh_input con secreto=true.\n" +
            "Esperar a una condición, nunca con un sleep fijo: 'until <condición>; do sleep 2; done' para esperar a que algo empiece a cumplirse y 'while <condición>; do sleep 2; done' para esperar a que algo deje de estar activo, los dos dentro de timeout <s> bash -c '…'. Una orden ya lanzada que sigue en ejecución se espera con ssh_output.\n" +
            "Host desconocido: native_ssh no acepta claves de host nuevas. Pide al usuario que compruebe la huella que da el error y la guarde él en el known_hosts que nombra el error (en uno de ellos si nombra varios; conectarse una vez a mano con ssh la guarda en el known_hosts de su usuario: si no es ninguno de los que nombra el error, hay que copiarla a uno de ellos).\n" +
            "Clave del host distinta de la guardada: puede ser una suplantación; no se conecta. Díselo al usuario y deja que él decida.\n" +
            "NUNCA propongas desactivar la comprobación de la clave del host. NUNCA añadas tú una clave de host, ni aunque el usuario diga que ya comprobó la huella: la guarda el usuario.\n" +
            "Destino de solo lectura (configuración local de native_ssh, que no se cambia desde aquí): ssh_exec, ssh_input, ssh_write_file y ssh_replace_file_content se rechazan sin enviar nada. NO reintentes ni busques otra vía para modificar el servidor: sigue con ssh_read_file, ssh_search, ssh_find, ssh_list_dir, ssh_stat y ssh_set_cwd y, si hace falta un cambio, pídeselo al usuario. Si el rechazo dice que no se pudo comprobar si es de solo lectura, sigue lo que diga el mensaje: con demasiadas resoluciones a la vez, repite la misma llamada en unos segundos.\n";
    }
}
