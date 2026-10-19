---
name: native-ssh
description: Use when a task on remote Linux servers through the native_ssh MCP tools needs several steps or several outputs — combining tools, parallel reads, large or unknown-size outputs (guardarSalida, "muchos pajares"), waiting for services, sudo/secrets, multi-file edits, read-only targets or unknown host keys. Keywords: native_ssh, ssh_exec, guardarSalida, ssh_search, ssh_read_file, ssh_set_cwd, salidas grandes, varias salidas, logs, journalctl, pm2, despliegue, solo lectura, known_hosts. For a single call, the tool descriptions are enough.
---

# native_ssh: cómo combinar las herramientas

Las descripciones de cada herramienta (`tools/list`) son su contrato completo: parámetros, valores por defecto, límites y formato de respuesta. Léelas; esta skill **no** los repite. Aquí está solo lo que ninguna descripción cubre por sí sola: cómo encadenar herramientas, qué lanzar en paralelo, cómo tratar salidas grandes y qué hacer ante los rechazos.

## Reglas que no se discuten

| Necesitas… | Usa SIEMPRE… | PROHIBIDO en `ssh_exec`… |
|---|---|---|
| Buscar texto en archivos | `ssh_search` | `grep -r`, `grep` sobre archivos, `rg`, `ag`, `ack` |
| Leer un archivo o sus últimas líneas | `ssh_read_file` (`lineaInicio` negativo = `tail -n`) | `cat`, `head`, `tail -n`, `sed -n`, `less` |
| Localizar archivos por nombre o tamaño | `ssh_find` | `find -name`, `find -size` |
| Ver la estructura de carpetas | `ssh_list_dir` | `ls`, `tree` (para los metadatos de todo un directorio, `ls -l`: ver excepciones) |
| Tipo, tamaño, permisos o dueño de una ruta | `ssh_stat` | `stat`, `ls -l` de esa ruta |
| Crear, sobrescribir o anexar un archivo | `ssh_write_file` | `echo >`, `printf >`, `cat > … <<EOF`, `tee` |
| Editar parte de un archivo | `ssh_replace_file_content` (varias ediciones: `ediciones`, en UNA llamada) | `sed -i`, `perl -i`, `awk` (salvo un archivo mayor que el límite fijo de `ssh_replace_file_content` o que mezcla saltos CRLF y LF: ver [references/escrituras.md](references/escrituras.md)) |
| Fijar el directorio de trabajo | `ssh_set_cwd` | `cd … &&` (dentro de un bucle que recorre directorios, sí) |
| Interrumpir un comando | `ssh_cancel` | Ctrl+C por `ssh_input` |
| Salida grande o de tamaño desconocido | `ssh_exec` con `guardarSalida=true` + `ssh_search`/`ssh_read_file` | volcarla entera, recortarla con `cut`/`head` |

Excepciones permitidas en `ssh_exec` (no son antipatrones): `ls -l` (o `ls -la`) de un directorio cuando necesitas los permisos, dueños, tamaños o fechas de todas sus entradas, porque ninguna herramienta los da de una vez; `grep` que filtra **la salida de otro comando** (`pm2 list | grep online`); `tail -f` y `journalctl -f` **siempre con un plazo corto** (`timeout 20 journalctl -fu app` o `timeoutSegundos` bajo); leer `/proc`, `/sys` y `/dev` dentro de una tubería; `find` para lo que `ssh_find` no hace (ver [data/find-por-ssh-exec.md](data/find-por-ssh-exec.md)); de un archivo con secretos (`.env`, credenciales), **solo** nombres o recuentos filtrando en el servidor (`cut -d= -f1 .env`, `cut -d= -f1 .env | grep -c '^DB_'`), NUNCA `ssh_read_file` ni `ssh_search` sobre él, que devolverían los valores.

## Qué leer según la tarea

- Varias lecturas, servicios en segundo plano, paralelismo, directorio de trabajo: [references/composicion-y-paralelismo.md](references/composicion-y-paralelismo.md)
- Salidas grandes, logs, SQL, «muchos pajares» con `guardarSalida`: [references/salidas-grandes.md](references/salidas-grandes.md)
- Buscar con regex, montajes de `ssh_find`, archivos con secretos: [references/busqueda.md](references/busqueda.md) y [data/motores-regex.md](data/motores-regex.md)
- Esperar a que algo arranque o termine, `ssh_input`, sudo, cancelar, perfiles rotos: [references/esperas-entrada-y-cancelacion.md](references/esperas-entrada-y-cancelacion.md)
- Escrituras, ediciones múltiples, archivos mayores que el límite: [references/escrituras.md](references/escrituras.md)
- Rechazos: solo lectura, host desconocido, clave distinta, cupos: [references/rechazos-y-seguridad.md](references/rechazos-y-seguridad.md)
- Una receta completa (diagnóstico de un servicio y arreglo de su configuración): [examples/receta-diagnostico.md](examples/receta-diagnostico.md)

## Antes de cada llamada

1. ¿Hay herramienta propia? Úsala (tabla de arriba). `ssh_exec` es para servicios, procesos, paquetes, git, docker, compilaciones y lo demás.
2. ¿La salida puede ser grande o no sabes cuánto ocupa? `guardarSalida=true`. Casos típicos: logs, `journalctl`, consultas SQL, volcados, `docker logs`, listados de paquetes o procesos, compilaciones y suites de pruebas, y cualquier orden sin un límite de líneas (`-n`, `LIMIT`, `head`) que acote su salida.
3. ¿Vas a hacer dos o más operaciones en el mismo directorio? `ssh_set_cwd` una vez y rutas relativas. Para un archivo suelto, ruta absoluta.
4. ¿Lees varias cosas? Lánzalas a la vez (ver composición).
5. Si la respuesta trae `aviso`, léelo y síguelo antes de seguir. Únicas excepciones: un aviso que sugiere `ssh_search` para contar o listar nombres de un archivo con secretos (devolvería los valores), el que sugiere `ssh_replace_file_content` para un archivo mayor que su límite fijo o que mezcla saltos CRLF y LF y el que sugiere `ssh_list_dir` tras un `ls -l` que pediste por los metadatos de todo un directorio.
