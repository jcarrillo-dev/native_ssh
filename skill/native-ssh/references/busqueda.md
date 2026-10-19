# Búsqueda: regex según el motor, montajes y secretos

## Escribe el patrón para el motor que hay

- La última línea de `ssh_search` dice el motor: `[motor: rg]`, `[motor: grep -P]` o `[motor: grep -E]`. Cada uno tiene su sintaxis de regex: ver [../data/motores-regex.md](../data/motores-regex.md).
- En la PRIMERA búsqueda de un servidor usa un patrón que valga en los tres (literal, `[0-9]`, `[[:space:]]`, alternativas con `|`). Cuando sepas el motor, puedes usar lo suyo.
- Con `[motor: grep -E]` NUNCA uses `\d`, `\w`, `\s`, lookaround ni cuantificadores perezosos: el efecto depende del grep del servidor y casi nunca es un error visible: con GNU grep, `\d` casa la letra «d», `.*?` se lee como `.*` y `(?=…)` casa sin el lookahead, todo con falsos positivos sin aviso; con busybox, `\d` casa dígitos y `(?=…)` da error. Usa las formas POSIX de [../data/motores-regex.md](../data/motores-regex.md).
- Para un texto con caracteres especiales (`.`, `(`, `[`, `$`…), escápalos con `\`.
- «No se encontraron coincidencias» con un aviso de que **pueden faltar** (directorios sin permiso) no es un «no está»: dilo así o busca más acotado.

## `ssh_find` y los montajes

- Si la respuesta acaba en `[montajes_no_explorados: …]`, lo que buscas puede estar en uno de ellos: repite `ssh_find` con `ruta` = cada montaje que interese (de uno en uno).
- Con `ruta='/'` no se recorren `/proc`, `/sys`, `/dev` ni los demás sistemas virtuales; para buscar ahí, `ruta` dentro de ellos.
- `ssh_find` distingue mayúsculas y mira el nombre, no la ruta. Para lo demás (sin distinguir mayúsculas, por ruta, por fecha, dueño o permisos, o para actuar sobre lo encontrado) usa `find` por `ssh_exec`: ver [../data/find-por-ssh-exec.md](../data/find-por-ssh-exec.md).
- Para liberar disco: `ssh_find` con `tamanoMin` (p. ej. `'100M'`) da los más grandes primero.
- No hay herramienta equivalente a `ls -la` para un directorio entero: para los metadatos de todas sus entradas, `ls -l` por `ssh_exec`; para los de unas pocas rutas, `ssh_stat` en paralelo; para los nombres, `ssh_list_dir` (`incluirArchivos=true`).

## Archivos con secretos

- De un `.env`, un fichero de credenciales o una clave, NUNCA `ssh_read_file` ni `ssh_search`: devuelven las líneas enteras, con los valores.
- Si necesitas los nombres de las variables o un recuento, sácalos con `ssh_exec` filtrando EN EL SERVIDOR y con `grep` solo en tubería: `cut -d= -f1 .env`, `cut -d= -f1 .env | grep -c '^DB_'`.
- Para saber si una variable tiene valor sin verlo: `sed -n 's/^API_KEY=..*/definida/p' .env`.
- Si aun así usas `grep` directamente sobre el archivo (`grep -c … .env`), saldrá un aviso que sugiere `ssh_search`: en un archivo con secretos, ignóralo; `ssh_search` devolvería los valores.
