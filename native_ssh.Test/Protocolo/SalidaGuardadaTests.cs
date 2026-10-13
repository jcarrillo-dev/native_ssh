using System.Text;
using FluentAssertions;
using NativeSsh.Buffers;
using NativeSsh.Protocolo;

namespace NativeSsh.Test.Protocolo
{
    // Las marcas que deja en el terminal un comando con guardarSalida (FiltroMarcasGuardado, SalidaGuardada), la ruta que lee
    // la vista previa y su texto.
    public sealed class SalidaGuardadaTests
    {
        private static readonly Guid Nonce = Guid.Parse("0123456789abcdef0123456789abcdef");

        private static string B64(string texto) => Convert.ToBase64String(Encoding.UTF8.GetBytes(texto));

        private static string Marca(string cuerpo, Guid? nonce = null) => $"__NSG_{nonce ?? Nonce:N}_{cuerpo}__\n";

        private static FiltroMarcasGuardado Filtro(out BufferSalida salida)
        {
            salida = new BufferSalida(1024 * 1024, TimeProvider.System);
            return new FiltroMarcasGuardado(Nonce, salida);
        }

        private static string Texto(BufferSalida salida)
        {
            byte[] datos = new byte[(int)salida.Longitud];
            _ = salida.Leer(0, datos);
            return Encoding.UTF8.GetString(datos);
        }

        // El flujo de un comando: texto, la marca A, más texto (sudo), una marca de otra petición (es texto) y la marca F.
        private static readonly Guid OtroNonce = Guid.Parse("fedcba9876543210fedcba9876543210");
        private static readonly string Ajena = Marca($"F_9_99_0", OtroNonce);
        private static readonly string Flujo = "antes" + Marca($"A_{B64("/tmp/b/s/i.out")}") + "[sudo] contraseña: " + Ajena + "medio __NS sin marca __NSG_ tampoco\n" + Marca("F_3_12_1") + "despues";
        private static readonly string FlujoSinMarcas = "antes[sudo] contraseña: " + Ajena + "medio __NS sin marca __NSG_ tampoco\ndespues";

        private static void ComprobarInforme(InformeGuardado informe)
        {
            _ = informe.Archivo.Should().Be("/tmp/b/s/i.out");
            _ = (informe.Lineas, informe.Bytes, informe.IsSinSaltoFinal, informe.IsTerminado).Should().Be((3, 12, true, true));
            _ = informe.Error.Should().BeNull();
        }

        // Las marcas de la petición no llegan al búfer y se aplican al informe; las de otra petición y lo que solo se parece
        // pasan tal cual. Igual de una vez, partido en dos trozos por cualquier byte y byte a byte.
        [Fact]
        public void Filtro_QuitaSusMarcasEnteras_AunquePartanEnCualquierByte()
        {
            byte[] flujo = Encoding.UTF8.GetBytes(Flujo);
            for (int corte = 0; corte <= flujo.Length; corte++)
            {
                // Arrange
                FiltroMarcasGuardado filtro = Filtro(out BufferSalida salida);

                // Act
                filtro.Escribir(flujo.AsSpan(0, corte));
                filtro.Escribir(flujo.AsSpan(corte));
                filtro.Vaciar();

                // Assert
                _ = Texto(salida).Should().Be(FlujoSinMarcas, $"corte en el byte {corte}");
                ComprobarInforme(filtro.Informe);
            }

            FiltroMarcasGuardado unoAUno = Filtro(out BufferSalida salidaUnoAUno);
            foreach (byte b in flujo)
                unoAUno.Escribir([b]);
            unoAUno.Vaciar();
            _ = Texto(salidaUnoAUno).Should().Be(FlujoSinMarcas);
            ComprobarInforme(unoAUno.Informe);
        }

