using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // guardarSalida (O-a de la auditoría de la línea 1-ter): el resumen (recuento, «ya no estaba», estado y código) no depende
    // de variables del envoltorio que el comando pueda reasignar, quitar o hacer de solo lectura; y si algo del shell impide
    // preparar el archivo sin que el envoltorio diga por qué, la respuesta no es Completado.
    public sealed partial class GuardarSalidaTests
    {
        // Las variables del envoltorio que el comando encuentra (con el nombre de antes o buscándolas con compgen) reasignadas a
        // otro archivo o quitadas: el recuento sigue siendo el del archivo del comando.
        [Theory]
        [InlineData("__ns_gf=/etc/passwd")]
        [InlineData("unset __ns_gf")]
        [InlineData("for v in $(compgen -v __ns_g); do eval \"$v=/etc/passwd\"; done")]
        [InlineData("for v in $(compgen -v __ns_g); do unset \"$v\"; done")]
        public async Task VariablesDelEnvoltorioCambiadas_ElRecuentoEsElDelArchivo(string cambio)
        {
            // Arrange
            HerramientasSsh h = Herramientas();

            // Act
            ResultadoComando r = await GuardarAsync(h, $"echo real; {cambio}");

            // Assert
            salida.WriteLine($"{r.Estado} {r.CodigoSalida} {r.Archivo} {r.Lineas} {r.Bytes} '{r.Salida}' '{r.Aviso}'");
            _ = (r.Estado, r.CodigoSalida, r.Lineas, r.Bytes).Should().Be((EstadoPeticionEnum.Completado, 0, 1L, 5L));
            _ = r.Aviso.Should().NotContain("ya no estaba");
        }

        // Variables del envoltorio de solo lectura (con el nombre de antes o todas las que el comando encuentra con compgen): el
        // comando y el siguiente guardarSalida se ejecutan con su código, su archivo y su recuento, sin errores del envoltorio en
        // el terminal.
        [Theory]
        [InlineData("readonly __ns_gf")]
        [InlineData("readonly __ns_gc")]
        [InlineData("readonly __ns_gr=0")]
        [InlineData("for v in $(compgen -v __ns_g); do readonly \"$v\"; done")]
        public async Task VariablesDelEnvoltorioDeSoloLectura_ElSiguienteSeEjecuta(string cambio)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string testigo = Path.Combine(_raiz, "ejecutado");

            // Act
            ResultadoComando primero = await GuardarAsync(h, $"echo real; {cambio}; (exit 3)");
            ResultadoComando segundo = await GuardarAsync(h, $"touch '{testigo}'; echo otro; (exit 4)");

            // Assert
            salida.WriteLine($"1.º {primero.Estado} {primero.CodigoSalida} {primero.Lineas} '{primero.Salida}' '{primero.Aviso}'");
            salida.WriteLine($"2.º {segundo.Estado} {segundo.CodigoSalida} {segundo.Lineas} '{segundo.Salida}' '{segundo.Aviso}'");
            _ = (primero.Estado, primero.CodigoSalida, primero.Lineas, primero.Bytes).Should().Be((EstadoPeticionEnum.Completado, 3, 1L, 5L));
            _ = (segundo.Estado, segundo.CodigoSalida, segundo.Lineas, segundo.Bytes).Should().Be((EstadoPeticionEnum.Completado, 4, 1L, 5L));
            _ = File.Exists(testigo).Should().BeTrue();
            _ = (await File.ReadAllTextAsync(segundo.Archivo!)).Should().Be("otro\n");
            _ = (primero.Salida + segundo.Salida).Should().NotContain("lectura").And.NotContain("readonly");
        }

        // Una variable de solo lectura que rompe la preparación del archivo en la subshell (hereda las del shell): el comando no
        // se ejecuta y la respuesta es un Error que lo dice, no un Completado.
        [Fact]
        public async Task PreparacionRotaSinMotivo_ErrorNoCompletado()
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            string testigo = Path.Combine(_raiz, "ejecutado");
            _ = await h.EjecutarAsync(Destino, "readonly d=x", esperarSegundos: 30).ComoComando();

            // Act
            ResultadoComando r = await GuardarAsync(h, $"touch '{testigo}'; echo otro");

            // Assert
            salida.WriteLine($"{r.Estado} {r.CodigoSalida} {r.Archivo} '{r.Salida}' '{r.Aviso}'");
            _ = File.Exists(testigo).Should().BeFalse();
            _ = (r.Estado, r.Archivo).Should().Be((EstadoPeticionEnum.Error, null));
            _ = r.Aviso.Should().Contain("NO se ejecutó").And.Contain("ssh_reset_shell");
        }

        // Ninguna variable que el comando pueda ver (las del shell con 'set', y las del entorno de un hijo con 'set -a') lleva el
        // nonce de las marcas, tampoco si un comando anterior redefinió 'unset': una F falsa impresa en /dev/tty con cada
        // candidato a nonce que encuentre (cada tramo de 32 hexadecimales) no cambia el recuento.
        [Theory]
        [InlineData("")]
        [InlineData("set -a")]
        [InlineData("unset() { :; }")]
        public async Task NonceDeLasMarcas_EnNingunaVariable_UnaFFalsaNoCambiaElRecuento(string antes)
        {
            // Arrange
            HerramientasSsh h = Herramientas();
            if (antes.Length > 0)
                _ = await h.EjecutarAsync(Destino, antes, esperarSegundos: 30).ComoComando();

            // Act
            ResultadoComando r = await GuardarAsync(h, "for t in $( { set; env; } 2>/dev/null | grep -o '[0-9a-f]\\{32,\\}' | sort -u); do i=0; while [ $((i + 32)) -le ${#t} ]; do printf '__NSG_%s_F_999_12345_0__\\n' \"${t:i:32}\" >/dev/tty; i=$((i + 1)); done; done; seq 1 3");

            // Assert
            salida.WriteLine($"{r.Estado} {r.CodigoSalida} {r.Lineas} {r.Bytes} '{r.Salida}' '{r.Aviso}'");
            _ = (r.Estado, r.CodigoSalida, r.Lineas, r.Bytes).Should().Be((EstadoPeticionEnum.Completado, 0, 3L, 6L));
        }
    }
}
