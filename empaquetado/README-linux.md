# native_ssh @VERSION@ para Linux x64 (@VARIANTE@)

native_ssh es un servidor MCP que da a un cliente MCP @HERRAMIENTAS@ herramientas para trabajar en servidores Linux por SSH: un shell persistente por servidor (`ssh_exec`, con sus variables y su directorio entre comandos), lectura, búsqueda y edición de archivos sin pasar por el shell, y control de sesiones. Es un único binario nativo, sin dependencias de .NET.

Este README explica cómo instalarlo y configurarlo. Está escrito para que lo siga paso a paso quien configure el cliente MCP, sea una persona o el propio modelo.

## Versión

Este paquete es la **versión @VERSION@**. Para comprobarla:

```bash
/home/usuario/.local/share/native_ssh/NativeSsh --version
```

Imprime `native_ssh @VERSION@` y termina con código 0, sin arrancar el servidor (es el mismo nombre y versión que da `serverInfo` al cliente MCP).

## Contenido del paquete

| Ruta | Qué es |
|---|---|
| `NativeSsh` | El servidor MCP (binario de Linux x64, @NIVEL@). |
| `skill-native-ssh/` | Skill con las pautas para combinar las herramientas (salidas grandes, paralelismo, rechazos). Ver «Instalar la skill». |
| `README.md` | Este archivo. |
| `LICENSE`, `THIRD-PARTY-NOTICES.md` | Licencia de native_ssh (MIT) y avisos de las bibliotecas de terceros que lleva el binario. |

## Qué paquete elegir

Hay tres paquetes de Linux con el mismo programa; solo cambia el juego de instrucciones de la CPU para el que se compiló:

- **`native_ssh-@VERSION@-linux-x64.tar.gz`**: nivel por defecto (x86-64 base). Funciona en cualquier CPU x64 y en cualquier máquina virtual. Es el de reserva.
- **`native_ssh-@VERSION@-linux-x64-v3.tar.gz`**: compilado para x86-64-v3 (AVX2, BMI1, BMI2, FMA, LZCNT, MOVBE, F16C): Intel desde Haswell (2013), AMD desde Excavator y todos los Zen. No arranca en CPU más antiguas ni en máquinas virtuales con una CPU genérica (p. ej. `qemu64`).
- **`native_ssh-@VERSION@-linux-x64-v4.tar.gz`**: compilado para x86-64-v4 (lo de v3 más AVX-512 F, BW, CD, DQ y VL): AMD desde Zen 4 e Intel con AVX-512 (Xeon desde Skylake-SP, Ice Lake, Tiger Lake, Rocket Lake…; no los Core de 12.ª a 14.ª generación, que lo traen desactivado).

La ganancia de v3 y v4 es pequeña: native_ssh pasa casi todo el tiempo esperando a la red. Ante la duda, el de nivel por defecto.

Para saber qué niveles admite la CPU (glibc 2.33 o posterior):

```bash
/lib64/ld-linux-x86-64.so.2 --help | grep -E 'x86-64-v[34]'
```

Si dice `x86-64-v4 (supported, searched)`, vale el `-v4`; si solo `x86-64-v3 (supported, searched)`, el `-v3`. Si ninguno sale como `supported`, usa el de nivel por defecto.

**Si el `-v3` o el `-v4` no arranca:** el proceso termina nada más empezar (código 134, señal SIGABRT) y escribe en su salida de errores:

```
The required instruction sets are not supported by the current CPU.
```

En el cliente MCP se ve solo que el servidor no conecta o se cierra al iniciar. Basta con cambiarlo por el binario de un nivel inferior (el `-v3` o el del paquete `native_ssh-@VERSION@-linux-x64.tar.gz`), con la misma configuración.

### Compilar para tu CPU

Desde el código fuente (sección «Compilación y pruebas» del README del repositorio) se puede compilar para las instrucciones exactas de la CPU de la máquina que compila:

```bash
dotnet publish native_ssh/native_ssh.csproj -c Release -r linux-x64 -p:IlcInstructionSet=native -o artefactos
```

**Ese binario solo sirve en esa CPU o en otra que tenga todas sus instrucciones**; en cualquier otra aborta al arrancar con «The required instruction sets are not supported by the current CPU.». No lo repartas a otras máquinas: para eso están los paquetes normal, `-v3` y `-v4`.

## Requisitos de la máquina donde corre native_ssh

- Linux x86-64 con **glibc 2.34 o posterior** (p. ej. Debian 12, Ubuntu 22.04, Fedora 35, RHEL 9 o más recientes). No sirve en distribuciones con musl (Alpine).
- **OpenSSL** (`libssl.so.3`, o `libssl.so.1.1`): sin él, el proceso se cierra en la primera conexión con `No usable version of libssl was found`. Viene instalado en casi todas las distribuciones; en una imagen mínima, instala `libssl3` (Debian/Ubuntu) u `openssl-libs` (Fedora/RHEL).
- **Opcional, el cliente `ssh` de OpenSSH:** si está en el `PATH`, native_ssh resuelve cada destino con `ssh -G`, así que se pueden usar los alias de `~/.ssh/config` (`HostName`, `User`, `Port`, `IdentityFile`). Sin él, el destino se usa tal cual (`usuario@servidor[:puerto]`).
- **En el servidor remoto:** Linux con `bash`, `stty` y `base64` (ver «Diferencias y límites»).

