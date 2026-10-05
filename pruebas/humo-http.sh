#!/usr/bin/env bash
# Prueba de humo del transporte HTTP: --http (solo loopback) y --http-unsecure (todas las interfaces).
# Uso: pruebas/humo-http.sh [ruta-binario]   (por defecto artefactos/NativeSsh)
set -euo pipefail

RAIZ="$(cd "$(dirname "$0")/.." && pwd)"
BINARIO="${1:-$RAIZ/artefactos/NativeSsh}"
ESTADO="$RAIZ/pruebas/.estado"
PUERTO_HTTP="${NATIVE_SSH_PUERTO_HTTP_PRUEBAS:-18722}"
PID_MCP=""

PUERTO_SSH="$("$RAIZ/pruebas/sshd-local.sh" iniciar)"
parar() {
    [[ -n "$PID_MCP" ]] && kill "$PID_MCP" 2>/dev/null || true
    "$RAIZ/pruebas/sshd-local.sh" parar
}
trap parar EXIT

arrancar() {
    NATIVE_SSH_DIRECTORIO_SSH="$ESTADO" NATIVE_SSH_IDENTIDADES="$ESTADO/cliente_ed25519" \
        "$BINARIO" "$1" --puerto "$PUERTO_HTTP" 2>"$ESTADO/mcp-http.log" &
    PID_MCP=$!
    for _ in $(seq 1 50); do
        (exec 3<>"/dev/tcp/127.0.0.1/$PUERTO_HTTP") 2>/dev/null && return 0
        sleep 0.1
    done
    echo "El servidor HTTP no arrancó:" >&2
    cat "$ESTADO/mcp-http.log" >&2
    return 1
}

# Direcciones locales en escucha en el puerto (columna 4 de ss).
escuchas() { ss -Hltn "sport = :$PUERTO_HTTP" | awk '{print $4}' | LC_ALL=C sort | tr '\n' ' '; }

detener() {
    kill "$PID_MCP"
    wait "$PID_MCP" 2>/dev/null || true
    PID_MCP=""
}

arrancar --http
DIRECCIONES="$(escuchas)"
[[ "$DIRECCIONES" == "127.0.0.1:$PUERTO_HTTP [::1]:$PUERTO_HTTP " ]] || { echo "FALLO: --http escucha en: $DIRECCIONES" >&2; exit 1; }
echo "ok - --http solo en loopback ($DIRECCIONES)"

python3 - "http://127.0.0.1:$PUERTO_HTTP/mcp" "$USER@127.0.0.1:$PUERTO_SSH" <<'PY'
import json, sys, urllib.error, urllib.request

url, destino = sys.argv[1], sys.argv[2]
sesion = None
siguiente_id = 0

def enviar(metodo, parametros=None, notificacion=False):
    global sesion, siguiente_id
    mensaje = {"jsonrpc": "2.0", "method": metodo}
    if parametros is not None:
        mensaje["params"] = parametros
    if not notificacion:
        siguiente_id += 1
        mensaje["id"] = siguiente_id
    cabeceras = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
    if sesion:
        cabeceras["Mcp-Session-Id"] = sesion
    peticion = urllib.request.Request(url, json.dumps(mensaje).encode(), cabeceras, method="POST")
    with urllib.request.urlopen(peticion, timeout=60) as respuesta:
        sesion = respuesta.headers.get("Mcp-Session-Id") or sesion
        cuerpo = respuesta.read().decode()
    if notificacion:
        return None
    # Streamable HTTP: JSON directo o eventos SSE con líneas 'data:'.
    for linea in cuerpo.splitlines():
        texto = linea[5:].strip() if linea.startswith("data:") else linea.strip()
        if texto.startswith("{"):
            datos = json.loads(texto)
            if datos.get("id") == siguiente_id:
                return datos
    raise SystemExit("FALLO: sin respuesta para " + metodo + ": " + cuerpo)

def comprobar(condicion, mensaje):
    if not condicion:
        raise SystemExit("FALLO: " + mensaje)
    print("ok -", mensaje)

inicio = enviar("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "humo-http", "version": "1"}})
comprobar(inicio["result"]["serverInfo"]["name"] == "native_ssh", "initialize por HTTP")
enviar("notifications/initialized", notificacion=True)

herramientas = sorted(h["name"] for h in enviar("tools/list")["result"]["tools"])
comprobar(len(herramientas) == 10 and herramientas == ["ssh_cancel", "ssh_disconnect", "ssh_exec", "ssh_input", "ssh_output", "ssh_read_file", "ssh_reset_shell", "ssh_search", "ssh_status", "ssh_write_file"], f"tools/list por HTTP: {herramientas}")

resultado = enviar("tools/call", {"name": "ssh_exec", "arguments": {"destino": destino, "comando": "echo hola-http"}})["result"]
datos = json.loads(resultado["content"][0]["text"])
comprobar(datos["estado"] == "Completado" and datos["salida"] == "hola-http\n", "ssh_exec por HTTP")

try:
    peticion_evil = urllib.request.Request(url, b"{}", {"Content-Type": "application/json", "Host": "evil.example.com"}, method="POST")
    urllib.request.urlopen(peticion_evil, timeout=5)
    raise SystemExit("FALLO: Host evil.example.com no devolvió 400")
except urllib.error.HTTPError as e:
    comprobar(e.code == 400, "Host evil.example.com rechazado con HTTP 400")
PY
detener

arrancar --http-unsecure
DIRECCIONES="$(escuchas)"
[[ "$DIRECCIONES" == "*:$PUERTO_HTTP " || "$DIRECCIONES" == "[::]:$PUERTO_HTTP " || "$DIRECCIONES" == "0.0.0.0:$PUERTO_HTTP " ]] || { echo "FALLO: --http-unsecure escucha en: $DIRECCIONES" >&2; exit 1; }
grep -q "SIN autenticación" "$ESTADO/mcp-http.log" || { echo "FALLO: falta el aviso de --http-unsecure en stderr" >&2; exit 1; }
echo "ok - --http-unsecure en todas las interfaces ($DIRECCIONES) con aviso"
detener

echo "Humo HTTP correcto."
