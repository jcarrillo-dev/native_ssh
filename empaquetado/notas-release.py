"""Escribe las notas de una Release de native_ssh a partir de los estados del humo (humo/*.json).

Uso: python3 empaquetado/notas-release.py --version X.Y.Z --humo DIR --repo DUEÑO/REPO > notas.md
"""
import argparse
import glob
import json
import os

ORDEN = {"normal": 0, "v3": 1, "v4": 2}


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--version", required=True)
    p.add_argument("--humo", required=True)
    p.add_argument("--repo", required=True)
    a = p.parse_args()

    estados = []
    for ruta in glob.glob(os.path.join(a.humo, "*.json")):
        with open(ruta, encoding="utf-8") as f:
            e = json.load(f)
        e["sistema"] = os.path.basename(ruta).split("-", 1)[0]
        estados.append(e)
    estados.sort(key=lambda e: (e["sistema"], ORDEN.get(e["variante"], 9)))
    if not estados:
        raise SystemExit("no hay estados de humo")

    v = a.version
    print(f"native_ssh {v}: servidor MCP para trabajar en servidores Linux por SSH (Native AOT, un único binario).")
    print()
    print("## Paquetes")
    print()
    print("| Sistema | Normal (x86-64 base) | x86-64-v3 | x86-64-v4 |")
    print("|---|---|---|---|")
    print(f"| Linux x64 (glibc 2.34 o posterior) | `native_ssh-{v}-linux-x64.tar.gz` | `native_ssh-{v}-linux-x64-v3.tar.gz` | `native_ssh-{v}-linux-x64-v4.tar.gz` |")
    print(f"| Windows x64 | `native_ssh-{v}-win-x64.zip` | `native_ssh-{v}-win-x64-v3.zip` | `native_ssh-{v}-win-x64-v4.zip` |")
    print()
    print("Ante la duda, el normal. Un `-v3` o `-v4` en una CPU sin sus instrucciones no arranca y dice "
          "«The required instruction sets are not supported by the current CPU.». Cada paquete lleva el binario, la skill "
          "`skill-native-ssh/`, un README con la instalación y el registro en el cliente MCP, `LICENSE` y "
          "`THIRD-PARTY-NOTICES.md`. No hay paquete de macOS.")
    print()
    print("## Humo de esta Release")
    print()
    print("Cada binario se ha comprobado en el runner de GitHub con `--version`, `initialize` y `tools/list`, sin conectar "
          "a ningún servidor. La suite completa se pasa en local antes de etiquetar.")
    print()
    print("| Sistema | Variante | Resultado |")
    print("|---|---|---|")
    no_ejecutados = []
    for e in estados:
        if e["resultado"] == "ejecutado":
            texto = f"correcto ({e['herramientas']} herramientas)"
        else:
            texto = f"**compilado pero no ejecutado**: {e.get('motivo', 'la CPU del runner no tiene sus instrucciones')}"
            no_ejecutados.append(e)
        print(f"| {e['sistema']} | {e['variante']} | {texto} |")
    if no_ejecutados:
        print()
        print("Las variantes «no ejecutadas» se compilaron igual que las demás, pero la CPU del runner no tenía sus "
              "instrucciones: se comprobó solo que abortan con el mensaje esperado.")
    print()
    print("## Verificar la descarga")
    print()
    print("```bash")
    print("sha256sum -c SHA256SUMS --ignore-missing")
    print(f"gh attestation verify native_ssh-{v}-linux-x64.tar.gz --repo {a.repo}")
    print("```")


if __name__ == "__main__":
    main()
