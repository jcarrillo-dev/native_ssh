# Esperas, entrada por terminal, sudo y cancelación

## Esperar a una condición

- NUNCA esperes con un `sleep` fijo seguido de una comprobación (`sleep 60; systemctl status app`): o esperas de más o compruebas antes de tiempo. Una pausa corta (menos de 10 s, p. ej. `sleep 2 && systemctl status app` tras un reinicio) sí vale.
- Para esperar a que algo **empiece** a cumplirse (un puerto que escucha, un archivo que aparece, un servicio activo): `timeout 120 bash -c 'until ss -ltn | grep -q ":8080 "; do sleep 2; done'`.
- Para esperar a que algo **deje** de estar activo (un proceso, un archivo de bloqueo): `timeout 120 bash -c 'while pgrep -x apt >/dev/null; do sleep 2; done'`.
- El `bash -c` nuevo NO ve las variables del shell que no exportaste: pásalas exportadas o escribe el valor dentro de las comillas.
- Para un comando YA lanzado que sigue en `Ejecutando`, espera con `ssh_output` (con su `esperarSegundos`), no con otro `ssh_exec` con `sleep`.
- Un comando largo (compilación, despliegue) lánzalo con `timeoutSegundos` holgado (por encima de lo que esperas que tarde, dentro del rango de su descripción) y `esperarSegundos` corto (p. ej. 10–20); mientras sigue, puedes LEER con las herramientas de lectura (no esperan a la cola), pero NO escribir ni lanzar otro `ssh_exec` en ese destino: esperarían detrás.
- Seguir un log en vivo (`tail -f`, `journalctl -f`) SOLO con plazo corto (`timeout 20 tail -f app.log`); para revisar un log, mejor `guardarSalida=true` o `ssh_read_file` con `lineaInicio` negativo.

## `ssh_input`

- Envía entrada SOLO cuando el comando la está pidiendo: mira antes su salida con `ssh_output`. UNA línea por llamada.
- Contraseñas, tokens y cualquier secreto: SIEMPRE `secreto=true`. Un secreto no puede llevar caracteres de control (ni saltos de línea en medio).
- Lo que el comando no lee se descarta sin ejecutarse: si el `aviso` del siguiente `ssh_exec` dice que se descartaron líneas, esa entrada se perdió; si el comando la necesitaba, vuelve a lanzarlo y envíala cuando la pida. Ese aviso sale una sola vez, en la siguiente respuesta de `ssh_exec`, `ssh_input`, `ssh_output` o `ssh_cancel` de ese shell, o en el error de una herramienta de archivo o de `ssh_set_cwd` que no terminó (interrumpida, cancelada o sin completar); no en las que terminan bien: no lo esperes otra vez.
- NUNCA intentes interrumpir con Ctrl+C, Ctrl+\ o Ctrl+Z por `ssh_input`: se rechazan. Con `enter=false`, el texto tampoco puede acabar en Ctrl+V. Para salir de un intérprete interactivo (`python`, `psql`), envía su orden de salida (`exit()`, `\q`) con `ssh_input` o termina el comando con `ssh_cancel`.
- Mejor que un programa interactivo, su forma no interactiva: `psql -c '…'`, `apt-get -y`, `DEBIAN_FRONTEND=noninteractive`, `yes | …` solo si sabes qué confirma.

## sudo

- Antes de una tanda con sudo: `ssh_exec` con `sudo -v`; si pide contraseña, `ssh_input` con `secreto=true`. Así las órdenes siguientes no la vuelven a pedir durante el tiempo de gracia de sudo.
- Antes de escribir con `sudo=true` (`ssh_write_file`, `ssh_replace_file_content`), haz SIEMPRE ese `sudo -v`: la escritura no te enseña la pregunta de la contraseña y, si sudo la pide, se queda esperando hasta su plazo y falla. (Se puede responder con `ssh_input` al `id` interno que muestra `ssh_status`, pero su respuesta no enseña la salida de esa petición.)
- NUNCA pongas una contraseña dentro del comando (`echo pass | sudo -S …`): queda en el historial y en la salida.

## Cancelar y recuperar

- `ssh_cancel` con el `id`: si está encolado, no llega a ejecutarse; si está en ejecución, Ctrl+C y, si no responde, se cierra el shell (se pierden las variables; el directorio de trabajo y las salidas guardadas se conservan).
- NUNCA `exec bash`, `exec su`, `exec zsh` ni ningún otro `exec` en `ssh_exec`: sustituye al shell de la sesión, que deja de responder; ese comando no termina y todo lo que venga detrás para ese destino espera hasta su plazo. Si ya pasó, `ssh_cancel` con su `id` (o un `timeoutSegundos` corto) cierra ese shell y el siguiente comando abre uno nuevo.
- Tras «Shell remoto recreado…» en un `aviso` (o después de tu propio `ssh_reset_shell`), vuelve a exportar las variables que necesites (y relanza lo que estuviera en segundo plano si hacía falta). El directorio de `ssh_set_cwd` ya se conserva.
- Si el shell no llega a abrirse porque el login no termina (un `.bashrc` con un `read`, un `sleep` o un `exit`): las lecturas siguen funcionando; revisa el perfil con `ssh_read_file` y, para arreglarlo, `ssh_reset_shell` con `sinPerfil=true` y edita con `ssh_replace_file_content`. Después, `ssh_reset_shell` sin `sinPerfil` para volver al login normal.
