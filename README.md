# native_ssh

Servidor MCP (*Model Context Protocol*) desarrollado en .NET 10 y compilado en Native AOT, diseñado para interactuar con servidores remotos mediante un shell bash persistente por destino a través de transporte stdio o HTTP (Streamable HTTP).

---

## 1. Descripción general y herramientas

El servidor mantiene una sesión SSH persistente con un shell PTY interactivo por cada destino (`usuario@servidor[:puerto]`). Los comandos se ejecutan secuencialmente en una cola FIFO dentro del mismo shell, permitiendo que variables de entorno, funciones y estado de sesión persistan entre invocaciones sucesivas, mientras que el directorio de trabajo se reinicia automáticamente al directorio configurado en `__NS_CWD` (o al directorio personal `~` por defecto) tras cada ejecución mediante el hook post-ejecución `cd "${__NS_CWD:-$HOME}" 2>/dev/null || cd ~;`.

### Herramientas MCP registradas

| Herramienta | Parámetros (por defecto) | Devuelve | Descripción |
|---|---|---|---|
| `ssh_cancel` | `id` (string) | Dos bloques: control (JSON) y salida | Cancela la petición; espera hasta `EsperaTrasCtrlC` + 2 s antes de responder. |
| `ssh_disconnect` | `destino` (string) | `ResultadoAccion` | Cierra la conexión SSH activa con el destino indicado. |
| `ssh_exec` | `destino` (string), `comando` (string), `timeoutSegundos` (300), `esperarSegundos` (20), `guardarSalida` (false) | Dos bloques: control (JSON) y salida | Ejecuta un comando en el shell del destino; devuelve la salida desde el byte 0. |
| `ssh_find` | `destino` (string), `patron` (string), `ruta` ("."), `tipo` ("file"), `profundidadMax` (10), `maxResultados` (100) | `string` | Busca archivos o carpetas por nombre o patrón glob (`*.service`, `docker-compose*.yml`, etc.) con soporte para poda inteligente ("Imprimir pero Podar") de carpetas excluidas. |
| `ssh_input` | `id` (string), `texto` (string), `enter` (true), `esperarSegundos` (2), `secreto` (false) | Dos bloques: control (JSON) y salida | Envía texto a la entrada estándar del proceso en ejecución y captura la salida posterior. Con `secreto=true` (contraseñas, tokens), las apariciones literales del texto en la salida (eco del terminal, un programa que lo imprime) se sustituyen por `*` y el aviso lo confirma; el texto debe ser una sola línea de caracteres imprimibles (cualquier control 0x00–0x1F o 0x7F se rechaza sin enviar nada). Nunca envía Ctrl+C, Ctrl+\\ ni Ctrl+Z (se rechazan: para interrumpir, `ssh_cancel`). Lo que el comando no lee (líneas de más, Ctrl+D, o texto enviado antes de que lo pida o cuando ya no lee) **nunca se ejecuta como orden**: cada envío al shell empieza por una línea centinela aleatoria del canal y, tras cada comando, el shell lee y descarta todo lo que llega antes de ella, sin plazos (no depende de la red, la carga ni el volumen). El recuento es exacto (una línea sin salto final, `enter=false`, cuenta como una) y se conoce al llegar el comando siguiente, así que el aviso va en la respuesta del **siguiente** `ssh_exec` del mismo shell (o, si nadie vio ese final, en la siguiente respuesta de `ssh_exec`, `ssh_input` u `ssh_output` del shell, aunque en medio haya herramientas de archivo o `ssh_set_cwd`), **una sola vez**. Caracteres especiales y excepciones: [Entrada por terminal](#entrada-por-terminal-ssh_input-caracteres-especiales). |
| `ssh_list_dir` | `destino` (string), `ruta` ("."), `profundidadMax` (3), `maxResultados` (100), `incluirArchivos` (false) | `string` | Explora la estructura topológica de carpetas y subcarpetas (o también archivos con `incluirArchivos=true`) en rutas relativas limpias excluyendo carpetas ruidosas. |
| `ssh_output` | `id` (string), `desdeByte` (0), `esperarSegundos` (0) | Dos bloques: control (JSON) y salida | Lee la salida acumulada a partir del offset indicado en `desdeByte`. |
| `ssh_read_file` | `destino` (string), `ruta` (string), `lineaInicio` (1), `lineaFin` (null) | `string` (TextContent) | Lee un archivo del servidor remoto con soporte para rangos de líneas (1-indexed). Devuelve texto plano con saltos de línea reales sin envoltorios JSON. |
| `ssh_replace_file_content` | `destino` (string), `ruta` (string), `targetContent` (string), `replacementContent` (string), `lineaInicio` (1), `lineaFin` (null), `allowMultiple` (false), `sudo` (false) | `string` | Sustituye quirúrgicamente un bloque de texto dentro de un rango delimitado de líneas con swap atómico. |
| `ssh_reset_shell` | `destino` (string) | `ResultadoAccion` | Reinicia el shell bash sobre la conexión TCP existente, descartando el entorno previo. |
| `ssh_search` | `destino` (string), `patron` (string), `ruta` ("."), `extension` (null), `maxResultados` (50) | `string` | Busca patrones de texto o regex con filtro opcional por extensión o glob ignorando carpetas ruidosas (.git, node_modules, .venv, bin, obj, etc.). |
| `ssh_set_cwd` | `destino` (string), `ruta` (null) | `string` | Establece o restablece el directorio de trabajo persistente del shell remoto. Si `ruta` es null o vacía, se restablece a `$HOME`. |
| `ssh_stat` | `destino` (string), `ruta` (string) | `string` (JSON) | Devuelve metadatos básicos estructurados (existe, tipo, tamaño en bytes, tamaño legible, permisos, última modificación) de forma defensiva. |
| `ssh_status` | `destino` (opcional, string) | `ResultadoEstado` | Reporta el estado de sesiones activas y las últimas 20 peticiones procesadas. |
| `ssh_write_file` | `destino` (string), `ruta` (string), `contenido` (string), `overwrite` (false), `append` (false), `sudo` (false) | `string` | Escribe o anexa contenido de forma atómica y segura mediante Base64. |

### Lectura limpia de archivos (`ssh_read_file`)

La herramienta `ssh_read_file` permite inspeccionar archivos y fragmentos de código fuente directamente desde el servidor remoto sin necesidad de ejecutar comandos `cat` o `sed` manuales a través de `ssh_exec`. A diferencia de `ssh_exec`, que devuelve un objeto JSON estructurado (`ResultadoComando`), `ssh_read_file` devuelve directamente texto plano (`TextContent`) con saltos de línea reales (`\n`), evitando la serialización JSON con caracteres escapados (`\n`, `\"`).

Admite lectura acotada mediante `lineaInicio` (por defecto 1) y `lineaFin` (por defecto hasta 800 líneas a partir de inicio). Si la ruta no existe o es un directorio, falla inmediatamente reportando el error.

### Escritura atómica y segura de archivos (`ssh_write_file`)

La herramienta `ssh_write_file` permite crear, sobrescribir o anexar contenido a archivos remotos sin los riesgos habituales de concatenación o escaping en cadenas bash (comillas simples, dobles, caracteres de escape o expansiones de variables). La carga útil se codifica localmente en Base64 y se decodifica en destino mediante `base64 -d`.

- **Escritura atómica:** En operaciones de sobrescritura o creación, los datos se vuelcan primero a un archivo temporal (`.<nombre>.tmp.<pid>`) en el mismo directorio y se reemplazan atómicamente mediante `mv -f`, protegiendo la integridad del archivo original ante cancelaciones o cortes de red.
- **Protección contra sobrescritura accidental:** Por seguridad, la herramienta no sobrescribe un archivo existente a menos que se indique explícitamente `overwrite: true` o se elija el modo `append: true`. Especificar ambas opciones simultáneamente produce un error de validación inmediato.
- **Permisos elevados (`sudo`):** Mediante el parámetro `sudo: true`, la herramienta utiliza `sudo tee` para interactuar con archivos protegidos por permisos de superusuario en el destino remoto.

### Modificación quirúrgica de archivos (`ssh_replace_file_content`)

La herramienta `ssh_replace_file_content` permite sustituir bloques específicos de código o texto dentro de un rango delimitado de líneas (`lineaInicio` y `lineaFin`, indexadas desde 1), minimizando el riesgo de corromper archivos extensos o cometer alucinaciones de contenido circundante.

- **Rango y coincidencia exacta:** El bloque objetivo `targetContent` se busca dentro del intervalo de líneas indicado. Detecta automáticamente el formato de fin de línea (`\r\n` o `\n`) del archivo remoto y normaliza la búsqueda.
- **Protección contra ambigüedad:** Si `targetContent` aparece más de una vez en el rango seleccionado y no se especifica `allowMultiple: true`, la herramienta aborta la operación reportando el conteo de apariciones para forzar una delimitación más precisa. Asimismo, si no se encuentra la cadena objetivo dentro del rango, emite un mensaje diagnóstico detallado para verificar números de línea e indentación.
- **Escritura atómica y elevación de privilegios (`sudo`):** Recompone el archivo en memoria y lo escribe de forma atómica mediante codificación Base64 y un archivo temporal con reemplazo seguro (`mv -f`), admitiendo el parámetro opcional `sudo: true` para editar archivos de sistema mediante `sudo tee` y `sudo mv -f`.

### Búsqueda inteligente de patrones (`ssh_search`)

La herramienta `ssh_search` proporciona capacidades de búsqueda rápida basada en `grep -rnIs -E` sobre el servidor remoto, orientada a la exploración de código y configuración.

- **Filtro por extensión o glob (`extension`):** Permite filtrar los archivos a analizar mediante una extensión simple (ej. `'py'`, `'.ts'`) o patrón glob (ej. `'*.json'`), activando el modo de inclusión selectiva previa en `grep`.
- **Exclusiones automáticas:** Ignora de forma predeterminada directorios ruidosos o de gran volumen (`.git`, `node_modules`, `.venv`, `venv`, `__pycache__`, `dist`, `build`, `.next`, `.cache`, `bin`, `obj`) y archivos de artefactos/bloqueo (`*.min.js`, `*.min.css`, `*.map`, `*.lock`, `package-lock.json`, `pnpm-lock.yaml`).
- **Control de contexto:** El parámetro `maxResultados` (por defecto 50, con límite de 1 a 200) acota el volumen de resultados devueltos para evitar la saturación del contexto de trabajo del cliente MCP, informando explícitamente mediante una nota final si se ha alcanzado el límite.

### Búsqueda de archivos por nombre o patrón (`ssh_find`)

La herramienta `ssh_find` permite localizar archivos o directorios por su nombre o patrón glob (ej. `*.service`, `docker-compose*.yml`, `.env*`) en el servidor remoto.

- **Filtrado por tipo:** El parámetro `tipo` permite restringir la búsqueda a archivos (`'file'`, por defecto), directorios (`'dir'` o `'directory'`) o ambos (`'all'`).
- **Poda inteligente con regla universal "Imprimir pero Podar":** Excluye sistemáticamente árboles pesados o ruidosos (`.git`, `node_modules`, `.venv`, `venv`, `__pycache__`, `dist`, `build`, `.next`, `bin`, `obj`, `.cache`). Mediante la regla universal `-print -prune`, si la consulta busca el directorio excluido en sí (ej. `.git` o `node_modules` con `tipo="dir"` o `tipo="all"`), la carpeta coincidente se incluye en los resultados pero su contenido interior se poda inmediatamente sin descender recursivamente.
- **Rutas relativas limpias:** Devuelve rutas relativas limpias ordenadas alfabéticamente sin el prefijo `./`.

### Exploración topológica de directorios (`ssh_list_dir`)

La herramienta `ssh_list_dir` permite mapear de un vistazo la estructura y jerarquía de directorios de un proyecto remoto sin saturar el contexto con listas masivas de archivos.

- **Inclusión opcional de archivos (`incluirArchivos`):** Por defecto (`false`), lista exclusivamente directorios para brindar un mapa topológico limpio. Con `incluirArchivos: true`, enumera tanto archivos como carpetas respetando la profundidad máxima configurada.
- **Recursión controlada:** El parámetro `profundidadMax` (por defecto 3, entre 1 y 10) define el nivel máximo de profundidad en el árbol de carpetas.
- **Distinción entre marcadores arquitectónicos y cachés:**
  - **Marcadores arquitectónicos de alto valor:** Carpetas raíz y marcadores clave del proyecto (`.git`, `node_modules`, `.venv`, `venv`, `dist`, `build`, `.next`, `bin`, `obj`) se listan en su nivel correspondiente con su interior podado de inmediato (`-print -prune`), ofreciendo visibilidad inmediata sobre la topología del proyecto sin sobrecargar el buffer.
  - **Cachés ruidosas repetitivas:** Directorios de caché efímera (`__pycache__`, `.cache`) se podan en silencio absoluto (`-prune`) sin imprimirse en la salida.
- **Resumen arquitectónico:** Ideal para entender rápidamente la organización de módulos y capas en repositorios remotos.

### Inspección defensiva de metadatos (`ssh_stat`)

La herramienta `ssh_stat` proporciona información detallada y estructurada sobre cualquier archivo, directorio o enlace simbólico remoto antes de proceder a lecturas o modificaciones.

- **Respuesta JSON estructurada:** Devuelve un documento JSON con propiedades normalizadas (`ruta`, `existe`, `tipo`, `tamanoBytes`, `tamanoHumano`, `permisos`, `ultimaModificacion`).
- **Lectura defensiva:** Permite verificar si un archivo supera límites seguros (ej. varios megabytes o gigabytes) antes de intentar volcarlo en memoria con `ssh_read_file`, evitando bloqueos o sobrecargas de contexto.
- **Detección de inexistencia segura:** Si el destino no existe, devuelve `{"existe": false, ...}` con código de éxito en lugar de abortar abruptamente la sesión.

### Respuesta de `ssh_exec`, `ssh_output`, `ssh_input` y `ssh_cancel`: dos bloques

Estas cuatro herramientas responden con **dos bloques de texto MCP**, siempre los dos y en este orden:

1. **Control:** JSON compacto en una sola línea con `id`, `destino`, `estado`, `codigoSalida`, `desdeByte`, `siguienteByte`, `hasMasSalida`, `hasSalidaDescartada`, `segundosEjecucion`, `segundosSinSalida` y `aviso` (incluidos los avisos de antipatrón). Los campos nulos no se escriben.
2. **Salida:** el texto del comando tal cual, sin escapar (vacío si no hay salida).

Antes iba todo en un único JSON y la salida llegaba doblemente codificada (el modelo veía `\n`, `\"` y `\\`, y una tabla ancha en una sola línea). Un cliente que vuelca a un archivo las respuestas grandes (el CLI de Antigravity, `agy`, lo hace por encima de unos pocos KB) obtiene así un archivo con líneas reales cuya primera línea es el control, legible por tramos. Las demás herramientas no cambian: las de lectura ya devolvían texto plano y las de estado, acción, metadatos y escritura siguen siendo un JSON pequeño.

### Salida a un archivo del servidor (`ssh_exec` con `guardarSalida=true`)

Para salidas grandes (consultas SQL, logs, volcados): la salida completa del comando (stdout y stderr, como en el terminal, **sin el límite de 1 MiB**) va a un archivo del servidor y la respuesta trae solo el control, con `archivo`, `lineas` y `bytes` además del estado, el código y los segundos, y en el bloque de salida las **5 primeras líneas** como vista previa (cada una cortada a 512 bytes sin partir caracteres, marcada con ` […]`; se leen del primer MiB del archivo). Después se lee con `ssh_read_file` por tramos o se busca con `ssh_search` sobre esa ruta. El comando va por el mismo shell que el resto de `ssh_exec` (PTY, `__NS_CWD`, variables exportadas) y su código de salida es el suyo; los avisos de antipatrón siguen funcionando. `lineas` cuenta una última línea sin salto final (como la cabecera de `ssh_read_file`).

- **Ruta fija, sin elección del LLM:** `/tmp/native_ssh-<usuario>/<id del shell>/<id de la petición>.out`, con `<usuario>` = `id -un` en el servidor (el de la sesión SSH, que es el que cuenta para los permisos) y un `<id del shell>` aleatorio de 128 bits, propio de cada shell. `/tmp` debe tener el bit sticky; el directorio base se crea con `mkdir -m 700` y, si ya existe, se exige que no sea un enlace, que sea un directorio del usuario y que su modo sea exactamente `drwx------` (sin ACL): si no, **error sin ejecutar el comando ni escribir nada** (otro usuario del servidor podría haberlo creado antes para leer o redirigir las salidas, también las de root). El directorio del shell se crea igual (exclusivo) y se comprueba igual; los archivos son 0600 y nunca se pisa uno existente.
- **Limpieza:** el directorio del shell se borra (por un canal exec) con `ssh_reset_shell`, `ssh_disconnect`, al cerrarse la sesión y, tras una caída del shell o de la conexión, al abrirse el shell siguiente. Al crear el directorio de un shell se barren los de otros shells del mismo usuario sin nada modificado en 24 h (restos de un proceso que murió sin limpiar).
- **Cupo:** como mucho 100 archivos o 256 MiB por shell; por encima, el comando no se ejecuta y la respuesta dice cómo liberar sitio (borrar con `rm` o `ssh_reset_shell`). Se comprueba antes de cada comando: uno solo puede escribir más.
- **En ejecución:** si `esperarSegundos` vence antes de que termine, el control ya trae `archivo` (sin `lineas` ni `bytes`) y el bloque de salida solo lo que el comando escribe directamente en el terminal; `ssh_output` con el mismo id da el control completo y la vista previa al terminar. Si se interrumpe (plazo, `ssh_cancel`), lo escrito hasta entonces queda en el archivo, sin recuento.
- **Limitaciones:** lo que el comando pregunte por su salida (menús, confirmaciones) va al archivo y no se ve: no es para comandos interactivos. La contraseña de sudo sí se ve (sudo la pide por el terminal) y lo que un programa escribe en `/dev/tty` sale aparte en el bloque de salida. Si se envió un secreto con `ssh_input`, no hay vista previa: el archivo no pasa por el enmascarado y tiene lo que imprimió el programa.

### Mecanismo de paginación y lectura

- El parámetro `esperarSegundos` está acotado al rango de 0 a 120 segundos.
- Cada bloque de respuesta tiene un tamaño máximo de 64 KiB (`MaxBytesPorRespuesta`).
- Si un comando genera una salida extensa o continúa en ejecución (`HasMasSalida = true`), las lecturas posteriores deben realizarse invocando `ssh_output` pasando en `desdeByte` el valor de `SiguienteByte` obtenido en la respuesta previa.

### Entrada por terminal (`ssh_input`): caracteres especiales

`ssh_input` escribe en el terminal (PTY) del shell, en modo canónico: el núcleo interpreta algunos caracteres antes de que nadie los lea. Comportamiento medido con bash 5.2 y 5.3 (el shell de la sesión) y con dash 0.5 y el `sh` de busybox 1.37 (los que pueden quedar ejecutando las líneas del protocolo dentro de un shell anidado), enviando cada carácter en medio de una línea con dos órdenes detrás a un comando que no lee, al final de lo enviado (`enter=false`) y a un programa que lee una línea (`head -n 1`). En ningún caso se ejecuta como orden nada de lo enviado ni se queda colgado el comando siguiente:

| Carácter | Qué hace el terminal | `ssh_input` | Recibe un programa que lee `a<c>b` |
|---|---|---|---|
| Ctrl+C (VINTR), Ctrl+\\ (VQUIT), Ctrl+Z (VSUSP) | Señal al proceso en primer plano y vaciado de la entrada pendiente | **Se rechaza** (usa `ssh_cancel`) | — |
| Ctrl+D (VEOF) | Al principio de una línea, fin de fichero; en medio, entrega lo escrito sin salto | Se envía | `ab` (`head` junta lo entregado con lo siguiente) |
| Ctrl+? / DEL (VERASE), Ctrl+U (VKILL), Ctrl+W (VWERASE) | Borran el carácter, la línea o la palabra anteriores **de la línea a medias** (solo lo tecleado, nunca una línea del protocolo) | Se envía | `b` |
| Ctrl+V (VLNEXT) | El carácter siguiente va literal | Se envía; **se rechaza como último carácter con `enter=false`** (haría literal lo siguiente que llegue, también el Ctrl+C de `ssh_cancel`) | `ab` |
| Ctrl+R (VREPRINT), Ctrl+O (VDISCARD) | Nada (sin eco); llegan tal cual | Se envía | `a`, el carácter, `b` |
| Ctrl+Q (VSTART), Ctrl+S (VSTOP) | Nada: la inicialización pone `stty -ixon` (sin ella, Ctrl+S detenía la salida y el comando esperaba hasta su plazo) | Se envía | `a`, el carácter, `b` |
| NUL, Ctrl+H (retroceso, no es VERASE) | Nada (VEOL sin definir); llegan tal cual (el `read` de bash quita NUL) | Se envía | `a`, el carácter, `b` |
| Línea a medias de 4095 bytes o más | El núcleo guarda 4095 por línea y tira el resto, pero acepta el salto | Se envía | — |

Con `secreto=true` se rechaza cualquier control (0x00–0x1F, 0x7F). El recuento de lo descartado cuenta líneas tal como las deja el terminal (una línea borrada entera con Ctrl+U no cuenta; un Ctrl+D al principio de una línea cuenta como una).

Excepciones conocidas (fuera de lo que native_ssh puede garantizar):

- **El comando cambia el terminal y no lo restaura:** con `stty noflsh`, el Ctrl+C de una interrupción (`ssh_cancel` o el plazo) no vacía lo tecleado que el comando no leyó, y bash lo ejecuta como órdenes antes de la línea de recuperación (medido); con `stty ixon`, un Ctrl+S tecleado vuelve a detener la salida; con `stty intr`/`quit`/`susp`/`lnext`/`eof`/`eol` a otros caracteres, la política de `ssh_input` sigue mirando los de por defecto: con `stty intr '^A'`, un `ssh_input` con Ctrl+A y texto detrás manda la señal, **el texto se ejecuta como orden** y el comando sigue «en ejecución» hasta su plazo (medido); `stty -icanon` o `raw` cambian qué es una línea; `stty iuclc` pasa a minúsculas todo lo que llega, también las líneas del protocolo: el comando y los siguientes agotan su plazo (medido; usa `ssh_reset_shell`); `stty istrip` quita el octavo bit de lo tecleado (el comando viaja en base64 y no le afecta; medido); `stty sane` vuelve a activar el eco y `ixon`: cada respuesta lleva el envío entero (centinela, comando en base64 y la comprobación con el testigo del shell).
- **Trazas y trampas que deja el comando:** con `set -x` (o `set -v`), la salida de cada comando lleva la traza de las líneas del protocolo, con el comando decodificado y el testigo del shell (lo tecleado no: la espera del centinela descarta su traza); con `set -x` y `BASH_XTRACEFD` apuntando a la salida estándar (p. ej. `BASH_XTRACEFD=1`), la traza se mezcla con la decodificación del comando y **ningún comando se ejecuta** (cada uno termina en error con el mensaje de envío incompleto; usa `ssh_reset_shell`; medido); un `trap '…' DEBUG` que imprime se ejecuta en cada orden del protocolo y enseña sus líneas, con el testigo (medido).
- **Un proceso en segundo plano que lee del terminal** compite con el shell por las líneas del envío siguiente: si se come una, normalmente es la línea vacía que precede al centinela y no pasa nada; si se come el centinela, el comando siguiente agota su plazo sin ejecutarse; si se come parte del comando, este **no se ejecuta** y la respuesta lo dice (nunca se ejecuta a medias). Usa `ssh_reset_shell`: el `kill` que lo terminaría viaja por el mismo terminal y también se lo puede comer.
- **Un programa que atrapa Ctrl+C y lee del terminal** (p. ej. «¿seguro? s/n»): al interrumpirlo, lee la línea vacía del salto o, si lee dos, el centinela de la línea de recuperación (puede mostrarlo); si lee tres o más, recibe también la línea de recuperación entera, con el testigo del shell (medido); nunca se ejecuta ni se cuenta como tecleada.
- **Interrumpir con entrada pendiente:** el Ctrl+C de `ssh_cancel` o del plazo hace que el núcleo vacíe lo tecleado que el comando aún no había leído; esas líneas no se ejecutan, pero tampoco se cuentan ni se avisa de ellas.
- **El centinela se ve** si el comando deja `set -v`, `stty echo` o `stty sane` (ver arriba) o `shopt -u interactive_comments` (con este último, además, cada envío da un error «orden no encontrada»); con `set -x` no (medido).
- **`command` redefinido** como función, o un alias de `while`, `do`, `done`, `{` o `}`: rompen la línea del protocolo (el comando siguiente agota su plazo). Una función o un alias `read`, `[`, `test` o `exit` no la rompen. Con una función o un alias `base64` o `printf` que cambia lo que imprimen, **ningún comando se ejecuta** (cada uno termina en error con el mensaje de envío incompleto, también el `unset -f` o `unalias` que intentara deshacerlo: usa `ssh_reset_shell`; medido); una función `base64` que solo añade un salto final no rompe nada; si la función `printf` no imprime nada, el comando agota su plazo.
- **1000 Ctrl+D seguidos** sin otra cosa en medio: el shell termina (un terminal colgado da fin de fichero sin fin) y lo que venía detrás no se ejecuta; el comando siguiente abre un shell nuevo o, si llega mientras el shell termina, acaba como shell perdido sin ejecutarse.
- **Ctrl+C con `ssh_input` no existe:** para interrumpir un intérprete interactivo (python, psql) hay que terminar el comando con `ssh_cancel`.

### Directorio de trabajo persistente (`ssh_set_cwd` y `__NS_CWD`)

Por defecto, el directorio de trabajo se restablece automáticamente a `$HOME` (`~`) tras cada comando. Cada comando ejecutado por el servidor se envuelve internamente con una cláusula final:

```bash
cd "${__NS_CWD:-$HOME}" 2>/dev/null || cd ~;
```

Esto garantiza que incluso si un comando ejecuta llamadas `cd` intermedias (p. ej. `cd /var/log && cat syslog`), la siguiente llamada comenzará siempre en el directorio de trabajo configurado.

#### Herramienta `ssh_set_cwd`

Permite configurar o restablecer de forma segura el directorio de trabajo persistente de la sesión:

- **Validación previa y resolución de ruta:** Comprueba en el host remoto que la ruta exista y sea un directorio (`test -d "$ruta"`). Si es válida, resuelve la ruta absoluta física canónica (`cd "$ruta" && pwd -P`) y exporta `export __NS_CWD="..."` en la sesión interactiva viva.
- **Restablecimiento a `$HOME`:** Si se invoca con `ruta: null` o vacía, la herramienta ejecuta `unset __NS_CWD` y devuelve el shell a `$HOME` (`cd ~`).
- **Fail-fast:** Si el directorio no existe o no se tienen permisos de acceso, la herramienta falla inmediatamente con un error descriptivo sin modificar el estado previo de la sesión.

---

## 2. Requisitos del servidor remoto

El host remoto al que se conecta el servidor debe disponer de:
- Intérprete `bash`.
- Utilidades `stty` y `base64` (incluidas habitualmente en `coreutils`).

### Verificación de identidad (`known_hosts`)

Por motivos de seguridad, el destino debe estar registrado previamente en el archivo `~/.ssh/known_hosts` local (admite entradas en texto plano, con hashing estándar y formatos `[host]:puerto`).

Si el host no se encuentra registrado, la primera llamada fallará inmediatamente devolviendo el comando `ssh-keyscan` exacto que debe ejecutarse de forma manual tras verificar la huella digital del servidor remoto.

---

## 3. Credenciales y autenticación

El servidor busca credenciales en el siguiente orden de precedencia:

1. **Claves privadas sin frase de paso:** Búsqueda automática en las rutas por defecto:
   - `~/.ssh/id_ed25519`
   - `~/.ssh/id_ecdsa`
   - `~/.ssh/id_rsa`
   O en las rutas especificadas mediante la variable `NATIVE_SSH_IDENTIDADES`.
2. **Contraseña explícita:** Configurada mediante la variable de entorno `NATIVE_SSH_PASSWORD`.

> [!NOTE]
> No se admite `ssh-agent` ni claves privadas protegidas mediante frase de paso (*passphrase*).

---

## 4. Variables de entorno

| Variable | Tipo | Valor por defecto | Descripción |
|---|---|---|---|
| `NATIVE_SSH_DIRECTORIO_SSH` | Ruta | `~/.ssh` | Directorio base donde se ubican las claves y el archivo `known_hosts`. |
| `NATIVE_SSH_IDENTIDADES` | Rutas (`:`) | Claves estándar en `DirectorioSsh` | Lista de rutas a claves privadas separadas por `:`. |
| `NATIVE_SSH_PASSWORD` | Cadena | *(Vacío)* | Contraseña de autenticación para destinos remotos. |
| `NATIVE_SSH_PUERTO_HTTP` | Entero | `8722` | Puerto TCP de escucha para los modos HTTP. |
| `NATIVE_SSH_NIVEL_LOG` | Nivel log | `Information` | Nivel mínimo de trazas (`Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`). Todos los logs se emiten invariablemente por `stderr`. Las categorías del SDK de MCP (`ModelContextProtocol.*`) nunca bajan de `Debug`: en `Trace` escribirían cada mensaje JSON-RPC completo (argumentos como el texto de `ssh_input` y respuestas con salidas y archivos). |
| `NATIVE_SSH_TOKEN_HTTP` | Cadena | *(Vacío)* | Token de los modos HTTP (al menos 16 caracteres, contados como unidades UTF-16: un emoji cuenta 2), en una sola línea. Obligatorio en `--http` y `--http-unsecure` (o el fichero de la variable siguiente, nunca las dos); no se usa con stdio. |
| `NATIVE_SSH_TOKEN_HTTP_FICHERO` | Ruta | *(Vacío)* | Solo Linux. Fichero con el token: un fichero normal (no un enlace simbólico, una FIFO ni un directorio) del usuario que ejecuta native_ssh y solo legible por él (`chmod 600`); si no, no arranca. Se comprueba en el fichero ya abierto, sin seguir el enlace final; un enlace en un directorio intermedio de la ruta se acepta (p. ej. `/home` → `/var/home`: solo puede llevar a un fichero propio y `0600`). Se quitan los saltos de línea finales; un token con más de una línea, o que empieza o termina con espacios, se rechaza. |

---

## 5. Modos de arranque

El ejecutable admite el transporte estándar a través de `stdio` o transporte Streamable HTTP mediante ASP.NET Core Kestrel:

```bash
NativeSsh [--http | --http-unsecure] [--puerto N]
```

| Modo | Escucha | Autenticación | Log al arrancar |
|---|---|---|---|
| *(sin argumentos)* | stdio | — | — |
| `--http` | `127.0.0.1` y `[::1]` (`ListenLocalhost`) | Token obligatorio (`Authorization: Bearer <token>`) | `Information: URL http://localhost:8722/mcp` |
| `--http-unsecure` | Todas las interfaces (`ListenAnyIP`) | Token obligatorio, **sin cifrar** | `Warning: «… SIN cifrar …»` |

Los dos modos HTTP exigen un token (`NATIVE_SSH_TOKEN_HTTP` o `NATIVE_SSH_TOKEN_HTTP_FICHERO`); sin él, o con un fichero que no cumple lo anterior, el servidor no arranca (código 2 y el motivo en `stderr`). Cada petición sin `Authorization: Bearer <token>` exacto recibe `401` con un mensaje claro; el token se compara en tiempo constante y nunca se escribe en los logs. Los rechazos se anotan en el log como mucho 10 veces por minuto (ventana fija que empieza con el primer rechazo tras la anterior: en el cambio de ventana pueden salir hasta 20 seguidos); los omitidos se cuentan en el siguiente aviso que se anote (en un minuto posterior), no al terminar la ráfaga: si no llega ningún rechazo más, no se anotan.

> [!WARNING]
> El modo `--http-unsecure` expone en la red, por HTTP sin cifrar, un servicio con capacidad para ejecutar comandos arbitrarios en los servidores remotos configurados: el token y todo lo enviado (comandos, archivos, contraseñas de `ssh_input`) viajan en claro. Este modo debe reservarse exclusivamente para redes privadas de total confianza o detrás de un proxy inverso con TLS.

---

## 6. Configuración en Claude Code

### Transporte por stdio

Registro directo del binario compilado:

```bash
claude mcp add native_ssh -- /ruta/artefactos/NativeSsh
```

### Transporte por HTTP

Arrancar el servidor en modo HTTP con un token en un fichero propio:

```bash
umask 077; head -c 32 /dev/urandom | base64 > ~/.config/native_ssh_token
NATIVE_SSH_TOKEN_HTTP_FICHERO=~/.config/native_ssh_token /ruta/artefactos/NativeSsh --http
```

Y registrar el endpoint correspondiente con el mismo token:

```bash
claude mcp add --transport http native_ssh http://localhost:8722/mcp --header "Authorization: Bearer $(cat ~/.config/native_ssh_token)"
```

### Configuración declarativa en `.mcp.json`

```json
{
  "mcpServers": {
    "native_ssh": {
      "command": "/ruta/artefactos/NativeSsh",
      "env": {
        "NATIVE_SSH_NIVEL_LOG": "Warning"
      }
    }
  }
}
```

---

## 7. Límites y protección frente a `ufw limit`

Para garantizar la estabilidad del servicio y evitar bloqueos defensivos por parte del firewall remoto (por ejemplo, reglas de límite de conexiones en `ufw`):

- **Control de conexiones TCP (D7):** Límite estricto de 4 conexiones nuevas cada 30 segundos por destino. En caso de error de conexión, se aplica un retroceso exponencial (*backoff*) de 2 a 60 segundos. Se envía un paquete de *keep-alive* cada 30 segundos para evitar desconexiones por inactividad en pasarelas NAT.
- **Cancelación escalonada sin apertura de sockets (D6):** La cancelación envía una señal de interrupción (Ctrl+C) directamente por el canal del shell abierto. Si el proceso no se detiene tras el intervalo de gracia (`EsperaTrasCtrlC`), se destruye únicamente el canal lógico del shell y se reabre uno nuevo sobre la conexión TCP existente, evitando consumir cupo de conexiones nuevas en el cortafuegos.
- **Gestión de memoria y buffers de salida (D10):** Límite máximo de 1 MiB de salida retenida por comando utilizando bloques de 16 KiB reciclados del pool de memoria (`ArrayPool<byte>`). Si se sobrepasa dicho límite, los datos más antiguos se descartan manteniendo offsets acumulativos absolutos. Se retienen en memoria hasta 64 peticiones finalizadas durante una ventana máxima de 30 minutos.
- **Motor de cero asignaciones con `ValueLINQDelay` y `JCarrillo.AOT.Core`:** Los pipelines de filtrado y acotación de colecciones internas (como el límite de peticiones en `ssh_status` o el podado de peticiones finalizadas) operan sobre estructuras `ref struct` en la pila mediante `JCarrillo.AOT.Core` y extensiones `ValueLINQDelay`, garantizando cero asignaciones en el heap y ausencia total de sobrecarga del recolector de basura (GC) durante la iteración diferida.

---

## 8. Verificación y pruebas

### Suite de pruebas automatizadas

```bash
dotnet test
```

> [!NOTE]
> Las pruebas de integración (`native_ssh.Test/Integracion`) requieren la presencia local de `sshd` y `ssh-keygen` de OpenSSH. El arnés levanta un demonio `sshd` efímero en modo usuario sobre una interfaz local, sin alterar la configuración del sistema ni los archivos de `~/.ssh`.

### Scripts de prueba de humo

- **Prueba de humo stdio:**
  ```bash
  pruebas/humo-stdio.sh
  ```
- **Prueba de humo HTTP:**
  ```bash
  pruebas/humo-http.sh
  ```

### Rendimiento y microbenchmarks

Resultados obtenidos en ejecución de referencia (`native_ssh.Benchmarks`):

```bash
dotnet run --project native_ssh.Benchmarks -c Release -- --filter '*'
```

| Benchmark | JIT | NativeAOT | Asignado |
|---|---:|---:|---:|
| `Constructor_Comando` | 93 ns | 106 ns | 0 B |
| `Escaner_Bloque16K` | 0,70 µs | 0,66 µs | 0 B |
| `Paginador_64K` | 6,4 µs | 6,2 µs | 31.920 B (el `string` del resultado) |
| `BufferSalida_Escribir1MiB` | 24 µs | 24 µs | 1.256 B (la lista de segmentos) |
| `LimpiarAnsi_Bloque16K` | 28 µs | 37 µs | 0 B |

---

## 9. Estándar de código y directrices de desarrollo

La solución comparte la configuración y políticas de análisis de `JCarrillo.AOT.Core` (.NET 10, C# 14, `Directory.Build.props` y `.editorconfig`):
- Tipado explícito obligatorio (`var` deshabilitado).
- Omitir llaves en bloques de sentencia única.
- Uso del operador de rango sobre llamadas `Slice`.
- Erradicación de `System.Linq` global en favor de pipelines en pila con `JCarrillo.AOT.Core.ValueLINQ` y `ValueLINQDelay`.
- Normalización de fines de línea a CRLF en archivos de código C# (manteniendo LF en scripts `.sh`).

### Advertencia sobre formateo automático (`IDE0010` / `IDE0072`)

No deben aplicarse correcciones automatizadas globales procedentes de `dotnet format` para los diagnósticos `IDE0010` o `IDE0072`. Estas reglas tienden a rellenar bloques `switch` exhaustivos insertando ramas vacías (`case X: break;`) o excepciones `NotImplementedException` antes de la captura por defecto, alterando la semántica y el comportamiento intencional de la máquina de estados.

---

## 10. Limitaciones conocidas

- **Interfaces de usuario en terminal (TUI):** No se admiten utilidades interactivas basadas en curses o terminal completa (como `vim`, `nano`, `top` o `less` sin `PAGER=cat`).
- **Segmentación de secuencias ANSI:** Si una secuencia de escape ANSI queda dividida exactamente en el límite de un bloque de 64 KiB durante la paginación, la fracción restante se interpretará como texto sin formato en la página siguiente.
- **Agentes y claves cifradas:** No existe soporte para reenvío de credenciales vía `ssh-agent` ni descifrado de claves privadas con frase de paso.
- **Túneles SSH:** No se contempla el reenvío de puertos locales o remotos (*port forwarding*).
- **Seguridad en HTTP:** El transporte HTTP no implementa mecanismos integrados de autenticación ni autorización.
- **Renegociación de claves con strict-kex (corregido en el submódulo):** SSH.NET 2026.0.0 aplica la regla estricta de *strict key exchange* («cortar ante cualquier mensaje inesperado») también a las renegociaciones, aunque la especificación de OpenSSH (`PROTOCOL`, §1.9, que remite al borrador IETF `draft-miller-sshm-strict-kex`) la limita al intercambio inicial; como OpenSSH 10.5 envía `SSH_MSG_IGNORE` entre `KEXINIT` y `NEWKEYS` al renegociar, SSH.NET cortaba la conexión (se perdía el shell con sus `export` y se gastaba una conexión del cupo de `ufw limit`; medido con `RekeyLimit 64K 2s`: 25 cortes en 56 renegociaciones en 60 s). native_ssh compila SSH.NET desde el submódulo `externo/SSH.NET` (rama `native-ssh`), que lo corrige: la regla estricta solo en el intercambio inicial, manteniendo el reinicio de los números de secuencia tras cada `NEWKEYS`. Ya no es una limitación de native_ssh; sí lo es del paquete NuGet de SSH.NET 2026.0.0 mientras la corrección no llegue al proyecto original.

---

## 11. Líneas de optimización pendientes (D11)

- **Asignaciones residuales por comando:** Reducción de objetos en el heap asociados al ciclo de vida de la petición: instancia de `Peticion`, `TaskCompletionSource`, tokens de `CancellationTokenSource` y la instanciación inmutable de `string` para cada página de salida entregada.
- **Procesamiento de secuencias ANSI:** Optimización de `ConversorTexto.LimpiarAnsi` para sustituir el análisis byte a byte actual por algoritmos vectorizados (SIMD) o escaneo por saltos.
- **Diferencial de rendimiento AOT vs JIT:** En Native AOT sin perfilado dinámico guiado por optimización (PGO dinámico), métodos intensivos como `Constructor_Comando` y `LimpiarAnsi` presentan una latencia ligeramente superior respecto al runtime JIT.
