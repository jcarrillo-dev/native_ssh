namespace NativeSsh.Buffers
{
    // A dónde va lo que el escáner del marcador de fin deja pasar (EscanerMarcador.Procesar): la salida de la petición
    // (BufferSalida) o un filtro delante de ella (FiltroMarcasGuardado, que quita las marcas de guardarSalida).
    public interface IDestinoSalida
    {
        void Escribir(ReadOnlySpan<byte> datos);
    }
}
