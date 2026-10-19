# Receta: diagnosticar un servicio caído y arreglar su configuración

Tarea: «la API de `app@prod` no responde; mira qué pasa y arréglalo». Todas las llamadas llevan `destino="app@prod"`.

1. **Estado y logs, guardados** (dos `ssh_exec`; van en cola por el mismo shell):
   - `ssh_exec(destino="app@prod", comando="systemctl status api --no-pager", guardarSalida=true)`
   - `ssh_exec(destino="app@prod", comando="journalctl -u api --since '-1h' --no-pager", guardarSalida=true)`
   Anota `id → comando` de cada respuesta. Si una llega en `Encolado` o `Ejecutando` sin `lineas`, espera con `ssh_output(id=…, esperarSegundos=30)`.
2. **Buscar el error en las dos salidas con una sola búsqueda:** `ssh_search(destino="app@prod", patron="error|fail|denied", ruta=<directorio de esos archivo>)`. Casa cada `.out` con su comando.
3. **Leer alrededor** de la línea hallada N: `ssh_read_file(destino="app@prod", ruta=<archivo>, lineaInicio=max(1, N-20), lineaFin=N+20)`.
4. **Ir al proyecto y leer la configuración** (dos o más operaciones en el mismo directorio):
   - `ssh_set_cwd(destino="app@prod", ruta="/srv/api")`
   - En paralelo: `ssh_read_file(destino="app@prod", ruta="config/app.yaml")` y `ssh_search(destino="app@prod", patron="port", ruta="config")`.
5. **Corregir varias líneas en una sola llamada:** `ssh_replace_file_content(destino="app@prod", ruta="config/app.yaml", ediciones=[{targetContent: "port: 80\n", replacementContent: "port: 8080\n"}, {targetContent: "debug: true\n", replacementContent: "debug: false\n"}])`.
6. **Validar y reiniciar:** `ssh_exec(destino="app@prod", comando="./bin/api --check-config config/app.yaml && sudo systemctl restart api")` (con `sudo -v` antes si pide contraseña; la contraseña, con `ssh_input` y `secreto=true`).
7. **Esperar a que escuche, sin `sleep` fijo:** `ssh_exec(destino="app@prod", comando="timeout 60 bash -c 'until ss -ltn | grep -q \":8080 \"; do sleep 2; done' && echo escuchando")`.
8. **Comprobar el final del log:** `ssh_read_file` no sirve para `journalctl`: `ssh_exec(destino="app@prod", comando="journalctl -u api -n 50 --no-pager", guardarSalida=true)` y lee su archivo.

Qué NO hacer en esta receta: `grep` sobre los logs guardados con `ssh_exec`, `cat` del archivo de configuración, `sed -i` para el puerto, `sleep 30; systemctl status api`, `ssh_disconnect` para «limpiar» antes de terminar (borraría las salidas guardadas).

En un destino de solo lectura, los pasos 1, 5, 6, 7 y 8 se rechazan: lee lo que puedas (configuración, logs en archivos con `ssh_read_file`) y pide al usuario los `journalctl` y el cambio.
