using System.Linq;
using FluentAssertions;
using NativeSsh.Comun;
using NativeSsh.Herramientas;
using NativeSsh.Test.Utilidades;

namespace NativeSsh.Test.Integracion
{
    // Lo tecleado con ssh_input que el comando no lee se descarta sin ejecutarse mientras la cola espera el centinela del
    // envío siguiente, y lo avisa la respuesta del comando siguiente (ConstructorComando.EscribirCola), con el bash de Debian
    // (5.2, glibc) y el de Alpine (5.3, musl, terminal y utilidades de busybox). Ver EntradaPorAdelantadoTests (sshd local).
    public sealed partial class ContenedorSudoTests
    {
        // Dos líneas en una entrada, dos entradas, muchas líneas (más de 4095 bytes, lo que cabe en el terminal en modo
        // canónico), una línea a medias (cuenta como una) y Ctrl+D: ninguna se ejecuta (el testigo no existe), el recuento es
        // exacto y el comando siguiente no ve nada.
        [TheoryContenedor]
        [InlineData(ContenedorSudo.Debian)]
        [InlineData(ContenedorSudo.Alpine)]
        public async Task Tecleado_QueElComandoNoLee_NoSeEjecutaYSeAvisa(string distro)
        {
            // Arrange
            await using Entorno entorno = Preparar(distro);
            HerramientasSsh herramientas = entorno.Herramientas();
            string testigo = $"{entorno.DirectorioUsuario}/testigo";
            string muchas = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"linea {i} {new string('x', 40)}"));
            (string Texto, bool IsEnter, string Aviso)[] casos =
            [
                ($"respuesta1\ntouch {testigo}", true, "no leyó 2 líneas"),
                ($"respuesta1\rtouch {testigo}", true, "no leyó 2 líneas"),
                ($"{muchas}\ntouch {testigo}", true, "no leyó 201 líneas"),
                ($"l1\nl2\ntouch {testigo}", false, "no leyó 3 líneas"),
                ($"\u0004\u0004touch {testigo}", true, "no leyó 3 líneas")
            ];

            foreach ((string texto, bool isEnter, string aviso) in casos)
            {
                ResultadoComando lento = await herramientas.EjecutarAsync(entorno.Destino, "sleep 2; (exit 5)", esperarSegundos: 1).ComoComando();

                // Act
                ResultadoComando entrada = await herramientas.EnviarEntradaAsync(lento.Id, texto, enter: isEnter, esperarSegundos: 10).ComoComando();
                ResultadoComando siguiente = await herramientas.EjecutarAsync(entorno.Destino, $"echo siguiente; test -e {testigo} && echo EJECUTADA; true", esperarSegundos: 15).ComoComando();

                // Assert
                salida.WriteLine($"{distro} {texto.Length} bytes, enter={isEnter}: entrada {entrada.Estado} {entrada.CodigoSalida} aviso '{entrada.Aviso}'; siguiente '{siguiente.Salida}'");
                _ = entrada.Estado.Should().Be(EstadoPeticionEnum.Completado);
                _ = entrada.CodigoSalida.Should().Be(5);
                _ = siguiente.Aviso.Should().Contain(aviso);
                _ = siguiente.Salida.Should().Be("siguiente\n", "nada de lo tecleado se ejecuta ni sale en el comando siguiente");
            }

            // Dos entradas de una línea.
            ResultadoComando otro = await herramientas.EjecutarAsync(entorno.Destino, "sleep 2", esperarSegundos: 1).ComoComando();
            _ = await herramientas.EnviarEntradaAsync(otro.Id, "respuesta1", esperarSegundos: 0).ComoComando();
            _ = await herramientas.EnviarEntradaAsync(otro.Id, $"touch {testigo}", esperarSegundos: 10).ComoComando();
            ResultadoComando comprobacion = await herramientas.EjecutarAsync(entorno.Destino, $"test -e {testigo} && echo EJECUTADA; echo fin", esperarSegundos: 15).ComoComando();
            _ = comprobacion.Aviso.Should().Contain("no leyó 2 líneas");
            _ = comprobacion.Salida.Should().Be("fin\n");

            // Lo tecleado pendiente al interrumpir el comando (Ctrl+C, la pausa de ET3 y la línea de recuperación) tampoco se
            // ejecuta: el núcleo vacía la entrada del terminal.
            ResultadoComando interrumpido = await herramientas.EjecutarAsync(entorno.Destino, "sleep 30", esperarSegundos: 1).ComoComando();
            _ = await herramientas.EnviarEntradaAsync(interrumpido.Id, $"touch {testigo}\ntouch {testigo}", esperarSegundos: 0).ComoComando();
            ResultadoComando cancelado = await herramientas.CancelarAsync(interrumpido.Id).ComoComando();
            ResultadoComando trasCtrlC = await herramientas.EjecutarAsync(entorno.Destino, $"test -e {testigo} && echo EJECUTADA; echo fin", esperarSegundos: 15).ComoComando();
            salida.WriteLine($"{distro} tras Ctrl+C: {cancelado.Estado}; '{trasCtrlC.Salida}' aviso '{trasCtrlC.Aviso}'");
            _ = cancelado.Estado.Should().Be(EstadoPeticionEnum.Cancelado);
            _ = trasCtrlC.Salida.Should().Be("fin\n");
        }
    }
}
