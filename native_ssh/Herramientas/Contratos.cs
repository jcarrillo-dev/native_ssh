using System.ComponentModel;
using NativeSsh.Comun;

namespace NativeSsh.Herramientas
{
    // Respuesta de ssh_exec, ssh_output, ssh_input y ssh_cancel. No se serializa entera: va en dos bloques de texto MCP
    // (HerramientasSsh.DosBloques), primero el control (ControlComando, todo menos la salida) y después la salida tal cual.
    public sealed record ResultadoComando(
        string Id,
        string Destino,
        EstadoPeticionEnum Estado,
        int? CodigoSalida,
        string Salida,
        long DesdeByte,
        long SiguienteByte,
        bool HasMasSalida,
        bool HasSalidaDescartada,
        double SegundosEjecucion,
        double? SegundosSinSalida,
        string? Aviso,
        string? Archivo = null,
        long? Lineas = null,
        long? Bytes = null)
    {
        public ControlComando Control() => new(Id, Destino, Estado, CodigoSalida, DesdeByte, SiguienteByte, HasMasSalida, HasSalidaDescartada, SegundosEjecucion, SegundosSinSalida, Archivo, Lineas, Bytes, Aviso);
    }

    // Bloque 1 (control) de la respuesta de ssh_exec, ssh_output, ssh_input y ssh_cancel: JSON compacto en una sola línea, que
    // va primero para que sea la primera línea del archivo cuando el cliente vuelca la respuesta a disco. La salida va en el
    // bloque 2, sin escapar: dentro del JSON el modelo veía '\n', '\"' y '\\', y las tablas anchas llegaban en una línea.
    // Archivo, Lineas y Bytes: solo con ssh_exec y guardarSalida=true (la salida está en ese archivo del servidor; el bloque 2
    // lleva sus primeras líneas); null (no se escriben) en el resto o mientras no se conocen.
    public sealed record ControlComando(
        string Id,
        string Destino,
        EstadoPeticionEnum Estado,
        int? CodigoSalida,
        long DesdeByte,
        long SiguienteByte,
        bool HasMasSalida,
        bool HasSalidaDescartada,
        double SegundosEjecucion,
        double? SegundosSinSalida,
        string? Archivo,
        long? Lineas,
        long? Bytes,
        string? Aviso);

    public sealed record EstadoSesion(
        string Destino,
        bool IsConectada,
        bool IsShellActivo,
        long ComandosEjecutados,
        int ConexionesAbiertas,
        int ComandosEnCola,
        string? PeticionActual,
        int CanalesExecActivos,
        IReadOnlyList<string>? Alias = null,
        IReadOnlyList<string>? IdentityFile = null,
        bool? IsSoloLectura = null);

    public sealed record ResumenPeticion(
        string Id,
        string Destino,
        EstadoPeticionEnum Estado,
        int? CodigoSalida,
        string Comando,
        double SegundosEjecucion);

    // Aviso: con un alias, sus sesiones para otro usuario o puerto y las de sesiones llenas que no lo guardaron, con qué
    // destino cerrarlas y qué más cerraría cada uno (null si no hay). SoloLectura: las entradas de NATIVE_SSH_SOLO_LECTURA
    // (null si no hay). En cada sesión, IsSoloLectura es true o false si ya se sabe sin esperar al DNS y null si aún no (o
    // si no hay modo solo lectura).
    public sealed record ResultadoEstado(IReadOnlyList<EstadoSesion> Sesiones, IReadOnlyList<ResumenPeticion> Peticiones, string? Aviso = null, IReadOnlyList<string>? SoloLectura = null);

    public sealed record ResultadoAccion(string Destino, bool IsHecho, string Mensaje);

    public sealed record ResultadoMetadatos(
        string Ruta,
        bool Existe,
        string? Mensaje = null,
        string? Tipo = null,
        long? TamanoBytes = null,
        string? TamanoHumano = null,
        string? Permisos = null,
        string? UltimaModificacion = null,
        string? Propietario = null,
        string? Grupo = null,
        string? DestinoEnlace = null);

    // ssh_write_file y ssh_replace_file_content. Bytes: tamaño escrito (anexado, o archivo final en un reemplazo).
    // Reemplazos: solo ssh_replace_file_content. IsAtomica: false si se sobrescribió en el sitio o se anexó.
    public sealed record ResultadoEscritura(string Ruta, long Bytes, int? Reemplazos, bool IsAtomica, string Mensaje);

    // Una edición de ssh_replace_file_content con 'ediciones' (CI5): se busca en todo el archivo, tras aplicar las anteriores.
    // ReplacementContent es obligatorio (está en 'required' del esquema), pero el JSON del cliente puede omitirlo o traerlo
    // nulo: la herramienta lo rechaza antes de enviar nada.
    [Description("Sin rango de líneas: las ediciones se aplican en orden, cada una buscada en todo el archivo tras aplicar las anteriores")]
    public sealed record EdicionTexto(
        [property: Description("Texto exacto a reemplazar (incluyendo espacios e indentación). No puede estar vacío.")] string TargetContent,
        [property: Description("Nuevo texto que reemplazará a targetContent. Obligatorio (\"\" para borrar targetContent).")] string? ReplacementContent,
        [property: Description("Si es true, reemplaza todas las apariciones de targetContent; por defecto false (falla si aparece más de una vez).")] bool AllowMultiple = false);

    // ssh_set_cwd. Directorio: ruta absoluta fijada, o null si se restableció a $HOME.
    public sealed record ResultadoDirectorioTrabajo(string Destino, string? Directorio, string Mensaje);
}
