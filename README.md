# native_ssh

Servidor MCP (*Model Context Protocol*) desarrollado en .NET 10 y compilado en Native AOT, diseñado para interactuar con servidores remotos mediante un shell bash persistente por destino a través de transporte stdio o HTTP (Streamable HTTP).

---

## 1. Descripción general y herramientas

El servidor mantiene una sesión SSH persistente con un shell PTY interactivo por cada destino (`usuario@servidor[:puerto]`, y sus claves si vienen de un `IdentityFile` de la configuración de ssh: ver «Configuración de ssh»). Los comandos se ejecutan secuencialmente en una cola FIFO dentro del mismo shell, permitiendo que variables de entorno, funciones y estado de sesión persistan entre invocaciones sucesivas, mientras que el directorio de trabajo se reinicia automáticamente al directorio configurado en `__NS_CWD` (o al directorio personal `~` por defecto) tras cada ejecución mediante el hook post-ejecución `cd "${__NS_CWD:-$HOME}" 2>/dev/null || cd ~;`.

### Herramientas MCP registradas

| Herramienta | Parámetros (por defecto) | Devuelve | Descripción |
|---|---|---|---|
| `ssh_cancel` | `id` (string) | `ResultadoComando` | Cancela la petición; espera hasta `EsperaTrasCtrlC` + 2 s antes de responder. |
| `ssh_disconnect` | `destino` (string) | `ResultadoAccion` | Cierra la conexión SSH activa con el destino indicado (con un alias, la de ese alias; con `usuario@servidor[:puerto]`, todas las de ese destino; el mensaje dice cuántas). |
| `ssh_exec` | `destino` (string), `comando` (string), `timeoutSegundos` (300), `esperarSegundos` (20) | `ResultadoComando` | Ejecuta un comando en el shell del destino; devuelve la salida desde el byte 0. |
| `ssh_find` | `destino` (string), `patron` (string), `ruta` ("."), `tipo` ("file"), `profundidadMax` (10), `maxResultados` (100) | `string` | Busca archivos o carpetas por nombre o patrón glob (`*.service`, `docker-compose*.yml`, etc.) con soporte para poda inteligente ("Imprimir pero Podar") de carpetas excluidas. |
| `ssh_input` | `id` (string), `texto` (string), `enter` (true), `esperarSegundos` (2), `secreto` (false) | `ResultadoComando` | Envía texto a la entrada estándar del proceso en ejecución y captura la salida posterior. Con `secreto=true` (contraseñas, tokens), las apariciones literales del texto en la salida (eco del terminal, un programa que lo imprime) se sustituyen por `*` y el aviso lo confirma. |
| `ssh_list_dir` | `destino` (string), `ruta` ("."), `profundidadMax` (3), `maxResultados` (100), `incluirArchivos` (false) | `string` | Explora la estructura topológica de carpetas y subcarpetas (o también archivos con `incluirArchivos=true`) en rutas relativas limpias excluyendo carpetas ruidosas. |
| `ssh_output` | `id` (string), `desdeByte` (0), `esperarSegundos` (0) | `ResultadoComando` | Lee la salida acumulada a partir del offset indicado en `desdeByte`. |
| `ssh_read_file` | `destino` (string), `ruta` (string), `lineaInicio` (1), `lineaFin` (null) | `string` (TextContent) | Lee un archivo del servidor remoto con soporte para rangos de líneas (1-indexed). Devuelve texto plano con saltos de línea reales sin envoltorios JSON. |
| `ssh_replace_file_content` | `destino` (string), `ruta` (string), `targetContent` (string), `replacementContent` (string), `lineaInicio` (1), `lineaFin` (null), `allowMultiple` (false), `sudo` (false) | `string` | Sustituye quirúrgicamente un bloque de texto dentro de un rango delimitado de líneas con swap atómico. |
| `ssh_reset_shell` | `destino` (string) | `ResultadoAccion` | Reinicia el shell bash sobre la conexión TCP existente, descartando el entorno previo. |
| `ssh_search` | `destino` (string), `patron` (string), `ruta` ("."), `extension` (null), `maxResultados` (50) | `string` | Busca patrones de texto o regex con filtro opcional por extensión o glob ignorando carpetas ruidosas (.git, node_modules, .venv, bin, obj, etc.). |
| `ssh_set_cwd` | `destino` (string), `ruta` (null) | `string` | Establece o restablece el directorio de trabajo persistente del shell remoto. Si `ruta` es null o vacía, se restablece a `$HOME`. |
| `ssh_stat` | `destino` (string), `ruta` (string) | `string` (JSON) | Devuelve metadatos básicos estructurados (existe, tipo, tamaño en bytes, tamaño legible, permisos, última modificación) de forma defensiva. |
| `ssh_status` | `destino` (opcional, string) | `ResultadoEstado` | Reporta el estado de sesiones activas y las últimas 20 peticiones procesadas. Cada sesión lleva su destino y, si los tiene, los alias con que se usó (`alias`) y las claves de su `IdentityFile` (`identityFile`). |
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

