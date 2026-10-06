#!/usr/bin/env bash
# Servidores de pruebas con sudo REAL en contenedores podman rootless (Debian estable y Alpine). Nunca usa sudo en la
# máquina ni cambia su configuración: todo pasa dentro de contenedores del usuario, con el puerto publicado solo en
# 127.0.0.1.
#
# Uso: pruebas/contenedor-sudo.sh construir|iniciar|parar|root|disponible <distro>
#      pruebas/contenedor-sudo.sh gemelos iniciar|parar
#      pruebas/contenedor-sudo.sh limpiar
#   construir <distro>  construye la imagen (si ya existe la de este Containerfile, no hace nada) y escribe su nombre.
#   iniciar <distro>    construye si hace falta, arranca el contenedor y escribe el puerto cuando sshd ya responde.
#   parar <distro>      elimina el contenedor y espera a que el puerto quede libre.
#   root <distro> CMD   ejecuta CMD con 'sh -c' como root DENTRO del contenedor (preparar archivos de root, xattrs...).
#   sondear <distro>    con el contenedor en marcha, lista qué herramientas y opciones de las que usan los scripts de
#                       native_ssh hay en la distro (para clasificar los fallos: producto o limitación de la distro).
#   disponible          sale con 0 si podman funciona (lo usan las pruebas para omitirse si no).
#   gemelos iniciar     arranca dos contenedores Debian GEMELOS (misma imagen y mismo arranque: sshd como PID 1) en una
#                       red propia de podman, A publicado en NATIVE_SSH_PUERTO_GEMELO_A (por defecto 25372) y B en
#                       NATIVE_SSH_PUERTO_GEMELO_B (25382), y escribe los dos puertos. Desde A, 'ssh -tt -i
#                       /etc/ns/cliente_ed25519 prueba@gemelo-b' entra en B, y el bash de una sesión tiene en los dos el
#                       mismo $$ y $PPID (los pid de un espacio de nombres nuevo se asignan en orden). Estado en
#                       pruebas/.estado-contenedor-gemelos/ (la clave del cliente va también montada en A, legible por
#                       el usuario de prueba).
#   gemelos parar       elimina los dos contenedores y su red.
#   limpiar             elimina todos los contenedores, redes e imágenes creados por este script (también restos de una
#                       ejecución interrumpida). Conserva las imágenes base descargadas (debian:13-slim, alpine:3.24).
#
# Distros: debian, alpine y sinbash (pruebas/contenedor/Containerfile.<distro>). sinbash es Alpine sin bash instalado,
# con el ash de busybox como shell de login (S4: qué hace native_ssh en un servidor sin bash).
# Deja en pruebas/.estado-contenedor-<distro>/ (o en NATIVE_SSH_DIR_ESTADO): cliente_ed25519, known_hosts, puerto,
# contenedor, usuario y contrasena. Puerto: NATIVE_SSH_PUERTO_CONTENEDOR (entre 23000 y 25999, para que cada agente
# en paralelo use su rango; por defecto 25352 para debian, 25362 para alpine y 25392 para sinbash: fuera del rango
# X400-X999 de los sshd manuales de las pruebas con el puerto de pruebas por defecto, distintos del 25022 del sshd de
# login lento por defecto y de los gemelos, 25372 y 25382).
# Usuario de prueba: 'prueba', contraseña 'contrasena-de-prueba' (solo existe dentro del contenedor), sudo con contraseña.
set -euo pipefail

RAIZ="$(cd "$(dirname "$0")" && pwd)"
ETIQUETA="io.native-ssh.pruebas=contenedor-sudo"
REPOSITORIO="localhost/native-ssh-pruebas"
USUARIO="prueba"
CONTRASENA="contrasena-de-prueba"

distro_valida() {
    case "${1:-}" in
        debian|alpine|sinbash) ;;
        *) echo "Distro desconocida: '${1:-}' (debian, alpine o sinbash)" >&2; exit 2 ;;
    esac
}

directorio_estado() { echo "${NATIVE_SSH_DIR_ESTADO:-$RAIZ/.estado-contenedor-$1}"; }

puerto_por_defecto() {
    case "$1" in
        debian) echo 25352 ;;
        alpine) echo 25362 ;;
        sinbash) echo 25392 ;;
    esac
}

