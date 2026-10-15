using System.Buffers;
using System.Text;
using BenchmarkDotNet.Attributes;
using NativeSsh.Buffers;
using NativeSsh.Herramientas;
using NativeSsh.Protocolo;
using NativeSsh.Sesion;

namespace NativeSsh.Benchmarks
{
    // Línea base de la ruta caliente (D11). Objetivo para optimizar después: 0 B asignados
    // en Escaner, Constructor y LimpiarAnsi; en BufferSalida solo los segmentos del pool.
    public class RutaCalienteBenchmarks
    {
        // Lo que entrega cada lectura del shell: los bloques miden exactamente esto, así "16K" son 16 KiB.
        private const int TamanoBloque = SesionShell.TamanoBloqueLectura;

        // Valores por defecto de OpcionesNativeSsh: la página de ssh_output (MaxBytesPorRespuesta) y el tope de
        // salida de una petición (MaxBytesSalidaPorPeticion).
        private const int TamanoPagina = 64 * 1024;
        private const int CapacidadSalida = 1024 * 1024;

        // Una salida que supera el tope: 1 MiB y 16 bloques más, que descartan los 16 segmentos más antiguos.
        private const int BloquesSalidaDesbordada = (CapacidadSalida / TamanoBloque) + 16;

        private static readonly Guid Id = Guid.Parse("0123456789abcdef0123456789abcdef");
        private static readonly Guid Centinela = Guid.Parse("fedcba9876543210fedcba9876543210");

        private readonly EscanerMarcador _escaner = new(TamanoBloque);
        private readonly ArrayBufferWriter<byte> _escritor = new(64 * 1024);
        private byte[] _bloque = [];
        private byte[] _bloqueConMarcador = [];
        private byte[] _bloqueAnsi = [];
        private byte[] _copiaAnsi = [];
        private string _comando = "";
        private BufferSalida _salidaLlena = null!;

        [GlobalSetup]
        public void Preparar()
        {
            _bloque = CrearBloque("linea de salida normal con __ algo ñ\n"u8);
            byte[] marcador = Encoding.UTF8.GetBytes($"__NSX_{Id:N}_0__\n");
            // CopyTo y no [.. _bloque], que aquí se compila a Enumerable.ToArray.
            _bloqueConMarcador = new byte[_bloque.Length];
            _bloque.CopyTo(_bloqueConMarcador, 0);
            marcador.CopyTo(_bloqueConMarcador, _bloqueConMarcador.Length - marcador.Length);

            _bloqueAnsi = CrearBloque("\u001b[32mverde\u001b[0m texto\r\n"u8);
            _copiaAnsi = new byte[_bloqueAnsi.Length];
            _comando = "cd /var/log && grep -R \"error\" . | tail -n 200 | awk '{print $1}' | sort | uniq -c";
            _salidaLlena = new BufferSalida(CapacidadSalida, TimeProvider.System);
            for (int i = 0; i < TamanoPagina / TamanoBloque; i++)
                _salidaLlena.Escribir(_bloque);

            ComprobarEscenarios();
        }

        [Benchmark]
        public bool Escaner_Bloque16K()
        {
            _escaner.Reiniciar(Id);
            return _escaner.Procesar(_bloque, null, out _);
        }

        [Benchmark]
        public bool Escaner_Bloque16KConMarcador()
        {
            _escaner.Reiniciar(Id);
            return _escaner.Procesar(_bloqueConMarcador, null, out _);
        }

        [Benchmark]
        public int Constructor_Comando()
        {
            _escritor.ResetWrittenCount();
            ConstructorComando.EscribirComando(_comando, Id, Centinela, _escritor);
            return _escritor.WrittenCount;
        }

        [Benchmark]
        public int LimpiarAnsi_Bloque16K()
        {
            _bloqueAnsi.CopyTo(_copiaAnsi, 0);
            return ConversorTexto.LimpiarAnsi(_copiaAnsi);
        }

        [Benchmark]
        public long BufferSalida_Escribir1MiB()
        {
            BufferSalida salida = Escribir(CapacidadSalida, BloquesSalidaDesbordada);
            long longitud = salida.Longitud;
            salida.Liberar();
            return longitud;
        }

        [Benchmark]
        public long BufferSalida_DescarteSegmentosAntiguos()
        {
            BufferSalida salida = Escribir(64 * 1024, 16);
            long longitud = salida.Longitud;
            salida.Liberar();
            return longitud;
        }

        [Benchmark]
        public int Paginador_64K() => PaginadorSalida.Leer(_salidaLlena, 0, TamanoPagina, isFinalizada: true).Texto.Length;

        // Bloque de exactamente TamanoBloque bytes: la línea repetida y la última cortada, como trocea el canal un flujo continuo.
        private static byte[] CrearBloque(ReadOnlySpan<byte> linea)
        {
            byte[] bloque = new byte[TamanoBloque];
            for (int i = 0; i < bloque.Length; i += linea.Length)
                linea[..Math.Min(linea.Length, bloque.Length - i)].CopyTo(bloque.AsSpan(i));
            return bloque;
        }

        private BufferSalida Escribir(int capacidad, int bloques)
        {
            BufferSalida salida = new(capacidad, TimeProvider.System);
            for (int i = 0; i < bloques; i++)
                salida.Escribir(_bloque);
            return salida;
        }

        // Fuera de la medida: si la preparación no reproduce lo que dice el nombre de cada benchmark, falla en lugar de
        // medir otra cosa.
        private void ComprobarEscenarios()
        {
            Exigir(!Escaner_Bloque16K(), "el bloque sin marcador no completa el comando");
            Exigir(Escaner_Bloque16KConMarcador(), "el bloque con el marcador al final completa el comando");

            PaginaSalida pagina = PaginadorSalida.Leer(_salidaLlena, 0, TamanoPagina, isFinalizada: true);
            Exigir(pagina.Siguiente == TamanoPagina && !pagina.HasMas, "la página recorre exactamente 64 KiB");

            BufferSalida desbordada = Escribir(CapacidadSalida, BloquesSalidaDesbordada);
            bool hasDescarte = desbordada.Leer(0, []).HasDatosDescartados;
            desbordada.Liberar();
            Exigir(hasDescarte, "escribir más de 1 MiB descarta los segmentos más antiguos");
        }

        private static void Exigir(bool isCumplido, string escenario)
        {
            if (!isCumplido)
                throw new InvalidOperationException($"La preparación no reproduce el escenario: {escenario}.");
        }
    }
}
