using System.Text;
using FluentAssertions;
using NativeSsh.Protocolo;

namespace NativeSsh.Test.Protocolo
{
    // Lectura de las marcas que deja en el terminal un comando con guardarSalida (SalidaGuardada) y vista previa.
    public sealed class SalidaGuardadaTests
    {
        private static readonly Guid Nonce = Guid.Parse("0123456789abcdef0123456789abcdef");

        private static string B64(string texto) => Convert.ToBase64String(Encoding.UTF8.GetBytes(texto));

        private static string Marca(string cuerpo, Guid? nonce = null) => $"__NSG_{nonce ?? Nonce:N}_{cuerpo}__\n";

        [Fact]
        public void Leer_TomaLasMarcasDeSuNonceEIgnoraLasDeOtro()
        {
            // Arrange
            Guid otro = Guid.NewGuid();
            string ajena = Marca($"F_9_99_0_{B64("/tmp/falso")}_{B64("falso\n")}", otro);
            string terminal = Marca($"A_{B64("/tmp/b/s/i.out")}") + ajena + "[sudo] contraseña: " + Marca($"F_3_12_1_{B64("/tmp/b/s/i.out")}_{B64("uno\ndos\ntres\n")}");

            // Act
            InformeGuardado informe = SalidaGuardada.Leer(terminal, Nonce);

            // Assert
            _ = informe.Archivo.Should().Be("/tmp/b/s/i.out");
            _ = (informe.Lineas, informe.Bytes, informe.IsSinSaltoFinal).Should().Be((3, 12, true));
            _ = informe.Error.Should().BeNull();
            _ = informe.IsTerminado.Should().BeTrue();
            _ = SalidaGuardada.QuitarMarcas(terminal, Nonce).Should().Be(ajena + "[sudo] contraseña: ");
        }

        // Sin contar (-1: el comando borró el archivo): líneas y bytes null; vista previa vacía.
        [Fact]
        public void Leer_SinRecuento_LineasYBytesNull()
        {
            // Act
            InformeGuardado informe = SalidaGuardada.Leer(Marca($"F_-1_-1_0_{B64("/tmp/x.out")}_"), Nonce);

            // Assert
            _ = informe.Lineas.Should().BeNull();
            _ = informe.Bytes.Should().BeNull();
            _ = informe.Archivo.Should().Be("/tmp/x.out");
            _ = informe.VistaPrevia.Should().BeEmpty();
        }

        // Error de la preparación: el mensaje dice que el comando no se ejecutó, con la ruta y, en el cupo, cuántos había.
        [Fact]
        public void Leer_Error_MensajeConLaRutaYElCupo()
        {
            // Act
            InformeGuardado cupo = SalidaGuardada.Leer(Marca($"E_archivos_{B64("/tmp/native_ssh-ana/abc")}_100_2048"), Nonce);
            InformeGuardado ajeno = SalidaGuardada.Leer(Marca($"E_base_{B64("/tmp/native_ssh-ana")}_0_0"), Nonce);

            // Assert
            _ = cupo.Error.Should().StartWith("El comando NO se ejecutó").And.Contain("100 archivo(s)").And.Contain("2048 bytes").And.Contain("/tmp/native_ssh-ana/abc").And.Contain("ssh_reset_shell");
            _ = ajeno.Error.Should().Contain("'/tmp/native_ssh-ana' ya existe y no es un directorio propio con permisos 700");
            _ = cupo.IsTerminado.Should().BeFalse();
        }

        // Una marca cortada (el principio del búfer descartado, o una a medias mientras llega) no se toma ni se quita.
        [Fact]
        public void Leer_MarcaIncompleta_NoSeToma()
        {
            // Arrange
            string terminal = $"__NSG_{Nonce:N}_F_3_12_0_{B64("/tmp/x")}";

            // Act y Assert
            _ = SalidaGuardada.Leer(terminal, Nonce).IsTerminado.Should().BeFalse();
            _ = SalidaGuardada.QuitarMarcas(terminal, Nonce).Should().Be(terminal);
        }

        // Vista previa: como mucho 5 líneas, sin ANSI ni el CR de los CRLF; una línea de más de 512 bytes, cortada con ' […]';
        // sin el salto que añade cut si el archivo entero cabe y no terminaba en salto.
        [Theory]
        [InlineData("uno\r\n\u001b[31mdos\u001b[0m\ntres\n", 3, false, "uno\ndos\ntres\n")]
        [InlineData("a\nb\n", 2, true, "a\nb")]
        [InlineData("1\n2\n3\n4\n5\n", 9, true, "1\n2\n3\n4\n5\n")]
        public void TextoVistaPrevia_LimpiaYRespetaElFinal(string vista, long lineas, bool isSinSalto, string esperado)
        {
            // Arrange
            InformeGuardado informe = new("/x", lineas, 1, Encoding.UTF8.GetBytes(vista), null, isSinSalto);

            // Act y Assert
            _ = SalidaGuardada.TextoVistaPrevia(informe).Should().Be(esperado);
        }

        [Fact]
        public void TextoVistaPrevia_LineaLarga_SeCortaSinPartirUnCaracter()
        {
            // Arrange: 'a' y 'é' (2 bytes): el byte 512 es el primero de una 'é'; cut deja 513.
            byte[] linea = Encoding.UTF8.GetBytes("a" + new string('é', 256));
            InformeGuardado informe = new("/x", 2, 1000, [.. linea, .. "\nsigue\n"u8], null);

            // Act
            string texto = SalidaGuardada.TextoVistaPrevia(informe);

            // Assert
            _ = texto.Should().Be("a" + new string('é', 255) + " […]\nsigue\n");
        }

        // El envoltorio lleva el comando como literal entre comillas simples (las del comando, escapadas) y las marcas con el
        // nonce partido (printf '%s_%s'): el texto no contiene ninguna marca entera.
        [Fact]
        public void Envolver_ComandoComoLiteralYSinMarcasEnteras()
        {
            // Act
            string texto = SalidaGuardada.Envolver("echo 'a' \"b\"", Nonce, Guid.NewGuid(), "id", "/tmp/native_ssh");

            // Assert
            _ = texto.Should().StartWith("__ns_gc='echo '\\''a'\\'' \"b\"'\n");
            _ = texto.Should().NotContain($"__NSG_{Nonce:N}");
        }
    }
}
