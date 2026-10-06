#!/usr/bin/env bash
# Servidores de pruebas con sudo REAL en contenedores podman rootless (Debian estable y Alpine). Nunca usa sudo en la
# máquina ni cambia su configuración: todo pasa dentro de contenedores del usuario, con el puerto publicado solo en
# 127.0.0.1.
#
# Uso: pruebas/contenedor-sudo.sh construir|iniciar|parar|root|disponible <distro>
#      pruebas/contenedor-sudo.sh limpiar
#   construir <distro>  construye la imagen (si ya existe la de este Containerfile, no hace nada) y escribe su nombre.
#   iniciar <distro>    construye si hace falta, arranca el contenedor y escribe el puerto cuando sshd ya responde.
#   parar <distro>      elimina el contenedor y espera a que el puerto quede libre.
#   root <distro> CMD   ejecuta CMD con 'sh -c' como root DENTRO del contenedor (preparar archivos de root, xattrs...).
#   disponible          sale con 0 si podman funciona (lo usan las pruebas para omitirse si no).
#   limpiar             elimina todos los contenedores e imágenes construidas por este script (también restos de una
#                       ejecución interrumpida). Conserva las imágenes base descargadas (debian:13-slim, alpine:3.24).
#
# Distros: debian, alpine (pruebas/contenedor/Containerfile.<distro>).
# Deja en pruebas/.estado-contenedor-<distro>/ (o en NATIVE_SSH_DIR_ESTADO): cliente_ed25519, known_hosts, puerto,
# contenedor, usuario y contrasena. Puertos: NATIVE_SSH_PUERTO_CONTENEDOR (por defecto 25522 para debian y 25622 para
# alpine; el 25022 es el del sshd de login lento).
# Usuario de prueba: 'prueba', contraseña 'contrasena-de-prueba' (solo existe dentro del contenedor), sudo con contraseña.
set -euo pipefail

RAIZ="$(cd "$(dirname "$0")" && pwd)"
ETIQUETA="io.native-ssh.pruebas=contenedor-sudo"
REPOSITORIO="localhost/native-ssh-pruebas"
USUARIO="prueba"
CONTRASENA="contrasena-de-prueba"

distro_valida() {
    case "${1:-}" in
        debian|alpine) ;;
        *) echo "Distro desconocida: '${1:-}' (debian o alpine)" >&2; exit 2 ;;
    esac
}

directorio_estado() { echo "${NATIVE_SSH_DIR_ESTADO:-$RAIZ/.estado-contenedor-$1}"; }

puerto_por_defecto() {
    case "$1" in
        debian) echo 25522 ;;
        alpine) echo 25622 ;;
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
    if (( puerto < 25000 || puerto > 25999 )); then
        echo "El puerto de los contenedores debe estar en 25000-25999 (es $puerto)." >&2
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
    local contenedores imagenes
    contenedores="$(podman ps --all --quiet --filter "label=$ETIQUETA")"
    if [[ -n "$contenedores" ]]; then
        # shellcheck disable=SC2086
        podman rm --force --time 0 $contenedores >/dev/null
    fi
    imagenes="$(podman images --quiet --filter "label=$ETIQUETA" | sort -u)"
    if [[ -n "$imagenes" ]]; then
        # shellcheck disable=SC2086
        podman rmi --force $imagenes >/dev/null
    fi
    rm -f "$RAIZ"/.estado-contenedor-*/contenedor
}

disponible() { command -v podman >/dev/null 2>&1 && podman info >/dev/null 2>&1; }

case "${1:-}" in
    construir) distro_valida "${2:-}"; construir "$2" ;;
    iniciar) distro_valida "${2:-}"; iniciar "$2" ;;
    parar) distro_valida "${2:-}"; parar "$2" ;;
    root) distro_valida "${2:-}"; root "$2" "${3:?falta el comando}" ;;
    disponible) disponible ;;
    limpiar) limpiar ;;
    *) echo "Uso: $0 construir|iniciar|parar|root <distro> | disponible | limpiar" >&2; exit 2 ;;
esac