# La etiqueta de la imagen es el hash del Containerfile y del sshd_config: reconstruir sin cambios no hace nada, y un
# cambio produce una imagen nueva (la anterior se borra).
nombre_imagen() {
    local hash
    hash="$(cat "$RAIZ/contenedor/Containerfile.$1" "$RAIZ/contenedor/sshd_config" | sha256sum | cut -c1-12)"
    echo "$REPOSITORIO-$1:$hash"
}

construir() {
    local imagen viejas
    imagen="$(nombre_imagen "$1")"
    if ! podman image exists "$imagen"; then
        podman build --quiet --pull=missing --label "$ETIQUETA" --build-arg "CONTRASENA=$CONTRASENA" \
            -f "$RAIZ/contenedor/Containerfile.$1" -t "$imagen" "$RAIZ/contenedor" >&2
    fi
    viejas="$(podman images --filter "reference=$REPOSITORIO-$1" --format '{{.Repository}}:{{.Tag}}' | grep -vxF "$imagen" || true)"
    if [[ -n "$viejas" ]]; then
        # shellcheck disable=SC2086
        podman rmi $viejas >/dev/null 2>&1 || true
    fi
    echo "$imagen"
}

# El banner de sshd ("SSH-2.0-...") prueba que el servidor del contenedor escucha: con pasta, el puerto del anfitrión
# acepta la conexión aunque dentro todavía no haya nadie.
responde() {
    local banner
    banner="$(timeout 2 bash -c "exec 3<>/dev/tcp/127.0.0.1/$1 && head -c 4 <&3" 2>/dev/null)" || return 1
    [[ "$banner" == "SSH-" ]]
}

