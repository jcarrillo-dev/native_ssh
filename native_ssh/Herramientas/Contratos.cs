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
        string? Aviso)
    {
        public ControlComando Control() => new(Id, Destino, Estado, CodigoSalida, DesdeByte, SiguienteByte, HasMasSalida, HasSalidaDescartada, SegundosEjecucion, SegundosSinSalida, Aviso);
    }

    // Bloque 1 (control) de la respuesta de ssh_exec, ssh_output, ssh_input y ssh_cancel: JSON compacto en una sola línea, que
    // va primero para que sea la primera línea del archivo cuando el cliente vuelca la respuesta a disco. La salida va en el
    // bloque 2, sin escapar: dentro del JSON el modelo veía '\n', '\"' y '\\', y las tablas anchas llegaban en una línea.
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
        string? Aviso);

    public sealed record EstadoSesion(
        string Destino,
        bool IsConectada,
        bool IsShellActivo,
        long ComandosEjecutados,
        int ConexionesAbiertas,
        int ComandosEnCola,
        string? PeticionActual,
        int CanalesExecActivos);

    public sealed record ResumenPeticion(
        string Id,
        string Destino,
        EstadoPeticionEnum Estado,
        int? CodigoSalida,
        string Comando,
        double SegundosEjecucion);

    public sealed record ResultadoEstado(IReadOnlyList<EstadoSesion> Sesiones, IReadOnlyList<ResumenPeticion> Peticiones);

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

    // ssh_set_cwd. Directorio: ruta absoluta fijada, o null si se restableció a $HOME.
    public sealed record ResultadoDirectorioTrabajo(string Destino, string? Directorio, string Mensaje);
}
