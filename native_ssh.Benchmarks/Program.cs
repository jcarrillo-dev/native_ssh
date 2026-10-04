using BenchmarkDotNet.Running;
using NativeSsh.Benchmarks.Infraestructura;

namespace NativeSsh.Benchmarks
{
    public class Program
    {
        public static void Main(string[] args)
            => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, new ConfiguracionBenchmarks());
    }
}