### Mecanismo de paginación y lectura

- El parámetro `esperarSegundos` está acotado al rango de 0 a 120 segundos.
- Cada bloque de respuesta tiene un tamaño máximo de 64 KiB (`MaxBytesPorRespuesta`).
- Si un comando genera una salida extensa o continúa en ejecución (`HasMasSalida = true`), las lecturas posteriores deben realizarse invocando `ssh_output` pasando en `desdeByte` el valor de `SiguienteByte` obtenido en la respuesta previa.

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

Por motivos de seguridad, el destino debe estar registrado previamente en el archivo `~/.ssh/known_hosts` local (admite entradas en texto plano, con hashing estándar y formatos `[host]:puerto`). Con un alias de `~/.ssh/config` se busca el `HostName` y el `Port` reales (ver «Configuración de ssh»).

Si el host no se encuentra registrado, la primera llamada fallará inmediatamente devolviendo el comando `ssh-keyscan` exacto que debe ejecutarse de forma manual tras verificar la huella digital del servidor remoto.

### Configuración de ssh (`~/.ssh/config`)

Si el cliente de OpenSSH está instalado, cada destino se resuelve con `ssh -G <destino>` (la misma lógica de `Host`, `Match` e `Include` que ssh) antes de conectar, y se usan su `HostName`, `User`, `Port` e `IdentityFile`. Así el destino puede ser un alias (`ssh_exec(destino="web", …)`). Se ejecuta en cada llamada (unos milisegundos), así que un cambio en la configuración vale desde la llamada siguiente; se aplica también a IPs y nombres reales (`Host *`, `Match`).

