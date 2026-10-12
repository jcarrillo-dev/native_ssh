using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Sesion;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // El terminal del shell persistente no admite mensajes de otros usuarios (C9): la inicialización hace 'mesg n' (y, si
    // el servidor no tiene mesg, quita a mano el permiso de escritura del grupo del terminal), para que otro usuario local
    // no pueda escribir con write/wall en el terminal y falsificar líneas de la salida, como los anuncios de las escrituras.
    // sshd deja el terminal con permiso de escritura para el grupo tty (mesg y).
    [Trait("Categoria", "Integracion")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Se liberan en IAsyncLifetime.DisposeAsync (xUnit 2 no llama a IAsyncDisposable en las clases de prueba).")]
    public sealed class TerminalMesgTests : IAsyncLifetime
    {
        private const string Comprobacion = "LC_ALL=C mesg; stat -L -c '%A' \"$(tty)\"; command -v mesg || echo sin-mesg";
        private readonly string _granja = Path.Combine(Path.GetTempPath(), $"ns_sinmesg_{Guid.NewGuid():N}");
        private readonly List<SshdPropio> _sshd = [];
        private readonly List<GestorSesiones> _gestores = [];

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            foreach (GestorSesiones gestor in _gestores)
                await gestor.DisposeAsync();
            foreach (SshdPropio sshd in _sshd)
                sshd.Dispose();
            if (Directory.Exists(_granja))
                Directory.Delete(_granja, recursive: true);
        }

        private (HerramientasSsh Herramientas, string Destino) Herramientas(string? forceCommand = null)
        {
            SshdPropio sshd = new(string.Empty, forceCommand);
            _sshd.Add(sshd);
            OpcionesNativeSsh opciones = sshd.Sshd.Opciones();
            GestorSesiones gestor = new(sshd.Sshd.Fabrica(opciones), new LimitadorConexiones(opciones, TimeProvider.System), new RegistroPeticiones(opciones, TimeProvider.System), opciones, TimeProvider.System, NullLoggerFactory.Instance);
            _gestores.Add(gestor);
            return (new HerramientasSsh(gestor, opciones), sshd.Sshd.Destino.Clave);
        }

        // Permisos del terminal (ls -l): el sexto carácter es la escritura del grupo.
        private static bool IsEscribiblePorElGrupo(string permisos) => permisos.Length >= 6 && permisos[5] == 'w';

        [Fact]
        public async Task ShellNuevo_ElTerminalNoAdmiteMensajesDeOtrosUsuarios()
        {
            // Arrange
            (HerramientasSsh herramientas, string destino) = Herramientas();

            // Act
            ResultadoComando antes = await herramientas.EjecutarAsync(destino, Comprobacion, esperarSegundos: 30).ComoComando();
            _ = herramientas.ReiniciarShell(destino);
            ResultadoComando trasReiniciar = await herramientas.EjecutarAsync(destino, Comprobacion, esperarSegundos: 30).ComoComando();

            // Assert
            foreach (ResultadoComando resultado in new[] { antes, trasReiniciar })
            {
                string[] lineas = resultado.Salida.Split('\n');
                _ = resultado.Estado.Should().Be(EstadoPeticionEnum.Completado, resultado.Salida);
                _ = lineas[0].Should().Be("is n");
                _ = IsEscribiblePorElGrupo(lineas[1]).Should().BeFalse(lineas[1]);
            }
        }

        // Sin mesg en el servidor (PATH sin él): el shell se inicializa igual, sin mensajes de error en la salida, y el
        // terminal queda sin escritura para el grupo.
        [Fact]
        public async Task SinMesgEnElServidor_ElShellFuncionaYElTerminalTampocoAdmiteMensajes()
        {
            // Arrange
            string granja = GranjaBinarios.Crear(_granja, "mesg");
            (HerramientasSsh herramientas, string destino) = Herramientas($"/usr/bin/env PATH={granja} /bin/bash --noprofile --norc");

            // Act
            ResultadoComando resultado = await herramientas.EjecutarAsync(destino, "stat -L -c '%A' \"$(tty)\"; command -v mesg || echo sin-mesg", esperarSegundos: 30).ComoComando();

            // Assert
            string[] lineas = resultado.Salida.Split('\n');
            _ = resultado.Estado.Should().Be(EstadoPeticionEnum.Completado, resultado.Salida);
            _ = resultado.CodigoSalida.Should().Be(0);
            _ = lineas[1].Should().Be("sin-mesg");
            _ = IsEscribiblePorElGrupo(lineas[0]).Should().BeFalse(lineas[0]);
            _ = resultado.Salida.Should().NotContain("mesg:").And.NotContain("not found").And.NotContain("no encontr");
        }
    }
}
