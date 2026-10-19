"""Monta los paquetes de una Release de native_ssh a partir de los binarios publicados.

Uso:
  python3 empaquetado/empaquetar.py --sistema linux|windows --version X.Y.Z --binarios DIR --salida DIR --herramientas N

DIR de binarios lleva una subcarpeta por variante (normal, v3, v4) con NativeSsh o NativeSsh.exe. Cada paquete tiene una
carpeta raíz con su nombre y dentro: el binario, skill-native-ssh/ (copia de skill/native-ssh del repositorio), README.md
(de la plantilla empaquetado/README-<sistema>.md), LICENSE y THIRD-PARTY-NOTICES.md. Linux va en .tar.gz y Windows en .zip. Es determinista: fecha fija
(SOURCE_DATE_EPOCH, o la del commit si no está) y entradas en orden.
"""
import argparse
import gzip
import io
import os
import re
import sys
import tarfile
import time
import zipfile

RAIZ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKILL = os.path.join(RAIZ, "skill", "native-ssh")
PLANTILLAS = os.path.join(RAIZ, "empaquetado")

VARIANTES = {
    "normal": ("", "nivel por defecto", "nivel por defecto, x86-64 base"),
    "v3": ("-v3", "x86-64-v3", "x86-64-v3"),
    "v4": ("-v4", "x86-64-v4", "x86-64-v4"),
}


def plantilla(sistema, subs):
    with open(os.path.join(PLANTILLAS, f"README-{sistema}.md"), encoding="utf-8") as f:
        texto = f.read()
    for clave, valor in subs.items():
        texto = texto.replace("@" + clave + "@", valor)
    sobrantes = re.findall(r"@[A-Z]+@", texto)
    if sobrantes:
        sys.exit(f"README-{sistema}.md: marcadores sin sustituir: {sorted(set(sobrantes))}")
    return texto.encode("utf-8")


def ficheros_skill():
    if not os.path.isfile(os.path.join(SKILL, "SKILL.md")):
        sys.exit(f"falta {SKILL}/SKILL.md")
    for raiz, dirs, archivos in os.walk(SKILL):
        dirs.sort()
        for nombre in sorted(archivos):
            ruta = os.path.join(raiz, nombre)
            with open(ruta, "rb") as f:
                yield os.path.relpath(ruta, SKILL).replace(os.sep, "/"), f.read()


def contenido(binario, nombre_bin, readme):
    with open(binario, "rb") as f:
        yield nombre_bin, f.read(), 0o755
    for rel, datos in ficheros_skill():
        yield "skill-native-ssh/" + rel, datos, 0o644
    yield "README.md", readme, 0o644
    for nombre in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
        with open(os.path.join(RAIZ, nombre), "rb") as f:
            yield nombre, f.read(), 0o644


def hacer_tar(ruta, carpeta, binario, readme, mtime):
    with open(ruta, "wb") as f, gzip.GzipFile(filename="", mode="wb", fileobj=f, mtime=mtime) as gz, \
            tarfile.open(fileobj=gz, mode="w", format=tarfile.PAX_FORMAT) as t:
        hechos = set()
        for rel, datos, modo in contenido(binario, "NativeSsh", readme):
            partes = (carpeta + "/" + rel).split("/")
            for i in range(1, len(partes)):
                d = "/".join(partes[:i])
                if d not in hechos:
                    hechos.add(d)
                    info = tarfile.TarInfo(d)
                    info.type = tarfile.DIRTYPE
                    info.mode = 0o755
                    info.mtime = mtime
                    t.addfile(info)
            info = tarfile.TarInfo(carpeta + "/" + rel)
            info.size = len(datos)
            info.mode = modo
            info.mtime = mtime
            t.addfile(info, io.BytesIO(datos))


def hacer_zip(ruta, carpeta, binario, readme, mtime):
    fecha = time.gmtime(mtime)[:6]
    with zipfile.ZipFile(ruta, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for rel, datos, modo in contenido(binario, "NativeSsh.exe", readme):
            info = zipfile.ZipInfo(carpeta + "/" + rel, date_time=fecha)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (0o100000 | modo) << 16
            z.writestr(info, datos)


def fecha_fija():
    valor = os.environ.get("SOURCE_DATE_EPOCH")
    if valor:
        return int(valor)
    return 1792368000  # 2026-10-19 00:00 UTC


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--sistema", choices=("linux", "windows"), required=True)
    p.add_argument("--version", required=True)
    p.add_argument("--binarios", required=True)
    p.add_argument("--salida", required=True)
    p.add_argument("--herramientas", required=True, type=int)
    p.add_argument("--variantes", default="normal,v3,v4")
    a = p.parse_args()

    if not re.fullmatch(r"\d+\.\d+\.\d+", a.version):
        sys.exit(f"versión no válida: {a.version}")
    os.makedirs(a.salida, exist_ok=True)
    mtime = fecha_fija()
    rid = "linux-x64" if a.sistema == "linux" else "win-x64"
    ext = ".tar.gz" if a.sistema == "linux" else ".zip"
    nombre_bin = "NativeSsh" if a.sistema == "linux" else "NativeSsh.exe"

    for variante in a.variantes.split(","):
        sufijo, texto_variante, nivel = VARIANTES[variante]
        carpeta = f"native_ssh-{a.version}-{rid}{sufijo}"
        paquete = carpeta + ext
        binario = os.path.join(a.binarios, variante, nombre_bin)
        if not os.path.isfile(binario):
            sys.exit(f"falta {binario}")
        readme = plantilla(a.sistema, {
            "VERSION": a.version, "VARIANTE": texto_variante, "NIVEL": nivel, "ZIP": paquete,
            "CARPETA": carpeta, "HERRAMIENTAS": str(a.herramientas)})
        ruta = os.path.join(a.salida, paquete)
        if a.sistema == "linux":
            hacer_tar(ruta, carpeta, binario, readme, mtime)
        else:
            hacer_zip(ruta, carpeta, binario, readme, mtime)
        print(f"{paquete}  {os.path.getsize(ruta)} bytes")


if __name__ == "__main__":
    main()
