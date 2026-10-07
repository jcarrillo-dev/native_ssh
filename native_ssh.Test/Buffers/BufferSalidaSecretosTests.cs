using System.Text;
using FluentAssertions;
using NativeSsh.Buffers;

namespace NativeSsh.Test.Buffers
{
    // Secretos (ssh_input con secreto=true): sus apariciones literales en la salida se sustituyen por '*' byte a byte antes
    // de que ningún lector las vea (misma longitud: las posiciones de desdeByte/siguienteByte no cambian), y lo que al final
    // de lo escrito podría ser el principio de un secreto no se deja leer hasta saberlo (al cerrar la salida, se enmascara).
    public class BufferSalidaSecretosTests
    {
        private const string Secreto = "Clave-9xZ";

        private static byte[] Utf8(string texto) => Encoding.UTF8.GetBytes(texto);

        private static string LeerTodo(BufferSalida buffer)
        {
            byte[] destino = new byte[buffer.Longitud + 16];
            LecturaSalida lectura = buffer.Leer(0, destino);
            return Encoding.UTF8.GetString(destino, 0, lectura.Copiados);
        }

        private static BufferSalida ConSecreto(int capacidad = 1 << 20, params string[] secretos)
        {
            BufferSalida buffer = new(capacidad, TimeProvider.System);
            foreach (string secreto in secretos.Length == 0 ? [Secreto] : secretos)
                buffer.RegistrarSecreto(Utf8(secreto));
            return buffer;
        }

        [Fact]
        public void SecretoEnUnaEscritura_SeSustituyePorAsteriscosConLaMismaLongitud()
        {
            // Arrange
            BufferSalida buffer = ConSecreto();

            // Act
            buffer.Escribir(Utf8($"antes {Secreto} medio {Secreto}\n"));

            // Assert
            string esperado = $"antes {new string('*', Secreto.Length)} medio {new string('*', Secreto.Length)}\n";
            _ = LeerTodo(buffer).Should().Be(esperado);
            _ = buffer.Longitud.Should().Be(Utf8(esperado).Length);
        }

        // Eco carácter a carácter (como lo devuelve un terminal): ninguna lectura intermedia devuelve un solo byte del secreto.
        [Fact]
        public void EcoByteAByte_NingunaLecturaIntermediaVeParteDelSecreto()
        {
            // Arrange
            BufferSalida buffer = ConSecreto();
            byte[] datos = Utf8($"Password: {Secreto}\r\nok\n");
            List<string> intermedias = [];

            // Act
            foreach (byte b in datos)
            {
                buffer.Escribir([b]);
                intermedias.Add(LeerTodo(buffer));
            }

            // Assert
            foreach (string intermedia in intermedias)
                _ = intermedia.Should().NotContain("Cl").And.NotContain("9xZ");
            _ = intermedias.Should().Contain("Password: ", "lo que no puede ser el secreto se ve al momento");
            _ = LeerTodo(buffer).Should().Be($"Password: {new string('*', Secreto.Length)}\r\nok\n");
        }

        [Fact]
        public void SecretoACaballoDeDosSegmentos_SeEnmascaraEntero()
        {
            // Arrange
            BufferSalida buffer = ConSecreto();
            byte[] relleno = new byte[BufferSalida.TamanoSegmento - 4];
            Array.Fill(relleno, (byte)'a');

            // Act
            buffer.Escribir(relleno);
            buffer.Escribir(Utf8(Secreto[..6]));
            string intermedia = LeerTodo(buffer);
            buffer.Escribir(Utf8(Secreto[6..] + "\n"));

            // Assert
            _ = intermedia.Should().Be(Encoding.UTF8.GetString(relleno));
            _ = LeerTodo(buffer).Should().Be(Encoding.UTF8.GetString(relleno) + new string('*', Secreto.Length) + "\n");
        }

        // Retención: Longitud y Fin no cuentan lo retenido (ssh_output no dice que haya más mientras tanto) y, si no resulta
        // ser el secreto, se libera con la escritura siguiente.
        [Fact]
        public void PrefijoQueNoEsElSecreto_SeRetieneYSeLiberaConLaEscrituraSiguiente()
        {
            // Arrange
            BufferSalida buffer = ConSecreto();

            // Act
            buffer.Escribir(Utf8("respuesta: Clave"));
            long longitudRetenida = buffer.Longitud;
            LecturaSalida lectura = buffer.Leer(0, new byte[64]);
            buffer.Escribir(Utf8(" normal\n"));

            // Assert
            _ = longitudRetenida.Should().Be("respuesta: ".Length);
            _ = lectura.Fin.Should().Be("respuesta: ".Length);
            _ = LeerTodo(buffer).Should().Be("respuesta: Clave normal\n");
        }

        // Al cerrar (la petición terminó) lo retenido ya no se completará: se enmascara (lado seguro: si el final de la salida
        // coincide por casualidad con el principio del secreto, sale con '*'). Lo que se escriba después se enmascara al
        // momento, también un posible principio del secreto al final, sin retenerlo.
        [Fact]
        public void Cerrar_EnmascaraLaColaRetenidaYLasEscriturasPosterioresNoRetienen()
        {
            // Arrange
            BufferSalida buffer = ConSecreto();
            buffer.Escribir(Utf8("final Clave-"));

            // Act
            buffer.Cerrar();
            string trasCerrar = LeerTodo(buffer);
            buffer.Escribir(Utf8($"tarde {Secreto[..3]}"));

            // Assert
            _ = trasCerrar.Should().Be("final ******");
            _ = LeerTodo(buffer).Should().Be("final ******tarde ***");
        }

        [Fact]
        public void VariosSecretosYOcurrenciasSolapadas_SeEnmascaranTodas()
        {
            // Arrange
            BufferSalida buffer = ConSecreto(1 << 20, "abab", "zz");

            // Act
            buffer.Escribir(Utf8("xababab zzz y\n"));

            // Assert
            _ = LeerTodo(buffer).Should().Be("x****** *** y\n");
        }

        // Con el tope de la salida, lo descartado no impide enmascarar lo que queda.
        [Fact]
        public void ConElTopeDeLaSalida_ElSecretoEnLoQueQuedaSeEnmascara()
        {
            // Arrange
            BufferSalida buffer = ConSecreto(capacidad: 1024);
            byte[] relleno = new byte[3 * BufferSalida.TamanoSegmento];
            Array.Fill(relleno, (byte)'b');

            // Act
            buffer.Escribir(relleno);
            buffer.Escribir(Utf8($"{Secreto}\n"));
            byte[] destino = new byte[4 * BufferSalida.TamanoSegmento];
            LecturaSalida lectura = buffer.Leer(0, destino);
            string texto = Encoding.UTF8.GetString(destino, 0, lectura.Copiados);

            // Assert
            _ = lectura.HasDatosDescartados.Should().BeTrue();
            _ = texto.Should().EndWith(new string('*', Secreto.Length) + "\n").And.NotContain(Secreto);
        }

        // Sin secretos registrados, el búfer se comporta como siempre: nada se retiene ni se enmascara.
        [Fact]
        public void SinSecretos_NoRetieneNiEnmascara()
        {
            // Arrange
            BufferSalida buffer = new(1 << 20, TimeProvider.System);

            // Act
            buffer.Escribir(Utf8($"{Secreto[..4]}"));

            // Assert
            _ = buffer.Longitud.Should().Be(4);
            _ = LeerTodo(buffer).Should().Be(Secreto[..4]);
        }
    }
}
