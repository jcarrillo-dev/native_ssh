#!/usr/bin/env bash
# sshd en modo usuario para las pruebas de integración. Nunca toca ~/.ssh ni servidores reales.
# Uso: pruebas/sshd-local.sh iniciar|parar
# Deja en pruebas/.estado/ (o en NATIVE_SSH_DIR_ESTADO): cliente_ed25519, known_hosts, puerto y sshd.log.
# NATIVE_SSH_FORCE_COMMAND cambia el shell forzado (por defecto /bin/bash -l), p. ej. para simular un login lento.
# El ForceCommand real es un envoltorio: los canales exec ejecutan su comando con bash y el shell interactivo, el forzado.
# NATIVE_SSH_SSHD_EXTRA añade líneas al final de sshd_config (p. ej. "MaxSessions 2").
set -euo pipefail

DIR="${NATIVE_SSH_DIR_ESTADO:-$(cd "$(dirname "$0")" && pwd)/.estado}"
PUERTO="${NATIVE_SSH_PUERTO_PRUEBAS:-22022}"
FORCE_COMMAND="${NATIVE_SSH_FORCE_COMMAND:-/bin/bash -l}"
SSHD="${NATIVE_SSH_SSHD:-$(command -v sshd || echo /usr/sbin/sshd)}"

# Vivo y no zombi (un zombi ya no escucha; solo falta que su padre lo recoja).
vivo() {
    local estado
    estado="$(ps -o stat= -p "$1" 2>/dev/null)" || return 1
    [[ -n "$estado" && "$estado" != Z* ]]
}

# Espera hasta 'decimas' décimas de segundo a que el proceso termine.
esperar_fin() {
    local i
    for i in $(seq 1 "$2"); do
        vivo "$1" || return 0
        sleep 0.1
    done
    ! vivo "$1"
}

# Procesos que cuelgan del maestro: los sshd-session de cada conexión y lo que ejecutan.
descendientes() {
    local hijo
    for hijo in $(pgrep -P "$1"); do
        echo "$hijo"
        descendientes "$hijo"
    done
}

# No vuelve hasta que el sshd ha terminado (TERM y, si en 3 s no basta, KILL) y su puerto ya no acepta conexiones:
# quien lo arranque justo después (otra instancia de las pruebas, la siguiente pasada) lo encuentra libre. Las sesiones
# que quedaran detenidas (una prueba que muere con el servidor congelado con SIGSTOP antes de reanudarlo) no terminan
# solas al morir el maestro: se reanudan y se matan.
parar() {
    [[ -f "$DIR/sshd.pid" ]] || return 0
    local pid puerto i sesiones
    pid="$(cat "$DIR/sshd.pid")"
    puerto="$(cat "$DIR/puerto" 2>/dev/null || echo "$PUERTO")"
    sesiones="$(descendientes "$pid" || true)"
    kill "$pid" 2>/dev/null || true
    if ! esperar_fin "$pid" 30; then
        kill -KILL "$pid" 2>/dev/null || true
        esperar_fin "$pid" 20 || { echo "sshd ($pid) no terminó" >&2; return 1; }
    fi
    for i in $sesiones; do
        kill -CONT "$i" 2>/dev/null || true
        kill -KILL "$i" 2>/dev/null || true
    done
    for i in $(seq 1 50); do
        (exec 3<>"/dev/tcp/127.0.0.1/$puerto") 2>/dev/null || break
        sleep 0.1
    done
    rm -f "$DIR/sshd.pid"
}

iniciar() {
    parar
    mkdir -p "$DIR"
    chmod 700 "$DIR"
    [[ -f "$DIR/host_ed25519" ]] || ssh-keygen -q -t ed25519 -N '' -f "$DIR/host_ed25519"
    [[ -f "$DIR/cliente_ed25519" ]] || ssh-keygen -q -t ed25519 -N '' -f "$DIR/cliente_ed25519"
    cp "$DIR/cliente_ed25519.pub" "$DIR/authorized_keys"
    echo "[127.0.0.1]:$PUERTO $(cut -d' ' -f1,2 "$DIR/host_ed25519.pub")" > "$DIR/known_hosts"
    echo "$PUERTO" > "$DIR/puerto"

    # Un ForceCommand a secas sustituiría también los comandos de los canales exec por el login.
    {
        echo '#!/bin/bash'
        echo 'if [[ -n "${SSH_ORIGINAL_COMMAND:-}" ]]; then'
        echo '    exec /bin/bash -c "$SSH_ORIGINAL_COMMAND"'
        echo 'fi'
        printf 'eval "exec %s"\n' "${FORCE_COMMAND//\"/\\\"}"
    } > "$DIR/force-command.sh"
    chmod 700 "$DIR/force-command.sh"

    cat > "$DIR/sshd_config" <<CFG
Port $PUERTO
ListenAddress 127.0.0.1
HostKey $DIR/host_ed25519
PidFile $DIR/sshd.pid
AuthorizedKeysFile $DIR/authorized_keys
PubkeyAuthentication yes
PasswordAuthentication no
KbdInteractiveAuthentication no
UsePAM no
StrictModes no
PrintMotd no
# El shell de login del usuario puede no ser bash (p. ej. fish): se fuerza un bash de login, como en producción,
# salvo en los canales exec, cuyo comando se ejecuta con bash.
ForceCommand $DIR/force-command.sh
CFG
    if [[ -n "${NATIVE_SSH_SSHD_EXTRA:-}" ]]; then
        printf '%s\n' "$NATIVE_SSH_SSHD_EXTRA" >> "$DIR/sshd_config"
    fi

    "$SSHD" -f "$DIR/sshd_config" -E "$DIR/sshd.log"
    for _ in $(seq 1 50); do
        if [[ -f "$DIR/sshd.pid" ]] && (exec 3<>"/dev/tcp/127.0.0.1/$PUERTO") 2>/dev/null; then
            echo "$PUERTO"
            return 0
        fi
        sleep 0.1
    done

    echo "sshd no arrancó; revisa $DIR/sshd.log" >&2
    cat "$DIR/sshd.log" >&2 || true
    return 1
}

case "${1:-}" in
    iniciar) iniciar ;;
    parar) parar ;;
    *) echo "Uso: $0 iniciar|parar" >&2; exit 2 ;;
esac
