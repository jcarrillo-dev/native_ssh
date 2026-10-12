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
import json, os, shutil, subprocess, sys, tempfile, threading

binario, destino = sys.argv[1], sys.argv[2]

# Segundos que se espera cada respuesta antes de matar el servidor (como el timeout de humo-http.sh).
PLAZO_RESPUESTA = 60

# Todo lo de esta ejecución vive en un directorio propio que se borra al terminar: el stderr del servidor y los archivos
# de las herramientas de archivo, que nunca buscan en todo /tmp (sus restos ajenos cambiarían el resultado).
trabajo = os.path.realpath(tempfile.mkdtemp(prefix="ns_humo_"))
ruta_stderr = os.path.join(trabajo, "servidor.stderr")
directorio = os.path.join(trabajo, "archivos")
os.mkdir(directorio)
archivo = os.path.join(directorio, "ns_humo_test.txt")

# El stderr va a un archivo abierto en modo append (cada escritura del servidor va al final): por mucho que escriba no se
# bloquea en una tubería llena ni depende de un hilo lector. La cola se lee con un descriptor aparte en los fallos.
with open(ruta_stderr, "ab") as salida_errores:
    proceso = subprocess.Popen([binario], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=salida_errores, encoding="utf-8", bufsize=1)

plazo_vencido = threading.Event()
siguiente_id = 0

def cola_stderr(lineas=40):
    with open(ruta_stderr, "rb") as fichero:
        texto = fichero.read().decode("utf-8", errors="replace")
    return "\n".join(texto.splitlines()[-lineas:])

def terminar_servidor():
    if proceso.poll() is None:
        proceso.kill()
    proceso.wait()

def fallar(mensaje):
    terminar_servidor()
    raise SystemExit(f"FALLO: {mensaje}\n--- últimas líneas del stderr del servidor ---\n{cola_stderr()}")

def leer_json(texto, contexto):
    try:
        return json.loads(texto)
    except json.JSONDecodeError as error:
        fallar(f"{contexto} no es JSON válido ({error}): {texto!r}")

def vencer_plazo():
    plazo_vencido.set()
    proceso.kill()

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
    vigilante = threading.Timer(PLAZO_RESPUESTA, vencer_plazo)
    vigilante.daemon = True
    vigilante.start()
    try:
        while True:
            linea = proceso.stdout.readline()
            if not linea:
                fallar(f"sin respuesta a {metodo} en {PLAZO_RESPUESTA} s" if plazo_vencido.is_set() else f"stdout cerrado sin respuesta a {metodo}")
            # Cualquier línea que no sea JSON (p. ej. un log escrito en stdout) es un fallo.
            respuesta = leer_json(linea, f"La línea recibida en respuesta a {metodo}")
            if respuesta.get("id") == siguiente_id:
                return respuesta
    finally:
        vigilante.cancel()

HERRAMIENTAS_DOS_BLOQUES = ("ssh_exec", "ssh_output", "ssh_input", "ssh_cancel")

# ssh_exec, ssh_output, ssh_input y ssh_cancel responden con dos bloques de texto: el control (JSON en una sola línea, sin la
# salida) y la salida tal cual. Se comprueba el contrato y se devuelve el control con la salida dentro, para las comprobaciones.
def llamar(nombre, argumentos):
    resultado = enviar("tools/call", {"name": nombre, "arguments": argumentos})["result"]
    es_error = resultado.get("isError", False)
    texto = resultado["content"][0]["text"]
    if nombre in HERRAMIENTAS_DOS_BLOQUES and not es_error:
        bloques = resultado["content"]
        if len(bloques) != 2 or any(b.get("type") != "text" for b in bloques) or "\n" in texto:
            fallar(f"{nombre}: se esperaban dos bloques de texto con el control en una línea: {json.dumps(bloques, ensure_ascii=False)[:2000]}")
        control = leer_json(texto, nombre)
        if "salida" in control:
            fallar(f"{nombre}: el control lleva la salida: {texto}")
        control["salida"] = bloques[1]["text"]
        texto = json.dumps(control, ensure_ascii=False)
    return es_error, texto

def comprobar(condicion, mensaje):
    if not condicion:
        fallar(mensaje)
    print("ok -", mensaje)

