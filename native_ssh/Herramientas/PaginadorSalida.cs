using NativeSsh.Buffers;
using NativeSsh.Protocolo;

namespace NativeSsh.Herramientas
{
    public readonly record struct PaginaSalida(string Texto, long Desde, long Siguiente, long Fin, bool HasDatosDescartados)
    {
        public bool HasMas => Siguiente < Fin;
    }

    // Convierte un tramo del BufferSalida en texto: sin caracteres UTF-8 partidos y sin secuencias ANSI. isComoTerminal: un
    // '\r' suelto se trata como en un terminal (ConversorTexto.LimpiarAnsi), para lo que ve el cliente de ssh_exec, ssh_output,
    // ssh_input y ssh_cancel; no para la salida de las herramientas por el shell, que se interpreta. Una línea partida entre
    // dos páginas no se colapsa entera: cada página trata lo suyo.
    public static class PaginadorSalida
    {
        public static PaginaSalida Leer(BufferSalida salida, long desde, int maxBytes, bool isFinalizada, bool isComoTerminal = false)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 4);
            using BufferAlquilado buffer = new(maxBytes);
            LecturaSalida lectura = salida.Leer(Math.Max(0, desde), buffer.Span);
            Span<byte> datos = buffer.Span[..lectura.Copiados];

            int saltados = lectura.Desde > 0 ? ConversorTexto.ContarContinuacionesIniciales(datos) : 0;
            datos = datos[saltados..];

            int utiles = ConversorTexto.AjustarFinUtf8(datos);
            // Una secuencia incompleta al final de una salida terminada no se completará nunca: se entrega tal cual.
            bool isFinReal = lectura.Desde + lectura.Copiados == lectura.Fin;
            if (utiles < datos.Length && isFinReal && isFinalizada)
                utiles = datos.Length;

            long siguiente = lectura.Desde + saltados + utiles;
            int longitud = ConversorTexto.LimpiarAnsi(datos[..utiles], isComoTerminal);
            return new PaginaSalida(ConversorTexto.ATexto(datos[..longitud]), lectura.Desde, siguiente, lectura.Fin, lectura.HasDatosDescartados);
        }
    }
}
