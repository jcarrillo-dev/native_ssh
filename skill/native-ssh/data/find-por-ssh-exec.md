# `find` por `ssh_exec`: cuándo sí

Regla: localizar por nombre (glob, distinguiendo mayúsculas), por tipo archivo/directorio o por tamaño es de `ssh_find`. Todo lo demás de `find` va por `ssh_exec`, porque `ssh_find` no lo hace.

**Usa `ssh_find` (PROHIBIDO `find` por `ssh_exec`)** si solo necesitas: rutas, `-name`, `-type f`, `-type d`, `-maxdepth`, `-mindepth 1`, `-size` (solo con archivos), `-xdev`/`-mount`, `-L`/`-H`/`-P`, `-print`/`-print0`.

**Usa `find` por `ssh_exec`** si necesitas cualquiera de estos (lista no cerrada: vale cualquier predicado, acción u operador que no esté arriba):

| Para | Opciones |
|---|---|
| Nombre sin distinguir mayúsculas | `-iname` |
| Comparar con la ruta, no con el nombre | `-path`, `-ipath`, `-regex`, `-iregex` |
| Fecha | `-mtime`, `-mmin`, `-newer`, `-ctime`, `-atime` |
| Dueño, grupo, permisos | `-user`, `-group`, `-perm`, `-nouser`, `-nogroup` |
| Otros tipos | `-type l`, `-type s`, `-type p`, `-empty`, `-xtype` |
| Profundidad mínima distinta de 1 | `-mindepth 2`… |
| Directorios por tamaño | `-type d -size …` |
| Actuar o formatear | `-exec`, `-execdir`, `-delete`, `-printf`, `-ls` |
| Combinar condiciones | `-o`, `-a`, `-not`, `!`, `( … )` |

Si el resultado puede ser largo, añade `guardarSalida=true`. Con `-delete` o `-exec rm`, ensaya antes la misma orden sin la acción y mira qué saldría.
