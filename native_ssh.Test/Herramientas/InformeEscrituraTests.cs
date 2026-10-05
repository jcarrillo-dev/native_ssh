using System.Linq;
using System.Text;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Herramientas
{
    public sealed class InformeEscrituraTests
    {
        private const string Id = "0123456789abcdef0123456789abcdef";
        private const string Marca = "__NS_W_" + Id + "__ ";
        private const string Temporal = "/srv/app/.f.txt.tmp.Ab12Cd";
        private const string Copia = "/srv/app/.f.txt.orig.Ef34Gh";

        private static string Base64(string texto) => Convert.ToBase64String(Encoding.UTF8.GetBytes(texto));

        private static string Anuncios(params string[] lineas) => string.Concat(lineas.Select(linea => Marca + linea + "\n"));

        private static readonly string RutasCreadas = Anuncios($"tmp {Base64(Temporal)}", $"copia {Base64(Copia)}");

        [Fact]
        public void Leer_SeparaLosAnunciosDelTexto()
        {
            // Arrange: rutas con un salto de línea y una comilla, ruido de otro proceso delante de un anuncio y un anuncio de
            // otra petición, que es texto.
            string temporal = "/srv/a'b\nc/.x.tmp.Ab12Cd";
            string salida = $"aviso de sudo\nruido{Marca}tmp {Base64(temporal)}\n{Anuncios($"copia {Base64(Copia)}", "fase escribiendo", "fase escrito", "limpio")}__NS_W_ffffffffffffffffffffffffffffffff__ fase fallido\nError: algo\n";

            // Act
            InformeEscritura informe = InformeEscritura.Leer(salida, Id);

            // Assert
            _ = informe.Should().BeEquivalentTo(new InformeEscritura("escrito", [], temporal, Copia, null, LimpiezaEscrituraEnum.Limpio, "aviso de sudo\nruido\n__NS_W_ffffffffffffffffffffffffffffffff__ fase fallido\nError: algo"));
        }

        [Theory]
        [InlineData("colocado", true)]
        [InlineData("escrito", false)]
        [InlineData("creado", false)]
        [InlineData(null, false)]
        public void IsAtomica_SoloSiElContenidoSeColocoConUnRenombradoOUnEnlace(string? fase, bool isAtomica)
        {
            // Act
            InformeEscritura informe = InformeEscritura.Leer(fase is null ? string.Empty : Anuncios($"fase {fase}"), Id);

            // Assert
            _ = informe.IsAtomica.Should().Be(isAtomica);
        }

        [Fact]
        public void AvisoExito_EnElSitio_DiceQueNoEsAtomicaYSiLaCopiaSeBorro()
        {
            // Act
            string limpio = InformeEscritura.Leer(RutasCreadas + Anuncios("fase escribiendo", "fase escrito", "limpio"), Id).AvisoExito();
            string conRestos = InformeEscritura.Leer(RutasCreadas + Anuncios("fase escribiendo", "fase escrito", "restos"), Id).AvisoExito();
            string sinConfirmar = InformeEscritura.Leer(string.Empty, Id).AvisoExito();

            // Assert
            _ = limpio.Should().Contain("Escritura no atómica").And.Contain("ya borrada").And.NotContain(Copia);
            _ = conRestos.Should().Contain("Escritura no atómica").And.Contain($"en '{Copia}', que no se pudo borrar");
            _ = sinConfirmar.Should().Contain("No se recibió del servidor la confirmación");
            _ = InformeEscritura.Leer(Anuncios("fase colocando", "fase colocado", "limpio"), Id).AvisoExito().Should().BeEmpty();
        }

        public static TheoryData<string, string> Interrupciones => new()
        {
            { "", "No se llegó a modificar '/srv/app/f.txt'." },
            { $"tmp {Base64(Temporal)}\nlimpio", "No se llegó a modificar '/srv/app/f.txt'." },
            { $"tmp {Base64(Temporal)}", $"No se llegó a modificar '/srv/app/f.txt'. Si la limpieza no llegó a ejecutarse, pueden quedar en el servidor el temporal '{Temporal}'." },
            { $"tmp {Base64(Temporal)}\nfase colocando", $"Se interrumpió durante el renombrado atómico del contenido nuevo: '/srv/app/f.txt' tiene su contenido anterior (o no existe, si era nuevo) o el nuevo completo, nunca una mezcla. Si la limpieza no llegó a ejecutarse, pueden quedar en el servidor el temporal '{Temporal}'." },
            { "fase colocando\nfase colocado\nlimpio", "'/srv/app/f.txt' ya tiene el contenido nuevo completo (renombrado atómico)." },
            { "fase colocando\nfase creando", "'/srv/app/f.txt', que no existía, puede haber quedado creado con solo una parte del contenido nuevo." },
            { $"tmp {Base64(Temporal)}\ncopia {Base64(Copia)}\nfase escribiendo", $"'/srv/app/f.txt' puede haber quedado a medio escribir: la copia de seguridad del original está en '{Copia}' y el contenido nuevo en '{Temporal}' (no se han borrado; para recuperar el original, copia la primera sobre el archivo)." },
            { $"tmp {Base64(Temporal)}\ncopia {Base64(Copia)}\nfase escribiendo\nfase restaurando", $"'/srv/app/f.txt' puede haber quedado a medio escribir: la copia de seguridad del original está en '{Copia}' y el contenido nuevo en '{Temporal}' (no se han borrado; para recuperar el original, copia la primera sobre el archivo)." },
            { $"tmp {Base64(Temporal)}\ncopia {Base64(Copia)}\nfase escribiendo\nfase restaurando\nfase fallido\nconservados", $"'/srv/app/f.txt' quedó a medio escribir y no se pudo restaurar: la copia de seguridad del original está en '{Copia}' y el contenido nuevo en '{Temporal}' (no se han borrado; para recuperar el original, copia la primera sobre el archivo)." },
            { $"tmp {Base64(Temporal)}\ncopia {Base64(Copia)}\nfase escribiendo\nfase restaurando\nfase restaurado\nlimpio", "Se restauró el contenido original de '/srv/app/f.txt'." },
            { $"tmp {Base64(Temporal)}\ncopia {Base64(Copia)}\nfase escribiendo\nfase escrito\nlimpio", "'/srv/app/f.txt' ya tiene el contenido nuevo completo (escrito en el sitio)." },
            { "fase anexando", "Al final de '/srv/app/f.txt' puede haberse anexado solo una parte del contenido." },
        };

        // Solo se afirma lo que el script llegó a anunciar: la última fase y, si no los borró, dónde quedan los temporales.
        [Theory]
        [MemberData(nameof(Interrupciones))]
        public void MensajeInterrupcion_DiceQuePasoConElArchivoSegunLaUltimaFaseAnunciada(string anuncios, string esperado)
        {
            // Arrange
            string salida = anuncios.Length == 0 ? string.Empty : Anuncios(anuncios.Split('\n'));

            // Act
            string mensaje = InformeEscritura.Leer(salida, Id).MensajeInterrupcion("/srv/app/f.txt", EstadoPeticionEnum.TiempoAgotado, Id, null);

            // Assert
            _ = mensaje.Should().Be($"La escritura en '/srv/app/f.txt' no terminó (estado: TiempoAgotado; petición {Id}, su salida completa con ssh_output). {esperado}");
        }

        [Fact]
        public void MensajeInterrupcion_AnadeElAvisoDeLaPeticionYLaSalidaSiNoSeAnuncioNada()
        {
            // Act
            string sinAnuncios = InformeEscritura.Leer("[sudo] password for ana: \n", Id).MensajeInterrupcion("f.txt", EstadoPeticionEnum.Cancelado, Id, "Cancelado: se envió Ctrl+C al proceso.");
            string conAnuncios = InformeEscritura.Leer("Error: x\n" + Anuncios("fase colocando", "fase colocado"), Id).MensajeInterrupcion("f.txt", EstadoPeticionEnum.Cancelado, Id, null);

            // Assert
            _ = sinAnuncios.Should().EndWith("No se llegó a modificar 'f.txt'. Cancelado: se envió Ctrl+C al proceso. Salida del script: [sudo] password for ana:");
            _ = conAnuncios.Should().EndWith("'f.txt' ya tiene el contenido nuevo completo (renombrado atómico).");
        }

        // Lo que el núcleo quita al escribir en el sitio y no se pudo devolver se dice explícitamente, con el valor original
        // de las capacidades para poder devolverlas; si se devolvió todo, se dice que se comprobó.
        [Fact]
        public void Perdidos_SeDicenExplicitamenteEnElExitoEnElErrorYEnLaInterrupcion()
        {
            // Arrange
            string anuncios = RutasCreadas + Anuncios($"capacidad {Base64("setfattr -n security.capability -v 0x0100000200")}", "fase escribiendo");

            // Act
            string exito = InformeEscritura.Leer(anuncios + Anuncios("fase escrito setuid,setgid,capacidades", "limpio"), Id).AvisoExito();
            string sinComprobar = InformeEscritura.Leer(anuncios + Anuncios("fase escrito capacidades?", "limpio"), Id).AvisoExito();
            string todoDevuelto = InformeEscritura.Leer(anuncios + Anuncios("fase escrito -", "limpio"), Id).AvisoExito();
            string error = InformeEscritura.Leer(anuncios + Anuncios("fase restaurando", "fase restaurado setuid") + "Error: falló.\n", Id).MensajeError("x");
            string interrupcion = InformeEscritura.Leer(anuncios + Anuncios("fase restaurando", "fase restaurado capacidades", "limpio"), Id).MensajeInterrupcion("f", EstadoPeticionEnum.TiempoAgotado, Id, null);

            // Assert
            _ = exito.Should().Contain("salvo lo que sigue. ATENCIÓN: el núcleo quitó al escribir el bit setuid y no se pudo devolver (chmod u+s, como propietario o con sudo=true). el núcleo quitó al escribir el bit setgid")
                .And.EndWith("hace falta sudo=true y setfattr o setcap. Para devolverlas, con privilegios: «setfattr -n security.capability -v 0x0100000200 <archivo>».");
            _ = sinComprobar.Should().EndWith("ATENCIÓN: no se pudo comprobar si el archivo tenía capacidades (security.capability): en el servidor no hay getfattr ni getcap; si las tenía, el núcleo las borró al escribir.");
            _ = todoDevuelto.Should().EndWith("El modo con sus bits setuid y setgid y las capacidades (security.capability), que el núcleo quita al escribir en un archivo, se devolvieron y se comprobaron.").And.NotContain("ATENCIÓN");
            _ = error.Should().Be("Error: falló. ATENCIÓN: el núcleo quitó al escribir el bit setuid y no se pudo devolver (chmod u+s, como propietario o con sudo=true).");
            _ = interrupcion.Should().EndWith("Se restauró el contenido original de 'f'. ATENCIÓN: el núcleo borró al escribir las capacidades del archivo (security.capability) y no se pudieron devolver: hace falta sudo=true y setfattr o setcap. Para devolverlas, con privilegios: «setfattr -n security.capability -v 0x0100000200 <archivo>».");
        }

        [Fact]
        public void MensajeError_EsElTextoDelScriptSinLosAnuncios()
        {
            // Act
            InformeEscritura informe = InformeEscritura.Leer(RutasCreadas + Anuncios("fase escribiendo", "fase restaurando", "fase restaurado") + "Error: falló la escritura en el sitio de f.txt; se ha restaurado su contenido original.\n" + Anuncios("limpio"), Id);

            // Assert
            _ = informe.MensajeError("por defecto").Should().Be("Error: falló la escritura en el sitio de f.txt; se ha restaurado su contenido original.");
            _ = InformeEscritura.Leer(Anuncios("limpio"), Id).MensajeError("por defecto").Should().Be("por defecto");
        }
    }
}
