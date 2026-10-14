using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;

namespace NativeSsh.Benchmarks.Infraestructura
{
    /// <summary>
    /// Perfil de GC (EventPipe con GcVerbose: colecciones, promociones y muestras de asignación por tipo), solo en .NET 10
    /// JIT: EventPipe no está en el job NativeAOT. Se elige con --perfil-gc (Program); deja un .nettrace por benchmark.
    /// </summary>
    public class ConfiguracionPerfilGc : ManualConfig
    {
        public ConfiguracionPerfilGc()
        {
            _ = AddLogger(ConsoleLogger.Default);
            _ = AddColumnProvider(DefaultColumnProviders.Instance);
            _ = AddDiagnoser(MemoryDiagnoser.Default);
            _ = AddDiagnoser(new EventPipeProfiler(EventPipeProfile.GcVerbose));
            _ = WithOptions(ConfigOptions.DisableLogFile);

            _ = AddJob(Job.Default.WithRuntime(CoreRuntime.Core10_0).WithId("Net10_0"));
        }
    }
}
