using System.Diagnostics;

namespace NativeSsh.Test.Integracion
{
    // Cuándo se ejecutan las pruebas de contenedores (categoría Contenedor): solo si podman funciona en la máquina (como
    // las de estrés, se omiten solas si no). NATIVE_SSH_SIN_CONTENEDORES=1 las omite aunque haya podman.
    // Fallos conocidos (categoría FalloConocido, Bloque S): la prueba afirma el comportamiento CORRECTO y por tanto falla
    // mientras el fallo siga ahí; se omite por defecto, con el motivo en el texto de la omisión, para que la suite siga en
    // verde. Se ejecutan con NATIVE_SSH_FALLOS_CONOCIDOS=1 (dotnet test --filter Categoria=FalloConocido).
    internal static class CondicionContenedor
    {
        public const string VariableSinContenedores = "NATIVE_SSH_SIN_CONTENEDORES";
        public const string VariableFallosConocidos = "NATIVE_SSH_FALLOS_CONOCIDOS";

        private static readonly Lazy<string?> MotivoSinPodman = new(CalcularMotivoSinPodman);

        public static string? Omision(string? falloConocido) =>
            Environment.GetEnvironmentVariable(VariableSinContenedores) == "1" ? $"Pruebas de contenedores desactivadas con {VariableSinContenedores}=1."
            : MotivoSinPodman.Value is { } motivo ? motivo
            : falloConocido is not null && Environment.GetEnvironmentVariable(VariableFallosConocidos) != "1" ? $"Fallo conocido (solo con {VariableFallosConocidos}=1): {falloConocido}"
            : null;

        private static string? CalcularMotivoSinPodman()
        {
            try
            {
                ProcessStartInfo inicio = new("podman", ["info", "--format", "{{.Host.Security.Rootless}}"])
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using Process proceso = Process.Start(inicio)!;
                Task<string> errores = proceso.StandardError.ReadToEndAsync();
                string salida = proceso.StandardOutput.ReadToEnd();
                if (!proceso.WaitForExit(TimeSpan.FromSeconds(30)))
                {
                    proceso.Kill(entireProcessTree: true);
                    return "Prueba de contenedores: 'podman info' no respondió en 30 s.";
                }

                // Nunca contra un podman con privilegios: las pruebas dan root dentro del contenedor.
                return proceso.ExitCode != 0 ? $"Prueba de contenedores: podman no funciona ({errores.Result.Trim()})."
                    : salida.Trim() != "true" ? "Prueba de contenedores: solo con podman sin privilegios (rootless)."
                    : null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return "Prueba de contenedores: podman no está instalado.";
            }
        }
    }

    public sealed class FactContenedorAttribute : FactAttribute
    {
        // Motivo y clasificación de un fallo conocido: la prueba se omite salvo con NATIVE_SSH_FALLOS_CONOCIDOS=1.
        public string? FalloConocido { get; set; }

        public override string? Skip
        {
            get => base.Skip ?? CondicionContenedor.Omision(FalloConocido);
            set => base.Skip = value;
        }
    }

    public sealed class TheoryContenedorAttribute : TheoryAttribute
    {
        public string? FalloConocido { get; set; }

        public override string? Skip
        {
            get => base.Skip ?? CondicionContenedor.Omision(FalloConocido);
            set => base.Skip = value;
        }
    }
}
