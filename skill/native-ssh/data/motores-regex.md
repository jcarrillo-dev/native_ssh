# Sintaxis de regex según el motor de `ssh_search`

| Necesitas | `[motor: rg]` (regex de Rust) | `[motor: grep -P]` (PCRE) | `[motor: grep -E]` (ERE POSIX) |
|---|---|---|---|
| Un dígito | `\d` o `[0-9]` | `\d` o `[0-9]` | `[0-9]` (NUNCA `\d`) |
| Letra, dígito o `_` | `\w` | `\w` | `[[:alnum:]_]` |
| Blanco | `\s` | `\s` | `[[:space:]]` |
| Límite de palabra | `\b` | `\b` | evítalo (no es POSIX): rodea con `[^[:alnum:]_]` |
| Grupo | `(…)` | `(…)` | `(…)` |
| Cuantificador perezoso | `.*?` | `.*?` | no existe |
| Mirar delante o detrás | no existe | `(?=…)`, `(?<=…)` | no existe |
| Retroreferencia | no existe | `\1` | evítala (no es ERE POSIX) |
| Sin distinguir mayúsculas | `(?i)` delante | `(?i)` delante | no hay opción: usa `[Ee]rror` |

Válido en los tres: literales, `[0-9]`, `[[:space:]]`, `[[:alnum:]_]`, `^`, `$`, `.`, `*`, `+`, `?`, `{n,m}`, grupos `( )` y la alternativa con la barra vertical: `error|fail` (sin barra invertida delante: con ella, `rg` y `grep -P` buscan la barra literal).

Notas:

- El patrón se busca línea a línea: no hay patrones de varias líneas.
- Sin grep de GNU (busybox) el motor es `grep -E` y los binarios solo se omiten por su extensión.
