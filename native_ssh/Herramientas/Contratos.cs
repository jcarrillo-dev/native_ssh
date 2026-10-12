using System.ComponentModel;
using NativeSsh.Comun;

namespace NativeSsh.Herramientas
{
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
        bool? IsSoloLectura = null);

    public sealed record ResumenPeticion(
        string Id,
        string Destino,
        EstadoPeticionEnum Estado,
        int? CodigoSalida,
        string Comando,
        double SegundosEjecucion);

    // SoloLectura: las entradas de NATIVE_SSH_SOLO_LECTURA (null si no hay). En cada sesión, IsSoloLectura es true o false si
    // ya se sabe sin esperar al DNS y null si aún no (o si no hay modo solo lectura).
    public sealed record ResultadoEstado(IReadOnlyList<EstadoSesion> Sesiones, IReadOnlyList<ResumenPeticion> Peticiones, IReadOnlyList<string>? SoloLectura = null);

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
    public sealed record EdicionTexto(
        [property: Description("Texto exacto a reemplazar (incluyendo espacios e indentación). No puede estar vacío.")] string TargetContent,
        [property: Description("Nuevo texto que reemplazará a targetContent. Obligatorio (\"\" para borrar targetContent).")] string? ReplacementContent,
        [property: Description("Si es true, reemplaza todas las apariciones de targetContent; por defecto false (falla si aparece más de una vez).")] bool AllowMultiple = false);

    // ssh_set_cwd. Directorio: ruta absoluta fijada, o null si se restableció a $HOME.
    public sealed record ResultadoDirectorioTrabajo(string Destino, string? Directorio, string Mensaje);
}