puerto_libre() { ! (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }

parar() {
    local dir contenedor puerto i
    dir="$(directorio_estado "$1")"
    [[ -f "$dir/contenedor" ]] || return 0
    contenedor="$(cat "$dir/contenedor")"
    puerto="$(cat "$dir/puerto" 2>/dev/null || true)"
    podman rm --force --time 0 "$contenedor" >/dev/null 2>&1 || true
    if [[ -n "$puerto" ]]; then
        for i in $(seq 1 50); do
            puerto_libre "$puerto" && break
            sleep 0.1
        done
    fi
    rm -f "$dir/contenedor"
}

iniciar() {
    local distro="$1" dir puerto imagen contenedor
    parar "$distro"
    dir="$(directorio_estado "$distro")"
    puerto="${NATIVE_SSH_PUERTO_CONTENEDOR:-$(puerto_por_defecto "$distro")}"
    if (( puerto < 23000 || puerto > 25999 )); then
        echo "El puerto de los contenedores debe estar en 23000-25999 (es $puerto)." >&2
        return 2
    fi
    if ! puerto_libre "$puerto"; then
        echo "El puerto $puerto ya está ocupado en 127.0.0.1." >&2
        return 1
    fi
    imagen="$(construir "$distro")"
    mkdir -p "$dir"
    chmod 700 "$dir"
    [[ -f "$dir/host_ed25519" ]] || ssh-keygen -q -t ed25519 -N '' -C "native-ssh-$distro" -f "$dir/host_ed25519"
    [[ -f "$dir/cliente_ed25519" ]] || ssh-keygen -q -t ed25519 -N '' -C "native-ssh-pruebas" -f "$dir/cliente_ed25519"
    cp "$dir/cliente_ed25519.pub" "$dir/authorized_keys"
    chmod 600 "$dir/host_ed25519" "$dir/cliente_ed25519"
    chmod 644 "$dir/authorized_keys"
    echo "[127.0.0.1]:$puerto $(cut -d' ' -f1,2 "$dir/host_ed25519.pub")" > "$dir/known_hosts"
    echo "$puerto" > "$dir/puerto"
    echo "$USUARIO" > "$dir/usuario"
    echo "$CONTRASENA" > "$dir/contrasena"

    contenedor="ns-sudo-$distro-$puerto"
    # Las claves se montan en una copia propia del contenedor: el directorio de estado lleva también la clave del cliente,
    # que el servidor no necesita.
    mkdir -p "$dir/montaje"
    cp "$dir/host_ed25519" "$dir/authorized_keys" "$dir/montaje/"
    podman run --detach --rm --name "$contenedor" --label "$ETIQUETA" --hostname "ns-$distro" \
        --publish "127.0.0.1:$puerto:22" --volume "$dir/montaje:/etc/ns:ro" "$imagen" >/dev/null
    echo "$contenedor" > "$dir/contenedor"
    for _ in $(seq 1 100); do
        if responde "$puerto"; then
            echo "$puerto"
            return 0
        fi
        sleep 0.1
    done

    echo "sshd no respondió en el contenedor $contenedor; su log:" >&2
    podman logs "$contenedor" >&2 || true
    parar "$distro"
    return 1
}

root() {
    local dir
    dir="$(directorio_estado "$1")"
    shift
    podman exec --user 0 "$(cat "$dir/contenedor")" sh -c "$1"
}

limpiar() {
    local contenedores redes imagenes
    contenedores="$(podman ps --all --quiet --filter "label=$ETIQUETA")"
    if [[ -n "$contenedores" ]]; then
        # shellcheck disable=SC2086
        podman rm --force --time 0 $contenedores >/dev/null
    fi
    redes="$(podman network ls --quiet --filter "label=$ETIQUETA")"
    if [[ -n "$redes" ]]; then
        # shellcheck disable=SC2086
        podman network rm --force $redes >/dev/null
    fi
    imagenes="$(podman images --quiet --filter "label=$ETIQUETA" | sort -u)"
    if [[ -n "$imagenes" ]]; then
        # shellcheck disable=SC2086
        podman rmi --force $imagenes >/dev/null
    fi
    rm -f "$RAIZ"/.estado-contenedor-*/contenedor
}

sondear() {
    root "$1" '
        for h in bash flock python3 perl getfattr setfattr getfacl setcap getcap rg sha256sum cksum timeout base64 mktemp stat readlink; do
            printf "%-10s %s\n" "$h" "$(command -v "$h" || echo -)"
        done
        prueba() { if eval "$2" >/dev/null 2>&1; then printf "%-28s si\n" "$1"; else printf "%-28s no\n" "$1"; fi; }
        cd /tmp
        printf x > ns_sondeo
        prueba "flock -E" "flock -n -E 75 ns_sondeo true"
        prueba "mv -T" "cp ns_sondeo ns_sondeo2 && mv -T ns_sondeo2 ns_sondeo"
        prueba "cp --attributes-only" "cp --attributes-only ns_sondeo ns_sondeo3"
        prueba "stat -c %C" "stat -c %C ns_sondeo"
        prueba "stat --version GNU" "stat --version | grep -q GNU"
        prueba "grep -P" "echo 1 | grep -qP \"\\d\""
        prueba "grep --exclude-dir" "grep -r --exclude-dir=x -e x ns_sondeo"
        prueba "timeout -k" "timeout -k 1 1 true"
        prueba "sed conserva sin salto" "[ \"\$(printf a | sed -n 1p | wc -c)\" = 1 ]"
        rm -f ns_sondeo ns_sondeo2 ns_sondeo3
        printf "%-10s %s\n" "login" "$(getent passwd prueba | cut -d: -f7)"
        sudo -V | head -n 1
        grep -hv "^#" /etc/sudoers /etc/sudoers.d/* 2>/dev/null | grep -i "defaults" || true
        sudo -V 2>/dev/null | grep -v "^Configure" | grep -iE "pseudo-terminal|lecture user|timestamp type|Authentication timestamp timeout" || true
    '
}

# Contenedores gemelos (ver el uso). Los dos se arrancan igual y se sondean una vez cada uno (cada sondeo con éxito es una
# conexión a su sshd, que crea un proceso), para que el pid de cada proceso nuevo siga la misma secuencia en los dos.
puerto_gemelo() {
    local puerto
    case "$1" in
        a) puerto="${NATIVE_SSH_PUERTO_GEMELO_A:-25372}" ;;
        b) puerto="${NATIVE_SSH_PUERTO_GEMELO_B:-25382}" ;;
    esac
    if (( puerto < 23000 || puerto > 25999 )); then
        echo "El puerto de los contenedores debe estar en 23000-25999 (es $puerto)." >&2
        return 2
    fi
    echo "$puerto"
}

parar_gemelos() {
    local dir red
    dir="$(directorio_estado gemelos)"
    [[ -f "$dir/red" ]] || return 0
    red="$(cat "$dir/red")"
    for contenedor in $(cat "$dir/contenedores" 2>/dev/null); do
        podman rm --force --time 0 "$contenedor" >/dev/null 2>&1 || true
    done
    podman network rm --force "$red" >/dev/null 2>&1 || true
    for puerto in $(cat "$dir/puertos" 2>/dev/null); do
        for _ in $(seq 1 50); do
            puerto_libre "$puerto" && break
            sleep 0.1
        done
    done
    rm -f "$dir/red" "$dir/contenedores" "$dir/puertos"
}

iniciar_gemelos() {
    local dir imagen puerto_a puerto_b red n puerto contenedor
    parar_gemelos
    dir="$(directorio_estado gemelos)"
    puerto_a="$(puerto_gemelo a)"
    puerto_b="$(puerto_gemelo b)"
    for puerto in "$puerto_a" "$puerto_b"; do
        if ! puerto_libre "$puerto"; then
            echo "El puerto $puerto ya está ocupado en 127.0.0.1." >&2
            return 1
        fi
    done
    imagen="$(construir debian)"
    mkdir -p "$dir/montaje"
    chmod 700 "$dir"
    [[ -f "$dir/host_ed25519" ]] || ssh-keygen -q -t ed25519 -N '' -C "native-ssh-gemelos" -f "$dir/host_ed25519"
    [[ -f "$dir/cliente_ed25519" ]] || ssh-keygen -q -t ed25519 -N '' -C "native-ssh-pruebas" -f "$dir/cliente_ed25519"
    chmod 600 "$dir/host_ed25519" "$dir/cliente_ed25519"
    cp "$dir/cliente_ed25519.pub" "$dir/montaje/authorized_keys"
    cp "$dir/host_ed25519" "$dir/cliente_ed25519" "$dir/montaje/"
    # El usuario de prueba de A usa la clave del cliente para entrar en B (solo existe para estas pruebas).
    chmod 644 "$dir/montaje/authorized_keys" "$dir/montaje/cliente_ed25519"
    echo "[127.0.0.1]:$puerto_a $(cut -d' ' -f1,2 "$dir/host_ed25519.pub")" > "$dir/known_hosts"
    echo "$USUARIO" > "$dir/usuario"
    echo "$CONTRASENA" > "$dir/contrasena"

    red="ns-gemelos-$puerto_a"
    podman network create --label "$ETIQUETA" "$red" >/dev/null
    echo "$red" > "$dir/red"
    echo "$puerto_a $puerto_b" > "$dir/puertos"
    : > "$dir/contenedores"
    for n in a b; do
        if [[ "$n" == a ]]; then puerto="$puerto_a"; else puerto="$puerto_b"; fi
        contenedor="ns-gemelo-$n-$puerto_a"
        podman run --detach --rm --name "$contenedor" --label "$ETIQUETA" --hostname "gemelo-$n" --network "$red" \
            --network-alias "gemelo-$n" --publish "127.0.0.1:$puerto:22" --volume "$dir/montaje:/etc/ns:ro" "$imagen" >/dev/null
        echo "$contenedor" >> "$dir/contenedores"
    done
    for puerto in "$puerto_a" "$puerto_b"; do
        for _ in $(seq 1 100); do
            responde "$puerto" && continue 2
            sleep 0.1
        done
        echo "sshd no respondió en el gemelo del puerto $puerto." >&2
        parar_gemelos
        return 1
    done
    echo "$puerto_a $puerto_b"
}

disponible() { command -v podman >/dev/null 2>&1 && podman info >/dev/null 2>&1; }

case "${1:-}" in
    construir) distro_valida "${2:-}"; construir "$2" ;;
    iniciar) distro_valida "${2:-}"; iniciar "$2" ;;
    parar) distro_valida "${2:-}"; parar "$2" ;;
    root) distro_valida "${2:-}"; root "$2" "${3:?falta el comando}" ;;
    sondear) distro_valida "${2:-}"; sondear "$2" ;;
    disponible) disponible ;;
    gemelos)
        case "${2:-}" in
            iniciar) iniciar_gemelos ;;
            parar) parar_gemelos ;;
            *) echo "Uso: $0 gemelos iniciar|parar" >&2; exit 2 ;;
        esac ;;
    limpiar) limpiar ;;
    *) echo "Uso: $0 construir|iniciar|parar|root|sondear <distro> | gemelos iniciar|parar | disponible | limpiar" >&2; exit 2 ;;
esac
