using System.Globalization;

namespace NativeSsh.Comun
{
    public enum ModoTransporteEnum
    {
        Stdio,
        HttpLoopback,
        HttpTodasLasInterfaces
    }

    // Argumentos de línea de comandos: [--http | --http-unsecure] [--puerto N].
    public readonly record struct OpcionesArranque(ModoTransporteEnum Modo, int Puerto)
    {
        public const int PuertoPorDefecto = 8722;

        public bool IsHttp => Modo != ModoTransporteEnum.Stdio;

        public static bool TryParsear(IReadOnlyList<string> args, string? puertoEntorno, out OpcionesArranque opciones, out string? error)
        {
            ModoTransporteEnum modo = ModoTransporteEnum.Stdio;
            string? puertoTexto = puertoEntorno is { Length: > 0 } ? puertoEntorno : null;
            opciones = default;
            error = null;

            for (int i = 0; i < args.Count; i++)
            {
                switch (args[i])
                {
                    case "--http" when modo == ModoTransporteEnum.Stdio:
                        modo = ModoTransporteEnum.HttpLoopback;
                        break;
                    case "--http-unsecure" when modo == ModoTransporteEnum.Stdio:
                        modo = ModoTransporteEnum.HttpTodasLasInterfaces;
                        break;
                    case "--http" or "--http-unsecure":
                        error = "Usa solo uno de --http o --http-unsecure.";
                        return false;
                    case "--puerto" when i + 1 < args.Count:
                        puertoTexto = args[++i];
                        break;
                    default:
                        error = $"Argumento no reconocido: '{args[i]}'. Uso: native_ssh [--http | --http-unsecure] [--puerto N]";
                        return false;
                }
            }

            int puerto = PuertoPorDefecto;
            if (puertoTexto is not null && !(int.TryParse(puertoTexto, NumberStyles.None, CultureInfo.InvariantCulture, out puerto) && puerto is > 0 and <= 65535))
            {
                error = $"Puerto no válido: '{puertoTexto}'.";
                return false;
            }

            opciones = new OpcionesArranque(modo, puerto);
            return true;
        }
    }
}
