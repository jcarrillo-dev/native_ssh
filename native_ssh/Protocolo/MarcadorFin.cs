using System.Buffers;

namespace NativeSsh.Protocolo
{
    // Marcador de fin: "__NSX_" + id (32 hex minúsculas) + "_" + código (1-3 dígitos) + "__".
    public static class MarcadorFin
    {
        public const int LongitudCabecera = 6;
        public const int LongitudId = 32;
        public const int MaxDigitosCodigo = 3;
        public const int MaxLongitud = LongitudCabecera + LongitudId + 1 + MaxDigitosCodigo + 2;

        public static ReadOnlySpan<byte> Cabecera => "__NSX_"u8;

        public static void EscribirId(Guid id, IBufferWriter<byte> escritor)
        {
            _ = id.TryFormat(escritor.GetSpan(LongitudId), out int escritos, "N");
            escritor.Advance(escritos);
        }
    }
}