        // En ningún momento (tras cada trozo) el búfer tiene un trozo de una marca de la petición: lo que puede serlo se
        // retiene hasta saber qué es.
        [Fact]
        public void Filtro_NingunEstadoIntermedioEnsenaUnTrozoDeMarca()
        {
            // Arrange
            FiltroMarcasGuardado filtro = Filtro(out BufferSalida salida);
            byte[] flujo = Encoding.UTF8.GetBytes(Marca($"A_{B64("/tmp/b/s/i.out")}") + Marca("F_3_12_0"));

            // Act y Assert
            foreach (byte b in flujo)
            {
                filtro.Escribir([b]);
                _ = Texto(salida).Should().BeEmpty();
            }
        }

        // Con '\r\n' (terminal con onlcr) también se quita el salto entero.
        [Fact]
        public void Filtro_SaltoCrLf_SeQuitaEntero()
        {
            // Arrange
            FiltroMarcasGuardado filtro = Filtro(out BufferSalida salida);
            string marca = Marca("F_1_2_0").Replace("\n", "\r\n", StringComparison.Ordinal);

            // Act
            filtro.Escribir(Encoding.UTF8.GetBytes(Marca($"A_{B64("/x")}") + "a" + marca[..^1]));
            filtro.Escribir(Encoding.UTF8.GetBytes(marca[^1..] + "b"));

            // Assert
            _ = Texto(salida).Should().Be("ab");
            _ = filtro.Informe.IsTerminado.Should().BeTrue();
        }

        // Sin recuento (-1: el archivo ya no estaba): líneas y bytes null, terminado.
        [Fact]
        public void Filtro_SinRecuento_LineasYBytesNull()
        {
            // Arrange
            FiltroMarcasGuardado filtro = Filtro(out _);

            // Act
            filtro.Escribir(Encoding.UTF8.GetBytes(Marca($"A_{B64("/x")}") + Marca("F_-1_-1_0")));

            // Assert
            _ = (filtro.Informe.Lineas, filtro.Informe.Bytes, filtro.Informe.IsTerminado).Should().Be((null, null, true));
        }

        // Error de la preparación (también con un campo vacío): el mensaje dice que el comando no se ejecutó, con la ruta y, en
        // el cupo, cuántos había.
        [Fact]
        public void Filtro_Error_MensajeConLaRutaYElCupo()
        {
            // Arrange
            FiltroMarcasGuardado cupo = Filtro(out _);
            FiltroMarcasGuardado ajeno = Filtro(out _);
            FiltroMarcasGuardado usuario = Filtro(out BufferSalida salidaUsuario);

            // Act
            cupo.Escribir(Encoding.UTF8.GetBytes(Marca($"E_archivos_{B64("/tmp/native_ssh-ana/abc")}_100_2048")));
            ajeno.Escribir(Encoding.UTF8.GetBytes(Marca($"E_base_{B64("/tmp/native_ssh-ana")}_0_0")));
            usuario.Escribir(Encoding.UTF8.GetBytes(Marca("E_usuario__0_0") + "x"));

            // Assert
            _ = cupo.Informe.Error.Should().StartWith("El comando NO se ejecutó").And.Contain("100 archivo(s)").And.Contain("2048 bytes").And.Contain("/tmp/native_ssh-ana/abc").And.Contain("ssh_reset_shell");
            _ = ajeno.Informe.Error.Should().Contain("'/tmp/native_ssh-ana' ya existe y no es un directorio propio con permisos 700");
            _ = usuario.Informe.Error.Should().Contain("'id -un'");
            _ = Texto(salidaUsuario).Should().Be("x");
            _ = cupo.Informe.IsTerminado.Should().BeFalse();
        }

