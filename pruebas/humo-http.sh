#!/usr/bin/env bash
# Prueba de humo del transporte HTTP: --http (solo loopback) y --http-unsecure (todas las interfaces), los dos con el
# token obligatorio (NATIVE_SSH_TOKEN_HTTP o un fichero 0600 en NATIVE_SSH_TOKEN_HTTP_FICHERO); sin token no arranca.
# Uso: pruebas/humo-http.sh [ruta-binario]   (por defecto artefactos/NativeSsh)
set -euo pipefail

RAIZ="$(cd "$(dirname "$0")/.." && pwd)"
BINARIO="${1:-$RAIZ/artefactos/NativeSsh}"
ESTADO="$RAIZ/pruebas/.estado"
PUERTO_HTTP="${NATIVE_SSH_PUERTO_HTTP_PRUEBAS:-18722}"
PID_MCP=""
# Token de la prueba, en un fichero 0600 propio (se borra al salir).
FICHERO_TOKEN="$(mktemp)"
TOKEN="humo-$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
printf '%s\n' "$TOKEN" > "$FICHERO_TOKEN"
chmod 600 "$FICHERO_TOKEN"

PUERTO_SSH="$("$RAIZ/pruebas/sshd-local.sh" iniciar)"
parar() {
    [[ -n "$PID_MCP" ]] && kill "$PID_MCP" 2>/dev/null || true
    rm -f -- "$FICHERO_TOKEN"
    "$RAIZ/pruebas/sshd-local.sh" parar
}
trap parar EXIT

arrancar() {
    NATIVE_SSH_DIRECTORIO_SSH="$ESTADO" NATIVE_SSH_IDENTIDADES="$ESTADO/cliente_ed25519" NATIVE_SSH_TOKEN_HTTP_FICHERO="$FICHERO_TOKEN" \
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

# Sin token no arranca (sale con 2 y lo dice) y no abre el puerto; con el fichero legible por otros, tampoco.
set +e
env -u NATIVE_SSH_TOKEN_HTTP -u NATIVE_SSH_TOKEN_HTTP_FICHERO "$BINARIO" --http --puerto "$PUERTO_HTTP" 2>"$ESTADO/mcp-http.log"
CODIGO=$?
chmod 644 "$FICHERO_TOKEN"
env -u NATIVE_SSH_TOKEN_HTTP NATIVE_SSH_TOKEN_HTTP_FICHERO="$FICHERO_TOKEN" "$BINARIO" --http --puerto "$PUERTO_HTTP" 2>"$ESTADO/mcp-http-0644.log"
CODIGO_0644=$?
chmod 600 "$FICHERO_TOKEN"
set -e
[[ $CODIGO == 2 ]] && grep -q "NATIVE_SSH_TOKEN_HTTP" "$ESTADO/mcp-http.log" || { echo "FALLO: sin token: código $CODIGO" >&2; cat "$ESTADO/mcp-http.log" >&2; exit 1; }
[[ $CODIGO_0644 == 2 ]] && grep -q "0644" "$ESTADO/mcp-http-0644.log" || { echo "FALLO: fichero 0644: código $CODIGO_0644" >&2; cat "$ESTADO/mcp-http-0644.log" >&2; exit 1; }
[[ -z "$(escuchas)" ]] || { echo "FALLO: sin token se abrió el puerto" >&2; exit 1; }
echo "ok - sin token (o con el fichero 0644) el modo HTTP no arranca y lo dice"

arrancar --http
DIRECCIONES="$(escuchas)"
[[ "$DIRECCIONES" == "127.0.0.1:$PUERTO_HTTP [::1]:$PUERTO_HTTP " ]] || { echo "FALLO: --http escucha en: $DIRECCIONES" >&2; exit 1; }
echo "ok - --http solo en loopback ($DIRECCIONES)"

python3 - "http://127.0.0.1:$PUERTO_HTTP/mcp" "$USER@127.0.0.1:$PUERTO_SSH" "$TOKEN" <<'PY'
import json, sys, urllib.error, urllib.request

url, destino, token = sys.argv[1], sys.argv[2], sys.argv[3]
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
    cabeceras = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream", "Authorization": "Bearer " + token}
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

def rechazo(cabeceras):
    cuerpo = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}}).encode()
    peticion = urllib.request.Request(url, cuerpo, dict({"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}, **cabeceras), method="POST")
    try:
        urllib.request.urlopen(peticion, timeout=10)
        return None, ""
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()

codigo, texto = rechazo({})
comprobar(codigo == 401 and "Authorization: Bearer" in texto, f"sin token: HTTP {codigo} ({texto})")
codigo, texto = rechazo({"Authorization": "Bearer otro-token-cualquiera-123"})
comprobar(codigo == 401 and "no es el configurado" in texto, f"token incorrecto: HTTP {codigo} ({texto})")

inicio = enviar("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "humo-http", "version": "1"}})
comprobar(inicio["result"]["serverInfo"]["name"] == "native_ssh", "initialize por HTTP")
enviar("notifications/initialized", notificacion=True)

herramientas = sorted(h["name"] for h in enviar("tools/list")["result"]["tools"])
comprobar(len(herramientas) == 15 and herramientas == ["ssh_cancel", "ssh_disconnect", "ssh_exec", "ssh_find", "ssh_input", "ssh_list_dir", "ssh_output", "ssh_read_file", "ssh_replace_file_content", "ssh_reset_shell", "ssh_search", "ssh_set_cwd", "ssh_stat", "ssh_status", "ssh_write_file"], f"tools/list por HTTP: {herramientas}")

resultado = enviar("tools/call", {"name": "ssh_exec", "arguments": {"destino": destino, "comando": "echo hola-http"}})["result"]
datos = json.loads(resultado["content"][0]["text"])
comprobar(datos["estado"] == "Completado" and datos["salida"] == "hola-http\n", "ssh_exec por HTTP")

resultado = enviar("tools/call", {"name": "ssh_stat", "arguments": {"destino": destino, "ruta": "/tmp"}})["result"]
datos = json.loads(resultado["content"][0]["text"])
comprobar(not resultado.get("isError", False) and datos["existe"] and datos["ruta"] == "/tmp", "ssh_stat por HTTP (contrato por canal exec)")

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
grep -q "SIN cifrar" "$ESTADO/mcp-http.log" || { echo "FALLO: falta el aviso de --http-unsecure en stderr" >&2; exit 1; }
grep -q -- "$TOKEN" "$ESTADO"/mcp-http*.log && { echo "FALLO: el token aparece en los logs" >&2; exit 1; }
echo "ok - --http-unsecure en todas las interfaces ($DIRECCIONES) con aviso"
detener

echo "Humo HTTP correcto."
