using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;
using NativeSsh.Benchmarks.Copias;
using NativeSsh.Ssh;

namespace NativeSsh.Benchmarks
{
    // known_hosts antes y después de leerlo por spans (C1-quater), en la misma tanda, con Gen1 (el de la lista rentada salía de
    // tener vivas a la vez las ~200 líneas como string). Mismo known_hosts de 200 líneas y la clave conocida al final, con
    // los nombres en claro o hasheados (IsHasheado: |1|sal|hash, como lo deja OpenSSH con HashKnownHosts yes).
    // - Antes_Iterador: el código de 7ffba5c (las líneas se leen a demanda con un iterador y mueren una a una).
    // - ListaRentada: el código de d64a784 (las líneas de cada fichero como string en una lista rentada; el antes inmediato).
    // - Producto: VerificadorHostConocido (el fichero en un búfer de caracteres rentado, líneas y campos como spans).
    // - SpansDelay: las mismas piezas que el producto con el recorrido en ValueLINQ Delay, los Where primero
    //   (VerificadorHostConocidoSpansDelay).
    public class GeneracionesKnownHostsBenchmarks
    {
        private const int Lineas = 200;
        private const string Servidor = "destino.ejemplo";
        private const int Puerto = 2222;
        private const string Tipo = "ssh-ed25519";

        private readonly string _directorio = Path.Combine(Path.GetTempPath(), $"ns_bench_gen_{Guid.NewGuid():N}");
        private VerificadorHostConocido _producto = null!;
        private VerificadorHostConocidoAntes _antes = null!;
        private VerificadorHostConocidoListaRentada _listaRentada = null!;
        private VerificadorHostConocidoSpansDelay _spansDelay = null!;
        private byte[] _clave = [];

        [Params(false, true)]
        public bool IsHasheado { get; set; }

        [GlobalSetup]
        public void Preparar()
        {
            _ = Directory.CreateDirectory(_directorio);
            string ruta = Path.Combine(_directorio, "known_hosts");
            StringBuilder texto = new();
            for (int i = 0; i < Lineas - 1; i++)
                _ = texto.Append(Hosts($"host{i}.ejemplo")).Append(" ssh-ed25519 ").Append(Convert.ToBase64String(Blob((byte)i))).Append('\n');
            _clave = Blob(255);
            _ = texto.Append(Hosts("[destino.ejemplo]:2222")).Append(" ssh-ed25519 ").Append(Convert.ToBase64String(_clave)).Append('\n');
            File.WriteAllText(ruta, texto.ToString());
            _producto = new VerificadorHostConocido([ruta]);
            _antes = new VerificadorHostConocidoAntes([ruta]);
            _listaRentada = new VerificadorHostConocidoListaRentada([ruta]);
            _spansDelay = new VerificadorHostConocidoSpansDelay([ruta]);
            bool isTodasConocen = Antes_Iterador() == ResultadoHostEnum.Conocido && ListaRentada() == ResultadoHostEnum.Conocido
                && Producto() == ResultadoHostEnum.Conocido && SpansDelay() == ResultadoHostEnum.Conocido
                && TiposClave_Antes_Iterador() == 1 && TiposClave_ListaRentada() == 1 && TiposClave_Producto() == 1 && TiposClave_SpansDelay() == 1;
            if (!isTodasConocen)
                throw new InvalidOperationException("El escenario no reconoce la clave conocida.");
        }

        [Benchmark]
        public ResultadoHostEnum Antes_Iterador() => _antes.Verificar(Servidor, Puerto, Tipo, _clave);

        [Benchmark(Baseline = true)]
        public ResultadoHostEnum ListaRentada() => _listaRentada.Verificar(Servidor, Puerto, Tipo, _clave);

        [Benchmark]
        public ResultadoHostEnum Producto() => _producto.Verificar(Servidor, Puerto, Tipo, _clave);

        [Benchmark]
        public ResultadoHostEnum SpansDelay() => _spansDelay.Verificar(Servidor, Puerto, Tipo, _clave);

        [Benchmark]
        public int TiposClave_Antes_Iterador() => _antes.ObtenerTiposClave(Servidor, Puerto).Count;

        [Benchmark]
        public int TiposClave_ListaRentada() => _listaRentada.ObtenerTiposClave(Servidor, Puerto).Count;

        [Benchmark]
        public int TiposClave_Producto() => _producto.ObtenerTiposClave(Servidor, Puerto).Count;

        [Benchmark]
        public int TiposClave_SpansDelay() => _spansDelay.ObtenerTiposClave(Servidor, Puerto).Count;

        [GlobalCleanup]
        public void Limpiar()
        {
            if (Directory.Exists(_directorio))
                Directory.Delete(_directorio, recursive: true);
        }

        // El campo de hosts de una entrada: el nombre, o su forma hasheada con una sal de 20 bytes.
        [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "Reproduce el formato hasheado de known_hosts.")]
        private string Hosts(string nombre)
        {
            if (!IsHasheado)
                return nombre;

            byte[] sal = RandomNumberGenerator.GetBytes(20);
            byte[] hash = HMACSHA1.HashData(sal, Encoding.UTF8.GetBytes(nombre));
            return $"|1|{Convert.ToBase64String(sal)}|{Convert.ToBase64String(hash)}";
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
