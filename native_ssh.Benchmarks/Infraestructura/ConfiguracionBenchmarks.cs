using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Order;

namespace NativeSsh.Benchmarks.Infraestructura
{
    /// <summary>
    /// Configuración centralizada de BenchmarkDotNet: memoria, hilos y los dos runtimes que importan
    /// para native_ssh (.NET 10 JIT y NativeAOT, que es como se publica).
    /// </summary>
    public class ConfiguracionBenchmarks : ManualConfig
    {
        public ConfiguracionBenchmarks()
        {
            _ = AddLogger(ConsoleLogger.Default);
            _ = AddColumnProvider(DefaultColumnProviders.Instance);
            _ = AddDiagnoser(MemoryDiagnoser.Default);
            _ = AddDiagnoser(ThreadingDiagnoser.Default);
            _ = WithOrderer(new DefaultOrderer(SummaryOrderPolicy.FastestToSlowest));
            _ = WithOptions(ConfigOptions.DisableLogFile);

            _ = AddJob(Job.Default.WithRuntime(CoreRuntime.Core10_0).WithId("Net10_0"));
            _ = AddJob(Job.Default.WithRuntime(NativeAotRuntime.Net10_0).WithId("NativeAot10_0"));
        }
    }
}
