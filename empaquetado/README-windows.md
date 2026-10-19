# native_ssh @VERSION@ para Windows x64 (@VARIANTE@)

native_ssh es un servidor MCP que da a un cliente MCP @HERRAMIENTAS@ herramientas para trabajar en servidores Linux por SSH: un shell persistente por servidor (`ssh_exec`, con sus variables y su directorio entre comandos), lectura, búsqueda y edición de archivos sin pasar por el shell, y control de sesiones. Es un único ejecutable nativo, sin dependencias de .NET.

Este README explica cómo instalarlo y configurarlo en Windows. Está escrito para que lo siga paso a paso quien configure el cliente MCP, sea una persona o el propio modelo.

## Versión

Este paquete es la **versión @VERSION@**. Para comprobarla, en PowerShell:

```powershell
& "$env:LOCALAPPDATA\native_ssh\NativeSsh.exe" --version
```

Imprime `native_ssh @VERSION@` y termina con código 0, sin arrancar el servidor (es el mismo nombre y versión que da `serverInfo` al cliente MCP).

## Contenido del paquete

| Ruta | Qué es |
|---|---|
| `NativeSsh.exe` | El servidor MCP (Windows x64, @NIVEL@). |
| `skill-native-ssh\` | Skill con las pautas para combinar las herramientas (salidas grandes, paralelismo, rechazos). Ver «Instalar la skill». |
| `README.md` | Este archivo. |
| `LICENSE`, `THIRD-PARTY-NOTICES.md` | Licencia de native_ssh (MIT) y avisos de las bibliotecas de terceros que lleva el binario. |

## Qué paquete elegir

Hay tres paquetes de Windows con el mismo programa; solo cambia el juego de instrucciones de la CPU para el que se compiló:

- **`native_ssh-@VERSION@-win-x64.zip`**: nivel por defecto (x86-64 base). Funciona en cualquier CPU x64 y en cualquier máquina virtual. Es el de reserva.
- **`native_ssh-@VERSION@-win-x64-v3.zip`**: compilado para x86-64-v3 (AVX2, BMI1, BMI2, FMA, LZCNT, MOVBE, F16C): Intel desde Haswell (2013), AMD desde Excavator y todos los Zen. No arranca en CPU más antiguas ni en máquinas virtuales con una CPU genérica.
- **`native_ssh-@VERSION@-win-x64-v4.zip`**: compilado para x86-64-v4 (lo de v3 más AVX-512 F, BW, CD, DQ y VL): AMD desde Zen 4 e Intel con AVX-512 (Xeon desde Skylake-SP, Ice Lake, Tiger Lake, Rocket Lake…; no los Core de 12.ª a 14.ª generación, que lo traen desactivado).

La ganancia de v3 y v4 es pequeña: native_ssh pasa casi todo el tiempo esperando a la red. Ante la duda, el de nivel por defecto. Para saber si un nivel vale en esta CPU, basta con ejecutar su `NativeSsh.exe --version` (ver «Versión»): si arranca, vale.

**Si el `-v3` o el `-v4` no arranca:** el proceso termina nada más empezar con el código `0xC0000602` (en PowerShell, `$LASTEXITCODE` = `-1073740286`) y escribe en su salida de errores:

```
The required instruction sets are not supported by the current CPU.
```

En el cliente MCP se ve solo que el servidor no conecta o se cierra al iniciar. Basta con cambiarlo por el `NativeSsh.exe` de un nivel inferior (el `-v3` o el del paquete `native_ssh-@VERSION@-win-x64.zip`), con la misma configuración. Para comprobarlo a mano, en PowerShell: `$null | & "C:\ruta\NativeSsh.exe"; $LASTEXITCODE` (el de nivel por defecto termina con `0`).

### Compilar para tu CPU

Desde el código fuente (sección «Compilación y pruebas» del README del repositorio; en Windows hacen falta además las herramientas de C++ de Visual Studio) se puede compilar para las instrucciones exactas de la CPU de la máquina que compila:

```powershell
dotnet publish native_ssh\native_ssh.csproj -c Release -r win-x64 -p:IlcInstructionSet=native -o artefactos
```

**Ese ejecutable solo sirve en esa CPU o en otra que tenga todas sus instrucciones**; en cualquier otra aborta al arrancar con «The required instruction sets are not supported by the current CPU.». No lo repartas a otras máquinas: para eso están los paquetes normal, `-v3` y `-v4`.

## Requisitos

- Windows 10 o 11 de 64 bits (x64).
- **Opcional, el cliente `ssh`:** el «Cliente OpenSSH» de Windows (`C:\Windows\System32\OpenSSH\ssh.exe`, de serie en Windows 10 1809 y posteriores) o el de Git for Windows. Si está en el `PATH`, native_ssh resuelve cada destino con `ssh -G`, así que se pueden usar los alias de `%USERPROFILE%\.ssh\config`. Sin él, el destino se usa tal cual (`usuario@servidor[:puerto]`). Se busca solo en el `PATH`, nunca en la carpeta actual ni junto a `NativeSsh.exe`.
- **En el servidor remoto:** Linux con `bash`, `stty` y `base64` (ver «Diferencias y límites»).

## Instalación

En PowerShell:

```powershell
$destino = "$env:LOCALAPPDATA\native_ssh"
$tmp = Join-Path $env:TEMP ("native_ssh_" + [guid]::NewGuid())
Expand-Archive -Path .\@ZIP@ -DestinationPath $tmp
New-Item -ItemType Directory -Force $destino | Out-Null
Copy-Item -Recurse -Force "$tmp\@CARPETA@\*" $destino
Remove-Item -Recurse -Force $tmp
Unblock-File "$destino\NativeSsh.exe"
"$destino\NativeSsh.exe"
```

La última línea imprime la **ruta absoluta** del ejecutable (p. ej. `C:\Users\usuario\AppData\Local\native_ssh\NativeSsh.exe`), que es la que piden los clientes MCP. `Unblock-File` quita la marca de «descargado de Internet» para que Windows no lo bloquee.

### Credenciales y `known_hosts`

- **Claves:** por defecto, `%USERPROFILE%\.ssh\id_ed25519`, `id_ecdsa` e `id_rsa`, o los `IdentityFile` de `%USERPROFILE%\.ssh\config`. No se usa `ssh-agent` ni Pageant. Una clave con passphrase necesita `NATIVE_SSH_PASSPHRASE` (ver «Variables de entorno»).
- **`known_hosts`:** la clave de cada servidor tiene que estar ya en `%USERPROFILE%\.ssh\known_hosts` (o `known_hosts2`). native_ssh **no acepta claves de host nuevas**: si el servidor no está, falla con un error que dice `… no está en ninguno de estos known_hosts …` y da la huella. Entonces el usuario debe comprobar esa huella y conectarse él una vez a mano desde PowerShell (`ssh usuario@servidor`), que la guarda en ese fichero. El modelo no debe añadirla nunca por su cuenta ni desactivar la comprobación.

## Registro en el cliente MCP (stdio)

En Windows native_ssh solo funciona por **stdio** (no hay modo HTTP). En los ejemplos, sustituye `C:\Users\usuario\AppData\Local\native_ssh\NativeSsh.exe` por la ruta real.

### Claude Code

En PowerShell:

```powershell
claude mcp add --scope user native_ssh -- "C:\Users\usuario\AppData\Local\native_ssh\NativeSsh.exe"
```

Con variables de entorno, cada una con `-e` antes del nombre:

```powershell
claude mcp add --scope user -e NATIVE_SSH_SOLO_LECTURA=prod.ejemplo.com native_ssh -- "C:\Users\usuario\AppData\Local\native_ssh\NativeSsh.exe"
```

### Claude Desktop

Abre el fichero de configuración desde la propia aplicación (Configuración → Desarrollador → Editar configuración; suele ser `%APPDATA%\Claude\claude_desktop_config.json`) y añade la entrada al bloque `mcpServers`. En JSON cada `\` de la ruta se escribe `\\`:

```json
{
  "mcpServers": {
    "native_ssh": {
      "command": "C:\\Users\\usuario\\AppData\\Local\\native_ssh\\NativeSsh.exe",
      "env": {
        "NATIVE_SSH_NIVEL_LOG": "Warning"
      }
    }
  }
}
```

Después, cierra Claude Desktop del todo (también desde el icono de la bandeja) y vuelve a abrirlo.

### Gemini CLI, agy y otros clientes con `mcpServers`

- agy: `agy mcp add native_ssh "C:\Users\usuario\AppData\Local\native_ssh\NativeSsh.exe"` (las opciones, como `-e CLAVE=valor`, van antes del nombre).
- Gemini CLI: el bloque `mcpServers` de `%USERPROFILE%\.gemini\settings.json`, con el formato de Claude Desktop.
- Otros clientes: el mismo bloque `mcpServers` en su fichero de configuración. `env` es opcional.

## Variables de entorno

| Variable | Para qué |
|---|---|
| `NATIVE_SSH_DIRECTORIO_SSH` | Directorio de las claves, `known_hosts` y `config` (por defecto `%USERPROFILE%\.ssh`). |
| `NATIVE_SSH_IDENTIDADES` | Rutas de claves privadas separadas por **`;`**. Si está, manda sobre los `IdentityFile`. |
| `NATIVE_SSH_PASSPHRASE` | Passphrase de las claves cifradas (en Windows no existe la variante con fichero). Queda en claro en la configuración del cliente: protégela. |
| `NATIVE_SSH_PASSWORD` | Contraseña SSH, si el servidor no usa claves. |
| `NATIVE_SSH_CONFIG_SSH` | Fichero de configuración de ssh para resolver los destinos, o `none` para no usar ninguno. |
| `NATIVE_SSH_SOLO_LECTURA` | Destinos de solo lectura, separados por comas (ver abajo). |
| `NATIVE_SSH_NIVEL_LOG` | `Trace`, `Debug`, `Information` (por defecto), `Warning`, `Error`, `Critical` o `None`. El log va siempre por la salida de errores. |

Ninguna herramienta pide ni acepta contraseñas o passphrases: van solo en estas variables. Las variables del modo HTTP (`NATIVE_SSH_TOKEN_HTTP`…) no sirven en Windows: con ellas native_ssh no arranca.

### Modo solo lectura

`NATIVE_SSH_SOLO_LECTURA="prod.ejemplo.com, deploy@10.0.0.7:2222"` (o `*` para todos) rechaza en esos destinos `ssh_exec`, `ssh_input`, `ssh_write_file` y `ssh_replace_file_content` antes de conectar o enviar nada; las lecturas (`ssh_read_file`, `ssh_search`, `ssh_find`, `ssh_list_dir`, `ssh_stat`), `ssh_set_cwd` y `ssh_status` siguen funcionando. Es configuración local: ninguna herramienta la cambia. Si un nombre de la lista no se puede resolver, se rechazan las escrituras por seguridad (si dudas, pon la IP; para los nombres se mira también `C:\Windows\System32\drivers\etc\hosts`). Una entrada con formato no válido (`@prod`, `prod:0`, `*.ejemplo.com`…) impide arrancar; un nombre mal escrito que no se resuelve bloquea las escrituras a todos los destinos con su usuario y puerto hasta que se corrija.

## Comprobar que funciona

1. `--version` da `native_ssh @VERSION@` (ver «Versión»).
2. El ejecutable arranca y responde. En `cmd` (no en PowerShell), debe salir un JSON con `"serverInfo":{"name":"native_ssh","version":"@VERSION@"}`:

   ```bat
   (echo {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"prueba","version":"1"}}}& ping -n 3 127.0.0.1 >nul) | "C:\Users\usuario\AppData\Local\native_ssh\NativeSsh.exe" 2>nul
   ```

3. El cliente lo ve: `claude mcp list` (o `/mcp` dentro de Claude Code) muestra `native_ssh` conectado, con @HERRAMIENTAS@ herramientas; en Claude Desktop, aparece en Configuración → Desarrollador sin error.
4. Primera prueba real: `ssh_exec` con `destino` = `usuario@servidor` y `comando` = `echo ok`. Si falla por `known_hosts`, ver «Credenciales y `known_hosts`».

## Instalar la skill

La carpeta `skill-native-ssh\` (con `SKILL.md`) enseña al modelo a combinar las herramientas.

- Claude Code: cópiala como `%USERPROFILE%\.claude\skills\native-ssh` (en PowerShell: `Copy-Item -Recurse skill-native-ssh "$env:USERPROFILE\.claude\skills\native-ssh"`).
- agy: `%USERPROFILE%\.gemini\config\skills\native-ssh`.
- Claude Desktop y otros clientes con skills: comprime la carpeta en un `.zip` y súbela donde el cliente admita skills personalizadas (en Claude Desktop, en Configuración → Capacidades, si tu versión lo ofrece). Sin soporte de skills, las descripciones de las herramientas bastan para usarlas una a una.

## Diferencias y límites (Windows)

- **Solo stdio:** el modo HTTP (`--http`, `--http-unsecure`) no existe en Windows; tampoco `NATIVE_SSH_PASSPHRASE_FICHERO` ni `NATIVE_SSH_TOKEN_HTTP_FICHERO`.
- **Directorio personal:** `~\.ssh` y `~` en los `IdentityFile` salen de `USERPROFILE`. El ssh de Git for Windows usa `HOME`: si `HOME` apunta a otro sitio, los dos pueden leer configuraciones distintas.
- `NATIVE_SSH_IDENTIDADES` se separa con `;`. Un `IdentityFile /c/…` se entiende como `C:\…` solo si el ssh que leyó la configuración es el de Git for Windows.
- `ssh -G` escribe sus mensajes en un fichero temporal de `%TEMP%`: si `TEMP` no existe o no admite escritura, el destino da el error de ssh `Couldn't open logfile …` (corrige `TEMP` o usa `NATIVE_SSH_CONFIG_SSH=none`).
- Solo **servidores Linux** como destino, con `bash`, `stty` y `base64`; un servidor Windows (OpenSSH for Windows) se detecta y se rechaza sin ejecutar nada.
- Si el shell de login del usuario remoto es **zsh y no tiene ningún archivo de inicio** (`.zshenv`, `.zprofile`, `.zshrc`, `.zlogin`), el asistente de zsh para usuarios nuevos se queda esperando y `ssh_exec` agota la inicialización (15 s). Arreglo: crear un `~/.zshrc` en el servidor, aunque sea vacío.
- Sin `ProxyJump`/`ProxyCommand` (un destino que los usa se rechaza), sin certificados de host (`@cert-authority`) y sin `ssh-agent`/Pageant.
- Como mucho 4 conexiones nuevas cada 30 s por servidor y puerto; las sesiones se mantienen abiertas hasta `ssh_disconnect` o el fin de native_ssh.
- Sin interfaces de terminal completa (`vim`, `top`, `less`).
