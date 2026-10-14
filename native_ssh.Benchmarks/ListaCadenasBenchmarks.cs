using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;

namespace NativeSsh.Benchmarks
{
    // Solo para medir: la forma de PooledListStringBenchmarks de JCarrillo.AOT.Core (llenar y recorrer una List o una
    // PooledListRef de strings), con strings preexistentes (creados en GlobalSetup, como allí) y con strings nuevos en cada
    // operación (como las líneas de known_hosts). Cadenas de 100 caracteres, el largo de una línea de known_hosts.
    public class ListaCadenasBenchmarks
    {
        private readonly Consumer _consumidor = new();
        private string[] _cadenas = [];

        [Params(200, 1000)]
        public int Tamano { get; set; }

        [GlobalSetup]
        public void Preparar()
        {
            _cadenas = new string[Tamano];
            for (int i = 0; i < Tamano; i++)
                _cadenas[i] = $"Item-{i}".PadRight(100, 'x');
        }

        [Benchmark(Baseline = true)]
        public void List_Preexistentes()
        {
            List<string> lista = [];
            for (int i = 0; i < Tamano; i++)
                lista.Add(_cadenas[i]);
            for (int i = 0; i < lista.Count; i++)
                _consumidor.Consume(lista[i]);
        }

        [Benchmark]
        public void PooledListRef_Preexistentes()
        {
            using PooledListRef<string> lista = new();
            for (int i = 0; i < Tamano; i++)
                lista.Add(_cadenas[i]);
            foreach (string cadena in lista.Span)
                _consumidor.Consume(cadena);
        }

        [Benchmark]
        public void List_Nuevas()
        {
            List<string> lista = [];
            for (int i = 0; i < Tamano; i++)
                lista.Add(new string(_cadenas[i].AsSpan()));
            for (int i = 0; i < lista.Count; i++)
                _consumidor.Consume(lista[i]);
        }

        [Benchmark]
        public void PooledListRef_Nuevas()
        {
            using PooledListRef<string> lista = new();
            for (int i = 0; i < Tamano; i++)
                lista.Add(new string(_cadenas[i].AsSpan()));
            foreach (string cadena in lista.Span)
                _consumidor.Consume(cadena);
        }

        // Sin lista: cada string nuevo muere en cuanto se consume (como las líneas del iterador de antes).
        [Benchmark]
        public void SinLista_Nuevas()
        {
            for (int i = 0; i < Tamano; i++)
                _consumidor.Consume(new string(_cadenas[i].AsSpan()));
        }
    }
}