## Instalación

```bash
mkdir -p ~/.local/share/native_ssh
tar -xzf @ZIP@ -C ~/.local/share/native_ssh --strip-components=1
chmod +x ~/.local/share/native_ssh/NativeSsh
```

Los clientes MCP necesitan la **ruta absoluta** del binario (aquí, `$HOME/.local/share/native_ssh/NativeSsh`; escríbela expandida, sin `~`).

### Credenciales y `known_hosts`

- **Claves:** por defecto, `~/.ssh/id_ed25519`, `~/.ssh/id_ecdsa` y `~/.ssh/id_rsa`, o los `IdentityFile` de `~/.ssh/config`. No se usa `ssh-agent`. Una clave con passphrase necesita `NATIVE_SSH_PASSPHRASE_FICHERO` (ver «Variables de entorno»).
- **`known_hosts`:** la clave de cada servidor tiene que estar ya en `~/.ssh/known_hosts` (o `known_hosts2`). native_ssh **no acepta claves de host nuevas**: si el servidor no está, falla con un error que dice `… no está en ninguno de estos known_hosts …` y da la huella. Entonces el usuario debe comprobar esa huella y conectarse él una vez a mano (`ssh usuario@servidor`), que la guarda. El modelo no debe añadirla nunca por su cuenta ni desactivar la comprobación.

## Registro en el cliente MCP (stdio)

En todos los ejemplos, sustituye `/home/usuario/.local/share/native_ssh/NativeSsh` por la ruta absoluta real.

### Claude Code

```bash
claude mcp add --scope user native_ssh -- /home/usuario/.local/share/native_ssh/NativeSsh
```

Con variables de entorno, cada una con `-e` antes del nombre:

```bash
claude mcp add --scope user -e NATIVE_SSH_SOLO_LECTURA=prod.ejemplo.com native_ssh -- /home/usuario/.local/share/native_ssh/NativeSsh
```

### Gemini CLI y agy

agy:

```bash
agy mcp add native_ssh /home/usuario/.local/share/native_ssh/NativeSsh
agy mcp add -e NATIVE_SSH_SOLO_LECTURA=prod.ejemplo.com native_ssh /home/usuario/.local/share/native_ssh/NativeSsh
```

(las opciones van antes del nombre). Gemini CLI: añade la entrada al bloque `mcpServers` de `~/.gemini/settings.json`, con el formato de la sección siguiente.

### Otros clientes con `mcpServers`

```json
{
  "mcpServers": {
    "native_ssh": {
      "command": "/home/usuario/.local/share/native_ssh/NativeSsh",
      "env": {
        "NATIVE_SSH_NIVEL_LOG": "Warning"
      }
    }
  }
}
```

`env` es opcional. Para un proyecto concreto, el mismo bloque va en un `.mcp.json` en su raíz (Claude Code lo lee de ahí).

## Modo HTTP con token (opcional)

Además de stdio, native_ssh puede servir MCP por HTTP (Streamable HTTP) en `127.0.0.1` y `[::1]`, con un token obligatorio:

```bash
(umask 077; head -c 32 /dev/urandom | base64 > ~/.config/native_ssh_token)
NATIVE_SSH_TOKEN_HTTP_FICHERO=$HOME/.config/native_ssh_token /home/usuario/.local/share/native_ssh/NativeSsh --http
claude mcp add --transport http native_ssh http://localhost:8722/mcp --header "Authorization: Bearer $(cat ~/.config/native_ssh_token)"
```

- Puerto: `--puerto N` o `NATIVE_SSH_PUERTO_HTTP` (por defecto 8722).
- El token (al menos 16 caracteres) va en `NATIVE_SSH_TOKEN_HTTP_FICHERO` (un fichero propio con `chmod 600`, recomendado) o en `NATIVE_SSH_TOKEN_HTTP`; nunca las dos. Cada petición lleva `Authorization: Bearer <token>`.
- Todos los clientes HTTP comparten las mismas sesiones SSH (shell, variables, directorio).
- `--http-unsecure` escucha en todas las interfaces **sin cifrar**: solo en una red de total confianza o detrás de un proxy con TLS.
- El token queda en claro en la configuración del cliente: protégela como el propio token.

## Variables de entorno

