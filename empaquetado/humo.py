"""Humo de un binario publicado de native_ssh: --version, initialize y tools/list por stdio, sin conectar a ningún servidor.

Uso:
  python3 empaquetado/humo.py <binario> --version X.Y.Z --variante normal|v3|v4 --estado <fichero.json>

Una variante v3 o v4 en una CPU sin sus instrucciones no se puede ejecutar: el runtime de .NET aborta al arrancar con
«The required instruction sets are not supported by the current CPU.» (SIGABRT en Linux, 0xC0000602 en Windows). En ese
caso el humo comprueba ese mensaje y deja el resultado «no ejecutado» en el fichero de estado, que usan las notas de la
Release. En Linux se mira /proc/cpuinfo: si la CPU tiene las instrucciones, abortar es un fallo. La variante normal tiene
que ejecutarse siempre. Sale con 0 si todo es correcto (también con «no ejecutado») y con 1 si algo falla.
"""
import argparse
import json
import os
import queue
import subprocess
import sys
import threading

MENSAJE_CPU = "The required instruction sets are not supported by the current CPU."
STATUS_WINDOWS_ISA = 0xC0000602
PLAZO = 30

V2 = {"pni", "ssse3", "sse4_1", "sse4_2", "popcnt", "cx16"}
V3 = V2 | {"avx", "avx2", "bmi1", "bmi2", "fma", "abm", "movbe", "f16c"}
V4 = V3 | {"avx512f", "avx512bw", "avx512cd", "avx512dq", "avx512vl"}
REQUISITOS = {"normal": set(), "v3": V3, "v4": V4}


def banderas_cpu():
    """Banderas de /proc/cpuinfo, o None si no hay (Windows)."""
    try:
        with open("/proc/cpuinfo", encoding="utf-8") as f:
            for linea in f:
                if linea.startswith("flags"):
                    return set(linea.split(":", 1)[1].split())
    except OSError:
        return None
    return set()


def es_aborto_cpu(codigo, errores):
    return MENSAJE_CPU in errores and codigo in (-6, 134, STATUS_WINDOWS_ISA, STATUS_WINDOWS_ISA - 2**32)


def mcp(binario, version):
    proceso = subprocess.Popen([binario], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               encoding="utf-8")
    lineas = queue.Queue()
    threading.Thread(target=lambda: [lineas.put(l) for l in proceso.stdout], daemon=True).start()
    errores = []
    threading.Thread(target=lambda: [errores.append(l) for l in proceso.stderr], daemon=True).start()

    def enviar(mensaje):
        proceso.stdin.write(json.dumps(mensaje) + "\n")
        proceso.stdin.flush()

    def respuesta(ident):
        while True:
            try:
                linea = lineas.get(timeout=PLAZO)
            except queue.Empty:
                raise SystemExit(f"FALLO: sin respuesta a id={ident} en {PLAZO} s\n{''.join(errores[-20:])}")
            mensaje = json.loads(linea)
            if mensaje.get("id") == ident:
                return mensaje

    try:
        enviar({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
            "protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "humo", "version": "1"}}})
        info = respuesta(1)["result"]["serverInfo"]
        if info.get("name") != "native_ssh" or info.get("version") != version:
            raise SystemExit(f"FALLO: serverInfo {info}, se esperaba native_ssh {version}")
        enviar({"jsonrpc": "2.0", "method": "notifications/initialized"})
        enviar({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
        herramientas = respuesta(2)["result"]["tools"]
        nombres = [h["name"] for h in herramientas]
        if not nombres or any(not n.startswith("ssh_") for n in nombres):
            raise SystemExit(f"FALLO: tools/list inesperado: {nombres}")
        return nombres
    finally:
        try:
            proceso.stdin.close()
        except OSError:
            pass
        try:
            proceso.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proceso.kill()
            proceso.wait()


def main():
    p = argparse.ArgumentParser()
    p.add_argument("binario")
    p.add_argument("--version", required=True)
    p.add_argument("--variante", required=True, choices=sorted(REQUISITOS))
    p.add_argument("--estado", required=True)
    a = p.parse_args()

    banderas = banderas_cpu()
    faltan = sorted(REQUISITOS[a.variante] - banderas) if banderas is not None else None
    estado = {"variante": a.variante, "binario": os.path.basename(a.binario), "plataforma": sys.platform}

    r = subprocess.run([a.binario, "--version"], capture_output=True, text=True, timeout=PLAZO)
    if r.returncode != 0 and es_aborto_cpu(r.returncode, r.stderr):
        if a.variante == "normal":
            raise SystemExit("FALLO: la variante normal no arranca en esta CPU")
        if faltan == []:
            raise SystemExit(f"FALLO: {a.variante} aborta por la CPU, pero /proc/cpuinfo tiene todas sus instrucciones")
        estado.update(resultado="no ejecutado",
                      motivo="la CPU del runner no tiene las instrucciones de la variante"
                             + (f" (faltan: {', '.join(faltan)})" if faltan else ""),
                      codigo=r.returncode)
        print(f"{a.variante}: NO EJECUTADO, aborta con el mensaje esperado (código {r.returncode})")
    else:
        if r.returncode != 0 or r.stdout.strip() != f"native_ssh {a.version}":
            raise SystemExit(f"FALLO: --version dio código {r.returncode}, salida {r.stdout!r}, errores {r.stderr[-500:]!r}")
        nombres = mcp(a.binario, a.version)
        estado.update(resultado="ejecutado", herramientas=len(nombres), nombres=nombres)
        print(f"{a.variante}: --version, initialize y tools/list correctos ({len(nombres)} herramientas)")
        if faltan:
            print(f"aviso: se ejecutó aunque /proc/cpuinfo no tiene {faltan}")

    with open(a.estado, "w", encoding="utf-8") as f:
        json.dump(estado, f, ensure_ascii=False, indent=2)


if __name__ == "__main__":
    main()
