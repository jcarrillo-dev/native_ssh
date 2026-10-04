using System.Buffers;
using System.Text;
using BenchmarkDotNet.Attributes;
using NativeSsh.Buffers;
using NativeSsh.Herramientas;
using NativeSsh.Protocolo;

namespace NativeSsh.Benchmarks
{
    // Línea base de la ruta caliente (D11). Objetivo para optimizar después: 0 B asignados
    // en Escaner, Constructor y LimpiarAnsi; en BufferSalida solo los segmentos del pool.
    public class RutaCalienteBenchmarks
    {
        private static readonly Guid Id = Guid.Parse("0123456789abcdef0123456789abcdef");

        private readonly EscanerMarcador _escaner = new(16 * 1024);
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
            _bloque = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("linea de salida normal con __ algo ñ\n", 16 * 1024 / 38)));
            byte[] marcador = Encoding.UTF8.GetBytes($"__NSX_{Id:N}_0__\n");
            _bloqueConMarcador = new byte[_bloque.Length];
            _bloque.CopyTo(_bloqueConMarcador, 0);
            marcador.CopyTo(_bloqueConMarcador, _bloqueConMarcador.Length - marcador.Length);

            _bloqueAnsi = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("\u001b[32mverde\u001b[0m texto\r\n", 16 * 1024 / 24)));
            _copiaAnsi = new byte[_bloqueAnsi.Length];
            _comando = "cd /var/log && grep -R \"error\" . | tail -n 200 | awk '{print $1}' | sort | uniq -c";
            _salidaLlena = new BufferSalida(1 << 20, TimeProvider.System);
            for (int i = 0; i < 4; i++)
                _salidaLlena.Escribir(_bloque);
        }

        [Benchmark]
        public bool Escaner_Bloque16K()
        {
            _escaner.Reiniciar(Id);
            return _escaner.Procesar(_bloque, null, out _);
        }

        [Benchmark]
        public bool Escaner_BloqueConMarcador()
        {
            _escaner.Reiniciar(Id);
            return _escaner.Procesar(_bloqueConMarcador, null, out _);
        }

        [Benchmark]
        public int Constructor_Comando()
        {
            _escritor.ResetWrittenCount();
            ConstructorComando.EscribirComando(_comando, Id, _escritor);
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
            BufferSalida salida = new(1 << 20, TimeProvider.System);
            for (int i = 0; i < 64; i++)
                salida.Escribir(_bloque);
            long longitud = salida.Longitud;
            salida.Liberar();
            return longitud;
        }

        [Benchmark]
        public long BufferSalida_DescarteSegmentosAntiguos()
        {
            BufferSalida salida = new(64 * 1024, TimeProvider.System);
            for (int i = 0; i < 16; i++)
                salida.Escribir(_bloque);
            long longitud = salida.Longitud;
            salida.Liberar();
            return longitud;
        }

        [Benchmark]
        public int Paginador_64K() => PaginadorSalida.Leer(_salidaLlena, 0, 64 * 1024, isFinalizada: true).Texto.Length;
    }
}
