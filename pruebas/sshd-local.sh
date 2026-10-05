#!/usr/bin/env bash
# sshd en modo usuario para las pruebas de integración. Nunca toca ~/.ssh ni servidores reales.
# Uso: pruebas/sshd-local.sh iniciar|parar
# Deja en pruebas/.estado/ (o en NATIVE_SSH_DIR_ESTADO): cliente_ed25519, known_hosts, puerto y sshd.log.
# NATIVE_SSH_FORCE_COMMAND cambia el shell forzado (por defecto /bin/bash -l), p. ej. para simular un login lento.
# El ForceCommand real es un envoltorio: los canales exec ejecutan su comando con bash y el shell interactivo, el forzado.
set -euo pipefail

DIR="${NATIVE_SSH_DIR_ESTADO:-$(cd "$(dirname "$0")" && pwd)/.estado}"
PUERTO="${NATIVE_SSH_PUERTO_PRUEBAS:-22022}"
FORCE_COMMAND="${NATIVE_SSH_FORCE_COMMAND:-/bin/bash -l}"
SSHD="${NATIVE_SSH_SSHD:-$(command -v sshd || echo /usr/sbin/sshd)}"

parar() {
    if [[ -f "$DIR/sshd.pid" ]]; then
        kill "$(cat "$DIR/sshd.pid")" 2>/dev/null || true
        rm -f "$DIR/sshd.pid"
    fi
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