| Variable | Para qué |
|---|---|
| `NATIVE_SSH_DIRECTORIO_SSH` | Directorio de las claves, `known_hosts` y `config` (por defecto `~/.ssh`). |
| `NATIVE_SSH_IDENTIDADES` | Rutas de claves privadas separadas por `:`. Si está, manda sobre los `IdentityFile`. |
| `NATIVE_SSH_PASSPHRASE_FICHERO` | Fichero (propio, `chmod 600`) con la passphrase de las claves cifradas. Recomendado frente a `NATIVE_SSH_PASSPHRASE`; nunca las dos. |
| `NATIVE_SSH_PASSPHRASE` | Passphrase en la propia variable (visible para los procesos del mismo usuario). |
| `NATIVE_SSH_PASSWORD` | Contraseña SSH, si el servidor no usa claves. |
| `NATIVE_SSH_CONFIG_SSH` | Fichero de configuración de ssh para resolver los destinos, o `none` para no usar ninguno. |
| `NATIVE_SSH_SOLO_LECTURA` | Destinos de solo lectura, separados por comas (ver abajo). |
| `NATIVE_SSH_NIVEL_LOG` | `Trace`, `Debug`, `Information` (por defecto), `Warning`, `Error`, `Critical` o `None`. El log va siempre por la salida de errores. |
| `NATIVE_SSH_PUERTO_HTTP`, `NATIVE_SSH_TOKEN_HTTP`, `NATIVE_SSH_TOKEN_HTTP_FICHERO` | Modo HTTP (ver arriba). |

Ninguna herramienta pide ni acepta contraseñas, passphrases o tokens: van solo en estas variables.

### Modo solo lectura

`NATIVE_SSH_SOLO_LECTURA="prod.ejemplo.com, deploy@10.0.0.7:2222"` (o `*` para todos) rechaza en esos destinos `ssh_exec`, `ssh_input`, `ssh_write_file` y `ssh_replace_file_content` antes de conectar o enviar nada; las lecturas (`ssh_read_file`, `ssh_search`, `ssh_find`, `ssh_list_dir`, `ssh_stat`), `ssh_set_cwd` y `ssh_status` siguen funcionando. Es configuración local: ninguna herramienta la cambia. Si un nombre de la lista no se puede resolver, se rechazan las escrituras por seguridad (si dudas, pon la IP). Una entrada con formato no válido (`@prod`, `prod:0`, `*.ejemplo.com`…) impide arrancar; un nombre mal escrito que no se resuelve bloquea las escrituras a todos los destinos con su usuario y puerto hasta que se corrija.

## Comprobar que funciona

1. `--version` da `native_ssh @VERSION@` (ver «Versión»).
2. El binario arranca y responde (debe salir un JSON con `"serverInfo":{"name":"native_ssh","version":"@VERSION@"}`):

   ```bash
   { printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"prueba","version":"1"}}}'; sleep 2; } | /home/usuario/.local/share/native_ssh/NativeSsh 2>/dev/null
   ```

3. El cliente lo ve: `claude mcp list` (o `/mcp` dentro de Claude Code) muestra `native_ssh` conectado, con @HERRAMIENTAS@ herramientas.
4. Primera prueba real: `ssh_exec` con `destino` = `usuario@servidor` (o un alias de `~/.ssh/config`) y `comando` = `echo ok`. Si falla por `known_hosts`, ver «Credenciales y `known_hosts`».

## Instalar la skill

La carpeta `skill-native-ssh/` (con `SKILL.md`) enseña al modelo a combinar las herramientas. Cópiala a la carpeta de skills del cliente con el nombre `native-ssh`:

- Claude Code: `cp -r skill-native-ssh ~/.claude/skills/native-ssh` (o en `.claude/skills/native-ssh` de un proyecto).
- agy: `cp -r skill-native-ssh ~/.gemini/config/skills/native-ssh`.
- Otros clientes con skills: la carpeta que use ese cliente. Sin soporte de skills, las descripciones de las herramientas bastan para usarlas una a una.

## Diferencias y límites (Linux)

- Solo **servidores Linux** como destino, con `bash`, `stty` y `base64`; un servidor Windows (OpenSSH for Windows) se detecta y se rechaza sin ejecutar nada.
- Si el shell de login del usuario remoto es **zsh y no tiene ningún archivo de inicio** (`.zshenv`, `.zprofile`, `.zshrc`, `.zlogin`), el asistente de zsh para usuarios nuevos se queda esperando y `ssh_exec` agota la inicialización (15 s). Arreglo: crear un `~/.zshrc`, aunque sea vacío. Las lecturas no se ven afectadas.
- Sin `ProxyJump`/`ProxyCommand` (un destino que los usa se rechaza), sin certificados de host (`@cert-authority`) y sin `ssh-agent`.
- Como mucho 4 conexiones nuevas cada 30 s por servidor y puerto (para no disparar un `ufw limit`); las sesiones se mantienen abiertas hasta `ssh_disconnect` o el fin de native_ssh.
- Sin interfaces de terminal completa (`vim`, `top`, `less`).
- `NATIVE_SSH_PASSPHRASE_FICHERO` y `NATIVE_SSH_TOKEN_HTTP_FICHERO` solo existen en Linux.
