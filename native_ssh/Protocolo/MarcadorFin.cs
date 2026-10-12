using System.Buffers;

namespace NativeSsh.Protocolo
{
    // Marcador de fin: "__NSX_" + id (32 hex minúsculas) + "_" + código (1-3 dígitos) + "__". Puede llevar además, entre el
    // código y el "__" final, "_" + las líneas tecleadas que la cola ANTERIOR descartó mientras esperaba el centinela que
    // abre este envío (1-6 dígitos, como mucho MaxLineasDescartadas), solo si hubo alguna (ConstructorComando.EscribirCola):
    // líneas que llegaron antes que este envío y que ningún comando leyó.
    public static class MarcadorFin
    {
        public const int LongitudCabecera = 6;
        public const int LongitudId = 32;
        public const int MaxDigitosCodigo = 3;
        public const int MaxDigitosDescartadas = 6;
        public const int MaxLineasDescartadas = 999_999;
        public const int MaxLongitud = LongitudCabecera + LongitudId + 1 + MaxDigitosCodigo + 1 + MaxDigitosDescartadas + 2;

        // Código del marcador cuando la línea la ejecutó un shell distinto del de la sesión (IdentidadShell): fuera del rango
        // de los códigos de salida (0-255), así que ningún comando puede devolverlo.
        public const int CodigoShellAnidado = 999;

        // Código del marcador cuando lo que decodificó el shell no era el comando enviado y no se ejecutó
        // (ConstructorComando.EscribirComando; causas en SesionShell.MensajeEnvioIncompleto). Fuera del rango de los códigos
        // de salida.
        public const int CodigoEnvioIncompleto = 997;

        public static ReadOnlySpan<byte> Cabecera => "__NSX_"u8;

        public static void EscribirId(Guid id, IBufferWriter<byte> escritor)
        {
            _ = id.TryFormat(escritor.GetSpan(LongitudId), out int escritos, "N");
            escritor.Advance(escritos);
        }
    }
}
