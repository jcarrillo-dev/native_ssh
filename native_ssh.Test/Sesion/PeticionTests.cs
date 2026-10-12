using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Sesion
{
    public class PeticionTests
    {
        private static Peticion Nueva(TimeProvider? reloj = null)
        {
            _ = DestinoSsh.TryParse("ana@servidor", "x", out DestinoSsh destino);
            return new Peticion(destino, "ls", TimeSpan.FromSeconds(10), 1024, reloj ?? TimeProvider.System);
        }

        [Fact]
        public void Nueva_EstaEncolada()
        {
            // Arrange
            Peticion peticion = Nueva();

            // Act & Assert
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Encolado);
            _ = peticion.CodigoSalida.Should().Be(-1);
            _ = peticion.IdTexto.Length.Should().Be(32);
            _ = peticion.TiempoSinSalida.Should().BeNull();
        }

        [Fact]
        public void TryMarcarEjecutando_SoloUnaVez()
        {
            // Arrange
            Peticion peticion = Nueva();

            // Act & Assert
            _ = peticion.TryMarcarEjecutando().Should().BeTrue();
            _ = peticion.TryMarcarEjecutando().Should().BeFalse();
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
        }

        [Fact]
        public async Task TryFinalizar_GanaLaPrimera()
        {
            // Arrange
            Peticion peticion = Nueva();
            _ = peticion.TryMarcarEjecutando();

            // Act
            _ = peticion.TryFinalizar(EstadoPeticionEnum.Completado, 3, "aviso").Should().BeTrue();
            _ = peticion.TryFinalizar(EstadoPeticionEnum.Error, -1, "tarde").Should().BeFalse();
            await peticion.Finalizacion.WaitAsync(TimeSpan.FromSeconds(1));

            // Assert
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Completado);
            _ = peticion.CodigoSalida.Should().Be(3);
            _ = peticion.Aviso.Should().Be("aviso");
            _ = peticion.IsFinalizada.Should().BeTrue();
        }

        [Fact]
        public void TryFinalizar_ConEstadoNoFinal_Lanza() => _ = FluentActions.Invoking(() => Nueva().TryFinalizar(EstadoPeticionEnum.Ejecutando, 0, null)).Should().Throw<ArgumentException>();

        [Fact]
        public void Cancelar_Encolada_FinalizaAlMomento()
        {
            // Arrange
            Peticion peticion = Nueva();

            // Act
            _ = peticion.SolicitarCancelacion().Should().BeTrue();

            // Assert
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = peticion.TryMarcarEjecutando().Should().BeFalse();
            _ = peticion.SolicitarCancelacion().Should().BeFalse();
        }

        [Fact]
        public void Cancelar_EnEjecucion_SoloDisparaElToken()
        {
            // Arrange
            Peticion peticion = Nueva();
            _ = peticion.TryMarcarEjecutando();

            // Act
            _ = peticion.SolicitarCancelacion().Should().BeTrue();

            // Assert: el token se cancela en el pool, no en el hilo de quien cancela.
            _ = peticion.IsCancelacionSolicitada.Should().BeTrue();
            _ = peticion.TokenInterrupcion.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)).Should().BeTrue();
            _ = peticion.Estado.Should().Be(EstadoPeticionEnum.Ejecutando);
        }

        [Fact]
        public async Task Cancelar_EnEjecucion_VuelveAunqueUnaContinuacionDelTokenNoTermine()
        {
            // Arrange: una continuación del token que no vuelve, como la del bucle que interrumpe el comando cuando espera
            // al candado de escritura de una entrada atascada en un envío que el servidor no acepta.
            Peticion peticion = Nueva();
            _ = peticion.TryMarcarEjecutando();
            using ManualResetEventSlim liberacion = new(false);
            using ManualResetEventSlim ejecutada = new(false);
            using CancellationTokenRegistration registro = peticion.TokenInterrupcion.Register(() =>
            {
                ejecutada.Set();
                _ = liberacion.Wait(TimeSpan.FromSeconds(10));
            });

            try
            {
                // Act
                Task<bool> solicitud = Task.Run(peticion.SolicitarCancelacion);
                bool isVuelta = await Task.WhenAny(solicitud, Task.Delay(TimeSpan.FromSeconds(2))) == solicitud;

                // Assert: vuelve aunque la continuación siga dentro (y la continuación sí llega a ejecutarse).
                _ = isVuelta.Should().BeTrue();
                _ = (await solicitud).Should().BeTrue();
                _ = ejecutada.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            }
            finally
            {
                liberacion.Set();
            }
        }

        [Fact]
        public void Tiempos_UsanElReloj()
        {
            // Arrange
            RelojFalso reloj = new();
            Peticion peticion = Nueva(reloj);
            reloj.Avanzar(TimeSpan.FromSeconds(2));
            _ = peticion.TryMarcarEjecutando();

            // Act
            reloj.Avanzar(TimeSpan.FromSeconds(3));
            _ = peticion.TiempoSinSalida.Should().Be(TimeSpan.FromSeconds(3));

            peticion.Salida.Escribir([1]);
            reloj.Avanzar(TimeSpan.FromSeconds(1));
            _ = peticion.TiempoSinSalida.Should().Be(TimeSpan.FromSeconds(1));

            // Assert
            _ = peticion.TryFinalizar(EstadoPeticionEnum.Completado, 0, null);
            reloj.Avanzar(TimeSpan.FromSeconds(10));
            _ = peticion.TiempoEjecucion.Should().Be(TimeSpan.FromSeconds(4));
            _ = peticion.TiempoSinSalida.Should().BeNull();
        }

        // Antes de enlazar la petición siguiente, la cadena de avisos de lo tecleado descartado se queda solo con las que
        // aún tienen el aviso sin dar, en orden: la cabeza ya dada (p. ej. por el error de una herramienta, que da el suyo sin
        // recorrer los anteriores) no se enlaza, pero el aviso pendiente de detrás tampoco se pierde.
        [Fact]
        public void CompactarAvisosTecleadoPendientes_QuitaLosDadosYConservaLosPendientes()
        {
            // Arrange
            Peticion sinDar = Nueva();
            sinDar.FijarLineasTecleadoDescartadas(2, "a");
            Peticion dada = Nueva();
            dada.FijarLineasTecleadoDescartadas(1, "b");
            dada.FijarAnteriorConTecleadoDescartado(sinDar);
            Peticion otraSinDar = Nueva();
            otraSinDar.FijarLineasTecleadoDescartadas(3, "c");
            otraSinDar.FijarAnteriorConTecleadoDescartado(dada);
            Peticion cabezaDada = Nueva();
            cabezaDada.FijarLineasTecleadoDescartadas(1, "d");
            cabezaDada.FijarAnteriorConTecleadoDescartado(otraSinDar);
            _ = dada.TryTomarAvisoTecleado();
            _ = cabezaDada.TryTomarAvisoTecleado();

            // Act
            Peticion? cabeza = Peticion.CompactarAvisosTecleadoPendientes(cabezaDada);

            // Assert
            _ = cabeza.Should().BeSameAs(otraSinDar);
            _ = otraSinDar.AnteriorConTecleadoDescartado.Should().BeSameAs(sinDar);
            _ = sinDar.AnteriorConTecleadoDescartado.Should().BeNull();
            _ = Peticion.CompactarAvisosTecleadoPendientes(Nueva()).Should().BeNull("sin líneas descartadas no hay aviso que dar");
        }

        // Con más avisos pendientes que el tope, se quedan los MaxAvisosTecleadoEnlazados más recientes.
        [Fact]
        public void CompactarAvisosTecleadoPendientes_SeQuedaEnElTope()
        {
            // Arrange
            Peticion? ultima = null;
            for (int k = 0; k < 3 * Peticion.MaxAvisosTecleadoEnlazados; k++)
            {
                Peticion peticion = Nueva();
                peticion.FijarLineasTecleadoDescartadas(1, null);
                peticion.FijarAnteriorConTecleadoDescartado(ultima);
                ultima = peticion;
            }

            // Act
            Peticion? cabeza = Peticion.CompactarAvisosTecleadoPendientes(ultima);

            // Assert
            int largo = 0;
            for (Peticion? p = cabeza; p is not null; p = p.AnteriorConTecleadoDescartado)
                largo++;
            _ = cabeza.Should().BeSameAs(ultima);
            _ = largo.Should().Be(Peticion.MaxAvisosTecleadoEnlazados);
        }

        // Al terminar, la salida se cierra: lo retenido por si era el principio de un secreto (ssh_input con secreto=true) ya
        // no se completará, se enmascara y se puede leer (BufferSalida.Cerrar); sin cerrarla quedaría oculto para siempre.
        [Theory]
        [InlineData(EstadoPeticionEnum.Completado)]
        [InlineData(EstadoPeticionEnum.Cancelado)]
        public void AlTerminar_CierraLaSalidaYEnmascaraLoRetenidoDeUnSecreto(EstadoPeticionEnum estado)
        {
            // Arrange
            Peticion peticion = Nueva();
            _ = peticion.TryMarcarEjecutando();
            peticion.Salida.RegistrarSecreto("Clave-9xZ"u8);
            peticion.Salida.Escribir("fin Clave"u8);
            long antes = peticion.Salida.Longitud;

            // Act
            _ = peticion.TryFinalizar(estado, estado == EstadoPeticionEnum.Cancelado ? 130 : 0, null);

            // Assert
            byte[] destino = new byte[64];
            int copiados = peticion.Salida.Leer(0, destino).Copiados;
            _ = antes.Should().Be(4);
            _ = peticion.Salida.Longitud.Should().Be(9);
            _ = System.Text.Encoding.UTF8.GetString(destino, 0, copiados).Should().Be("fin *****");
        }
    }
}
