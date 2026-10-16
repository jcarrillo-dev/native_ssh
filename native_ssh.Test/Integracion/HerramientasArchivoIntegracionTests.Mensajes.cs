using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;

namespace NativeSsh.Test.Integracion
{
    // Errores de las herramientas de escritura que deben llegar al usuario tal cual y describirse como lo que son.
    public sealed partial class HerramientasArchivoIntegracionTests
    {
        // Anexar a una ruta relativa que empieza por dígitos y un espacio («12 x»), de solo lectura: el error de bash al
        // abrirla («ns: línea N: 12 x: Permiso denegado») tiene la misma cabecera que su aviso de un hijo muerto por una
        // señal («ns: línea N: PID ...») y el filtro de esos avisos no puede comérselo: el usuario debe ver por qué falló.
        [Fact]
        public async Task Anexar_RutaRelativaQueEmpiezaPorDigitosSinPermiso_ElErrorDeBashLlegaAlUsuario()
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            string archivo = Ruta("12 x");
            await File.WriteAllTextAsync(archivo, "original\n");
            _ = Bash($"chmod 444 '{archivo}'");
            _ = await herramientas.FijarDirectorioAsync(Destino, _directorio);

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, "12 x", "mas\n", append: true))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().MatchRegex(@"ns: \S+ [0-9]+: 12 x: \S");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("original\n");
        }

        // Un fallo normal al abrir el archivo para anexar (permiso denegado) no es una muerte a mitad: el error es el de bash,
        // sin «terminó sin que el script confirmara cómo terminó» ni «puede haberse anexado solo una parte», porque el script
        // solo anuncia que anexa después de abrirlo.
        [Fact]
        public async Task Anexar_SinPermisoParaAbrirlo_ElErrorEsElDeBashNoUnaMuerteAMitad()
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            string archivo = Ruta("solo_lectura.txt");
            await File.WriteAllTextAsync(archivo, "original\n");
            _ = Bash($"chmod 444 '{archivo}'");

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, archivo, "mas\n", append: true))
                .Should().ThrowAsync<NativeSshException>()).Which;

            // Assert
            _ = error.Message.Should().MatchRegex($@"^ns: \S+ [0-9]+: {System.Text.RegularExpressions.Regex.Escape(archivo)}: [^\n]+$");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("original\n");
        }

        // Sobrescribir sin sudo un archivo propio con escritura pero sin lectura (0200): no se puede copiar a un temporal ni
        // hacer la copia de seguridad para escribirlo en el sitio. El error lo explica con un mensaje propio, sin la línea
        // cruda de cat («cat: …: Permiso denegado») delante; no se modifica nada ni quedan restos (revisión del A-septies,
        // caso Y08).
        [Fact]
        public async Task Sobrescribir_ArchivoSinPermisoDeLectura_ErrorPropioSinLaLineaDeCat()
        {
            // Arrange
            HerramientasSsh herramientas = Herramientas();
            string archivo = Ruta("solo_escritura.txt");
            await File.WriteAllTextAsync(archivo, "original\n");
            _ = Bash($"chmod 0200 '{archivo}'");

            // Act
            NativeSshException error = (await FluentActions.Awaiting(() => herramientas.EscribirArchivoAsync(Destino, archivo, "NUEVO\n", overwrite: true))
                .Should().ThrowAsync<NativeSshException>()).Which;
            string modo = Bash($"stat -c %a '{archivo}'");
            _ = Bash($"chmod 0600 '{archivo}'");

            // Assert
            _ = error.Message.Should().Be($"Error: no se pudo hacer la copia de seguridad de {archivo}: no tiene permiso de lectura (solo de escritura) y no se sobrescribe en el sitio sin ella. No se ha modificado: dale permiso de lectura o usa sudo=true.");
            _ = modo.Should().Be("200");
            _ = (await File.ReadAllTextAsync(archivo)).Should().Be("original\n");
            _ = Directory.GetFileSystemEntries(_directorio).Should().BeEquivalentTo([archivo]);
        }
    }
}
