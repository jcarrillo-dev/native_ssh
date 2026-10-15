using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using BenchmarkDotNet.Attributes;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;
using NativeSsh.Benchmarks.Copias;
using NativeSsh.Ssh;

namespace NativeSsh.Benchmarks
{
    // Solo para medir de dónde sale el Gen1 de KnownHostsBenchmarks tras pasar las líneas de known_hosts a una lista rentada
    // (0,43 → ~1,8 colecciones por cada 1000 operaciones): el antes (copia de 7ffba5c) y el después en la misma tanda, y
    // variantes que difieren en una sola cosa del camino. Mismo known_hosts de 200 líneas y la clave conocida al final.
    // - Antes_Iterador: el código de 7ffba5c (las líneas se leen a demanda con un iterador y mueren una a una).
    // - Antes_ListaMaterializada: el mismo código de 7ffba5c, pero las líneas se leen antes en una List (lo único distinto
    //   del anterior: todas las líneas vivas a la vez).
    // - Actual_List / Actual_PooledListRef / Actual_PooledListRef256 / Actual_Array: el código actual (estático, con
    //   ReadOnlySpan) con las líneas en una List, en una lista rentada (el producto), en una rentada con capacidad para
    //   todas desde el principio (sin crecer) o en un string[] (File.ReadAllLines).
    // - Actual_LineasPreexistentes: el código actual sobre líneas leídas en GlobalSetup (sin leer el archivo ni crear las
    //   líneas: separa el coste de las líneas del de procesarlas).
    // - Spans: el fichero en un búfer rentado y sus líneas y campos como ReadOnlySpan<char>, sin un string por línea
    //   (VerificadorHostConocidoSpans).
    // - Delay: ValueLINQ Delay con procesadores struct, fichero a fichero (VerificadorHostConocidoDelay).
    // - PorFichero: el producto, pero fichero a fichero (VerificadorHostConocidoPorFichero).
    // - SpansDelay: el lector por spans con el recorrido en ValueLINQ Delay (VerificadorHostConocidoSpansDelay).
    public class GeneracionesKnownHostsBenchmarks
    {
        private const int Lineas = 200;
        private const string Servidor = "destino.ejemplo";
        private const int Puerto = 2222;
        private const string Tipo = "ssh-ed25519";

        private readonly string _directorio = Path.Combine(Path.GetTempPath(), $"ns_bench_gen_{Guid.NewGuid():N}");
        private string _ruta = "";
        private VerificadorHostConocido _verificador = null!;
        private VerificadorHostConocidoAntes _antes = null!;
        private VerificadorHostConocidoSpans _spans = null!;
        private VerificadorHostConocidoDelay _delay = null!;
        private VerificadorHostConocidoPorFichero _porFichero = null!;
        private VerificadorHostConocidoSpansDelay _spansDelay = null!;
        private string[] _lineasPreexistentes = [];
        private byte[] _clave = [];

        [GlobalSetup]
        public void Preparar()
        {
            _ = Directory.CreateDirectory(_directorio);
            _ruta = Path.Combine(_directorio, "known_hosts");
            StringBuilder texto = new();
            for (int i = 0; i < Lineas - 1; i++)
                _ = texto.Append("host").Append(i).Append(".ejemplo ssh-ed25519 ").Append(Convert.ToBase64String(Blob((byte)i))).Append('\n');
            _clave = Blob(255);
            _ = texto.Append("[destino.ejemplo]:2222 ssh-ed25519 ").Append(Convert.ToBase64String(_clave)).Append('\n');
            File.WriteAllText(_ruta, texto.ToString());
            _verificador = new VerificadorHostConocido([_ruta]);
            _antes = new VerificadorHostConocidoAntes([_ruta]);
            _spans = new VerificadorHostConocidoSpans([_ruta]);
            _delay = new VerificadorHostConocidoDelay([_ruta]);
            _porFichero = new VerificadorHostConocidoPorFichero([_ruta]);
            _spansDelay = new VerificadorHostConocidoSpansDelay([_ruta]);
            _lineasPreexistentes = File.ReadAllLines(_ruta);
            bool isTodasConocen = _verificador.Verificar(Servidor, Puerto, Tipo, _clave) == ResultadoHostEnum.Conocido
                && _antes.Verificar(Servidor, Puerto, Tipo, _clave) == ResultadoHostEnum.Conocido
                && Actual_PooledListRef256() == ResultadoHostEnum.Conocido
                && Spans() == ResultadoHostEnum.Conocido && Delay() == ResultadoHostEnum.Conocido && PorFichero() == ResultadoHostEnum.Conocido
                && SpansDelay() == ResultadoHostEnum.Conocido && TiposClave_SpansDelay() == 1
                && TiposClave_Spans() == 1 && TiposClave_Delay() == 1 && TiposClave_Actual_PooledListRef() == 1;
            if (!isTodasConocen)
                throw new InvalidOperationException("El escenario no reconoce la clave conocida.");
        }

