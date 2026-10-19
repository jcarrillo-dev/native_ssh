#!/usr/bin/env bash
# Publica native_ssh para linux-x64 (Native AOT) en las variantes pedidas y comprueba cada binario.
# Uso: empaquetado/publicar-linux.sh <directorio-salida> [normal] [v3] [v4]   (sin variantes, las tres)
# Se ejecuta en la raíz del repositorio, con el submódulo inicializado, y debe correr en un sistema con una glibc genérica
# (el workflow de Releases usa un contenedor Debian 12): la nota ELF «x86 ISA needed» y el suelo de glibc del binario salen
# de la glibc con la que se enlaza. Una glibc compilada para x86-64-v3 (p. ej. la de CachyOS) deja la nota x86-64-v3 y
# entonces ld.so rechaza el binario en una CPU sin v3 aunque se haya pedido el nivel por defecto.
set -euo pipefail

SUELO_GLIBC="2.34"

salida="${1:?uso: publicar-linux.sh <directorio-salida> [normal] [v3] [v4]}"
shift
variantes=("$@")
[ ${#variantes[@]} -eq 0 ] && variantes=(normal v3 v4)

mkdir -p "$salida"
salida="$(cd "$salida" && pwd)"

for variante in "${variantes[@]}"; do
  case "$variante" in
    normal) ilc="" ;;
    v3) ilc="x86-64-v3" ;;
    v4) ilc="x86-64-v4" ;;
    *) echo "variante desconocida: $variante" >&2; exit 2 ;;
  esac

  # Sin restos de la variante anterior: el enlazado nativo de AOT se rehace entero.
  rm -rf -- native_ssh/obj/Release/net10.0/linux-x64 native_ssh/bin/Release/net10.0/linux-x64 "$salida/$variante"
  echo "::group::dotnet publish linux-x64 $variante"
  dotnet publish native_ssh/native_ssh.csproj -c Release -r linux-x64 ${ilc:+-p:IlcInstructionSet=$ilc} -o "$salida/$variante"
  echo "::endgroup::"

  binario="$salida/$variante/NativeSsh"
  [ -x "$binario" ] || { echo "no se generó $binario" >&2; exit 1; }
  # Solo el binario: fuera los símbolos de depuración (.dbg) que deja el publish.
  find "$salida/$variante" -mindepth 1 ! -name NativeSsh -delete

  # Nota ELF: debe pedir solo x86-64-baseline. El nivel de IlcInstructionSet no aparece aquí (lo comprueba el runtime al
  # arrancar, con su mensaje); una nota con v2, v3 o v4 vendría de la glibc del sistema de compilación.
  nota="$(readelf -n "$binario" | grep -i 'x86 ISA needed' || true)"
  echo "$variante: $nota"
  if [ "$(echo "$nota" | sed 's/.*x86 ISA needed:[[:space:]]*//')" != "x86-64-baseline" ]; then
    echo "la nota ELF de $variante no es solo x86-64-baseline: «$nota»" >&2
    exit 1
  fi

  # Suelo de glibc: el README de los paquetes promete glibc $SUELO_GLIBC o posterior.
  glibc="$(readelf --dyn-syms -W "$binario" | grep -o 'GLIBC_[0-9.]*' | sed 's/GLIBC_//' | sort -Vu | tail -n 1)"
  echo "$variante: GLIBC_$glibc"
  if [ "$(printf '%s\n%s\n' "$glibc" "$SUELO_GLIBC" | sort -V | tail -n 1)" != "$SUELO_GLIBC" ]; then
    echo "$variante necesita GLIBC_$glibc, más que el suelo $SUELO_GLIBC del README" >&2
    exit 1
  fi
  sha256sum "$binario"
done