def principal():
    inicio = enviar("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "humo", "version": "1"}})
    comprobar(inicio["result"]["serverInfo"]["name"] == "native_ssh", "initialize")
    enviar("notifications/initialized", notificacion=True)

    herramientas = sorted(h["name"] for h in enviar("tools/list")["result"]["tools"])
    comprobar(herramientas == ["ssh_cancel", "ssh_disconnect", "ssh_exec", "ssh_find", "ssh_input", "ssh_list_dir", "ssh_output", "ssh_read_file", "ssh_replace_file_content", "ssh_reset_shell", "ssh_search", "ssh_set_cwd", "ssh_stat", "ssh_status", "ssh_write_file"], f"tools/list: {herramientas}")

    es_error, texto = llamar("ssh_exec", {"destino": destino, "comando": "export NS_HUMO=1; echo hola"})
    datos = leer_json(texto, "ssh_exec")
    comprobar(not es_error and datos["estado"] == "Completado" and datos["salida"] == "hola\n" and datos["codigoSalida"] == 0, f"ssh_exec: {texto}")

    _, texto = llamar("ssh_exec", {"destino": destino, "comando": "echo $NS_HUMO"})
    comprobar(leer_json(texto, "ssh_exec")["salida"] == "1\n", "variables persistentes")

    es_error, texto_write = llamar("ssh_write_file", {"destino": destino, "ruta": archivo, "contenido": "linea1\nlinea2\nlinea3\nlinea4\n", "overwrite": True})
    datos_write = leer_json(texto_write, "ssh_write_file")
    comprobar(not es_error and datos_write["ruta"] == archivo and datos_write["bytes"] == 28 and datos_write["isAtomica"] and "reemplazos" not in datos_write and "Archivo escrito correctamente" in datos_write["mensaje"], f"ssh_write_file: {texto_write}")

    es_error, contenido = llamar("ssh_read_file", {"destino": destino, "ruta": archivo, "lineaInicio": 2, "lineaFin": 3})
    comprobar(not es_error and contenido == "[Líneas 2-3 de 4]\nlinea2\nlinea3\n", f"ssh_read_file: {contenido}")

    # En el directorio propio la búsqueda solo puede encontrar el archivo del humo: la coincidencia exacta y el motor.
    es_error, texto_search = llamar("ssh_search", {"destino": destino, "ruta": directorio, "patron": "linea3"})
    resultados, _, pie = texto_search.rpartition("\n\n")
    comprobar(not es_error and resultados == f"{archivo}:3:linea3" and pie.startswith("[motor: "), f"ssh_search: {texto_search}")

    es_error, texto_replace = llamar("ssh_replace_file_content", {"destino": destino, "ruta": archivo, "lineaInicio": 2, "lineaFin": 2, "targetContent": "linea2\n", "replacementContent": "linea2_modificada\n"})
    datos_replace = leer_json(texto_replace, "ssh_replace_file_content")
    comprobar(not es_error and datos_replace["reemplazos"] == 1 and datos_replace["bytes"] == 39 and datos_replace["isAtomica"] and "modificado quirúrgicamente" in datos_replace["mensaje"], f"ssh_replace_file_content: {texto_replace}")

    es_error, contenido_mod = llamar("ssh_read_file", {"destino": destino, "ruta": archivo, "lineaInicio": 2, "lineaFin": 3})
    comprobar(not es_error and contenido_mod == "[Líneas 2-3 de 4]\nlinea2_modificada\nlinea3\n", f"ssh_read_file post-replace: {contenido_mod}")

    es_error, texto_find = llamar("ssh_find", {"destino": destino, "ruta": directorio, "patron": "*.txt"})
    comprobar(not es_error and texto_find == "ns_humo_test.txt", f"ssh_find: {texto_find}")

    es_error, texto_stat = llamar("ssh_stat", {"destino": destino, "ruta": directorio})
    datos_stat = leer_json(texto_stat, "ssh_stat")
    comprobar(not es_error and datos_stat["existe"] and datos_stat["ruta"] == directorio and datos_stat["tipo"] == "directorio" and "propietario" in datos_stat, f"ssh_stat: {texto_stat}")

    es_error, texto_cwd = llamar("ssh_set_cwd", {"destino": destino, "ruta": directorio})
    datos_cwd = leer_json(texto_cwd, "ssh_set_cwd")
    comprobar(not es_error and datos_cwd["directorio"] == directorio and datos_cwd["destino"] == destino, f"ssh_set_cwd fijar: {texto_cwd}")

    _, texto_pwd = llamar("ssh_exec", {"destino": destino, "comando": "pwd"})
    comprobar(leer_json(texto_pwd, "ssh_exec")["salida"].strip() == directorio, f"ssh_exec en el cwd fijado: {texto_pwd}")

    es_error, texto_cwd_reset = llamar("ssh_set_cwd", {"destino": destino, "ruta": None})
    datos_cwd_reset = leer_json(texto_cwd_reset, "ssh_set_cwd")
    comprobar(not es_error and "directorio" not in datos_cwd_reset and "restablecido" in datos_cwd_reset["mensaje"], f"ssh_set_cwd reset: {texto_cwd_reset}")

    _, texto_pwd_reset = llamar("ssh_exec", {"destino": destino, "comando": "pwd"})
    comprobar(leer_json(texto_pwd_reset, "ssh_exec")["salida"].strip() != directorio, f"ssh_exec en cwd reset: {texto_pwd_reset}")

    _, texto = llamar("ssh_exec", {"destino": destino, "comando": "sleep 30", "esperarSegundos": 0})
    id_lento = leer_json(texto, "ssh_exec")["id"]
    _, texto = llamar("ssh_cancel", {"id": id_lento})
    comprobar(leer_json(texto, "ssh_cancel")["estado"] == "Cancelado", "ssh_cancel")

    _, texto = llamar("ssh_status", {})
    estado = leer_json(texto, "ssh_status")
    comprobar(estado["sesiones"][0]["conexionesAbiertas"] == 1, "una sola conexión TCP")

    es_error, texto = llamar("ssh_exec", {"destino": "ana@", "comando": "ls"})
    comprobar(es_error and "no válido" in texto, "error de destino como isError")

    proceso.stdin.close()
    proceso.wait(timeout=10)

# Cualquier salida que no sea la correcta (comprobación fallida, JSON inválido, KeyError, tubería rota, plazo vencido,
# Ctrl+C) mata el servidor; y el directorio propio se borra siempre.
try:
    principal()
except SystemExit:
    raise
except BaseException as error:
    fallar(f"{type(error).__name__}: {error}")
finally:
    terminar_servidor()
    shutil.rmtree(trabajo, ignore_errors=True)
print("Humo correcto.")
PY
