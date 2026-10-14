using System.Buffers.Binary;
using System.Text;
using BenchmarkDotNet.Attributes;
using NativeSsh.Ssh;

namespace NativeSsh.Benchmarks
{
    // Comprobación de la clave del servidor contra un known_hosts de 200 líneas (una vez por conexión): la clave conocida
    // está al final, así que se recorren todas. Mide el paso de las líneas leídas con un iterador a una lista rentada.
    public class KnownHostsBenchmarks
    {
        private const int Lineas = 200;

        private readonly string _directorio = Path.Combine(Path.GetTempPath(), $"ns_bench_kh_{Guid.NewGuid():N}");
        private VerificadorHostConocido _verificador = null!;
        private byte[] _clave = [];

        [GlobalSetup]
        public void Preparar()
        {
            _ = Directory.CreateDirectory(_directorio);
            string ruta = Path.Combine(_directorio, "known_hosts");
            StringBuilder texto = new();
            for (int i = 0; i < Lineas - 1; i++)
                _ = texto.Append("host").Append(i).Append(".ejemplo ssh-ed25519 ").Append(Convert.ToBase64String(Blob((byte)i))).Append('\n');
            _clave = Blob(255);
            _ = texto.Append("[destino.ejemplo]:2222 ssh-ed25519 ").Append(Convert.ToBase64String(_clave)).Append('\n');
            File.WriteAllText(ruta, texto.ToString());
            _verificador = new VerificadorHostConocido([ruta]);
            if (_verificador.Verificar("destino.ejemplo", 2222, "ssh-ed25519", _clave) != ResultadoHostEnum.Conocido)
                throw new InvalidOperationException("El escenario no reconoce la clave conocida.");
        }

        [Benchmark]
        public ResultadoHostEnum Verificar_200Lineas() => _verificador.Verificar("destino.ejemplo", 2222, "ssh-ed25519", _clave);

        [Benchmark]
        public int TiposClave_200Lineas() => _verificador.ObtenerTiposClave("destino.ejemplo", 2222).Count;

        [GlobalCleanup]
        public void Limpiar()
        {
            if (Directory.Exists(_directorio))
                Directory.Delete(_directorio, recursive: true);
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
