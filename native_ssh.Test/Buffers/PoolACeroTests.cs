using System.Buffers;
using System.Reflection;
using System.Text;
using FluentAssertions;
using NativeSsh.Buffers;
using NativeSsh.Protocolo;
using NativeSsh.Ssh;

namespace NativeSsh.Test.Buffers
{
    // Los buffers que llevan un comando, la salida remota o el contenido de un archivo vuelven al ArrayPool compartido puestos
    // a cero: otro alquiler (de cualquier código del proceso) no ve lo que contenían. Donde el array es local al código
    // probado, la prueba lo siembra: alquila en su hilo un array del mismo tamaño, lo rellena y lo devuelve sin limpiar; el
    // pool compartido guarda lo último devuelto en la caché del hilo y lo vuelve a dar en el siguiente alquiler del mismo
    // tamaño en ese hilo, así que el código probado recibe ese array, y el alquiler de después del Act tiene que ser el
    // mismo (si no, la prueba no demostraría nada) y tener a cero el tramo que el código pudo escribir (Rent puede dar un
    // array mayor: lo de después no se toca y conserva el relleno de la siembra).
    public sealed class PoolACeroTests
    {
        private const byte Relleno = 0x5A;

        private static byte[] Sembrar(int longitud)
        {
            byte[] sembrado = ArrayPool<byte>.Shared.Rent(longitud);
            sembrado.AsSpan().Fill(Relleno);
            ArrayPool<byte>.Shared.Return(sembrado);
            return sembrado;
        }

        private static byte[] Realquilar(int longitud)
        {
            byte[] siguiente = ArrayPool<byte>.Shared.Rent(longitud);
            ArrayPool<byte>.Shared.Return(siguiente);
            return siguiente;
        }

        // ConstructorComando (el comando y lo tecleado) y PaginadorSalida (una página de la salida) usan BufferAlquilado.
        [Fact]
        public void BufferAlquilado_AlLiberar_VuelveAlPoolACero()
        {
            // Arrange
            byte[] sembrado = Sembrar(100);
            BufferAlquilado buffer = new(100);
            "echo PIN=4711"u8.CopyTo(buffer.Span);

            // Act
            buffer.Dispose();
            byte[] siguiente = Realquilar(100);

            // Assert
            _ = siguiente.Should().BeSameAs(sembrado);
            _ = siguiente[..100].Should().OnlyContain(b => b == 0);
        }

        // La ventana que junta lo retenido (el principio de una marca) con lo que llega después lleva salida del comando.
        [Fact]
        public void FiltroMarcasGuardado_VentanaDeLoRetenido_VuelveAlPoolACero()
        {
            // Arrange
            FiltroMarcasGuardado filtro = new(Guid.Parse("0123456789abcdef0123456789abcdef"), new BufferSalida(1 << 20, TimeProvider.System));
            filtro.Escribir("texto __NSG_0123"u8);
            byte[] resto = Encoding.ASCII.GetBytes(new string('s', 80));
            byte[] sembrado = Sembrar(128);

            // Act
            filtro.Escribir(resto);
            byte[] siguiente = Realquilar(128);

            // Assert: lo retenido ("__NSG_0123") más lo nuevo, a cero; detrás, solo el relleno de la siembra.
            _ = siguiente.Should().BeSameAs(sembrado);
            _ = siguiente[..("__NSG_0123".Length + resto.Length)].Should().OnlyContain(b => b == 0);
            _ = siguiente.Should().OnlyContain(b => b == 0 || b == Relleno);
        }

        // El bloque con el que el canal exec lee stdout y stderr del comando (CanalExecSshNet.Leer, privado: por reflexión).
        [Fact]
        public void CanalExec_BloqueDeLectura_VuelveAlPoolACero()
        {
            // Arrange
            MethodInfo leer = typeof(CanalExecSshNet).GetMethod("Leer", BindingFlags.NonPublic | BindingFlags.Static)!;
            Type tipoControl = typeof(CanalExecSshNet).GetNestedType("ControlLectura", BindingFlags.NonPublic)!;
            object control = Activator.CreateInstance(tipoControl, 1 << 20)!;
            using MemoryStream flujo = new(Encoding.ASCII.GetBytes("contenido del archivo: PIN=4711\n"));
            byte[] sembrado = Sembrar(16 * 1024);

            // Act
            using MemoryStream leido = (MemoryStream)leer.Invoke(null, [flujo, control])!;
            byte[] siguiente = Realquilar(16 * 1024);

            // Assert
            _ = leido.Length.Should().Be(flujo.Length);
            _ = siguiente.Should().BeSameAs(sembrado);
            _ = siguiente.Should().OnlyContain(b => b == 0);
        }

        // Sin secretos registrados, los segmentos de la salida también vuelven a cero: llevan la salida remota.
        [Fact]
        public void BufferSalida_SinSecretos_SegmentoDescartadoPorElTope_VuelveAlPoolACero()
        {
            // Arrange
            BufferSalida buffer = new(64, TimeProvider.System);
            buffer.Escribir("contenido del archivo"u8);
            FieldInfo campo = typeof(BufferSalida).GetField("_segmentos", BindingFlags.NonPublic | BindingFlags.Instance)!;
            byte[] primero = ((List<byte[]>)campo.GetValue(buffer)!)[0];

            // Act
            buffer.Escribir(new byte[BufferSalida.TamanoSegmento * 2]);

            // Assert
            _ = ((List<byte[]>)campo.GetValue(buffer)!).Should().NotContain(primero, "el tope lo descartó");
            _ = primero.Should().OnlyContain(b => b == 0);
        }

        [Fact]
        public void BufferSalida_SinSecretos_Liberar_VuelveAlPoolACero()
        {
            // Arrange
            BufferSalida buffer = new(1 << 20, TimeProvider.System);
            buffer.Escribir("contenido del archivo"u8);
            FieldInfo campo = typeof(BufferSalida).GetField("_segmentos", BindingFlags.NonPublic | BindingFlags.Instance)!;
            byte[] primero = ((List<byte[]>)campo.GetValue(buffer)!)[0];

            // Act
            buffer.Liberar();

            // Assert
            _ = primero.Should().OnlyContain(b => b == 0);
        }
    }
}