- **Qué fichero:** `NATIVE_SSH_CONFIG_SSH` con una ruta usa ese fichero (`ssh -F`, que no lee `/etc/ssh/ssh_config`); `NATIVE_SSH_CONFIG_SSH=none` no usa ninguna configuración (el destino se toma tal cual). Sin ella: si está definida `NATIVE_SSH_DIRECTORIO_SSH`, se usa el `config` de ese directorio si existe al arrancar, y si no, ninguno (nunca el `~/.ssh/config` real cuando se ha pedido otro directorio); si no, la configuración de ssh por defecto (`~/.ssh/config` y `/etc/ssh/ssh_config`).
- **Precedencia:** lo que lleva el destino manda: `usuario@` se pasa a ssh con `-l` y `:puerto` con `-p` (así `Match user` y `%r` lo ven, como en ssh). Para las claves, `NATIVE_SSH_IDENTIDADES`, si está definida, manda y los `IdentityFile` se ignoran; si no, los `IdentityFile` de la configuración sustituyen a las claves por defecto de native_ssh. Si la configuración no pone ningún `IdentityFile`, se usan las de native_ssh (las de `NATIVE_SSH_DIRECTORIO_SSH`), no las `~/.ssh/id_*` que ssh listaría por defecto.
- **Varios `IdentityFile`:** se prueban todos, en orden y sin repetir; los que no existen se saltan, como en ssh. Ojo con `MaxAuthTries` del servidor (6 por defecto) si son muchos.
- **Rutas de `IdentityFile`:** `ssh -G` no las expande; native_ssh expande `~` inicial, `%d`, `%u`, `%h` (el `HostName`), `%r`, `%p`, `%n` (el alias), `%%` y `${VARIABLE}` (salvo las variables secretas de native_ssh, `NATIVE_SSH_PASSWORD`, `NATIVE_SSH_PASSPHRASE` y `NATIVE_SSH_TOKEN_HTTP`, que cuentan como no definidas: ssh tampoco las recibe y su valor acabaría en los mensajes y el log). Cualquier otro token (`%C`, `%l`, `%i`…), `~usuario`, una variable sin definir o una ruta relativa (sería relativa al directorio de trabajo de native_ssh) dan un error del destino que lo dice, como haría ssh.
- **`known_hosts` y límites intactos:** la verificación estricta usa siempre los `known_hosts` de `NATIVE_SSH_DIRECTORIO_SSH`, buscando el `HostName` y el `Port` reales; `StrictHostKeyChecking`, `UserKnownHostsFile` y `HostKeyAlias` de la configuración se ignoran. El límite de conexiones también va por el servidor real, sean cuales sean las claves.
- **Sesiones y claves (qué se comparte):** como en ssh, cada alias se autentica con sus claves. La sesión (conexión, shell, variables exportadas y directorio de `ssh_set_cwd`) es una por `usuario@HostName:Port` **y** lista de claves (las de sus `IdentityFile`, en orden): un alias y su `usuario@HostName:Port`, o dos alias, comparten sesión solo si resuelven a las mismas claves; dos alias al mismo servidor con `IdentityFile` distintos tienen sesiones distintas (un alias con una clave no autorizada no entra con la sesión de otro, ni le estropea la suya). Lo que sí comparten es el límite de conexiones del servidor y `known_hosts`. Si cambia el `IdentityFile` de un alias con la sesión abierta, la llamada siguiente usa una sesión nueva con las claves nuevas; la anterior sigue abierta (native_ssh no cierra sesiones por inactividad) hasta `ssh_disconnect`, el fin de native_ssh o que el servidor la corte.
- **`ssh_disconnect` y `ssh_status` con destino:** con un alias (un nombre que la configuración cambia por otro `HostName`) actúan sobre la sesión de ese alias, la de su configuración actual y las que se abrieron con él antes de cambiarla (así no responde «No había conexión» si cambió su `HostName` con la sesión viva). Con el servidor real (`usuario@servidor[:puerto]` o un nombre que la configuración no cambia), sobre todas las sesiones de ese `usuario@servidor:puerto`, sean de las claves que sean; `ssh_disconnect` dice cuántas cerró. `ssh_reset_shell` actúa sobre la sesión que usaría el próximo comando con ese destino.
- **`ProxyJump` y `ProxyCommand`:** no se admiten; un destino cuya configuración los usa se rechaza con un error que lo dice (conectar directamente al `HostName` se saltaría el salto y podría llegar a otra máquina). Queda como posible bloque futuro.
- **Destino seguro para ssh:** el destino lo escribe el LLM; con la configuración activa, servidor y usuario solo admiten letras y números ASCII, `.`, `_`, `-` (y `:` el servidor, para IPv6), sin empezar por `-` (una IPv6 con zona, `fe80::1%eth0`, no se admite así: usa `NATIVE_SSH_CONFIG_SSH=none`), y el servidor se pasa tras `--`. Así no puede inyectar opciones a `ssh -G` ni URIs `ssh://` que cambien usuario o puerto.
- **Plazo:** si `ssh -G` no termina en 5 s (un `Match exec` o una búsqueda DNS de `CanonicalizeHostname` que se cuelga), se mata junto con lo que lanzó y el destino da un error. En Linux, `ssh -G` se lanza con `setsid` (si está instalado; viene en util-linux) en su propio grupo de procesos: si un `Match exec` deja algo en segundo plano con la salida de ssh abierta, ese grupo se mata medio segundo después de que ssh termine y el destino se resuelve con lo que ssh escribió (sin `setsid`, solo se puede matar lo que sigue colgando de ssh, y el destino da un error que lo dice).
- **Sin el programa `ssh`:** el destino se usa tal cual (comportamiento anterior) y se anota un aviso en el log una sola vez.
- `ssh -G` se ejecuta sin `NATIVE_SSH_PASSWORD`, `NATIVE_SSH_PASSPHRASE` ni `NATIVE_SSH_TOKEN_HTTP` en su entorno (un `Match exec` los vería). Los `Match exec` de tu configuración sí se ejecutan, como al usar ssh.

---

## 3. Credenciales y autenticación

El servidor busca credenciales en el siguiente orden de precedencia:

