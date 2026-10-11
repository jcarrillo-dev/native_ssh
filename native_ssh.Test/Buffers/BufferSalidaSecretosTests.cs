using System.Linq;
using System.Reflection;
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

        // Escribe 'flujo' en trozos, lee lo nuevo tras cada escritura (como ssh_output con desdeByte = siguienteByte) y cierra:
        // lo leído una vez ya no cambia, así que la concatenación es todo lo que llegó a ver el modelo.
        private static string EscribirYLeerPorTrozos(BufferSalida buffer, string flujo, IEnumerable<int> trozos)
        {
            byte[] datos = Utf8(flujo);
            StringBuilder visto = new();
            long desde = 0;
            int posicion = 0;
            foreach (int trozo in trozos)
            {
                int n = Math.Min(trozo, datos.Length - posicion);
                buffer.Escribir(datos.AsSpan(posicion, n));
                posicion += n;
                desde = LeerNuevo(buffer, desde, visto);
            }

            buffer.Cerrar();
            _ = LeerNuevo(buffer, desde, visto);
            return visto.ToString();
        }

        private static long LeerNuevo(BufferSalida buffer, long desde, StringBuilder visto)
        {
            byte[] destino = new byte[1 << 16];
            LecturaSalida lectura = buffer.Leer(desde, destino);
            _ = visto.Append(Encoding.Latin1.GetString(destino, 0, lectura.Copiados));
            return lectura.Desde + lectura.Copiados;
        }

        // Oráculo: toda aparición (también solapada) de cada secreto en el flujo completo, enmascarada; y la cola final que es
        // principio de algún secreto (se enmascara al cerrar).
        private static string Oraculo(string flujo, string[] secretos)
        {
            char[] resultado = flujo.ToCharArray();
            foreach (string secreto in secretos)
                for (int i = flujo.IndexOf(secreto, StringComparison.Ordinal); i >= 0; i = flujo.IndexOf(secreto, i + 1, StringComparison.Ordinal))
                    Array.Fill(resultado, '*', i, secreto.Length);
            int cola = 0;
            foreach (string secreto in secretos)
                for (int k = Math.Min(secreto.Length - 1, flujo.Length); k > cola; k--)
                    if (flujo.EndsWith(secreto[..k], StringComparison.Ordinal))
                    {
                        cola = k;
                        break;
                    }

            Array.Fill(resultado, '*', flujo.Length - cola, cola);
            return new string(resultado);
        }

        // Apariciones solapadas repartidas entre dos escrituras (CB2a): la búsqueda se hace sobre los bytes originales de la
        // escritura anterior, no sobre los ya enmascarados, y lo retenido se decide también sobre los originales.
        [Theory]
        [InlineData("ababab.", new[] { 4, 3 }, new[] { "abab" }, "******.")]
        [InlineData("abcXYZ.", new[] { 3, 4 }, new[] { "abc", "abcXYZ" }, "******.")]
        [InlineData("abcd.", new[] { 3, 2 }, new[] { "bc", "abcd" }, "****.")]
        [InlineData("ababa.", new[] { 4, 2 }, new[] { "aba" }, "*****.")]
        public void SolapadosEntreDosEscrituras_SeEnmascaranEnteros(string flujo, int[] trozos, string[] secretos, string esperado)
        {
            // Arrange
            BufferSalida buffer = ConSecreto(1 << 20, secretos);

            // Act
            string visto = EscribirYLeerPorTrozos(buffer, flujo, trozos);

            // Assert
            _ = visto.Should().Be(esperado).And.Be(Oraculo(flujo, secretos));
        }

        // Aleatoria contra el oráculo (alfabetos de 2 o 3 letras para forzar solapamientos; trozos de 1 a 11 bytes): lo que se
        // lee tras cada escritura más lo que queda al cerrar es exactamente el flujo con el oráculo aplicado (ni de menos, que
        // sería una fuga, ni de más).
        [Fact]
        public void Aleatoria_LoLeidoEsIgualAlOraculo()
        {
            Random azar = new(20261011);
            for (int vuelta = 0; vuelta < 20000; vuelta++)
            {
                // Arrange
                string alfabeto = azar.Next(2) == 0 ? "ab" : "abc";
                string[] secretos = new string[azar.Next(1, 4)];
                for (int i = 0; i < secretos.Length; i++)
                    secretos[i] = string.Create(azar.Next(1, 8), azar, (letras, a) =>
                    {
                        for (int j = 0; j < letras.Length; j++)
                            letras[j] = alfabeto[a.Next(alfabeto.Length)];
                    });
                StringBuilder flujo = new();
                int longitud = azar.Next(1, 200);
                while (flujo.Length < longitud)
                    _ = azar.Next(4) == 0 ? flujo.Append(secretos[azar.Next(secretos.Length)]) : flujo.Append(alfabeto[azar.Next(alfabeto.Length)]);
                List<int> trozos = [];
                for (int total = 0; total < flujo.Length; total += trozos[^1])
                    trozos.Add(azar.Next(1, 12));
                BufferSalida buffer = ConSecreto(1 << 20, secretos);

                // Act
                string visto = EscribirYLeerPorTrozos(buffer, flujo.ToString(), trozos);

                // Assert
                _ = visto.Should().Be(Oraculo(flujo.ToString(), secretos), $"secretos [{string.Join(",", secretos)}], trozos [{string.Join(",", trozos)}]");
            }
        }

        // Un secreto registrado a mitad de la salida no oculta lo que ya se pudo leer: Longitud nunca retrocede.
        [Fact]
        public void SecretoRegistradoTarde_LongitudNoRetrocede()
        {
            // Arrange
            BufferSalida buffer = new(1 << 20, TimeProvider.System);
            buffer.Escribir(Utf8("xyab"));
            long antes = buffer.Longitud;

            // Act
            buffer.RegistrarSecreto(Utf8("abcd"));
            buffer.Escribir(Utf8("c"));

            // Assert
            _ = antes.Should().Be(4);
            _ = buffer.Longitud.Should().BeGreaterThanOrEqualTo(antes);
        }

        // Un secreto más largo registrado cuando la aparición de otro más corto, que es su principio, ya se enmascaró: el resto
        // que llega después se enmascara (la búsqueda usa los bytes tal como llegaron, no los ya tapados).
        [Fact]
        public void SecretoLargoRegistradoTrasUnoCortoYaTapado_SuRestoSeEnmascara()
        {
            // Arrange
            BufferSalida buffer = ConSecreto(1 << 20, "ab");
            buffer.Escribir(Utf8("xab"));

            // Act
            buffer.RegistrarSecreto(Utf8("abcd"));
            buffer.Escribir(Utf8("cd."));
            buffer.Cerrar();

            // Assert
            _ = LeerTodo(buffer).Should().Be("x****.");
        }

        // Lo mismo con un secreto más largo que la copia mínima de bytes originales (BufferSalida.MinBytesColaOriginal): al
        // ampliarla, los bytes que ya guardaba (originales) sustituyen a los del búfer, que pueden estar enmascarados.
        [Fact]
        public void SecretoMasLargoQueLaCopiaMinima_RegistradoTrasUnoCortoYaTapado_SuRestoSeEnmascara()
        {
            // Arrange
            BufferSalida buffer = ConSecreto(1 << 20, "ab");
            buffer.Escribir(Utf8("xab"));
            string resto = new('c', BufferSalida.MinBytesColaOriginal + 100);

            // Act
            buffer.RegistrarSecreto(Utf8("ab" + resto));
            buffer.Escribir(Utf8(resto + "."));
            buffer.Cerrar();

            // Assert
            _ = LeerTodo(buffer).Should().Be("x" + new string('*', resto.Length + 2) + ".");
        }

        // Oráculo con registros tardíos: cada secreto se enmascara solo desde la posición en que se registró (lo anterior ya
        // se pudo leer); de una aparición empezada antes del registro, lo que llega después. La cola final que es principio
        // de un secreto se enmascara al cerrar, desde su registro.
        private static byte[] OraculoTardio(byte[] flujo, byte[][] secretos, long[] registros)
        {
            byte[] r = (byte[])flujo.Clone();
            for (int s = 0; s < secretos.Length; s++)
                for (int i = 0; i + secretos[s].Length <= flujo.Length; i++)
                    if (flujo.AsSpan(i, secretos[s].Length).SequenceEqual(secretos[s]))
                        for (long k = Math.Max(i, registros[s]); k < i + secretos[s].Length; k++)
                            r[k] = (byte)'*';

            int cola = 0;
            int colaSecreto = -1;
            for (int s = 0; s < secretos.Length; s++)
                for (int k = Math.Min(secretos[s].Length - 1, flujo.Length); k > cola; k--)
                    if (flujo.AsSpan(flujo.Length - k).SequenceEqual(secretos[s].AsSpan(0, k)))
                    {
                        cola = k;
                        colaSecreto = s;
                        break;
                    }

            if (colaSecreto >= 0)
                for (long k = Math.Max(flujo.Length - cola, registros[colaSecreto]); k < flujo.Length; k++)
                    r[k] = (byte)'*';
            return r;
        }

        // Aleatoria con los secretos registrados a mitad de la salida (como ssh_input con secreto=true durante el comando),
        // contra el oráculo tardío: ni un byte de un secreto llegado tras su registro sin enmascarar (tampoco de una aparición
        // empezada antes o tapada en parte por otro secreto) y Longitud nunca retrocede.
        [Fact]
        public void Aleatoria_SecretosRegistradosAMitad_NadaDeMenosQueElOraculoTardio()
        {
            Random azar = new(1110);
            int deMenos = 0;
            int retrocesos = 0;
            for (int vuelta = 0; vuelta < 20_000; vuelta++)
            {
                // Arrange
                string alfabeto = azar.Next(2) == 0 ? "ab" : "abc";
                byte[][] secretos = new byte[azar.Next(1, 4)][];
                for (int i = 0; i < secretos.Length; i++)
                    secretos[i] = Utf8(string.Create(azar.Next(1, 8), azar, (letras, a) =>
                    {
                        for (int j = 0; j < letras.Length; j++)
                            letras[j] = alfabeto[a.Next(alfabeto.Length)];
                    }));
                StringBuilder texto = new();
                int longitud = azar.Next(1, 200);
                while (texto.Length < longitud)
                    _ = azar.Next(4) == 0 ? texto.Append(Encoding.UTF8.GetString(secretos[azar.Next(secretos.Length)])) : texto.Append(alfabeto[azar.Next(alfabeto.Length)]);
                byte[] flujo = Utf8(texto.ToString());
                List<int> trozos = [];
                for (int total = 0; total < flujo.Length; total += trozos[^1])
                    trozos.Add(azar.Next(1, 12));
                List<int> fronteras = [0];
                foreach (int t in trozos)
                    fronteras.Add(fronteras[^1] + t);
                long[] registros = new long[secretos.Length];
                Dictionary<int, byte[]> registrosEn = [];
                if (fronteras.Count - 1 < secretos.Length)
                    continue;
                for (int s = 0; s < secretos.Length; s++)
                {
                    int p = fronteras[azar.Next(fronteras.Count - 1)];
                    while (registrosEn.ContainsKey(p))
                        p = fronteras[azar.Next(fronteras.Count - 1)];
                    registrosEn[p] = secretos[s];
                    registros[s] = p;
                }

                BufferSalida buffer = new(1 << 20, TimeProvider.System);

                // Act: escribe por trozos, registra cada secreto en su frontera y lee lo nuevo tras cada escritura.
                List<(long Desde, byte[] Bytes)> lecturas = [];
                long desde = 0;
                long longitudAnterior = 0;
                int posicion = 0;
                byte[] destino = new byte[1 << 16];
                void Leer()
                {
                    LecturaSalida l = buffer.Leer(desde, destino);
                    lecturas.Add((l.Desde, destino.AsSpan(0, l.Copiados).ToArray()));
                    desde = l.Desde + l.Copiados;
                    retrocesos += buffer.Longitud < longitudAnterior ? 1 : 0;
                    longitudAnterior = buffer.Longitud;
                }

                foreach (int trozo in trozos)
                {
                    if (registrosEn.TryGetValue(posicion, out byte[]? secreto))
                        buffer.RegistrarSecreto(secreto);
                    int n = Math.Min(trozo, flujo.Length - posicion);
                    buffer.Escribir(flujo.AsSpan(posicion, n));
                    posicion += n;
                    Leer();
                }

                buffer.Cerrar();
                Leer();

                // Assert (se acumula y se comprueba al final)
                byte[] oraculo = OraculoTardio(flujo, secretos, registros);
                foreach ((long d, byte[] bytes) in lecturas)
                    for (int i = 0; i < bytes.Length; i++)
                        deMenos += oraculo[d + i] == (byte)'*' && bytes[i] != (byte)'*' ? 1 : 0;
            }

            _ = (deMenos, retrocesos).Should().Be((0, 0));
        }

        // Al liberar, la copia de los últimos bytes tal como llegaron (que puede contener un secreto) se pone a cero.
        [Fact]
        public void Liberar_BorraLaCopiaDeLosBytesOriginales()
        {
            // Arrange
            BufferSalida buffer = ConSecreto();
            buffer.Escribir(Utf8($"antes {Secreto[..^2]}"));
            FieldInfo campo = typeof(BufferSalida).GetField("_colaOriginal", BindingFlags.NonPublic | BindingFlags.Instance)!;
            byte[] cola = (byte[])campo.GetValue(buffer)!;
            bool isConDatos = cola.Any(b => b != 0);

            // Act
            buffer.Liberar();

            // Assert
            _ = isConDatos.Should().BeTrue();
            _ = cola.Should().OnlyContain(b => b == 0);
            _ = campo.GetValue(buffer).Should().BeNull();
        }

        // Los segmentos que el tope de la salida descarta vuelven al pool puestos a cero si hay secretos (como al liberar):
        // otro alquiler del pool no ve lo que contenían.
        [Fact]
        public void SegmentoDescartadoPorElTope_VuelveAlPoolACero()
        {
            // Arrange
            BufferSalida buffer = ConSecreto(capacidad: 64);
            buffer.Escribir(Utf8($"{Secreto} y más"));
            FieldInfo campo = typeof(BufferSalida).GetField("_segmentos", BindingFlags.NonPublic | BindingFlags.Instance)!;
            byte[] primero = ((List<byte[]>)campo.GetValue(buffer)!)[0];

            // Act
            buffer.Escribir(new byte[BufferSalida.TamanoSegmento * 2]);

            // Assert
            _ = ((List<byte[]>)campo.GetValue(buffer)!).Should().NotContain(primero, "el tope lo descartó");
            _ = primero.Should().OnlyContain(b => b == 0);
        }

        // Con un secreto más largo que el tope, lo retenido por si es su principio puede empezar antes de lo que conserva el
        // búfer: Leer no lanza y no devuelve nada retenido.
        [Fact]
        public void SecretoMasLargoQueElTope_LeerNoLanza()
        {
            // Arrange
            BufferSalida buffer = new(64, TimeProvider.System);
            byte[] secreto = Utf8(new string('s', 100) + "fin");
            buffer.RegistrarSecreto(secreto);
            buffer.Escribir(Utf8(new string('x', (BufferSalida.TamanoSegmento * 2) - 10)));
            buffer.Escribir(secreto.AsSpan(0, 90));
            byte[] destino = new byte[1 << 16];

            // Act
            LecturaSalida lectura = buffer.Leer(0, destino);

            // Assert
            _ = destino.AsSpan(0, lectura.Copiados).IndexOf((byte)'s').Should().Be(-1);
            _ = lectura.HasDatosDescartados.Should().BeTrue();
        }
    }
}
