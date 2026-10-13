using System.Diagnostics;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Ssh;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Ssh
{
    // Lo de Windows de 'ssh -G' (solo se compila para Windows), con el ssh.exe del sistema (OpenSSH para Windows) y un
    // fichero de configuración propio de la prueba (ssh -F), nunca el del usuario.
    public sealed class ResolutorConfiguracionSshWindowsTests : IDisposable
    {
        private readonly string _directorio = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ns_cfgw_{Guid.NewGuid():N}")).FullName;
        private readonly LoggerPrueba _log = new();

        public void Dispose()
        {
            _log.Dispose();
            Directory.Delete(_directorio, recursive: true);
        }

        // ssh.exe con la entrada en una tubería escribe en la salida de errores que no pedirá un terminal, y con esa salida
        // también en una tubería se quedaba colgado hasta el plazo: con la configuración leída a tiempo, no se cuelga.
        [Fact]
        public void SshDelSistema_ConLaEntradaYLosErroresEnTuberias_LeeLaConfiguracionSinColgarse()
        {
            // Arrange
            string configuracion = Path.Combine(_directorio, "config");
            File.WriteAllText(configuracion, "Host web\n  HostName 127.0.0.1\n  User deploy\n  Port 2222\n");
            ResolutorConfiguracionSsh resolutor = new(new OpcionesNativeSsh
            {
                UsuarioPorDefecto = Environment.UserName,
                ConfiguracionSsh = configuracion,
                BinarioSsh = "ssh",
                TimeoutConfiguracionSsh = TimeSpan.FromSeconds(5)
            }, new LoggerDe<ResolutorConfiguracionSsh>(_log));
            _ = DestinoSsh.TryParse("web", Environment.UserName, out DestinoSsh destino, out bool isUsuario, out bool isPuerto).Should().BeTrue();
            Stopwatch reloj = Stopwatch.StartNew();

            // Act
            DestinoSsh resuelto = resolutor.Resolver(destino, isUsuario, isPuerto);

            // Assert
            _ = resuelto.Clave.Should().Be("deploy@127.0.0.1:2222");
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        }
    }
}
