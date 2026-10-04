#!/usr/bin/env bash
# Prueba de humo del binario publicado (Native AOT) por stdio contra el sshd local.
# Uso: pruebas/humo-stdio.sh [ruta-binario]   (por defecto artefactos/NativeSsh)
set -euo pipefail

RAIZ="$(cd "$(dirname "$0")/.." && pwd)"
BINARIO="${1:-$RAIZ/artefactos/NativeSsh}"
ESTADO="$RAIZ/pruebas/.estado"

PUERTO="$("$RAIZ/pruebas/sshd-local.sh" iniciar)"
trap '"$RAIZ/pruebas/sshd-local.sh" parar' EXIT

NATIVE_SSH_DIRECTORIO_SSH="$ESTADO" \
NATIVE_SSH_IDENTIDADES="$ESTADO/cliente_ed25519" \
python3 - "$BINARIO" "$USER@127.0.0.1:$PUERTO" <<'PY'
import json, subprocess, sys, threading

binario, destino = sys.argv[1], sys.argv[2]
proceso = subprocess.Popen([binario], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1)
lineas_stderr = []

def drenar_stderr():
    for linea in proceso.stderr:
        lineas_stderr.append(linea)

hilo_stderr = threading.Thread(target=drenar_stderr, daemon=True)
hilo_stderr.start()
siguiente_id = 0

def enviar(metodo, parametros=None, notificacion=False):
    global siguiente_id
    mensaje = {"jsonrpc": "2.0", "method": metodo}
    if parametros is not None:
        mensaje["params"] = parametros
    if not notificacion:
        siguiente_id += 1
        mensaje["id"] = siguiente_id
    proceso.stdin.write(json.dumps(mensaje) + "\n")
    proceso.stdin.flush()
    if notificacion:
        return None
    while True:
        linea = proceso.stdout.readline()
        if not linea:
            raise SystemExit("stdout cerrado. stderr:\n" + "".join(lineas_stderr))
        respuesta = json.loads(linea)  # Cualquier línea que no sea JSON (un log) rompe aquí.
        if respuesta.get("id") == siguiente_id:
            return respuesta

def llamar(nombre, argumentos):
    resultado = enviar("tools/call", {"name": nombre, "arguments": argumentos})["result"]
    texto = resultado["content"][0]["text"]
    return resultado.get("isError", False), texto

def comprobar(condicion, mensaje):
    if not condicion:
        proceso.kill()
        raise SystemExit("FALLO: " + mensaje)
    print("ok -", mensaje)

inicio = enviar("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "humo", "version": "1"}})
comprobar(inicio["result"]["serverInfo"]["name"] == "native_ssh", "initialize")
enviar("notifications/initialized", notificacion=True)

herramientas = sorted(h["name"] for h in enviar("tools/list")["result"]["tools"])
comprobar(herramientas == ["ssh_cancel", "ssh_disconnect", "ssh_exec", "ssh_input", "ssh_output", "ssh_reset_shell", "ssh_status"], f"tools/list: {herramientas}")

es_error, texto = llamar("ssh_exec", {"destino": destino, "comando": "export NS_HUMO=1; echo hola"})
datos = json.loads(texto)
comprobar(not es_error and datos["estado"] == "Completado" and datos["salida"] == "hola\n" and datos["codigoSalida"] == 0, f"ssh_exec: {texto}")

_, texto = llamar("ssh_exec", {"destino": destino, "comando": "echo $NS_HUMO"})
comprobar(json.loads(texto)["salida"] == "1\n", "variables persistentes")

_, texto = llamar("ssh_exec", {"destino": destino, "comando": "sleep 30", "esperarSegundos": 0})
id_lento = json.loads(texto)["id"]
_, texto = llamar("ssh_cancel", {"id": id_lento})
comprobar(json.loads(texto)["estado"] == "Cancelado", "ssh_cancel")

_, texto = llamar("ssh_status", {})
estado = json.loads(texto)
comprobar(estado["sesiones"][0]["conexionesAbiertas"] == 1, "una sola conexión TCP")

es_error, texto = llamar("ssh_exec", {"destino": "ana@", "comando": "ls"})
comprobar(es_error and "no válido" in texto, "error de destino como isError")

proceso.stdin.close()
proceso.wait(timeout=10)
print("Humo correcto.")
PY
