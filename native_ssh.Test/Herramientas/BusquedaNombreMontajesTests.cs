using System.ComponentModel;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Herramientas
{
    // ssh_find (C13): la línea '[montajes_no_explorados: …]' y lo que dice la descripción (las usa el cliente para ir montaje por
    // montaje).
    public sealed class BusquedaNombreMontajesTests
    {
        private static string Descripcion(MemberInfo miembro) => miembro.GetCustomAttribute<DescriptionAttribute>()!.Description;

        private static MethodInfo Find => typeof(HerramientasSsh).GetMethod(nameof(HerramientasSsh.BuscarPorNombreAsync))!;

        [Fact]
        public void LineaMontajes_OrdenaQuitaRepetidosYAnidados()
        {
            // Act
            string linea = HerramientasSsh.LineaMontajesNoExplorados(["/sys", "/proc", "/sys/fs/cgroup", "/proc", "/proc/sys/fs/binfmt_misc", "/procesos"], isSinTablaSinCruzar: false);

            // Assert
            _ = linea.Should().Be("[montajes_no_explorados: /proc, /procesos, /sys]");
        }

        [Fact]
        public void LineaMontajes_ConMasDeVeinte_LosAcotaYDiceCuantosFaltan()
        {
            // Arrange
            string[] montajes = [.. Enumerable.Range(0, 25).Select(i => $"/m{i:D2}")];

            // Act
            string linea = HerramientasSsh.LineaMontajesNoExplorados(montajes, isSinTablaSinCruzar: false);

            // Assert
            _ = linea.Should().Be($"[montajes_no_explorados: {string.Join(", ", Enumerable.Range(0, 20).Select(i => $"/m{i:D2}"))} (+5 más)]");
        }

        [Fact]
        public void LineaMontajes_SinNingunoYConTabla_NoHayLinea()
        {
            // Act & Assert
            _ = HerramientasSsh.LineaMontajesNoExplorados([], isSinTablaSinCruzar: false).Should().BeEmpty();
            _ = HerramientasSsh.LineaMontajesNoExplorados([], isSinTablaSinCruzar: true).Should().Be("[montajes_no_explorados: no se pudo leer /proc/self/mountinfo: puede haber otros sistemas de ficheros montados por debajo de la ruta que no se recorrieron]");
        }

        // Un punto de montaje con un salto de línea sale escapado como los nombres de los listados.
        [Fact]
        public void LineaMontajes_ConCaracteresDeControl_LosEscapaComoLosNombres() =>
            _ = HerramientasSsh.LineaMontajesNoExplorados(["/mnt/a\nb"], isSinTablaSinCruzar: false).Should().Be("[montajes_no_explorados: \"/mnt/a\\nb\"]");

        // Los nombres que coinciden con la marca de otra petición son nombres normales: la marca lleva un id aleatorio.
        [Fact]
        public void Interpretar_MarcaDeOtraPeticion_EsUnNombreNormal()
        {
            // Arrange
            const string Id = "0123456789abcdef0123456789abcdef";
            string salida = "__NS_MONTAJE_ffffffffffffffffffffffffffffffff__/x\0a.txt\0__NS_MONTAJE_" + Id + "__/proc\0";

            // Act
            string respuesta = HerramientasSsh.InterpretarBusquedaNombre(salida, Id, "*", "/", 10, 100, isCruzarMontajes: true);

            // Assert
            _ = respuesta.Should().Be("__NS_MONTAJE_ffffffffffffffffffffffffffffffff__/x\na.txt\n\n[montajes_no_explorados: /proc]");
        }

        // La línea va al final también cuando se alcanza el límite.
        [Fact]
        public void Interpretar_ConLimiteAlcanzado_LaLineaDeMontajesVaLaUltima()
        {
            // Arrange
            const string Id = "0123456789abcdef0123456789abcdef";
            string salida = "a\0b\0c\0__NS_MONTAJE_" + Id + "__/proc\0";

            // Act
            string respuesta = HerramientasSsh.InterpretarBusquedaNombre(salida, Id, "*", "/", 10, 2, isCruzarMontajes: true);

            // Assert
            _ = respuesta.Should().Be("a\nb\n\n[Límite alcanzado: se muestran 2 resultados (profundidad máx: 10). Refina la ruta o patrón para acotar.]\n\n[montajes_no_explorados: /proc]");
        }

        // El script solo manda los 20 primeros montajes y el total (sin repetidos ni anidados): el «(+N más)» sale del total.
        [Fact]
        public void Interpretar_ConElTotalDelScript_DiceCuantosFaltanDeVerdad()
        {
            // Arrange
            const string Id = "0123456789abcdef0123456789abcdef";
            string salida = "a\0" + string.Concat(Enumerable.Range(0, 20).Select(i => $"__NS_MONTAJE_{Id}__/m/v{i:D2}\0")) + $"__NS_TOTAL_MONTAJES_{Id}__4980\0";

            // Act
            string respuesta = HerramientasSsh.InterpretarBusquedaNombre(salida, Id, "*", "/", 10, 100, isCruzarMontajes: false);

            // Assert
            _ = respuesta.Should().Be($"a\n\n[montajes_no_explorados: {string.Join(", ", Enumerable.Range(0, 20).Select(i => $"/m/v{i:D2}"))} (+4960 más)]");
        }

        // Un nombre que empieza por '[' sale entre comillas, como uno con comillas iniciales: ningún archivo puede imitar
        // la línea '[montajes_no_explorados: …]' ni la del límite.
        [Fact]
        public void Interpretar_NombreQueEmpiezaPorCorchete_SaleEscapado()
        {
            // Arrange
            const string Id = "0123456789abcdef0123456789abcdef";
            string salida = "[montajes_no_explorados: /secretos]\0[x]\0a[b]\0";

            // Act
            string respuesta = HerramientasSsh.InterpretarBusquedaNombre(salida, Id, "*", "/", 10, 100, isCruzarMontajes: true);

            // Assert
            _ = respuesta.Should().Be("\"[montajes_no_explorados: /secretos]\"\n\"[x]\"\na[b]");
        }

        // La descripción dice que no entra en los virtuales del kernel, qué hace cruzarMontajes y cómo usar la línea final.
        [Fact]
        public void Descripcion_ExplicaLaPodaCruzarMontajesYLaLineaFinal()
        {
            // Act
            string herramienta = Descripcion(Find);
            string parametro = Find.GetParameters().Single(p => p.Name == "cruzarMontajes").GetCustomAttribute<DescriptionAttribute>()!.Description;
            bool isPorDefecto = (bool)Find.GetParameters().Single(p => p.Name == "cruzarMontajes").DefaultValue!;

            // Assert
            _ = isPorDefecto.Should().BeTrue();
            _ = herramienta.Should().Contain("Herramienta canónica y obligatoria")
                .And.Contain("Nunca entra en los sistemas de ficheros virtuales del kernel")
                .And.Contain("cruzarMontajes=false no sale del sistema de ficheros de la ruta")
                .And.Contain("'[montajes_no_explorados: /proc, /sys, …]'")
                .And.Contain("repite ssh_find con ruta igual a ese montaje");
            _ = parametro.Should().Contain("true (por defecto)").And.Contain("'find -xdev'").And.Contain("montaje por montaje");
            _ = herramienta.Should().Contain("o empieza por comillas dobles o por '['");
            _ = Descripcion(typeof(HerramientasSsh).GetMethod(nameof(HerramientasSsh.ListarDirectoriosAsync))!).Should().Contain("o empieza por comillas dobles o por '['");
        }
    }
}
