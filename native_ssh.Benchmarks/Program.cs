using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using NativeSsh.Benchmarks.Infraestructura;

namespace NativeSsh.Benchmarks
{
    public class Program
    {
        // --perfil-gc: perfil de GC con EventPipe (ConfiguracionPerfilGc) en lugar de la configuración normal.
        public static void Main(string[] args)
        {
            bool isPerfilGc = Array.IndexOf(args, "--perfil-gc") >= 0;
            string[] resto = isPerfilGc ? Array.FindAll(args, a => a != "--perfil-gc") : args;
            ManualConfig configuracion = isPerfilGc ? new ConfiguracionPerfilGc() : new ConfiguracionBenchmarks();
            _ = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(resto, configuracion);
        }
    }
}
