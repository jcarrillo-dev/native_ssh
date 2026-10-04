# native_ssh

Servidor MCP (*Model Context Protocol*) desarrollado en .NET 10 y compilado en Native AOT, diseñado para interactuar con servidores remotos mediante un shell bash persistente por destino a través de transporte stdio o HTTP (Streamable HTTP).

---

## 1. Descripción general y herramientas

El servidor mantiene una sesión SSH persistente con un shell PTY interactivo por cada destino (`usuario@servidor[:puerto]`). Los comandos se ejecutan secuencialmente en una cola FIFO dentro del mismo shell, permitiendo que variables de entorno, funciones y estado de sesión persistan entre invocaciones sucesivas, mientras que el directorio de trabajo se reinicia automáticamente al directorio configurado en `__NS_CWD` (o al directorio personal `~` por defecto) tras cada ejecución.

### Herramientas MCP registradas

| Herramienta | Parámetros (por defecto) | Devuelve | Descripción |
|---|---|---|---|
| `ssh_exec` | `destino` (string), `comando` (string), `timeoutSegundos` (300), `esperarSegundos` (20) | `ResultadoComando` | Ejecuta un comando en el shell del destino; devuelve la salida desde el byte 0. |
| `ssh_output` | `id` (string), `desdeByte` (0), `esperarSegundos` (0) | `ResultadoComando` | Lee la salida acumulada a partir del offset indicado en `desdeByte`. |
| `ssh_input` | `id` (string), `texto` (string), `enter` (true), `esperarSegundos` (2) | `ResultadoComando` | Envía texto a la entrada estándar del proceso en ejecución y captura la salida posterior. |
| `ssh_cancel` | `id` (string) | `ResultadoComando` | Cancela la petición; espera hasta `EsperaTrasCtrlC` + 2 s antes de responder. |
| `ssh_status` | `destino` (opcional, string) | `ResultadoEstado` | Reporta el estado de sesiones activas y las últimas 20 peticiones procesadas. |
| `ssh_disconnect` | `destino` (string) | `ResultadoAccion` | Cierra la conexión SSH activa con el destino indicado. |
| `ssh_reset_shell` | `destino` (string) | `ResultadoAccion` | Reinicia el shell bash sobre la conexión TCP existente, descartando el entorno previo. |
| `ssh_read_file` | `destino` (string), `ruta` (string), `lineaInicio` (1), `lineaFin` (null) | `string` (TextContent) | Lee un archivo del servidor remoto con soporte para rangos de líneas (1-indexed). Devuelve texto plano con saltos de línea reales sin envoltorios JSON. |

### Lectura limpia de archivos (`ssh_read_file`)

La herramienta `ssh_read_file` permite inspeccionar archivos y fragmentos de código fuente directamente desde el servidor remoto sin necesidad de ejecutar comandos `cat` o `sed` manuales a través de `ssh_exec`. A diferencia de `ssh_exec`, que devuelve un objeto JSON estructurado (`ResultadoComando`), `ssh_read_file` devuelve directamente texto plano (`TextContent`) con saltos de línea reales (`\n`), evitando la serialización JSON con caracteres escapados (`\n`, `\"`).

Admite lectura acotada mediante `lineaInicio` (por defecto 1) y `lineaFin` (por defecto hasta 800 líneas a partir de inicio). Si la ruta no existe o es un directorio, falla inmediatamente reportando el error.

### Mecanismo de paginación y lectura

- El parámetro `esperarSegundos` está acotado al rango de 0 a 120 segundos.
- Cada bloque de respuesta tiene un tamaño máximo de 64 KiB (`MaxBytesPorRespuesta`).
- Si un comando genera una salida extensa o continúa en ejecución (`HasMasSalida = true`), las lecturas posteriores deben realizarse invocando `ssh_output` pasando en `desdeByte` el valor de `SiguienteByte` obtenido en la respuesta previa.

### Directorio de trabajo persistente (`__NS_CWD`)

Por defecto, el directorio de trabajo se restablece automáticamente a `$HOME` (`~`) tras cada comando. La variable `__NS_CWD` permite al usuario o agente cambiar el directorio base de la sesión persistente simplemente ejecutando:

```bash
export __NS_CWD=/ruta/del/proyecto
```

Si la variable `__NS_CWD` no está definida o apunta a una ruta inexistente, el shell aplica de forma transparente un *fallback* a `$HOME` (`cd ~`), asegurando que la ejecución continúe con normalidad y sin errores.

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
| `NATIVE_SSH_NIVEL_LOG` | Nivel log | `Information` | Nivel mínimo de trazas (`Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`). Todos los logs se emiten invariablemente por `stderr`. |

---

## 5. Modos de arranque

El ejecutable admite el transporte estándar a través de `stdio` o transporte Streamable HTTP mediante ASP.NET Core Kestrel:

```bash
NativeSsh [--http | --http-unsecure] [--puerto N]
```

| Modo | Escucha | Autenticación | Log al arrancar |
|---|---|---|---|
| *(sin argumentos)* | stdio | — | — |
| `--http` | `127.0.0.1` y `[::1]` (`ListenLocalhost`) | Ninguna | `Information: URL http://localhost:8722/mcp` |
| `--http-unsecure` | Todas las interfaces (`ListenAnyIP`) | **Ninguna** | `Warning: «SIN autenticación…»` |

> [!WARNING]
> El modo `--http-unsecure` expone sin autenticación un servicio con capacidad para ejecutar comandos arbitrarios en los servidores remotos configurados. Este modo debe reservarse exclusivamente para redes privadas de total confianza o entornos aislados tras un proxy inverso con autenticación perimetral estricta.

---

## 6. Configuración en Claude Code

### Transporte por stdio

Registro directo del binario compilado:

```bash
claude mcp add native_ssh -- /ruta/artefactos/NativeSsh
```

### Transporte por HTTP

Arrancar el servidor en modo HTTP:

```bash
/ruta/artefactos/NativeSsh --http
```

Y registrar el endpoint correspondiente:

```bash
claude mcp add --transport http native_ssh http://localhost:8722/mcp
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

---

## 11. Líneas de optimización pendientes (D11)

- **Asignaciones residuales por comando:** Reducción de objetos en el heap asociados al ciclo de vida de la petición: instancia de `Peticion`, `TaskCompletionSource`, tokens de `CancellationTokenSource` y la instanciación inmutable de `string` para cada página de salida entregada.
- **Procesamiento de secuencias ANSI:** Optimización de `ConversorTexto.LimpiarAnsi` para sustituir el análisis byte a byte actual por algoritmos vectorizados (SIMD) o escaneo por saltos.
- **Diferencial de rendimiento AOT vs JIT:** En Native AOT sin perfilado dinámico guiado por optimización (PGO dinámico), métodos intensivos como `Constructor_Comando` y `LimpiarAnsi` presentan una latencia ligeramente superior respecto al runtime JIT.