1. **Claves privadas:** Búsqueda automática en las rutas por defecto:
   - `~/.ssh/id_ed25519`
   - `~/.ssh/id_ecdsa`
   - `~/.ssh/id_rsa`
   O en las rutas especificadas mediante la variable `NATIVE_SSH_IDENTIDADES`, o en los `IdentityFile` de la configuración de ssh (ver «Configuración de ssh»).
2. **Contraseña explícita:** Configurada mediante la variable de entorno `NATIVE_SSH_PASSWORD`.

### Claves con passphrase

Una clave cifrada (formatos OpenSSH, PEM y PKCS#8) se abre con la passphrase de `NATIVE_SSH_PASSPHRASE_FICHERO` (recomendado) o de `NATIVE_SSH_PASSPHRASE`, nunca las dos. Nunca sale del LLM: ninguna herramienta la pide ni la acepta.

- El fichero se comprueba igual que el del token HTTP: un fichero normal (no un enlace simbólico, una FIFO ni un directorio) del usuario que ejecuta native_ssh y sin permisos para el grupo ni para otros (`chmod 600`), comprobado en el fichero ya abierto. Solo Linux. Se quitan los saltos de línea finales; los espacios de los extremos sí son parte de la passphrase. Un fichero vacío o con varias líneas no se usa: el motivo de la clave lo dice.
- Una sola passphrase para todas las claves cifradas. Solo se lee cuando alguna clave está cifrada (con claves sin cifrar, el fichero ni se abre), no se guarda y nunca aparece en respuestas ni logs.
- Si falta, es incorrecta o el fichero no cumple lo anterior, la conexión falla con un mensaje que da el motivo de cada clave («La clave … no se pudo cargar: está cifrada con passphrase y no hay ninguna configurada…», «…la passphrase de NATIVE_SSH_PASSPHRASE no la abre…», «…tiene permisos 0644…»). Una clave que no se pudo abrir se reintenta en cuanto cambia su fichero o el de la passphrase (fecha de modificación, tamaño o permisos): basta con arreglarlo, sin reiniciar. Mientras nada cambie no se vuelve a intentar (abrir una clave cifrada cuesta casi un segundo): cada conexión repite el mismo motivo y el log lo avisa una sola vez. Con `NATIVE_SSH_PASSPHRASE`, que solo se lee al arrancar, cambiarla exige reiniciar native_ssh.
- La variable de entorno queda visible para los procesos del mismo usuario (`/proc/<pid>/environ`): por eso se recomienda el fichero.

> [!NOTE]
> No se admite `ssh-agent`.

---

## 4. Variables de entorno

| Variable | Tipo | Valor por defecto | Descripción |
|---|---|---|---|
| `NATIVE_SSH_DIRECTORIO_SSH` | Ruta | `~/.ssh` | Directorio base donde se ubican las claves, el archivo `known_hosts` y, si existe, el `config` de ssh (ver «Configuración de ssh»). |
| `NATIVE_SSH_IDENTIDADES` | Rutas (`:`) | Claves estándar en `DirectorioSsh` | Lista de rutas a claves privadas separadas por `:`. |
| `NATIVE_SSH_PASSWORD` | Cadena | *(Vacío)* | Contraseña de autenticación para destinos remotos. |
| `NATIVE_SSH_PASSPHRASE` | Cadena | *(Vacío)* | Passphrase de las claves cifradas (ver «Claves con passphrase»). Mejor el fichero de la variable siguiente; nunca las dos. |
| `NATIVE_SSH_PASSPHRASE_FICHERO` | Ruta | *(Vacío)* | Solo Linux. Fichero con la passphrase, con los mismos requisitos que el del token HTTP (`chmod 600`, propio, sin enlaces). Se quitan los saltos de línea finales; los espacios se conservan. |
| `NATIVE_SSH_CONFIG_SSH` | Ruta o `none` | Ver «Configuración de ssh» | Fichero de configuración de ssh con el que se resuelven los destinos (`ssh -G -F`), o `none` para no usar ninguno. Sin ella: el `config` de `NATIVE_SSH_DIRECTORIO_SSH` si está definida (y existe), o la configuración de ssh por defecto. |
| `NATIVE_SSH_PUERTO_HTTP` | Entero | `8722` | Puerto TCP de escucha para los modos HTTP. |
| `NATIVE_SSH_NIVEL_LOG` | Nivel log | `Information` | Nivel mínimo de trazas (`Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`). Todos los logs se emiten invariablemente por `stderr`. Las categorías del SDK de MCP (`ModelContextProtocol.*`) nunca bajan de `Debug`: en `Trace` escribirían cada mensaje JSON-RPC completo (argumentos como el texto de `ssh_input` y respuestas con salidas y archivos). |
| `NATIVE_SSH_TOKEN_HTTP` | Cadena | *(Vacío)* | Token de los modos HTTP (al menos 16 caracteres). Obligatorio en `--http` y `--http-unsecure` (o el fichero de la variable siguiente, nunca las dos); no se usa con stdio. |
| `NATIVE_SSH_TOKEN_HTTP_FICHERO` | Ruta | *(Vacío)* | Solo Linux. Fichero con el token: un fichero normal (no un enlace simbólico, una FIFO ni un directorio) del usuario que ejecuta native_ssh y solo legible por él (`chmod 600`); si no, no arranca. Se comprueba en el fichero ya abierto, sin seguir el enlace final. Se quitan los saltos de línea finales; un token que empieza o termina con espacios se rechaza. |

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

Los dos modos HTTP exigen un token (`NATIVE_SSH_TOKEN_HTTP` o `NATIVE_SSH_TOKEN_HTTP_FICHERO`); sin él, o con un fichero que no cumple lo anterior, el servidor no arranca (código 2 y el motivo en `stderr`). Cada petición sin `Authorization: Bearer <token>` exacto recibe `401` con un mensaje claro; el token se compara en tiempo constante y nunca se escribe en los logs. Los rechazos se anotan en el log como mucho 10 veces por minuto; los omitidos se cuentan en el siguiente aviso que se anote (en un minuto posterior), no al terminar la ráfaga.

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
- **Agentes:** No existe soporte para reenvío de credenciales vía `ssh-agent`. Las claves con passphrase sí se admiten (ver «Claves con passphrase»).
- **Saltos (`ProxyJump`, `ProxyCommand`):** Un destino cuya configuración de ssh los usa se rechaza; tampoco se usan `HostKeyAlias`, `UserKnownHostsFile`, `CertificateFile` ni `IdentitiesOnly` de la configuración.
- **Túneles SSH:** No se contempla el reenvío de puertos locales o remotos (*port forwarding*).
- **Seguridad en HTTP:** El transporte HTTP no implementa mecanismos integrados de autenticación ni autorización.
- **Renegociación de claves con strict-kex (corregido en el submódulo):** SSH.NET 2026.0.0 aplica la regla estricta de *strict key exchange* («cortar ante cualquier mensaje inesperado») también a las renegociaciones, aunque la especificación de OpenSSH (`PROTOCOL`, §1.9, que remite al borrador IETF `draft-miller-sshm-strict-kex`) la limita al intercambio inicial; como OpenSSH 10.5 envía `SSH_MSG_IGNORE` entre `KEXINIT` y `NEWKEYS` al renegociar, SSH.NET cortaba la conexión (se perdía el shell con sus `export` y se gastaba una conexión del cupo de `ufw limit`; medido con `RekeyLimit 64K 2s`: 25 cortes en 56 renegociaciones en 60 s). native_ssh compila SSH.NET desde el submódulo `externo/SSH.NET` (rama `native-ssh`), que lo corrige: la regla estricta solo en el intercambio inicial, manteniendo el reinicio de los números de secuencia tras cada `NEWKEYS`. Ya no es una limitación de native_ssh; sí lo es del paquete NuGet de SSH.NET 2026.0.0 mientras la corrección no llegue al proyecto original.

---

## 11. Líneas de optimización pendientes (D11)

- **Asignaciones residuales por comando:** Reducción de objetos en el heap asociados al ciclo de vida de la petición: instancia de `Peticion`, `TaskCompletionSource`, tokens de `CancellationTokenSource` y la instanciación inmutable de `string` para cada página de salida entregada.
- **Procesamiento de secuencias ANSI:** Optimización de `ConversorTexto.LimpiarAnsi` para sustituir el análisis byte a byte actual por algoritmos vectorizados (SIMD) o escaneo por saltos.
- **Diferencial de rendimiento AOT vs JIT:** En Native AOT sin perfilado dinámico guiado por optimización (PGO dinámico), métodos intensivos como `Constructor_Comando` y `LimpiarAnsi` presentan una latencia ligeramente superior respecto al runtime JIT.