        // Al terminar el comando, lo retenido: una marca de la petición cortada (con el nonce entero) se descarta; un principio
        // que no llega al nonce es texto del comando.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Filtro_Vaciar_MarcaCortadaSeDescartaYTextoSeSuelta(bool isConNonce)
        {
            // Arrange
            FiltroMarcasGuardado filtro = Filtro(out BufferSalida salida);
            string resto = isConNonce ? $"__NSG_{Nonce:N}_F_3" : "__NSG_0123";

            // Act
            filtro.Escribir(Encoding.UTF8.GetBytes("texto" + resto));
            string antes = Texto(salida);
            filtro.Vaciar();

            // Assert
            _ = antes.Should().Be("texto");
            _ = Texto(salida).Should().Be(isConNonce ? "texto" : "texto" + resto);
            _ = filtro.Informe.IsTerminado.Should().BeFalse();
        }

        // Una marca que no cuadra (otro número de campos, un carácter que no va, más larga que el tope) es texto.
        [Fact]
        public void Filtro_MarcaQueNoCuadra_EsTexto()
        {
            // Arrange
            FiltroMarcasGuardado filtro = Filtro(out BufferSalida salida);
            string dosCampos = Marca("F_3_12");
            string conEspacio = Marca("A_a b");
            string larga = Marca("A_" + new string('A', FiltroMarcasGuardado.MaxLongitudMarca + 10));

            // Act
            filtro.Escribir(Encoding.UTF8.GetBytes(dosCampos + conEspacio + larga));
            filtro.Vaciar();

            // Assert
            _ = Texto(salida).Should().Be(dosCampos + conEspacio + larga);
            _ = filtro.Informe.Archivo.Should().BeNull();
        }

        // La marca A solo avisa una vez de que el archivo se creó (la sesión lo cuenta).
        [Fact]
        public void Filtro_AlCrearArchivo_UnaVez()
        {
            // Arrange
            FiltroMarcasGuardado filtro = Filtro(out _);
            int veces = 0;
            filtro.AlCrearArchivo = () => veces++;

            // Act
            filtro.Escribir(Encoding.UTF8.GetBytes(Marca($"A_{B64("/x")}") + Marca($"A_{B64("/x")}") + Marca("F_1_1_0")));

            // Assert
            _ = veces.Should().Be(1);
        }

        // Una marca con el nonce de la petición que no es la que toca (una segunda A, una E tras la A, una F antes de la A o una
        // segunda F) la pudo imprimir el propio comando: se quita de la salida igual, pero no cambia el archivo, el recuento ni
        // el estado.
        [Fact]
        public void Filtro_MarcaFueraDeOrden_SeQuitaSinCambiarElInforme()
        {
            // Arrange
            FiltroMarcasGuardado filtro = Filtro(out BufferSalida salida);
            int veces = 0;
            filtro.AlCrearArchivo = () => veces++;
            string flujo = Marca("F_7_77_0") + "a" + Marca($"A_{B64("/tmp/b/s/i.out")}") + "b" + Marca($"A_{B64("/etc/passwd")}")
                + Marca($"E_archivo_{B64("/p")}_0_0") + "c" + Marca("F_3_12_1") + Marca("F_9_99_0") + Marca($"A_{B64("/etc/shadow")}") + "d";

            // Act
            filtro.Escribir(Encoding.UTF8.GetBytes(flujo));
            filtro.Vaciar();

            // Assert
            _ = Texto(salida).Should().Be("abcd");
            ComprobarInforme(filtro.Informe);
            _ = veces.Should().Be(1);
        }

        // Una E antes de la A (la preparación falló) sí se aplica.
        [Fact]
        public void Filtro_ErrorAntesDeLaA_SeAplica()
        {
            // Arrange
            FiltroMarcasGuardado filtro = Filtro(out _);

            // Act
            filtro.Escribir(Encoding.UTF8.GetBytes(Marca($"E_archivo_{B64("/p")}_0_0")));

            // Assert
            _ = filtro.Informe.Error.Should().Contain("'/p'");
        }