        [Benchmark(Baseline = true)]
        public ResultadoHostEnum Antes_Iterador() => _antes.Verificar(Servidor, Puerto, Tipo, _clave);

        [Benchmark]
        public ResultadoHostEnum Antes_ListaMaterializada() => VerificadorHostConocidoAntes.Verificar(LeerEnList(), Servidor, Puerto, Tipo, _clave);

        [Benchmark]
        public ResultadoHostEnum Actual_List() => VerificadorHostConocido.Verificar(CollectionsMarshal.AsSpan(LeerEnList()), Servidor, Puerto, Tipo, _clave);

        [Benchmark]
        public ResultadoHostEnum Actual_PooledListRef() => _verificador.Verificar(Servidor, Puerto, Tipo, _clave);

        [Benchmark]
        public ResultadoHostEnum Actual_PooledListRef256()
        {
            using PooledListRef<string> lineas = new(256);
            foreach (string linea in File.ReadLines(_ruta))
                lineas.Add(linea);
            return VerificadorHostConocido.Verificar(lineas.Span, Servidor, Puerto, Tipo, _clave);
        }

        [Benchmark]
        public ResultadoHostEnum Actual_Array() => VerificadorHostConocido.Verificar(File.ReadAllLines(_ruta), Servidor, Puerto, Tipo, _clave);

        [Benchmark]
        public ResultadoHostEnum Actual_LineasPreexistentes() => VerificadorHostConocido.Verificar(_lineasPreexistentes, Servidor, Puerto, Tipo, _clave);

        [Benchmark]
        public ResultadoHostEnum Spans() => _spans.Verificar(Servidor, Puerto, Tipo, _clave);

        [Benchmark]
        public ResultadoHostEnum Delay() => _delay.Verificar(Servidor, Puerto, Tipo, _clave);

        [Benchmark]
        public ResultadoHostEnum PorFichero() => _porFichero.Verificar(Servidor, Puerto, Tipo, _clave);

        [Benchmark]
        public ResultadoHostEnum SpansDelay() => _spansDelay.Verificar(Servidor, Puerto, Tipo, _clave);

        [Benchmark]
        public int TiposClave_SpansDelay() => _spansDelay.ObtenerTiposClave(Servidor, Puerto).Count;

        [Benchmark]
        public int TiposClave_Spans() => _spans.ObtenerTiposClave(Servidor, Puerto).Count;

        [Benchmark]
        public int TiposClave_Delay() => _delay.ObtenerTiposClave(Servidor, Puerto).Count;

        [Benchmark]
        public int TiposClave_Antes_Iterador() => _antes.ObtenerTiposClave(Servidor, Puerto).Count;

        [Benchmark]
        public int TiposClave_Antes_ListaMaterializada() => VerificadorHostConocidoAntes.ObtenerTiposClave(LeerEnList(), Servidor, Puerto).Count;

        [Benchmark]
        public int TiposClave_Actual_List() => VerificadorHostConocido.ObtenerTiposClave(CollectionsMarshal.AsSpan(LeerEnList()), Servidor, Puerto).Count;

        [Benchmark]
        public int TiposClave_Actual_PooledListRef() => _verificador.ObtenerTiposClave(Servidor, Puerto).Count;

        [GlobalCleanup]
        public void Limpiar()
        {
            if (Directory.Exists(_directorio))
                Directory.Delete(_directorio, recursive: true);
        }

        private List<string> LeerEnList()
        {
            List<string> lineas = [];
            foreach (string linea in File.ReadLines(_ruta))
                lineas.Add(linea);
            return lineas;
        }

        // Blob SSH de una clave ed25519 (string del tipo y 32 bytes de clave).
        private static byte[] Blob(byte semilla)
        {
            byte[] tipo = "ssh-ed25519"u8.ToArray();
            byte[] blob = new byte[4 + tipo.Length + 4 + 32];
            BinaryPrimitives.WriteInt32BigEndian(blob, tipo.Length);
            tipo.CopyTo(blob, 4);
            BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(4 + tipo.Length), 32);
            blob.AsSpan(8 + tipo.Length).Fill(semilla);
            return blob;
        }
    }
}
