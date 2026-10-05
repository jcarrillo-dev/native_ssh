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
comprobar(herramientas == ["ssh_cancel", "ssh_disconnect", "ssh_exec", "ssh_find", "ssh_input", "ssh_list_dir", "ssh_output", "ssh_read_file", "ssh_replace_file_content", "ssh_reset_shell", "ssh_search", "ssh_set_cwd", "ssh_stat", "ssh_status", "ssh_write_file"], f"tools/list: {herramientas}")

es_error, texto = llamar("ssh_exec", {"destino": destino, "comando": "export NS_HUMO=1; echo hola"})
datos = json.loads(texto)
comprobar(not es_error and datos["estado"] == "Completado" and datos["salida"] == "hola\n" and datos["codigoSalida"] == 0, f"ssh_exec: {texto}")

_, texto = llamar("ssh_exec", {"destino": destino, "comando": "echo $NS_HUMO"})
comprobar(json.loads(texto)["salida"] == "1\n", "variables persistentes")

es_error, texto_write = llamar("ssh_write_file", {"destino": destino, "ruta": "/tmp/ns_humo_test.txt", "contenido": "linea1\nlinea2\nlinea3\nlinea4\n", "overwrite": True})
comprobar(not es_error and "Archivo escrito correctamente" in texto_write, f"ssh_write_file: {texto_write}")

es_error, contenido = llamar("ssh_read_file", {"destino": destino, "ruta": "/tmp/ns_humo_test.txt", "lineaInicio": 2, "lineaFin": 3})
comprobar(not es_error and contenido == "[Líneas 2-3 de 4]\nlinea2\nlinea3\n", f"ssh_read_file: {contenido}")

es_error, texto_search = llamar("ssh_search", {"destino": destino, "ruta": "/tmp", "patron": "linea3"})
comprobar(not es_error and "/tmp/ns_humo_test.txt" in texto_search and "linea3" in texto_search, f"ssh_search: {texto_search}")

es_error, texto_replace = llamar("ssh_replace_file_content", {"destino": destino, "ruta": "/tmp/ns_humo_test.txt", "lineaInicio": 2, "lineaFin": 2, "targetContent": "linea2\n", "replacementContent": "linea2_modificada\n"})
comprobar(not es_error and "modificado quirúrgicamente" in texto_replace, f"ssh_replace_file_content: {texto_replace}")

es_error, contenido_mod = llamar("ssh_read_file", {"destino": destino, "ruta": "/tmp/ns_humo_test.txt", "lineaInicio": 2, "lineaFin": 3})
comprobar(not es_error and contenido_mod == "[Líneas 2-3 de 4]\nlinea2_modificada\nlinea3\n", f"ssh_read_file post-replace: {contenido_mod}")

es_error, texto_find = llamar("ssh_find", {"destino": destino, "ruta": "/tmp", "patron": "*.txt"})
comprobar(not es_error and "ns_humo_test.txt" in texto_find, f"ssh_find: {texto_find}")

es_error, texto_stat = llamar("ssh_stat", {"destino": destino, "ruta": "/tmp"})
datos_stat = json.loads(texto_stat)
comprobar(not es_error and datos_stat["existe"], f"ssh_stat: {texto_stat}")

llamar("ssh_exec", {"destino": destino, "comando": "rm -f /tmp/ns_humo_test.txt"})

es_error, texto_cwd = llamar("ssh_set_cwd", {"destino": destino, "ruta": "/tmp"})
comprobar(not es_error and "/tmp" in texto_cwd, f"ssh_set_cwd fijar: {texto_cwd}")

_, texto_pwd = llamar("ssh_exec", {"destino": destino, "comando": "pwd"})
comprobar(json.loads(texto_pwd)["salida"].strip() == "/tmp", f"ssh_exec en cwd /tmp: {texto_pwd}")

es_error, texto_cwd_reset = llamar("ssh_set_cwd", {"destino": destino, "ruta": None})
comprobar(not es_error and "restablecido" in texto_cwd_reset, f"ssh_set_cwd reset: {texto_cwd_reset}")

_, texto_pwd_reset = llamar("ssh_exec", {"destino": destino, "comando": "pwd"})
comprobar(json.loads(texto_pwd_reset)["salida"].strip() != "/tmp", f"ssh_exec en cwd reset: {texto_pwd_reset}")

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