        [Theory]
        [InlineData("/tmp/native_ssh-ana/S/I.out", true)]
        [InlineData("/tmp/native_ssh-a.b/S/I.out", true)]
        [InlineData("/tmp/native_ssh-ana/otro/S/I.out", false)]
        [InlineData("/tmp/native_ssh-/S/I.out", false)]
        [InlineData("/tmp/native_ssh/S/I.out", false)]
        [InlineData("/etc/native_ssh-ana/S/I.out", false)]
        [InlineData("/tmp/native_ssh-ana/S/otro.out", false)]
        public void IsRutaEsperada_SoloLaDeEsteShellYEstaPeticion(string ruta, bool esperado)
        {
            // Arrange
            Guid shell = Guid.Parse("11111111111111111111111111111111");
            string real = ruta.Replace("/S/", $"/{shell:N}/", StringComparison.Ordinal).Replace("/I.out", "/idpeticion.out", StringComparison.Ordinal);

            // Act y Assert
            _ = SalidaGuardada.IsRutaEsperada(real, "/tmp/native_ssh", shell, "idpeticion").Should().Be(esperado);
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
        // nonce partido (printf '%s_%s'): el texto no contiene ninguna marca entera. Nada del contenido del archivo sale por el
        // terminal: la marca final solo lleva números (no hay base64 ni head del archivo en el resumen), las subshells empiezan
        // sin traza y los permisos se fijan con chmod tras crear.
        [Fact]
        public void Envolver_ComandoComoLiteralSinMarcasEnterasNiContenidoDelArchivo()
        {
            // Act
            string texto = SalidaGuardada.Envolver("echo 'a' \"b\"", Nonce, Guid.NewGuid(), "id", "/tmp/native_ssh");

            // Assert
            _ = texto.Should().StartWith($"unset __ns_k __ns_b\n__ns_gc{Nonce:N}='echo '\\''a'\\'' \"b\"'\n");
            _ = texto.Should().NotContain($"__NSG_{Nonce:N}");
            string resumen = texto[texto.IndexOf("_F_", StringComparison.Ordinal)..];
            _ = resumen.Should().NotContain("base64").And.NotContain("head").And.Contain("_F_%d_%d_%d__").And.NotContain("$__ns_g");
            _ = texto.Should().Contain("{ set +x +v; } 2>/dev/null").And.Contain("chmod 700 -- \"$b\"").And.Contain("chmod 700 -- \"$d\"").And.Contain("chmod 600 -- \"$f\"");
        }

        // Lo que cuenta ScriptLimpieza: antes de borrar, lo que queda y los shells cuyo directorio sigue; null si la última línea
        // no cuadra.
        [Theory]
        [InlineData("x\n__ns_archivos=3_0_\n", 3, 0, 0)]
        [InlineData("rm: no\n__ns_archivos=4_1_0123456789abcdef0123456789abcdef,fedcba9876543210fedcba9876543210\n", 4, 1, 2)]
        [InlineData("\n__ns_archivos=2_2_0123456789abcdef0123456789abcdef", 2, 2, 1)]
        public void LeerRecuentoLimpieza_AntesQuedanYShells(string salida, int contados, int quedan, int shells)
        {
            // Act
            RecuentoLimpieza? recuento = SalidaGuardada.LeerRecuentoLimpieza(Encoding.ASCII.GetBytes(salida));

            // Assert
            _ = recuento.Should().NotBeNull();
            _ = (recuento!.Contados, recuento.Quedan, recuento.ShellsQueSiguen.Length).Should().Be((contados, quedan, shells));
            if (shells > 0)
                _ = recuento.ShellsQueSiguen[0].Should().Be(Nonce);
        }

        [Theory]
        [InlineData("")]
        [InlineData("\n__ns_archivos=3\n")]
        [InlineData("\n__ns_archivos=3_x_\n")]
        [InlineData("\n__ns_archivos=3_0_noesunguid\n")]
        public void LeerRecuentoLimpieza_SinLineaQueCuadre_Null(string salida)
        {
            // Act
            RecuentoLimpieza? recuento = SalidaGuardada.LeerRecuentoLimpieza(Encoding.ASCII.GetBytes(salida));

            // Assert
            _ = recuento.Should().BeNull();
        }
    }
}
