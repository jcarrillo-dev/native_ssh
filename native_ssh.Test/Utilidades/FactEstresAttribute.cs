namespace NativeSsh.Test.Utilidades
{
    // Pruebas de estrés pesadas (carga de CPU y cientos de canales): fuera de la suite por defecto. Se ejecutan con
    // NATIVE_SSH_ESTRES=1 dotnet test --filter Categoria=Estres, mejor confinadas a unas pocas CPU (taskset -c 0-7):
    // la carga se dimensiona con Environment.ProcessorCount, que respeta la afinidad del proceso.
    public sealed class FactEstresAttribute : FactAttribute
    {
        public const string Variable = "NATIVE_SSH_ESTRES";

        public FactEstresAttribute() => Skip = Environment.GetEnvironmentVariable(Variable) == "1" ? null : $"Prueba de estrés: solo con {Variable}=1.";
    }
}
