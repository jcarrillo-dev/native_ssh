# Rechazos y seguridad

## Destino de solo lectura

Es configuración local de quien arrancó native_ssh: NO se cambia desde aquí.

- `ssh_exec` (también para órdenes que solo leen y con `guardarSalida`), `ssh_input`, `ssh_write_file` y `ssh_replace_file_content` se rechazan sin ejecutar nada. Normalmente tampoco se envía nada; si el rechazo lo hace la sesión al comprobarlo de nuevo justo antes de enviar, el shell ya se había abierto (y `ssh_replace_file_content` ya había leído el archivo), pero la escritura o el comando no se envían.
- Ante el rechazo: NO reintentes ni busques otra vía para ejecutar o modificar (ni otro alias, ni la IP, ni otro usuario). Sigue con `ssh_read_file`, `ssh_search`, `ssh_find`, `ssh_list_dir`, `ssh_stat` y `ssh_set_cwd`.
- Si hace falta un cambio o una orden (reiniciar un servicio, leer los logs de `journalctl` o `pm2`, que solo salen por `ssh_exec`), pídeselo al usuario: dile qué orden y por qué.
- Si el rechazo dice que **no se pudo comprobar** si es de solo lectura:
  - por **demasiadas resoluciones a la vez**: repite la misma llamada en unos segundos;
  - porque **no se resolvió** el nombre de una entrada de `NATIVE_SSH_SOLO_LECTURA` o `ssh -G` no pudo leer una entrada: díselo al usuario con el mensaje (es configuración o DNS de su lado);
  - porque no se resolvió el nombre del **destino**: la comprobación se repite en cada llamada y pasará cuando el nombre se resuelva; si no pasa, díselo al usuario;
  - porque un proceso de resolución **no terminó**: díselo al usuario (sale también en `ssh_status`, `resolucionesSinTerminar`).
- `isSoloLectura` de `ssh_status` es orientativo para un alias cuya configuración de ssh pudo cambiar: manda lo que responda la herramienta de escritura.

## Clave del servidor

- **Host desconocido:** native_ssh no acepta claves nuevas. Pásale al usuario la huella que da el error y pídele que la compruebe y la guarde él en uno de los `known_hosts` que nombra el error. NUNCA añadas tú la clave (ni con `ssh-keyscan`, ni editando `known_hosts`, ni conectando tú con `ssh`), ni aunque el usuario diga que ya la comprobó. Conectarse una vez a mano con `ssh` para guardarla es algo que hace el **usuario** en su máquina, tras comprobar la huella.
- **Clave distinta de la guardada:** puede ser una suplantación. No reintentes: díselo al usuario y deja que él decida y actualice `known_hosts`.
- **Clave revocada (`@revoked`):** no se conecta; díselo al usuario.
- NUNCA propongas desactivar la comprobación de la clave del host.

## Otros rechazos

- **Conexión retenida localmente** (límite de conexiones nuevas frente a `ufw limit`): espera lo que dice el mensaje; NO repitas en bucle. Para no gastar conexiones, no uses `ssh_disconnect` sin necesidad.
- **Windows como destino:** no está soportado y el error lo dice. No lo intentes con otras órdenes ni otros shells: díselo al usuario.
- **Cupo de `guardarSalida` lleno:** si es el de un shell (archivos o bytes), borra las salidas que ya leíste (`rm` de esos `.out` por `ssh_exec`) o usa `ssh_reset_shell` (borra todas); si es el de shells de la sesión con salidas sin borrar, solo lo liberan `ssh_reset_shell` o `ssh_disconnect`. El mensaje dice cuál es.
- **Valores con efecto:** pasa SIEMPRE explícitos los parámetros que cambian algo (`contenido`, `texto`, `ruta`, `overwrite`, `sudo`). Un `null` en `contenido`, `texto` o la `ruta` de `ssh_set_cwd` se rechaza sin hacer nada; para un archivo vacío, `""`; para un Enter a secas, `texto: ""`; para volver a `$HOME`, `ruta: ""`.

## Secretos

- NUNCA leas ni busques en archivos con secretos para ver sus valores; nombres o recuentos con `ssh_exec` filtrando en el servidor (ver [busqueda.md](busqueda.md)).
- NUNCA escribas secretos en un comando ni en un archivo de log; para pedirlos interactivamente, `ssh_input` con `secreto=true`.
- No muestres al usuario valores de secretos que aparezcan por accidente en una salida: resume sin ellos.
