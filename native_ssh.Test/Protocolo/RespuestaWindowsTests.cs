using FluentAssertions;
using NativeSsh.Protocolo;

namespace NativeSsh.Test.Protocolo
{
    // WS1: señales de un shell de Windows que no es bash, con los bytes medidos en Windows Server 2025 (OpenSSH for Windows
    // 9.5) y sin depender del idioma.
    public sealed class RespuestaWindowsTests
    {
        [Theory]
        [InlineData("SSH-2.0-OpenSSH_for_Windows_9.5", true)]
        [InlineData("SSH-2.0-OpenSSH_for_Windows_8.1", true)]
        [InlineData("SSH-2.0-OpenSSH_9.9", false)]
        [InlineData("SSH-2.0-dropbear_2022.83", false)]
        [InlineData("SSH-1.99-OpenSSH_for_Windows_8.1", true)]
        // VersionAddendum de un OpenSSH de Linux, o un comentario: no es el campo de software.
        [InlineData("SSH-2.0-OpenSSH_9.6 OpenSSH_for_Windows", false)]
        [InlineData("SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13 OpenSSH_for_Windows_9.5", false)]
        [InlineData("SSH-2.0-XOpenSSH_for_Windows_9.5", false)]
        [InlineData("OpenSSH_for_Windows_9.5", false)]
        [InlineData(null, false)]
        public void IsVersionWindows_SoloElPortDeMicrosoft(string? version, bool esperado) =>
            RespuestaWindows.IsVersionWindows(version).Should().Be(esperado);

        [Fact]
        public void HasPrefijoConPty_LoPrimeroQueEnviaConPty()
        {
            _ = RespuestaWindows.HasPrefijoConPty("\e[?9001h\e[?1004h\e[?25l"u8).Should().BeTrue();
            _ = RespuestaWindows.HasPrefijoConPty("\e[?1004h\e[?9001h"u8).Should().BeFalse();
            _ = RespuestaWindows.HasPrefijoConPty("Bienvenido\r\n$ "u8).Should().BeFalse();
        }

        [Theory]
        // cmd (inglés y con la cabecera traducida al español).
        [InlineData("\e[?25l\e[2J\e[m\e[HMicrosoft Windows [Version 10.0.26100.1742]\e]0;C:\\WINDOWS\\system32\\conhost.exe\a\e[?25h", "Microsoft Windows [Version 10.0.26100.1742]")]
        [InlineData("\e[HMicrosoft Windows [Versión 10.0.26100.1742]\r\n(c) Microsoft Corporation. Todos los derechos reservados.", "Microsoft Windows [Versión 10.0.26100.1742]")]
        // Solo el indicador de cmd (con el usuario y el equipo que pone OpenSSH for Windows) o el de PowerShell.
        [InlineData("\e[4;1Hnsws@WINSRV C:\\Users\\nsws>exec bash --noediting", "C:\\Users\\nsws>")]
        [InlineData("PS C:\\Users\\nsws> ", "PS C:\\Users\\nsws>")]
        // PowerShell 5.1 y 7.
        [InlineData("\e[HWindows PowerShell\r\nCopyright (C) Microsoft Corporation.", "Windows PowerShell")]
        [InlineData("PowerShell 7.4.6\r\n", "PowerShell 7.4.6")]
        public void BuscarShell_CabeceraOIndicadorDeCmdOPowerShell(string salida, string esperado) =>
            RespuestaWindows.BuscarShell(salida).Should().Be(esperado);

        [Theory]
        // Git Bash como shell de login (medido) y un bash de Linux con un perfil que escribe cosas.
        [InlineData("\e[?9001h\e[?1004h\e[?25l\e[2J\e[m\e[H\e]0;UCRT64:/c/Users/nsws\a\e[?25h\e[32m\r\nnsws@WINSRV \e[35mUCRT64 \e[33m~\e[m\r\n$ exec bash --noediting --norc --noprofile\r\nbash: __git_ps1: command not found\r\n")]
        [InlineData("Linux debian 6.12.0 #1 SMP x86_64\r\n\r\nLast login: Tue Oct 14 09:00:00 2026\r\nprueba@debian:~$ ")]
        // El título de la ventana lleva una ruta de Windows, pero no es un indicador.
        [InlineData("\e]0;C:\\WINDOWS\\system32\\conhost.exe\a\e[?25h> ")]
        public void BuscarShell_SinSenalDeCmdNiPowerShell_Null(string salida) =>
            RespuestaWindows.BuscarShell(salida).Should().BeNull();

        // WS1: la línea que se envía al abrir el shell, mostrada como eco sin ejecutarse (bajo ConPTY un '\n' no es Intro, así
        // que cmd y PowerShell la juntan con la siguiente). Un bash, también Git Bash, nunca la escribe así: la termina en su
        // salto de línea, la ejecuta y contesta al protocolo. Bytes medidos en Windows Server 2025.
        [Theory]
        [InlineData("\e[4;1Hnsws@WINSRV C:\\Users\\nsws>exec bash --noediting --norc --noprofilePS1=; PS2=; PS4=; unset HISTFILE", true)]
        [InlineData("PS C:\\Users\\nsws> \e[93mexec \e[37mbash \e[90m--noediting --norc --noprofile\e[6;38H\e[?25h\e[m\e[?25l\e[93m\e[6;19HPS1=\e[37m; \r\n>> \e[93mexec \e[37mbash \e[90m--noediting --norc --noprofilePS1=", true)]
        [InlineData("\r\nnsws@WINSRV UCRT64 ~\r\n$ exec bash --noediting --norc --noprofile\r\nbash: __git_ps1: command not found\r\n$ PS1=; PS2=; PS4=;", false)]
        [InlineData("Microsoft Windows [Version 10.0.26100.1742]\r\nC:\\Users\\nsws> exec bash --noediting --norc --noprofile\r\nPS1=; PS2=;", false)]
        public void IsEcoSinEjecutar_SoloCuandoLaLineaNoSeTerminoComoEnUnBash(string salida, bool esperado) =>
            RespuestaWindows.IsEcoSinEjecutar(salida).Should().Be(esperado);

        // La señal se forma con la línea real de la inicialización: si ConstructorComando la cambia, esta prueba lo dice.
        [Fact]
        public void EcoSinEjecutar_CoincideConLaInicializacionDeConstructorComando()
        {
            System.Buffers.ArrayBufferWriter<byte> escritor = new();
            ConstructorComando.EscribirInicializacion(Guid.NewGuid(), escritor);
            string inicializacion = System.Text.Encoding.UTF8.GetString(escritor.WrittenSpan);

            _ = RespuestaWindows.IsEcoSinEjecutar(inicializacion.Replace("\n", "", StringComparison.Ordinal)).Should().BeTrue();
            _ = RespuestaWindows.IsEcoSinEjecutar(inicializacion).Should().BeFalse();
        }

        [Theory]
        [InlineData(1, "", "3>& was unexpected at this time.\r\n", true)]
        [InlineData(1, "", "The command line is too long.\r\n", true)]
        [InlineData(1, "", "bash: /dev/nu: No such file or directory\n", false)]
        [InlineData(1, "x", "error\r\n", false)]
        [InlineData(0, "", "aviso\r\n", false)]
        [InlineData(null, "", "error\r\n", false)]
        public void IsRespuestaExec_SinStdoutConErrorEnCrlf(int? codigo, string salida, string errores, bool esperado) =>
            RespuestaWindows.IsRespuestaExec(codigo, System.Text.Encoding.UTF8.GetBytes(salida), System.Text.Encoding.UTF8.GetBytes(errores)).Should().Be(esperado);
    }
}
