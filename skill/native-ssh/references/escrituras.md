# Escrituras y ediciones

## Elegir la herramienta

- Cambiar parte de un archivo: `ssh_replace_file_content`. Varias partes del mismo archivo: UNA llamada con `ediciones` (en orden, cada una sobre el resultado de la anterior, todas o ninguna). NUNCA varias llamadas seguidas recalculando números de línea.
- Archivo nuevo o reescrito entero: `ssh_write_file` (`overwrite=true` para sustituir uno que existe; `append=true` para anexar).
- NUNCA `sed -i`, `perl -i`, `echo >`, `printf >`, `tee` ni heredocs por `ssh_exec` para escribir o editar (únicas excepciones: `sed -i` o similar para un archivo mayor que el límite o que mezcla saltos CRLF y LF, abajo).

## Antes de editar

- Lee el trozo que vas a cambiar con `ssh_read_file` y copia `targetContent` EXACTO (espacios, tabuladores e indentación incluidos). No hace falta `ssh_stat` antes: `ssh_read_file` ya acota y dice si no existe.
- Si `targetContent` puede aparecer más de una vez, añade contexto (las líneas de alrededor) hasta que sea único, o acota con `lineaInicio`/`lineaFin`. `allowMultiple=true` SOLO si de verdad quieres cambiarlas todas.
- Si el error dice que el archivo cambió desde su lectura, vuelve a leer y repite: otro proceso lo tocó.
- Si dice que hay otra escritura en curso (candado), espera a que termine y reintenta; NO busques otra vía para escribir.

## Archivos mayores que el límite fijo de `ssh_replace_file_content`

Solo dos vías, y ninguna más:

1. Escribirlo entero por partes con `ssh_write_file`: la primera con `overwrite=true` y las demás con `append=true` (no es atómico: un fallo a mitad deja el archivo a medias).
2. Editarlo con herramientas del servidor por `ssh_exec`, como `sed -i`. Saldrá un aviso que sugiere `ssh_replace_file_content`: para un archivo mayor que su límite, ignóralo.

NUNCA con `echo >`, `tee` ni heredocs.

## Archivos del sistema y sudo

- Archivos de root: `sudo=true` en la propia herramienta (no `sudo tee` por `ssh_exec`). Haz antes `sudo -v` (ver [esperas-entrada-y-cancelacion.md](esperas-entrada-y-cancelacion.md)).
- Tras editar una configuración, valídala con la herramienta del servicio antes de recargar (`nginx -t`, `sshd -t`, `systemd-analyze verify`, `visudo -c -f`), y solo entonces `systemctl reload`/`restart`.
- Si la respuesta dice `isAtomica: false` o trae un aviso «ATENCIÓN» (permisos especiales o capacidades que no se pudieron devolver), díselo al usuario.
- Si una escritura termina con error por plazo o cancelación, el mensaje dice en qué estado quedó el archivo y dónde están la copia y el temporal: léelo antes de reintentar y no borres esas copias sin decírselo al usuario.

## No escribas para leer

- Para ver una salida larga no la redirijas a un archivo tuyo: `guardarSalida=true` ya lo hace, con limpieza automática.
- Archivos temporales propios: en `/tmp`, con nombre único, y bórralos al terminar (y dile al usuario cuáles quedan si no puedes).

## Archivos que mezclan saltos CRLF y LF

`ssh_replace_file_content` no los edita (lo dice su error, con cuántos saltos hay de cada tipo). Dos vías:

1. Reescribirlo entero con `ssh_write_file`, con el salto que deba llevar cada línea.
2. Normalizar sus saltos con herramientas del servidor por `ssh_exec` (p. ej. `sed`), sabiendo que eso cambia los saltos de todo el archivo y puede quitar un `\r` que estuviera a propósito: díselo al usuario antes. Saldrá un aviso que sugiere `ssh_replace_file_content`: en este caso, ignóralo. Después ya puedes editarlo con `ssh_replace_file_content`.
