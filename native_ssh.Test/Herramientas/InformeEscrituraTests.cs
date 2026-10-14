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

        // El aviso de bash al recoger un hijo que murió por una señal («ns: línea N: PID Superado el límite... (`core'
        // generado) orden», en el idioma del servidor) no es un error del script y no forma parte del texto; otros avisos de
        // bash con el nombre del script sí.
        [Fact]
        public void Leer_QuitaDelTextoLosAvisosDeBashDeHijosMuertosPorUnaSenal()
        {
            // Arrange
            string salida = "ns: línea 155: 2904918 Superado el límite de tamaño de fichero              (`core' generado) cat -- \"$tmp\" > \"$destino\"\n"
                + "ns: line 12: 4021 Killed                  cat -- \"$copia\" > \"$destino\"\n"
                + "ns: línea 3: base64: orden no encontrada\n"
                + "Error: falló la escritura en el sitio de f.txt; se ha restaurado su contenido original.\n";

            // Act
            InformeEscritura informe = InformeEscritura.Leer(salida, Id);

            // Assert
            _ = informe.Texto.Should().Be("ns: línea 3: base64: orden no encontrada\nError: falló la escritura en el sitio de f.txt; se ha restaurado su contenido original.");
        }

        // Un error de bash con una ruta del usuario que empieza por dígitos y un espacio («ns: línea 12: 12 x: Permiso
        // denegado») tiene la misma cabecera que el aviso de un hijo muerto por una señal, pero no es un aviso: se queda en el
        // texto. Solo se quitan los avisos con su forma completa: tras el PID (con «%5d», alineado con espacios si es corto),
        // la orden del script tal como la escribió, con sus variables sin expandir («"$destino"»), que una ruta expandida no
        // contiene.
        [Theory]
        [InlineData("ns: línea 12: 12 x: Permiso denegado", true)]
        [InlineData("ns: line 7: 12 x: Permission denied", true)]
        [InlineData("ns: línea 12: 4021 Killed: no es un aviso", true)]
        [InlineData("ns: line 12:   402 Killed                     cat -- \"$copia\" > \"$destino\"", false)]
        [InlineData("ns: línea 30: 3569366 Terminado (killed)         base64 -d > \"$tmp\"", false)]
        [InlineData("ns: línea 9: 77777 Violación de segmento     (`core' generado) chmod -- u+s \"$destino\" 2> /dev/null", false)]
        public void Leer_SoloQuitaLosAvisosDeBashConSuFormaCompleta(string linea, bool isTexto)
        {
            // Act
            InformeEscritura informe = InformeEscritura.Leer(linea + "\n", Id);

            // Assert
            _ = informe.Texto.Should().Be(isTexto ? linea : string.Empty);
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
            string sinLimpieza = InformeEscritura.Leer(RutasCreadas + Anuncios("fase escribiendo", "fase escrito"), Id).AvisoExito();
            string sinConfirmar = InformeEscritura.Leer(string.Empty, Id).AvisoExito();

            // Assert
            _ = limpio.Should().Contain("Escritura no atómica").And.Contain("ya borrada").And.NotContain(Copia);
            _ = conRestos.Should().Contain("Escritura no atómica").And.Contain($"en '{Copia}', que no se pudo borrar");
            _ = sinLimpieza.Should().Contain($"en '{Copia}', que puede seguir en el servidor").And.NotContain("no se pudo borrar");
            _ = sinConfirmar.Should().Contain("No se recibió del servidor la confirmación");
            _ = InformeEscritura.Leer(Anuncios("fase colocando", "fase colocado", "limpio"), Id).AvisoExito().Should().BeEmpty();
        }

        private const string NoConfirmado = " El script no confirmó cómo terminó (pudo interrumpirse o perderse su salida después de lo anterior, que es lo último que anunció): compruébalo con ssh_stat o ssh_read_file.";
        private const string AMedioEscribir = $"'/srv/app/f.txt' puede haber quedado a medio escribir. Tras la interrupción, el script restaura el original desde la copia de seguridad y solo después borra la copia y el contenido nuevo; si no lo consigue, la copia del original queda en '{Copia}' y el contenido nuevo en '{Temporal}' (para recuperar el original, copia la primera sobre el archivo).";

        public static TheoryData<string, string> Interrupciones => new()
        {
            { $"tmp {Base64(Temporal)}\nlimpio", "No se llegó a modificar '/srv/app/f.txt'." },
            { $"tmp {Base64(Temporal)}", $"No consta ninguna modificación de '/srv/app/f.txt'. Si la limpieza no llegó a ejecutarse, pueden quedar en el servidor el temporal '{Temporal}'.{NoConfirmado}" },
            { $"tmp {Base64(Temporal)}\nfase colocando", $"Se interrumpió durante el renombrado atómico del contenido nuevo: '/srv/app/f.txt' tiene su contenido anterior (o no existe, si era nuevo) o el nuevo completo, nunca una mezcla. Si la limpieza no llegó a ejecutarse, pueden quedar en el servidor el temporal '{Temporal}'.{NoConfirmado}" },
            { "fase colocando\nfase colocado\nlimpio", "'/srv/app/f.txt' ya tiene el contenido nuevo completo (renombrado atómico)." },
            { "fase colocando\nfase creando", $"'/srv/app/f.txt', que no existía, puede haber quedado creado con solo una parte del contenido nuevo.{NoConfirmado}" },
            { $"tmp {Base64(Temporal)}\ncopia {Base64(Copia)}\nfase escribiendo", AMedioEscribir + NoConfirmado },
            { $"tmp {Base64(Temporal)}\ncopia {Base64(Copia)}\nfase escribiendo\nfase restaurando", AMedioEscribir + NoConfirmado },
            { $"tmp {Base64(Temporal)}\ncopia {Base64(Copia)}\nfase escribiendo\nfase restaurando\nfase fallido\nconservados", $"'/srv/app/f.txt' quedó a medio escribir y no se pudo restaurar: la copia de seguridad del original está en '{Copia}' y el contenido nuevo en '{Temporal}' (no se han borrado; para recuperar el original, copia la primera sobre el archivo)." },
            { $"tmp {Base64(Temporal)}\ncopia {Base64(Copia)}\nfase escribiendo\nfase restaurando\nfase restaurado\nlimpio", "Se restauró el contenido original de '/srv/app/f.txt'." },
            { $"tmp {Base64(Temporal)}\ncopia {Base64(Copia)}\nfase escribiendo\nfase escrito\nlimpio", "'/srv/app/f.txt' ya tiene el contenido nuevo completo (escrito en el sitio)." },
            { "fase anexando", $"Al final de '/srv/app/f.txt' puede haberse anexado solo una parte del contenido.{NoConfirmado}" },
        };

        // Solo se afirma lo que el script llegó a anunciar: la última fase y, si no los borró, dónde quedan los temporales.
        // Si no confirmó cómo terminó (falta su anuncio final: la salida se cortó o sigue en marcha), se dice que es lo
        // último anunciado.
        [Theory]
        [MemberData(nameof(Interrupciones))]
        public void MensajeInterrupcion_DiceQuePasoConElArchivoSegunLaUltimaFaseAnunciada(string anuncios, string esperado)
        {
            // Arrange
            string salida = Anuncios(anuncios.Split('\n'));

            // Act
            string mensaje = InformeEscritura.Leer(salida, Id).MensajeInterrupcion("/srv/app/f.txt", EstadoPeticionEnum.TiempoAgotado, Id, null, isEjecutada: true);

            // Assert
            _ = mensaje.Should().Be($"La escritura en '/srv/app/f.txt' no terminó (estado: TiempoAgotado; petición {Id}). {esperado}");
        }

        // Si la salida superó el máximo por petición y se descartó su principio, pueden faltar anuncios: no se afirma que no se
        // modificó nada y se dice por qué. Si el anuncio final llegó (está al final, que es lo que se conserva), el mensaje
        // dice que el script anunció que terminó, no que no lo confirmó; sin él, que no lo confirmó.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SalidaIncompleta_NoSeApoyaEnLosAnunciosQueQuedanYDiceSiLlegoElFinal(bool isFinal)
        {
            // Arrange
            string final = isFinal ? Anuncios("limpio") : string.Empty;
            InformeEscritura informe = InformeEscritura.Leer(Anuncios("fase restaurado") + final, Id, isSalidaCompleta: false);

            // Act
            string interrupcion = InformeEscritura.Leer(final, Id, isSalidaCompleta: false).MensajeInterrupcion("f.txt", EstadoPeticionEnum.TiempoAgotado, Id, null, isEjecutada: true);
            string error = informe.MensajeError("por defecto", "f.txt", 1, Id);
            string exito = InformeEscritura.Leer(Anuncios("fase colocando", "fase colocado") + final, Id, isSalidaCompleta: false).AvisoExito();

            // Assert
            string descartada = isFinal
                ? "La salida del script superó el máximo de bytes por petición y se descartó su principio: el script anunció que terminó, pero puede faltar parte de lo que anunció antes."
                : "La salida del script superó el máximo de bytes por petición y se descartó su principio: puede faltar parte de lo que anunció.";
            _ = interrupcion.Should().Contain("No consta ninguna modificación de 'f.txt'.").And.NotContain("No se llegó a modificar").And.Contain(descartada);
            _ = exito.Should().Be($" {descartada}");
            if (isFinal)
            {
                _ = interrupcion.Should().NotContain("no confirmó");
                _ = error.Should().StartWith($"La escritura en 'f.txt' terminó con el código 1; el script anunció que terminó, pero se descartó el principio de su salida (petición {Id}). Se restauró el contenido original de 'f.txt'. {descartada}");
            }
            else
            {
                _ = interrupcion.Should().Contain(NoConfirmado.Trim());
                _ = error.Should().StartWith($"La escritura en 'f.txt' terminó con el código 1 sin que el script confirmara cómo terminó (petición {Id}").And.Contain(descartada);
            }
        }

        // Una petición que no llegó a ejecutarse (cancelada en la cola) no modificó nada, sin matices.
        [Fact]
        public void MensajeInterrupcion_PeticionQueNoLlegoAEjecutarse_DiceQueNoSeModifico()
        {
            // Act
            string mensaje = InformeEscritura.Leer(string.Empty, Id).MensajeInterrupcion("f.txt", EstadoPeticionEnum.Cancelado, Id, "Cancelada antes de empezar.", isEjecutada: false);

            // Assert
            _ = mensaje.Should().EndWith("No se llegó a ejecutar: 'f.txt' no se ha modificado. Cancelada antes de empezar.");
        }

        // La salida del script (sin sus anuncios) va en el error también con una fase anunciada: ssh_output no da la de una
        // petición interna, así que es el único sitio donde se ve (p. ej. «No space left on device»).
        [Fact]
        public void MensajeInterrupcion_AnadeElAvisoDeLaPeticionYLaSalidaDelScript()
        {
            // Act
            string sinAnuncios = InformeEscritura.Leer("[sudo] password for ana: \n", Id).MensajeInterrupcion("f.txt", EstadoPeticionEnum.Cancelado, Id, "Cancelado: se envió Ctrl+C al proceso.", isEjecutada: true);
            string conAnuncios = InformeEscritura.Leer("Error: x\n" + Anuncios("fase colocando", "fase colocado", "limpio"), Id).MensajeInterrupcion("f.txt", EstadoPeticionEnum.Cancelado, Id, null, isEjecutada: true);

            // Assert
            _ = sinAnuncios.Should().EndWith($"No consta ninguna modificación de 'f.txt'.{NoConfirmado} Cancelado: se envió Ctrl+C al proceso. Salida del script: [sudo] password for ana:");
            _ = conAnuncios.Should().EndWith("'f.txt' ya tiene el contenido nuevo completo (renombrado atómico). Salida del script: Error: x");
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
            string error = InformeEscritura.Leer(anuncios + Anuncios("fase restaurando", "fase restaurado setuid", "limpio") + "Error: falló.\n", Id).MensajeError("x", "f", 1, Id);
            string interrupcion = InformeEscritura.Leer(anuncios + Anuncios("fase restaurando", "fase restaurado capacidades", "limpio"), Id).MensajeInterrupcion("f", EstadoPeticionEnum.TiempoAgotado, Id, null, isEjecutada: true);

            // Assert
            _ = exito.Should().Contain("con estas excepciones. ATENCIÓN: el núcleo quitó al escribir el bit setuid y no se pudo devolver (chmod u+s, como propietario o con sudo=true). el núcleo quitó al escribir el bit setgid")
                .And.EndWith("hace falta sudo=true y setfattr o setcap. Para devolverlas, con privilegios: «setfattr -n security.capability -v 0x0100000200 <archivo>».");
            _ = sinComprobar.Should().EndWith("ATENCIÓN: no se pudo comprobar si el archivo tenía capacidades (security.capability): en el servidor no hay getfattr ni getcap; si las tenía, el núcleo las borró al escribir.");
            _ = new[] { exito, sinComprobar }.Should().AllSatisfy(aviso => aviso.Should().NotContain("se devolvieron y se comprobaron"));
            _ = todoDevuelto.Should().EndWith("El modo con sus bits setuid y setgid y las capacidades (security.capability), que el núcleo quita al escribir en un archivo (los bits, salvo si escribe root), se devolvieron y se comprobaron.").And.NotContain("ATENCIÓN");
            _ = error.Should().Be("Error: falló. ATENCIÓN: el núcleo quitó al escribir el bit setuid y no se pudo devolver (chmod u+s, como propietario o con sudo=true).");
            _ = interrupcion.Should().EndWith("Se restauró el contenido original de 'f'. ATENCIÓN: el núcleo borró al escribir las capacidades del archivo (security.capability) y no se pudieron devolver: hace falta sudo=true y setfattr o setcap. Para devolverlas, con privilegios: «setfattr -n security.capability -v 0x0100000200 <archivo>».");
        }

        // Si el script confirmó cómo terminó (su anuncio final) o falló antes de anunciar nada, el error es el suyo.
        [Fact]
        public void MensajeError_EsElTextoDelScriptSinLosAnuncios()
        {
            // Act
            InformeEscritura informe = InformeEscritura.Leer(RutasCreadas + Anuncios("fase escribiendo", "fase restaurando", "fase restaurado") + "Error: falló la escritura en el sitio de f.txt; se ha restaurado su contenido original.\n" + Anuncios("limpio"), Id);

            // Assert
            _ = informe.MensajeError("por defecto", "f.txt", 1, Id).Should().Be("Error: falló la escritura en el sitio de f.txt; se ha restaurado su contenido original.");
            _ = InformeEscritura.Leer(Anuncios("limpio"), Id).MensajeError("por defecto", "f.txt", 1, Id).Should().Be("por defecto");
            _ = InformeEscritura.Leer("Error: la ruta es un directorio: f.txt\n", Id).MensajeError("por defecto", "f.txt", 1, Id).Should().Be("Error: la ruta es un directorio: f.txt");
        }

        public static TheoryData<string, string> FallosSinConfirmar => new()
        {
            { $"tmp {Base64(Temporal)}\ncopia {Base64(Copia)}\nfase escribiendo", $"'/srv/app/f.txt' puede haber quedado a medio escribir y el script ya no lo restaurará: la copia de seguridad del original está en '{Copia}' y el contenido nuevo en '{Temporal}' (para recuperar el original, copia la primera sobre el archivo)." },
            { $"tmp {Base64(Temporal)}\ncopia {Base64(Copia)}\nfase escribiendo\nfase restaurando", $"'/srv/app/f.txt' puede haber quedado a medio escribir y el script ya no lo restaurará: la copia de seguridad del original está en '{Copia}' y el contenido nuevo en '{Temporal}' (para recuperar el original, copia la primera sobre el archivo)." },
            { $"tmp {Base64(Temporal)}", $"No consta ninguna modificación de '/srv/app/f.txt'. Si la limpieza no llegó a ejecutarse, pueden quedar en el servidor el temporal '{Temporal}'." },
            { "fase anexando", "Al final de '/srv/app/f.txt' puede haberse anexado solo una parte del contenido." },
        };

        // Un código distinto de 0 sin el anuncio final, con algo ya anunciado: el script murió a mitad (SIGKILL del OOM killer,
        // kill -9) y no dejó su propio error, o no tiene trampa de salida (anexar). El mensaje dice el estado según lo último
        // anunciado, las rutas y el id.
        [Theory]
        [MemberData(nameof(FallosSinConfirmar))]
        public void MensajeError_SinElAnuncioFinalDelScript_DiceElEstadoSegunLoAnunciadoYElId(string anuncios, string esperado)
        {
            // Arrange
            string salida = Anuncios(anuncios.Split('\n'));

            // Act
            string mensaje = InformeEscritura.Leer(salida, Id).MensajeError("Fallo al escribir (código 137).", "/srv/app/f.txt", 137, Id);

            // Assert
            _ = mensaje.Should().Be($"La escritura en '/srv/app/f.txt' terminó con el código 137 (SIGKILL: p. ej. el OOM killer o un kill -9) sin que el script confirmara cómo terminó (petición {Id}). {esperado}");
        }

        // Lo que el script llegó a escribir en su salida (sin los anuncios) se añade al final.
        [Fact]
        public void MensajeError_SinElAnuncioFinalConSalidaDelScript_LaAnade()
        {
            // Act
            string mensaje = InformeEscritura.Leer(RutasCreadas + Anuncios("fase escribiendo") + "cat: error de escritura\n", Id).MensajeError("x", "f.txt", 1, Id);

            // Assert
            _ = mensaje.Should().StartWith($"La escritura en 'f.txt' terminó con el código 1 sin que el script confirmara cómo terminó (petición {Id}). 'f.txt' puede haber quedado a medio escribir").And.EndWith(" Salida del script: cat: error de escritura");
        }

        // Interrumpida escribiendo o restaurando en el sitio, el script sigue en marcha: con el candado tomado, el error dice
        // que las operaciones nuevas sobre el archivo fallan hasta que termine (y, si no se pudo comprobar que esté sobre el
        // archivo de la ruta, en qué caso no excluye); sin candado, por qué y que no se escriba en él hasta comprobar que
        // terminó; sin anuncio del candado (o con uno desconocido), nada.
        [Theory]
        [InlineData("candado tomado", nameof(CandadoEscrituraEnum.Tomado), "Mientras el script siga en marcha tiene tomado el candado del archivo (flock): las escrituras, los anexados y los reemplazos nuevos sobre él fallan al momento sin tocarlo; reintenta cuando termine.")]
        [InlineData("candado sin_comprobar", nameof(CandadoEscrituraEnum.SinComprobar), "Mientras el script siga en marcha tiene tomado el candado del archivo (flock): las escrituras, los anexados y los reemplazos nuevos sobre él fallan al momento sin tocarlo; reintenta cuando termine. Pero no se pudo comprobar que quedara sobre el archivo que está en la ruta (en el servidor no hay /proc, ni stat de GNU coreutils, python3 o perl con que comparar los inodos, o no se pudo consultar la ruta): si otro proceso lo sustituyó justo mientras se tomaba, no excluye a nadie.")]
        [InlineData("candado ninguno", nameof(CandadoEscrituraEnum.SinHerramienta), "El script no pudo tomar el candado del archivo (flock): en el servidor no hay flock (de util-linux, o uno sin -E como el de busybox que funcione), python3 ni perl que lo consigan, o el sistema de archivos no admite flock. Hasta que el script termine, una escritura nueva sobre el archivo podría mezclarse con la restauración: antes de escribir, comprueba con ssh_stat o ssh_read_file que ya terminó.")]
        [InlineData("candado sin_abrir", nameof(CandadoEscrituraEnum.SinAbrir), "El script no pudo tomar el candado del archivo (flock): no pudo abrirlo ni para leer ni para anexar (sin permiso, o el archivo desapareció entretanto). Hasta que el script termine, una escritura nueva sobre el archivo podría mezclarse con la restauración: antes de escribir, comprueba con ssh_stat o ssh_read_file que ya terminó.")]
        [InlineData("candado otro", nameof(CandadoEscrituraEnum.SinAnuncio), null)]
        [InlineData(null, nameof(CandadoEscrituraEnum.SinAnuncio), null)]
        public void MensajeInterrupcion_EscribiendoEnElSitio_DiceQuePasaConLasEscriturasNuevas(string? anuncio, string candado, string? esperado)
        {
            // Arrange
            string anuncios = (anuncio is null ? string.Empty : Anuncios(anuncio)) + RutasCreadas + Anuncios("fase escribiendo");

            // Act
            InformeEscritura informe = InformeEscritura.Leer(anuncios, Id);
            string mensaje = informe.MensajeInterrupcion("f.txt", EstadoPeticionEnum.Cancelado, Id, null, isEjecutada: true);
            string trasSigkill = informe.MensajeError("x", "f.txt", 137, Id);

            // Assert
            _ = informe.Candado.ToString().Should().Be(candado);
            _ = esperado is null
                ? mensaje.Should().NotContain("candado")
                : mensaje.Should().Contain($"(para recuperar el original, copia la primera sobre el archivo). {esperado} El script no confirmó");
            _ = trasSigkill.Should().NotContain("candado", "con el script muerto ya no hay restauración en curso");
        }

        // Una escritura terminada sin candado (ninguna herramienta pudo tomarlo, o no se pudo abrir el archivo) o con un
        // candado que no se pudo comprobar lo avisa, con el motivo; con el candado tomado y comprobado, nada.
        [Theory]
        [InlineData("candado ninguno", " El script no pudo tomar el candado del archivo (flock): en el servidor no hay flock (de util-linux, o uno sin -E como el de busybox que funcione), python3 ni perl que lo consigan, o el sistema de archivos no admite flock. La escritura no estuvo protegida frente a otra simultánea sobre el mismo archivo.")]
        [InlineData("candado sin_abrir", " El script no pudo tomar el candado del archivo (flock): no pudo abrirlo ni para leer ni para anexar (sin permiso, o el archivo desapareció entretanto). La escritura no estuvo protegida frente a otra simultánea sobre el mismo archivo.")]
        [InlineData("candado sin_comprobar", " El script tomó el candado del archivo (flock), pero no se pudo comprobar que quedara sobre el archivo que está en la ruta (en el servidor no hay /proc, ni stat de GNU coreutils, python3 o perl con que comparar los inodos, o no se pudo consultar la ruta): si otro proceso lo sustituyó justo mientras se tomaba, la escritura no estuvo protegida frente a otra simultánea sobre el mismo archivo.")]
        [InlineData("candado tomado", "")]
        [InlineData("candado otro", "")]
        public void AvisoSinCandado_SoloSiNoSePudoTomarOComprobar(string anuncio, string esperado)
        {
            // Act
            InformeEscritura informe = InformeEscritura.Leer(Anuncios(anuncio, "fase colocado", "limpio"), Id);

            // Assert
            _ = informe.AvisoExito().Should().Be(informe.AvisoSinCandado());
            _ = informe.AvisoSinCandado().Should().Be(esperado);
        }
    }
}
