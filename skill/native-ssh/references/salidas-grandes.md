# Salidas grandes: `guardarSalida`, `ssh_output` y «muchos pajares»

## Cuándo `guardarSalida=true`

- **Si no sabes cuánto va a salir, o puede pasar de unas pantallas: `guardarSalida=true`.** Logs, `journalctl`, consultas SQL (`psql`, `mysql`), volcados, listados de paquetes o de procesos, compilaciones verbosas, `docker logs`.
- No es por ahorrar contexto: la salida en memoria se corta (si pasa del tope por comando de la descripción de `ssh_exec`, se pierde su principio) y llega mezclada; en el archivo no se pierde nada y se puede releer y buscar las veces que haga falta.
- NUNCA recortes una tabla ancha con `cut` ni la salida con `head` para que quepa: guárdala y léela por tramos con `ssh_read_file` (`lineaInicio`/`lineaFin`).
- NO uses `guardarSalida` con comandos interactivos: lo que pregunte el comando va al archivo y no lo verás.
- Con `guardarSalida=false` y `hasMasSalida=true`, pagina con `ssh_output` desde `siguienteByte`. Si `hasSalidaDescartada=true`, ya perdiste el principio: repite con `guardarSalida=true`.

## Después de guardar

1. Lee el control: `archivo`, `lineas` y `bytes`. La vista previa son solo las 5 primeras líneas (ninguna si antes enviaste un secreto con `ssh_input`). Si la respuesta llega en `Ejecutando` o `Encolado`, aún no hay `lineas` ni `bytes` (y en `Encolado`, tampoco `archivo`): espera con `ssh_output` y su `id`, que los da al terminar.
2. Busca lo que te interesa con `ssh_search` (`ruta` = `archivo`) y lee alrededor con `ssh_read_file` (`lineaInicio`/`lineaFin` en torno a la línea hallada) o el final con `lineaInicio` negativo.
3. NUNCA vuelques el archivo entero con `cat` ni lo leas entero de una vez si es largo.

## «Muchos pajares»: varias salidas, una búsqueda

Para buscar lo mismo en varias salidas (los logs de varios servicios, varias consultas):

1. Lanza cada comando con `guardarSalida=true` y **anota `id → comando`** (el nombre del `.out` es el `id` de la petición). Van en cola por el mismo shell: si una respuesta llega sin `archivo`, espera con `ssh_output` antes de buscar.
2. Un solo `ssh_search` con `ruta` = el **directorio del shell** (el directorio de `archivo`, `/tmp/native_ssh-<usuario>/<id del shell>/`) busca en todas a la vez. Cada coincidencia lleva el `.out`: cásalo con el comando que anotaste.
3. Lee alrededor de cada coincidencia con `ssh_read_file` (`lineaInicio` = la línea hallada menos 20, nunca menor que 1; un valor negativo significa «las últimas N líneas» y no admite `lineaFin`).
4. NO llames a `ssh_reset_shell` ni a `ssh_disconnect` hasta terminar: borran los `.out`.

Avisos:

- NUNCA busques en el directorio base `/tmp/native_ssh-<usuario>/`: tiene también los shells de otras sesiones del mismo usuario (otras conversaciones, restos de menos de 24 h) y mezclarías contextos.
- Si el shell se recreó entre medias (plazo agotado, `ssh_cancel`, caída: el aviso «Shell remoto recreado…»), las salidas anteriores siguen en el directorio del shell VIEJO y las nuevas van a otro: busca en cada directorio o archivo por archivo.
- `ssh_search` devuelve como mucho `maxResultados` coincidencias **en total** (no por archivo): si sale `[Límite alcanzado: …]`, afina el patrón o busca archivo por archivo.
- Un `.out` con algún byte NUL (salida binaria) se SALTA al buscar en el directorio (la respuesta avisa de que puede pasar, sin decir cuál). Si una salida puede ser binaria, búscala por la ruta de su `.out` o léela con `ssh_read_file`.
- Hay un cupo de salidas guardadas por shell (lo da la descripción de `guardarSalida`); por encima, el comando no se ejecuta y la respuesta dice cómo liberar sitio.
- En un destino de solo lectura no hay `guardarSalida` (`ssh_exec` se rechaza entero): ver [rechazos-y-seguridad.md](rechazos-y-seguridad.md).
