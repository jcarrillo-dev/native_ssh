using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // ssh_exec con guardarSalida=true (C12) en Debian (coreutils) y Alpine (busybox): 'id -un', 'mkdir -m 700', la
    // comprobación de propietario y modo con 'ls -ld', 'wc', 'tail -c', 'cut -b', 'find -mmin'. El directorio base es el de
    // producción (/tmp/native_ssh-<usuario> del contenedor); las pruebas de la colección van de una en una.
    public sealed partial class ContenedorSudoTests
    {
        private static string BaseGuardado(Entorno entorno) => $"/tmp/native_ssh-{entorno.Servidor.Usuario}";

        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task GuardarSalida_ArchivoIntegroPermisosCodigoYLimpieza(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            string baseGuardado = BaseGuardado(entorno);

            // Act
            ResultadoComando r = await herramientas.EjecutarAsync(entorno.Destino, "seq 1 200000; echo error >&2; (exit 4)", timeoutSegundos: 120, esperarSegundos: 90, guardarSalida: true).ComoComando();
            string permisos = entorno.Root("stat -c '%a %U' \"$1\" \"$(dirname \"$2\")\" \"$2\"; wc -c < \"$2\"; tail -n 1 \"$2\"", baseGuardado, r.Archivo ?? "/nada");
            string tramo = await herramientas.LeerArchivoAsync(entorno.Destino, r.Archivo!, 199_999, 200_001);
            string directorio = r.Archivo![..r.Archivo!.LastIndexOf('/')];
            _ = await herramientas.DesconectarAsync(entorno.Destino);
            string tras = entorno.Root("[ -e \"$1\" ] && echo sigue || echo borrado; [ -d \"$2\" ] && echo base", directorio, baseGuardado);

            // Assert
            salida.WriteLine($"{r.Estado} {r.CodigoSalida} {r.Archivo} {r.Lineas} {r.Bytes}\n{permisos}");
            _ = (r.Estado, r.CodigoSalida).Should().Be((EstadoPeticionEnum.Completado, 4));
            _ = Regex.IsMatch(r.Archivo, $"^{Regex.Escape(baseGuardado)}/[0-9a-f]{{32}}/{r.Id}\\.out$").Should().BeTrue(r.Archivo);
            string[] lineas = permisos.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string usuario = entorno.Servidor.Usuario;
            _ = lineas[..3].Should().Equal($"700 {usuario}", $"700 {usuario}", $"600 {usuario}");
            _ = r.Bytes.Should().Be(long.Parse(lineas[3].Trim(), CultureInfo.InvariantCulture));
            _ = lineas[4].Should().Be("error");
            _ = r.Lineas.Should().Be(200_001);
            _ = r.Salida.Should().Be("1\n2\n3\n4\n5\n");
            _ = tramo.Should().Be("[Líneas 199999-200001 de 200001]\n199999\n200000\nerror\n");
            _ = tras.Should().Be("borrado\nbase\n");
        }

        // Directorio base creado antes por otro usuario (aquí root, con 1777 para que el de prueba pudiera escribir): error
        // claro, el comando no se ejecuta y no se escribe nada dentro.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task GuardarSalida_BaseDeOtroUsuario_ErrorSinEscribir(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            string baseGuardado = BaseGuardado(entorno);
            _ = entorno.Root("rm -rf -- \"$1\" && mkdir -m 1777 \"$1\"", baseGuardado);
            string testigo = $"{entorno.DirectorioUsuario}/ejecutado";
            try
            {
                // Act
                ResultadoComando r = await herramientas.EjecutarAsync(entorno.Destino, $"touch {testigo}; echo x", esperarSegundos: 30, guardarSalida: true).ComoComando();
                string dentro = entorno.Root("ls -A \"$1\"; [ -e \"$2\" ] && echo ejecutado || true", baseGuardado, testigo);

                // Assert
                salida.WriteLine($"{r.Estado} '{r.Aviso}'");
                _ = r.Estado.Should().Be(EstadoPeticionEnum.Error);
                _ = r.Aviso.Should().StartWith("El comando NO se ejecutó").And.Contain(baseGuardado).And.Contain("otro usuario");
                _ = dentro.Should().BeEmpty();
            }
            finally
            {
                _ = entorno.Root("rm -rf -- \"$1\"", baseGuardado);
            }
        }

        // Barrido con 'find -mmin' (también el de busybox): un resto sin cambios en 24 h se borra al crear el directorio de un
        // shell; uno con un archivo reciente se queda.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task GuardarSalida_BarreLosRestosViejos(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            string baseGuardado = BaseGuardado(entorno);
            string viejo = $"{baseGuardado}/{new string('a', 32)}";
            string reciente = $"{baseGuardado}/{new string('b', 32)}";
            _ = entorno.Servidor.ComoUsuario("mkdir -p -m 700 \"$1\" && chmod 700 \"$1\" && mkdir -m 700 \"$2\" \"$3\" && echo v > \"$2/x.out\" && echo r > \"$3/x.out\" && touch -d '2020-01-01 00:00' \"$2/x.out\" \"$2\" \"$3\"", baseGuardado, viejo, reciente);

            // Act
            ResultadoComando r = await herramientas.EjecutarAsync(entorno.Destino, "echo hola", esperarSegundos: 30, guardarSalida: true).ComoComando();
            string quedan = entorno.Root("[ -e \"$1\" ] && echo viejo; [ -e \"$2\" ] && echo reciente; true", viejo, reciente);

            // Assert
            _ = r.Estado.Should().Be(EstadoPeticionEnum.Completado, r.Aviso);
            _ = quedan.Should().Be("reciente\n");
        }
    }
}
