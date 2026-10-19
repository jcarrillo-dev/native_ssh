# Composición entre herramientas y paralelismo

## Dos vías por destino

- **El shell persistente** (UNO por destino): `ssh_exec`, `ssh_write_file`, `ssh_replace_file_content` y `ssh_set_cwd` van por él **en cola**, una detrás de otra. Mientras un `ssh_exec` no termina, todo lo demás de esa lista espera, también las escrituras.
- **Canales aparte**: `ssh_read_file`, `ssh_search`, `ssh_find`, `ssh_list_dir` y `ssh_stat` no esperan a la cola y funcionan aunque el login del shell esté roto.

## Lecturas en paralelo

- Lanza A LA VEZ, en el mismo turno, las lecturas que no dependen entre sí (p. ej. `ssh_read_file` de tres configuraciones, o `ssh_search` + `ssh_list_dir`). Hay 4 canales por conexión: la quinta espera cupo dentro de su plazo.
- Puedes leer MIENTRAS un `ssh_exec` largo sigue en marcha (una compilación, un despliegue): las lecturas no esperan a su cola.
- NO lances a la vez varias escrituras del mismo destino esperando que vayan en paralelo: van en cola. Dos `ssh_replace_file_content` sobre el mismo archivo lanzados a la vez pueden intercalar lectura y escritura y fallar el segundo («cambió desde su lectura»): para varias ediciones de un archivo, UNA llamada con `ediciones`.
- Si una lectura falla porque los 4 canales siguen ocupados, NO la repitas en bucle: espera a que terminen las demás (o a que venza el plazo de las abandonadas) y reintenta una vez.

## Paralelismo dentro de un `ssh_exec`

- Para correr varias órdenes a la vez en el servidor, hazlo dentro de UN `ssh_exec`, guardando cada PID y esperando solo a esos: `cmd1 & p1=$!; cmd2 & p2=$!; wait $p1 $p2`. NUNCA `wait` a secas: esperaría también a procesos que dejaron comandos anteriores en ese shell.
- Varias órdenes encadenadas en un solo `ssh_exec` (con `&&` o `;`) cuestan una llamada; pero si cada una da mucha salida, usa `guardarSalida=true`.

## Servicios y procesos en segundo plano

- Un proceso que debe seguir vivo se lanza con su salida a un archivo y guardando su PID, en el directorio del proyecto (el fijado con `ssh_set_cwd`): `nohup ./servidor > servidor.log 2>&1 & echo $! > servidor.pid`. NUNCA con nombres fijos en `/tmp` (`/tmp/servidor.log`): en un servidor con más usuarios, otro puede haber creado antes ese nombre o un enlace simbólico con él. Si no hay directorio propio, crea uno con `mktemp -d` y apunta su ruta.
- `cmd &` devuelve 0 aunque el programa no exista o muera al momento: COMPRUEBA que sigue vivo (`kill -0 "$(< servidor.pid)"`) y lee su log con `ssh_read_file` (`lineaInicio` negativo para el final).
- Las variables del shell siguen de una llamada a otra (también `$!` de la anterior), pero se pierden si el shell se reabre solo (el aviso «Shell remoto recreado…» lo dice) y con `ssh_reset_shell`: para parar algo más tarde, guarda el PID en un archivo, no solo en una variable.

## Directorio de trabajo

- Dos o más operaciones en el mismo directorio o repositorio: `ssh_set_cwd` una vez y rutas relativas en todas las herramientas.
- Archivos sueltos del sistema (`/etc/...`, `/var/log/...`): ruta absoluta, sin `ssh_set_cwd`.
- NUNCA `cd … &&` al principio de un comando. Un `cd` dentro de `ssh_exec` solo dura ese comando. NUNCA `export __NS_CWD=…` a mano: cambia los `ssh_exec` siguientes de ese shell pero no las lecturas, y se pierde al recrearse el shell. `cd … &&` dentro de un bucle (`for d in a b; do cd $d && make; cd ..; done`) sí es correcto.
- El directorio fijado sobrevive a `ssh_reset_shell` y a un shell recreado; `ssh_disconnect` lo olvida. Si borras el directorio fijado, los comandos empiezan en `$HOME` sin aviso: vuelve a fijarlo.

## Sesiones

- Usa en todas las herramientas EL MISMO `destino`: un alias con otro `IdentityFile` es otra sesión, con su propio shell y su propio directorio.
- NO uses `ssh_disconnect` para «limpiar»: la siguiente llamada abrirá una conexión nueva, y native_ssh limita las conexiones nuevas por servidor (para no disparar `ufw limit`). Para un shell limpio, `ssh_reset_shell`, que reutiliza la conexión.
- `ssh_reset_shell` y `ssh_disconnect` BORRAN las salidas de `guardarSalida`: léelas antes.
- Si native_ssh corre en modo HTTP, todos sus clientes comparten las mismas sesiones: otro cliente conectado al mismo native_ssh comparte tu shell, tu cola, tus variables y tu directorio de trabajo. No asumas que el estado es solo tuyo: comprueba con `ssh_status` antes de cambiar el directorio o reiniciar el shell si sospechas que hay otro.
