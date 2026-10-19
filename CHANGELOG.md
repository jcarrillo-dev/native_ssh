# Registro de cambios

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/); las versiones siguen [Semantic Versioning](https://semver.org/lang/es/).

## [2.0.0] - 2026-10-19

Primera versión publicada. (Incluye todos los cambios anteriores, también la v1, que el autor escribió a mano: la estructura de carpetas y el código base, que después se fue mejorando por iteraciones.)

### Herramientas
- 15 herramientas MCP para trabajar en servidores Linux con bash por SSH:
  - `ssh_exec`, `ssh_output`, `ssh_input`, `ssh_cancel`: comandos en un shell persistente por destino, en cola, con respuesta en dos bloques (control JSON y salida tal cual), salida a un archivo del servidor (`guardarSalida`) para salidas grandes, entrada por terminal con secretos enmascarados (`secreto=true`) y cancelación;
  - `ssh_read_file`, `ssh_search`, `ssh_find`, `ssh_list_dir`, `ssh_stat`: lecturas por canales aparte, en paralelo, sin esperar a la cola del shell;
  - `ssh_write_file`, `ssh_replace_file_content`: escrituras atómicas con verificación del contenido, conservación de atributos y `sudo`;
  - `ssh_set_cwd`, `ssh_status`, `ssh_reset_shell`, `ssh_disconnect`: directorio de trabajo, estado y ciclo de vida de las sesiones.
- Avisos de antipatrón cuando `ssh_exec` se usa para lo que tiene herramienta propia.
- Instrucciones del servidor MCP y descripciones de cada herramienta como contrato para el cliente; skill `native-ssh` para tareas de varios pasos.

### Seguridad
- Verificación estricta de la clave del host contra `known_hosts`: native_ssh nunca acepta claves nuevas.
- Modo solo lectura por destino (`NATIVE_SSH_SOLO_LECTURA`), que falla cerrado.
- Secretos (contraseña de sudo, passphrases, `ssh_input` con `secreto`) enmascarados en respuestas y logs.
- Transporte HTTP con token obligatorio.

### Plataformas
- Native AOT para Linux x64 y Windows x64; servidores destino Linux con bash (Windows como destino se detecta y se rechaza).
- Cliente SSH basado en un fork de SSH.NET con correcciones para casos límite (ver «Por qué un fork de SSH.NET» en el README).

[2.0.0]: https://github.com/jcarrillo-dev/native_ssh/releases/tag/v2.0.0
