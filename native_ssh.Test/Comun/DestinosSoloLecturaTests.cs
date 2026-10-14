using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using NativeSsh.Comun;

namespace NativeSsh.Test.Comun
{
    // Formato y comparación de NATIVE_SSH_SOLO_LECTURA (CI6).
    public sealed class DestinosSoloLecturaTests
    {
        private static DestinoSsh Destino(string texto)
        {
            _ = DestinoSsh.TryParse(texto, "ana", out DestinoSsh destino).Should().BeTrue();
            return destino;
        }

        // Resolución falsa: prod.ejemplo → 10.0.0.5, otro.ejemplo → 10.0.0.9, mi-portatil → 127.0.1.1; nadie.ejemplo da un fallo
        // pasajero (TryAgain: el DNS no responde); cualquier otro nombre no existe (HostNotFound).
        private static Task<IPAddress[]> ResolverFalso(string nombre, CancellationToken _) => nombre switch
        {
            "prod.ejemplo" => Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") }),
            "otro.ejemplo" => Task.FromResult(new[] { IPAddress.Parse("10.0.0.9") }),
            "mi-portatil" => Task.FromResult(new[] { IPAddress.Parse("127.0.1.1") }),
            "nadie.ejemplo" => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.TryAgain)),
            _ => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound))
        };

        // Configuración de ssh falsa (el HostName de una entrada con ssh -G): prod → 10.0.0.5, interno → prod.ejemplo,
        // fantasma → noexiste.ejemplo (no existe en el DNS), mal → ssh -G falla; cualquier otro nombre no es un alias (null).
        private static string? HostNameFalso(string? usuario, string servidor, int? puerto) => servidor switch
        {
            "prod" => "10.0.0.5",
            "interno" => "prod.ejemplo",
            "fantasma" => "noexiste.ejemplo",
            "mal" => throw new NativeSshException("'ssh -G' no terminó en 5 s"),
            _ => null
        };

        private static DestinosSoloLectura ConConfiguracion(string configuracion, Func<string?, string, int?, string?>? hostName = null)
        {
            _ = DestinosSoloLectura.TryParsear(configuracion, out DestinosSoloLectura destinos, out string? error, ResolverFalso).Should().BeTrue(error);
            destinos.UsarConfiguracionSsh(hostName ?? HostNameFalso);
            return destinos;
        }

        private static async Task<bool> IsSoloLecturaAsync(string configuracion, string destino)
        {
            _ = DestinosSoloLectura.TryParsear(configuracion, out DestinosSoloLectura destinos, out string? error, ResolverFalso).Should().BeTrue(error);
            return await destinos.IsSoloLecturaAsync(Destino(destino), CancellationToken.None);
        }

        [Theory]
        [InlineData("prod.ejemplo", "root@prod.ejemplo", true)]
        [InlineData("prod.ejemplo", "deploy@PROD.Ejemplo:2222", true)]
        [InlineData("prod.ejemplo.", "root@prod.ejemplo", true)]
        [InlineData("prod.ejemplo", "root@prod.ejemplo.", true)]
        [InlineData("prod.ejemplo", "root@otro.ejemplo", false)]
        [InlineData("deploy@prod.ejemplo", "deploy@prod.ejemplo", true)]
        [InlineData("deploy@prod.ejemplo", "root@prod.ejemplo", false)]
        [InlineData("prod.ejemplo:2222", "root@prod.ejemplo:2222", true)]
        [InlineData("prod.ejemplo:2222", "root@prod.ejemplo", false)]
        [InlineData("otro, root@prod.ejemplo:22", "root@prod.ejemplo", true)]
        [InlineData("*", "cualquiera@servidor:2200", true)]
        [InlineData("[::1]:22", "ana@[0:0:0:0:0:0:0:1]", true)]
        [InlineData("::1", "ana@[::1]:2200", true)]
        [InlineData("10.0.0.5", "ana@::ffff:10.0.0.5", true)]
        // Por dirección: el nombre cubre su IP y la IP cubre el nombre.
        [InlineData("prod.ejemplo", "root@10.0.0.5", true)]
        [InlineData("10.0.0.5", "root@prod.ejemplo", true)]
        [InlineData("deploy@prod.ejemplo", "root@10.0.0.5", false)]
        [InlineData("prod.ejemplo", "root@10.0.0.6", false)]
        // Loopback: localhost, 127.0.0.0/8, ::1 y 0.0.0.0 (y ::) son la misma máquina, sin preguntar al DNS.
        [InlineData("localhost", "ana@127.0.0.2", true)]
        [InlineData("localhost", "ana@0.0.0.0", true)]
        [InlineData("localhost", "ana@[::]", true)]
        [InlineData("localhost", "ana@::ffff:127.0.0.1", true)]
        [InlineData("127.0.0.1", "ana@localhost", true)]
        [InlineData("127.0.0.1", "ana@LOCALHOST.", true)]
        [InlineData("::1", "ana@127.0.0.1", true)]
        [InlineData("0.0.0.0", "ana@[::1]:22", true)]
        [InlineData("localhost", "ana@mi-portatil", true)]
        [InlineData("localhost:2222", "ana@127.0.0.1", false)]
        [InlineData("localhost", "ana@10.0.0.5", false)]
        public async Task Comparacion_PorUsuarioPuertoYServidor(string configuracion, string destino, bool isSoloLectura) =>
            _ = (await IsSoloLecturaAsync(configuracion, destino)).Should().Be(isSoloLectura);

        // Un literal IPv6 con zona ('%1', '%lo') es la misma dirección para conectar: la zona no cuenta al comparar (ni para
        // el loopback), tampoco en ssh_status. Antes IPAddress.Equals/IsLoopback comparaban también el ScopeId y el destino
        // con zona se tomaba por otro distinto, escribible.
        [Theory]
        [InlineData("localhost", "root@[::1%1]:22")]
        [InlineData("::1", "root@[::1%1]:22")]
        [InlineData("[::1]:22", "root@[::1%1]:22")]
        [InlineData("2001:db8::5", "root@[2001:db8::5%1]:22")]
        [InlineData("2001:db8::5", "root@2001:db8::5%1")]
        [InlineData("localhost", "root@[::1%lo]:22")]
        [InlineData("::1", "root@::1%lo")]
        [InlineData("fe80::1%2", "root@[fe80::1]:22")]
        [InlineData("fe80::1", "root@[fe80::1%eth0]:22")]
        // Por dirección: un nombre que resuelve a loopback (mi-portatil → 127.0.1.1) y el destino ::1 con zona.
        [InlineData("mi-portatil", "root@[::1%1]:22")]
        public async Task Ipv6ConZona_SigueSiendoSoloLectura(string configuracion, string destino)
        {
            // Arrange
            _ = DestinosSoloLectura.TryParsear(configuracion, out DestinosSoloLectura destinos, out string? error, ResolverFalso).Should().BeTrue(error);

            // Act
            ComprobacionSoloLectura comprobacion = await destinos.ComprobarAsync(Destino(destino), CancellationToken.None);
            bool? isSinRed = destinos.IsSoloLecturaSinRed(Destino(destino));

            // Assert
            _ = comprobacion.Should().Be(new ComprobacionSoloLectura(true, null), $"{destino} es la misma dirección que '{configuracion}'");
            _ = isSinRed.Should().BeTrue("ssh_status no debe decir que es escribible ni que no se sabe");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData(" , ,")]
        public void SinConfiguracion_NingunDestino(string? configuracion)
        {
            // Act
            bool isValida = DestinosSoloLectura.TryParsear(configuracion, out DestinosSoloLectura destinos, out string? error);

            // Assert
            _ = isValida.Should().BeTrue();
            _ = error.Should().BeNull();
            _ = destinos.IsVacio.Should().BeTrue();
        }

        [Theory]
        [InlineData("@prod")]
        [InlineData("prod:0")]
        [InlineData("prod:70000")]
        [InlineData("prod:abc")]
        [InlineData("[::1")]
        [InlineData("[::1]x")]
        [InlineData("root@")]
        [InlineData("prod, mal entrada")]
        // Entradas que nunca podrían coincidir con un destino al que se conecte: también impiden arrancar.
        [InlineData("root@prod@ejemplo")]
        [InlineData("prod.ejemplo:22:2222")]
        [InlineData("prod/ejemplo")]
        [InlineData("/")]
        [InlineData("ssh://prod.ejemplo")]
        [InlineData("*.prod.ejemplo")]
        [InlineData("[prod.ejemplo:22]")]
        // Sin letras ni dígitos: no son el nombre de ninguna máquina ('.' y '..' se quedan en nada sin el punto final).
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("-")]
        [InlineData("_")]
        [InlineData("root@.")]
        [InlineData("root@-:22")]
        [InlineData("prod, _")]
        public void EntradaNoValida_ErrorQueLaNombra(string configuracion)
        {
            // Act
            bool isValida = DestinosSoloLectura.TryParsear(configuracion, out _, out string? error);

            // Assert
            _ = isValida.Should().BeFalse();
            _ = error.Should().StartWith("NATIVE_SSH_SOLO_LECTURA: la entrada '").And.Contain("no es válida. Usa '[usuario@]servidor[:puerto]'");
        }

        // Lo resuelto de las entradas se recuerda (cada ssh_exec a otro destino no espera otra vez al DNS por ellas); el destino
        // se resuelve en cada llamada.
        [Fact]
        public async Task Resolucion_SeRecuerdaLaDeLasEntradasNoLaDelDestino()
        {
            // Arrange
            Dictionary<string, int> consultas = [];
            Task<IPAddress[]> Contador(string nombre, CancellationToken token)
            {
                lock (consultas)
                    consultas[nombre] = consultas.GetValueOrDefault(nombre) + 1;
                return ResolverFalso(nombre, token);
            }

            _ = DestinosSoloLectura.TryParsear("prod.ejemplo", out DestinosSoloLectura destinos, out _, Contador).Should().BeTrue();

            // Act
            bool isPrimera = await destinos.IsSoloLecturaAsync(Destino("ana@otro.ejemplo"), CancellationToken.None);
            bool isSegunda = await destinos.IsSoloLecturaAsync(Destino("ana@otro.ejemplo"), CancellationToken.None);

            // Assert
            _ = isPrimera.Should().BeFalse();
            _ = isSegunda.Should().BeFalse();
            _ = consultas.Should().BeEquivalentTo(new Dictionary<string, int> { ["prod.ejemplo"] = 1, ["otro.ejemplo"] = 2 });
        }

        // El destino no se recuerda: si su nombre pasa a apuntar a la dirección de una entrada (DNS que cambia entre dos
        // llamadas), la llamada siguiente ya lo ve. Antes se comparaba con lo resuelto hasta 5 minutos antes mientras SSH.NET
        // resolvía de nuevo al conectar.
        [Fact]
        public async Task Destino_QueCambiaDeDireccion_SeVeEnLaLlamadaSiguiente()
        {
            // Arrange
            int consultas = 0;
            Task<IPAddress[]> Cambiante(string nombre, CancellationToken _) =>
                Task.FromResult(new[] { IPAddress.Parse(Interlocked.Increment(ref consultas) == 1 ? "10.0.0.9" : "10.0.0.5") });

            _ = DestinosSoloLectura.TryParsear("10.0.0.5", out DestinosSoloLectura destinos, out _, Cambiante).Should().BeTrue();

            // Act
            ComprobacionSoloLectura antes = await destinos.ComprobarAsync(Destino("root@cambia.ejemplo"), CancellationToken.None);
            bool? isSinRed = destinos.IsSoloLecturaSinRed(Destino("root@cambia.ejemplo"));
            ComprobacionSoloLectura despues = await destinos.ComprobarAsync(Destino("root@cambia.ejemplo"), CancellationToken.None);

            // Assert
            _ = antes.Should().Be(new ComprobacionSoloLectura(false, null));
            _ = isSinRed.Should().BeNull("lo resuelto del destino no se recuerda: ssh_status no puede saberlo sin red");
            _ = despues.Should().Be(new ComprobacionSoloLectura(true, null));
        }

        // Si una entrada que coincide en usuario y puerto, o el propio destino, no se pudo resolver, no se sabe si es la misma
        // máquina: se trata como de solo lectura y se dice qué nombre no se resolvió (ante la duda, se deniega).
        [Theory]
        [InlineData("prod.ejemplo, nadie.ejemplo", "root@10.0.0.9", "nadie.ejemplo")]
        [InlineData("nadie.ejemplo", "root@otro.ejemplo", "nadie.ejemplo")]
        [InlineData("10.0.0.5", "root@nadie.ejemplo", "nadie.ejemplo")]
        [InlineData("prod.ejemplo", "root@nadie.ejemplo", "nadie.ejemplo")]
        public async Task NombreNoResuelto_SeRechazaPorSeguridadYLoNombra(string configuracion, string destino, string noResuelto)
        {
            // Arrange
            _ = DestinosSoloLectura.TryParsear(configuracion, out DestinosSoloLectura destinos, out _, ResolverFalso).Should().BeTrue();

            // Act
            ComprobacionSoloLectura comprobacion = await destinos.ComprobarAsync(Destino(destino), CancellationToken.None);

            // Assert
            _ = comprobacion.IsSoloLectura.Should().BeTrue();
            _ = comprobacion.NoResuelto.Should().Be(noResuelto);
        }

        // Una entrada que no se resuelve no afecta a los destinos que no coinciden en usuario o puerto, ni a la que ya
        // coincide por otra entrada.
        [Theory]
        [InlineData("deploy@nadie.ejemplo", "root@otro.ejemplo", false)]
        [InlineData("nadie.ejemplo:2222", "root@otro.ejemplo", false)]
        [InlineData("nadie.ejemplo, otro.ejemplo", "root@otro.ejemplo", true)]
        [InlineData("nadie.ejemplo, prod.ejemplo", "root@10.0.0.5", true)]
        public async Task NombreNoResuelto_SoloCuentaSiPodriaSerElDestino(string configuracion, string destino, bool isSoloLectura)
        {
            // Arrange
            _ = DestinosSoloLectura.TryParsear(configuracion, out DestinosSoloLectura destinos, out _, ResolverFalso).Should().BeTrue();

            // Act
            ComprobacionSoloLectura comprobacion = await destinos.ComprobarAsync(Destino(destino), CancellationToken.None);

            // Assert
            _ = comprobacion.IsSoloLectura.Should().Be(isSoloLectura);
            _ = comprobacion.NoResuelto.Should().BeNull();
        }

        // Una resolución que acaba bien pero sin ninguna dirección cuenta como no resuelta (no se sabe a qué máquina apunta): no
        // se recuerda como un éxito vacío, que no coincidiría con nada y dejaría escribir.
        [Fact]
        public async Task ResolucionSinDirecciones_CuentaComoNoResuelta()
        {
            // Arrange
            int consultas = 0;
            Task<IPAddress[]> Vacio(string nombre, CancellationToken token)
            {
                _ = Interlocked.Increment(ref consultas);
                return Task.FromResult(Array.Empty<IPAddress>());
            }

            _ = DestinosSoloLectura.TryParsear("prod.ejemplo", out DestinosSoloLectura destinos, out _, Vacio).Should().BeTrue();

            // Act
            ComprobacionSoloLectura primera = await destinos.ComprobarAsync(Destino("root@10.0.0.5"), CancellationToken.None);
            bool? isSinRed = destinos.IsSoloLecturaSinRed(Destino("root@10.0.0.5"));
            ComprobacionSoloLectura segunda = await destinos.ComprobarAsync(Destino("root@10.0.0.5"), CancellationToken.None);

            // Assert
            _ = primera.Should().Be(new ComprobacionSoloLectura(true, "prod.ejemplo", Entrada: "prod.ejemplo"));
            _ = isSinRed.Should().BeNull("no se recordó nada");
            _ = segunda.Should().Be(new ComprobacionSoloLectura(true, "prod.ejemplo", Entrada: "prod.ejemplo"));
            _ = consultas.Should().Be(2, "se vuelve a resolver");
        }

        // Un fallo pasajero del DNS no se recuerda: en cuanto el nombre se resuelve, la comparación es por sus direcciones.
        [Fact]
        public async Task FalloDeResolucion_NoSeRecuerda()
        {
            // Arrange
            int consultas = 0;
            Task<IPAddress[]> Pasajero(string nombre, CancellationToken _) =>
                Interlocked.Increment(ref consultas) == 1
                    ? Task.FromException<IPAddress[]>(new SocketException((int)SocketError.TryAgain))
                    : Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") });

            _ = DestinosSoloLectura.TryParsear("prod.ejemplo", out DestinosSoloLectura destinos, out _, Pasajero).Should().BeTrue();

            // Act
            ComprobacionSoloLectura durante = await destinos.ComprobarAsync(Destino("root@10.0.0.6"), CancellationToken.None);
            ComprobacionSoloLectura despues = await destinos.ComprobarAsync(Destino("root@10.0.0.6"), CancellationToken.None);
            ComprobacionSoloLectura laIp = await destinos.ComprobarAsync(Destino("root@10.0.0.5"), CancellationToken.None);

            // Assert
            _ = durante.Should().Be(new ComprobacionSoloLectura(true, "prod.ejemplo", Entrada: "prod.ejemplo"));
            _ = despues.Should().Be(new ComprobacionSoloLectura(false, null));
            _ = laIp.Should().Be(new ComprobacionSoloLectura(true, null));
            _ = consultas.Should().Be(2, "el fallo no se recuerda; el éxito sí");
        }

        // El plazo se cumple aunque la resolución no haga caso de la cancelación (como getaddrinfo, que no se puede cancelar):
        // se deja de esperar y se rechaza. Las comprobaciones a la vez del mismo nombre comparten la resolución; la siguiente,
        // con la anterior ya vencida, lanza otra (así se vuelve a resolver cuando el DNS responda).
        [Fact]
        public async Task ResolucionQueNoTermina_SeRechazaAlVencerElPlazoYLaSiguienteLanzaOtra()
        {
            // Arrange
            int consultas = 0;
            TaskCompletionSource<IPAddress[]> nunca = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<IPAddress[]> Colgado(string nombre, CancellationToken token)
            {
                _ = Interlocked.Increment(ref consultas);
                return nunca.Task;
            }

            _ = DestinosSoloLectura.TryParsear("prod.ejemplo", out DestinosSoloLectura destinos, out _, Colgado).Should().BeTrue();
            Stopwatch reloj = Stopwatch.StartNew();

            // Act
            ComprobacionSoloLectura[] resultados = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => destinos.ComprobarAsync(Destino("root@10.0.0.5"), CancellationToken.None))).WaitAsync(TimeSpan.FromSeconds(30));
            ComprobacionSoloLectura otraVez = await destinos.ComprobarAsync(Destino("root@10.0.0.5"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

            // Assert
            _ = resultados.Should().AllBeEquivalentTo(new ComprobacionSoloLectura(true, "prod.ejemplo", Entrada: "prod.ejemplo"));
            _ = otraVez.Should().Be(new ComprobacionSoloLectura(true, "prod.ejemplo", Entrada: "prod.ejemplo"));
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(11), "cada llamada espera como mucho el plazo y la recogida (4,5 s)");
            _ = consultas.Should().Be(2, "una sola resolución en curso por nombre para las diez a la vez y otra para la siguiente");
            nunca.SetResult([IPAddress.Parse("10.0.0.5")]);
        }

        // Con muchos nombres distintos colgados a la vez, las resoluciones en curso tienen tope: las que pasan de él no se
        // lanzan y se rechazan al momento.
        [Fact]
        public async Task ResolucionesEnCurso_TienenTope()
        {
            // Arrange
            int consultas = 0;
            TaskCompletionSource<IPAddress[]> nunca = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<IPAddress[]> Colgado(string nombre, CancellationToken token)
            {
                _ = Interlocked.Increment(ref consultas);
                return nunca.Task;
            }

            _ = DestinosSoloLectura.TryParsear("10.0.0.5", out DestinosSoloLectura destinos, out _, Colgado).Should().BeTrue();

            // Act
            ComprobacionSoloLectura[] resultados = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => destinos.ComprobarAsync(Destino($"root@nombre{i}.ejemplo"), CancellationToken.None))).WaitAsync(TimeSpan.FromSeconds(30));

            // Assert
            _ = resultados.Should().OnlyContain(r => r.IsSoloLectura && r.NoResuelto != null);
            _ = consultas.Should().Be(DestinosSoloLectura.MaxResolucionesEnCurso);
            nunca.SetResult([IPAddress.Parse("10.0.0.9")]);
        }

        // Un destino cuya resolución se queda colgada (no hace caso del plazo, como getaddrinfo con el TCP/53 colgado) no se
        // sigue esperando: al vencer su plazo deja de estar en curso, y cuando el DNS ya responde, la comprobación siguiente
        // vuelve a resolver el nombre (antes esperaba para siempre la colgada y rechazaba).
        [Fact]
        public async Task DestinoColgado_DnsRecuperado_LaSiguienteVuelveAResolver()
        {
            // Arrange
            int consultas = 0;
            TaskCompletionSource<IPAddress[]> nunca = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<IPAddress[]> Resolver(string nombre, CancellationToken token) =>
                Interlocked.Increment(ref consultas) == 1 ? nunca.Task : Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") });

            _ = DestinosSoloLectura.TryParsear("10.0.0.5", out DestinosSoloLectura destinos, out _, Resolver).Should().BeTrue();
            ComprobacionSoloLectura colgada = await destinos.ComprobarAsync(Destino("root@prod.ejemplo"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

            // Act
            ComprobacionSoloLectura siguiente = await destinos.ComprobarAsync(Destino("root@prod.ejemplo"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            nunca.SetResult([IPAddress.Parse("10.0.0.9")]);

            // Assert
            _ = colgada.Should().Be(new ComprobacionSoloLectura(true, "prod.ejemplo"));
            _ = siguiente.Should().Be(new ComprobacionSoloLectura(true, null), "el DNS ya responde: prod.ejemplo es 10.0.0.5, que es de solo lectura");
            _ = consultas.Should().Be(2);
        }

        // Lo mismo con una entrada: su resolución colgada no se sigue esperando y, con el DNS ya sano, la siguiente la resuelve.
        [Fact]
        public async Task EntradaColgada_DnsRecuperado_LaSiguienteVuelveAResolver()
        {
            // Arrange
            int consultas = 0;
            TaskCompletionSource<DireccionesPorFamilia> nunca = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<DireccionesPorFamilia> ResolverEntrada(string nombre, CancellationToken token) =>
                Interlocked.Increment(ref consultas) == 1 ? nunca.Task : Task.FromResult(new DireccionesPorFamilia([IPAddress.Parse("10.0.0.5")], []));

            _ = DestinosSoloLectura.TryParsear("prod.ejemplo", out DestinosSoloLectura destinos, out _, ResolverFalso, ResolverEntrada).Should().BeTrue();
            ComprobacionSoloLectura colgada = await destinos.ComprobarAsync(Destino("root@10.0.0.5"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

            // Act
            ComprobacionSoloLectura siguiente = await destinos.ComprobarAsync(Destino("root@10.0.0.5"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            nunca.SetResult(default);

            // Assert
            _ = colgada.Should().Be(new ComprobacionSoloLectura(true, "prod.ejemplo", Entrada: "prod.ejemplo"));
            _ = siguiente.Should().Be(new ComprobacionSoloLectura(true, null));
            _ = consultas.Should().Be(2);
        }

        // Con muchos nombres de destino colgados (más que las resoluciones que puede haber a la vez), las ranuras se liberan al
        // vencer el plazo: después, un nombre que el DNS sí resuelve se resuelve (antes, con 16 colgados, todo se rechazaba
        // por el cupo para siempre).
        [Fact]
        public async Task RanurasConNombresColgados_SeLiberanAlVencerElPlazo()
        {
            // Arrange
            TaskCompletionSource<IPAddress[]> nunca = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<IPAddress[]> Resolver(string nombre, CancellationToken token) =>
                nombre.StartsWith("colgado", StringComparison.Ordinal) ? nunca.Task : ResolverFalso(nombre, token);

            _ = DestinosSoloLectura.TryParsear("10.0.0.5", out DestinosSoloLectura destinos, out _, Resolver).Should().BeTrue();
            ComprobacionSoloLectura[] colgados = await Task.WhenAll(Enumerable.Range(0, 2 * DestinosSoloLectura.MaxResolucionesEnCurso).Select(i => destinos.ComprobarAsync(Destino($"root@colgado{i}.ejemplo"), CancellationToken.None))).WaitAsync(TimeSpan.FromSeconds(30));

            // Act
            ComprobacionSoloLectura sano = await destinos.ComprobarAsync(Destino("root@prod.ejemplo"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            nunca.SetResult([IPAddress.Parse("10.0.0.9")]);

            // Assert
            _ = colgados.Should().OnlyContain(r => r.IsSoloLectura && r.NoResuelto != null);
            _ = sano.Should().Be(new ComprobacionSoloLectura(true, null));
        }

        // Con más entradas que resoluciones a la vez y el DNS sano, la primera comprobación no rechaza: las que no caben esperan
        // su turno dentro del plazo (antes, con 20 entradas, las que pasaban de 16 se rechazaban por el cupo).
        [Fact]
        public async Task MasEntradasQueResolucionesALaVez_DnsSano_NoRechaza()
        {
            // Arrange
            int enCurso = 0;
            int maximo = 0;
            async Task<DireccionesPorFamilia> ResolverEntrada(string nombre, CancellationToken token)
            {
                int ahora = Interlocked.Increment(ref enCurso);
                InterlockedMaximo(ref maximo, ahora);
                await Task.Delay(100, token);
                _ = Interlocked.Decrement(ref enCurso);
                return new DireccionesPorFamilia([IPAddress.Parse($"10.1.{nombre.Length}.{nombre[1..nombre.IndexOf('.', StringComparison.Ordinal)]}")], []);
            }

            int entradas = DestinosSoloLectura.MaxResolucionesEnCurso + 4;
            string configuracion = string.Join(',', Enumerable.Range(0, entradas).Select(i => $"x{i}.ejemplo"));
            _ = DestinosSoloLectura.TryParsear(configuracion, out DestinosSoloLectura destinos, out _, ResolverFalso, ResolverEntrada).Should().BeTrue();

            // Act
            ComprobacionSoloLectura resultado = await destinos.ComprobarAsync(Destino("root@10.0.0.99"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

            // Assert
            _ = resultado.Should().Be(new ComprobacionSoloLectura(false, null));
            _ = maximo.Should().BeLessThanOrEqualTo(DestinosSoloLectura.MaxResolucionesEnCurso);
        }

        private static void InterlockedMaximo(ref int maximo, int valor)
        {
            int actual = Volatile.Read(ref maximo);
            while (valor > actual)
            {
                int previo = Interlocked.CompareExchange(ref maximo, valor, actual);
                if (previo == actual)
                    return;
                actual = previo;
            }
        }

        // Un nombre que es a la vez el de una entrada (de otro usuario) y el del destino: lo recordado por la entrada no se usa
        // para el destino, que se resuelve de nuevo.
        [Fact]
        public async Task Destino_ConElNombreDeUnaEntradaRecordada_SeResuelveDeNuevo()
        {
            // Arrange
            int consultas = 0;
            Task<IPAddress[]> Cambiante(string nombre, CancellationToken token) =>
                Task.FromResult(new[] { IPAddress.Parse(Interlocked.Increment(ref consultas) == 1 ? "10.0.0.9" : "10.0.0.5") });

            _ = DestinosSoloLectura.TryParsear("10.0.0.5, deploy@cambia.ejemplo", out DestinosSoloLectura destinos, out _, Cambiante).Should().BeTrue();

            // Act
            ComprobacionSoloLectura laEntrada = await destinos.ComprobarAsync(Destino("deploy@10.0.0.9"), CancellationToken.None);
            ComprobacionSoloLectura elDestino = await destinos.ComprobarAsync(Destino("root@cambia.ejemplo"), CancellationToken.None);

            // Assert
            _ = laEntrada.Should().Be(new ComprobacionSoloLectura(true, null));
            _ = elDestino.Should().Be(new ComprobacionSoloLectura(true, null), "cambia.ejemplo apunta ya a 10.0.0.5, aunque la entrada recuerde 10.0.0.9");
            _ = consultas.Should().Be(2);
        }

        // Con todos los turnos de resolución ocupados durante más de lo que se espera turno, lo que no se llegó a resolver se
        // distingue de lo que no se resolvió (fallo o plazo): el rechazo puede decir que se vuelva a intentar en unos segundos
        // en lugar de culpar al DNS.
        [Fact]
        public async Task CupoLleno_SeDistingueDeNoResuelto()
        {
            // Arrange
            TaskCompletionSource<IPAddress[]> nunca = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<IPAddress[]> Resolver(string nombre, CancellationToken token) =>
                nombre.StartsWith("colgado", StringComparison.Ordinal) ? nunca.Task : ResolverFalso(nombre, token);

            _ = DestinosSoloLectura.TryParsear("10.0.0.5, deploy@prod.ejemplo", out DestinosSoloLectura destinos, out _, Resolver).Should().BeTrue();
            ComprobacionSoloLectura fallo = await destinos.ComprobarAsync(Destino("root@nadie.ejemplo"), CancellationToken.None);
            Task<ComprobacionSoloLectura>[] colgados = [.. Enumerable.Range(0, DestinosSoloLectura.MaxResolucionesEnCurso).Select(i => destinos.ComprobarAsync(Destino($"root@colgado{i}.ejemplo"), CancellationToken.None))];

            // Act
            Task<ComprobacionSoloLectura> sinCupoTarea = destinos.ComprobarAsync(Destino("root@otro.ejemplo"), CancellationToken.None);
            Task<ComprobacionSoloLectura> entradaSinCupoTarea = destinos.ComprobarAsync(Destino("deploy@10.0.0.9"), CancellationToken.None);
            ComprobacionSoloLectura sinCupo = await sinCupoTarea.WaitAsync(TimeSpan.FromSeconds(30));
            ComprobacionSoloLectura entradaSinCupo = await entradaSinCupoTarea.WaitAsync(TimeSpan.FromSeconds(30));
            ComprobacionSoloLectura[] porPlazo = await Task.WhenAll(colgados).WaitAsync(TimeSpan.FromSeconds(30));
            nunca.SetResult([IPAddress.Parse("10.0.0.9")]);

            // Assert
            _ = sinCupo.Should().Be(new ComprobacionSoloLectura(true, "otro.ejemplo", IsCupoLleno: true));
            _ = entradaSinCupo.Should().Be(new ComprobacionSoloLectura(true, "prod.ejemplo", IsCupoLleno: true));
            _ = porPlazo.Should().OnlyContain(r => r.IsSoloLectura && r.NoResuelto != null && !r.IsCupoLleno);
            _ = fallo.Should().Be(new ComprobacionSoloLectura(true, "nadie.ejemplo", IsCupoLleno: false));
        }

        // ssh_status marca las sesiones sin esperar al DNS: true o false si ya se sabe (texto, IP, loopback o lo ya resuelto de
        // las entradas), null si depende de un nombre de entrada aún no resuelto o del nombre del destino (no se recuerda).
        [Fact]
        public async Task SinRed_SoloLoQueYaSeSabe()
        {
            // Arrange
            _ = DestinosSoloLectura.TryParsear("prod.ejemplo, deploy@10.0.0.7:2222, localhost", out DestinosSoloLectura destinos, out _, ResolverFalso).Should().BeTrue();

            // Act
            bool? isPorTexto = destinos.IsSoloLecturaSinRed(Destino("root@PROD.ejemplo"));
            bool? isOtroUsuario = destinos.IsSoloLecturaSinRed(Destino("root@10.0.0.7:2222"));
            bool? isLoopback = destinos.IsSoloLecturaSinRed(Destino("root@127.0.0.1"));
            bool? isSinResolver = destinos.IsSoloLecturaSinRed(Destino("root@10.0.0.5"));
            _ = await destinos.ComprobarAsync(Destino("root@10.0.0.5"), CancellationToken.None);
            bool? isYaResuelto = destinos.IsSoloLecturaSinRed(Destino("root@10.0.0.5"));
            _ = await destinos.ComprobarAsync(Destino("root@otro.ejemplo"), CancellationToken.None);
            bool? isResueltoDistinto = destinos.IsSoloLecturaSinRed(Destino("root@otro.ejemplo"));

            // Assert
            _ = isPorTexto.Should().BeTrue();
            _ = isLoopback.Should().BeTrue();
            _ = isSinResolver.Should().BeNull();
            _ = isOtroUsuario.Should().BeNull("prod.ejemplo aún no se ha resuelto");
            _ = isYaResuelto.Should().BeTrue();
            _ = isResueltoDistinto.Should().BeNull("el destino se resuelve en cada llamada: no se recuerda");
            _ = destinos.Entradas.ToArray().Should().Equal("prod.ejemplo", "deploy@10.0.0.7:2222", "localhost");
        }

        // Que el DNS diga que el nombre de una entrada no existe (HostNotFound) no la deja solo como texto: falla cerrado como
        // cualquier otro fallo. Con el nsswitch de systemd (resolve [!UNAVAIL=return]), un SERVFAIL o un REFUSED llegan también
        // como HostNotFound (medido), así que «no existe» no se distingue de «el DNS falla», y quien hace fallar el DNS no debe
        // poder escribir en la dirección protegida. El rechazo nombra la entrada.
        [Theory]
        [InlineData("noexiste.ejemplo", "root@otro.ejemplo", true, "noexiste.ejemplo")]
        [InlineData("noexiste.ejemplo", "root@10.0.0.5", true, "noexiste.ejemplo")]
        [InlineData("noexiste.ejemplo", "root@NOEXISTE.ejemplo.", true, null)]
        [InlineData("noexiste.ejemplo, prod.ejemplo", "root@10.0.0.5", true, null)]
        [InlineData("noexiste.ejemplo, nadie.ejemplo", "root@10.0.0.5", true, "noexiste.ejemplo")]
        [InlineData("deploy@noexiste.ejemplo", "root@10.0.0.5", false, null)]
        public async Task EntradaQueNoExiste_FallaCerrado(string configuracion, string destino, bool isSoloLectura, string? noResuelto)
        {
            // Arrange
            _ = DestinosSoloLectura.TryParsear(configuracion, out DestinosSoloLectura destinos, out string? error, ResolverFalso).Should().BeTrue(error);

            // Act
            ComprobacionSoloLectura comprobacion = await destinos.ComprobarAsync(Destino(destino), CancellationToken.None);

            // Assert
            _ = comprobacion.IsSoloLectura.Should().Be(isSoloLectura);
            _ = comprobacion.NoResuelto.Should().Be(noResuelto);
            _ = comprobacion.Entrada.Should().Be(noResuelto);
        }

        // Cualquier fallo al resolver una entrada falla cerrado: HostNotFound (NXDOMAIN o un nombre sin direcciones; con
        // nss-resolve, también SERVFAIL y REFUSED), TryAgain (SERVFAIL, REFUSED, sin respuesta o sin servidor con el módulo dns
        // de glibc), NoData, NoRecovery o un error genérico. No se sabe adónde apunta el nombre.
        [Theory]
        [InlineData(SocketError.HostNotFound)]
        [InlineData(SocketError.TryAgain)]
        [InlineData(SocketError.NoData)]
        [InlineData(SocketError.NoRecovery)]
        [InlineData(SocketError.SocketError)]
        public async Task EntradaQueNoSeResuelve_FallaCerradoSeaCualSeaElFallo(SocketError codigo)
        {
            // Arrange
            Task<IPAddress[]> Fallo(string nombre, CancellationToken _) => Task.FromException<IPAddress[]>(new SocketException((int)codigo));
            _ = DestinosSoloLectura.TryParsear("prod.ejemplo", out DestinosSoloLectura destinos, out _, Fallo).Should().BeTrue();

            // Act
            ComprobacionSoloLectura comprobacion = await destinos.ComprobarAsync(Destino("root@10.0.0.9"), CancellationToken.None);

            // Assert
            _ = comprobacion.Should().Be(new ComprobacionSoloLectura(true, "prod.ejemplo", Entrada: "prod.ejemplo"));
        }

        // Que el nombre no exista no se recuerda: si se crea, la llamada siguiente ya lo compara por sus direcciones.
        [Fact]
        public async Task EntradaQueNoExiste_FallaCerradoYNoSeRecuerda()
        {
            // Arrange
            int consultas = 0;
            Task<IPAddress[]> Creado(string nombre, CancellationToken _) =>
                Interlocked.Increment(ref consultas) == 1
                    ? Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound))
                    : Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") });

            _ = DestinosSoloLectura.TryParsear("nuevo.ejemplo", out DestinosSoloLectura destinos, out _, Creado).Should().BeTrue();

            // Act
            ComprobacionSoloLectura antes = await destinos.ComprobarAsync(Destino("root@10.0.0.5"), CancellationToken.None);
            ComprobacionSoloLectura despues = await destinos.ComprobarAsync(Destino("root@10.0.0.5"), CancellationToken.None);

            // Assert
            _ = antes.Should().Be(new ComprobacionSoloLectura(true, "nuevo.ejemplo", Entrada: "nuevo.ejemplo"));
            _ = despues.Should().Be(new ComprobacionSoloLectura(true, null));
        }

        // Una entrada que es un alias cuyo HostName no se resuelve también falla cerrado; el rechazo nombra el HostName y la
        // entrada tal como se escribió, y dice qué hacer (corregir la entrada o comprobar el DNS). Un destino que se llama como
        // su HostName coincide por texto.
        [Fact]
        public async Task EntradaQueEsAliasConHostNameQueNoSeResuelve_FallaCerradoYNombraLaEntrada()
        {
            // Arrange
            DestinosSoloLectura destinos = ConConfiguracion("fantasma");

            // Act
            ComprobacionSoloLectura otro = await destinos.ComprobarAsync(Destino("root@otro.ejemplo"), CancellationToken.None);
            ComprobacionSoloLectura porTexto = await destinos.ComprobarAsync(Destino("root@noexiste.ejemplo"), CancellationToken.None);
            string mensaje = DestinosSoloLectura.MensajeRechazo(otro, "root@otro.ejemplo:22", "ssh_exec")!;

            // Assert
            _ = otro.Should().Be(new ComprobacionSoloLectura(true, "noexiste.ejemplo", Entrada: "fantasma"));
            _ = porTexto.Should().Be(new ComprobacionSoloLectura(true, null));
            _ = mensaje.Should().Be("Rechazado: root@otro.ejemplo:22: no se pudo comprobar si es de solo lectura (no se resolvió noexiste.ejemplo, de la entrada 'fantasma' de NATIVE_SSH_SOLO_LECTURA); se rechaza por seguridad (configuración local de native_ssh, variable NATIVE_SSH_SOLO_LECTURA), así que ssh_exec no se ha ejecutado y no se ha enviado nada al servidor. ssh_read_file, ssh_search, ssh_find, ssh_list_dir, ssh_stat y ssh_set_cwd siguen disponibles. No busques otra vía para modificar el servidor: la comprobación se repite en cada llamada y pasará cuando ese nombre se resuelva. Díselo al usuario: mientras no se resuelva, esa entrada rechaza las escrituras a todos los destinos con su usuario y puerto; si está mal escrita o el nombre ya no existe, quien configura native_ssh debe corregirla o quitarla de NATIVE_SSH_SOLO_LECTURA, y si no, comprobar el DNS (o /etc/hosts) de la máquina donde corre native_ssh.");
        }

        // El rechazo por una entrada que no se resuelve la nombra tal como se escribió, con su usuario y su puerto (no solo el
        // servidor), para que se pueda encontrar en NATIVE_SSH_SOLO_LECTURA; también si es un alias.
        [Theory]
        [InlineData("deploy@nadie.ejemplo:2222", "nadie.ejemplo")]
        [InlineData("deploy@fantasma:2222", "noexiste.ejemplo")]
        public async Task EntradaConUsuarioYPuertoQueNoSeResuelve_ElRechazoLaNombraEntera(string entrada, string noResuelto)
        {
            // Arrange
            DestinosSoloLectura destinos = ConConfiguracion(entrada);

            // Act
            ComprobacionSoloLectura comprobacion = await destinos.ComprobarAsync(Destino("deploy@10.0.0.6:2222"), CancellationToken.None);
            string mensaje = DestinosSoloLectura.MensajeRechazo(comprobacion, "deploy@10.0.0.6:2222", "ssh_exec")!;

            // Assert
            _ = comprobacion.Should().Be(new ComprobacionSoloLectura(true, noResuelto, Entrada: entrada));
            _ = mensaje.Should().Contain($"(no se resolvió {noResuelto}, de la entrada '{entrada}' de NATIVE_SSH_SOLO_LECTURA)");
        }

        // Una entrada que es un alias de la configuración de ssh se compara por el HostName al que lleva (por texto y, si es un
        // nombre, por sus direcciones), conservando el usuario y el puerto de la entrada (sin ellos, cualquiera); el alias
        // como texto pedido sigue contando. El nombre del alias no se busca en el DNS: no es adonde conecta ssh.
        [Theory]
        [InlineData("prod", "root@10.0.0.5", true)]
        [InlineData("prod", "deploy@10.0.0.5:2222", true)]
        [InlineData("prod", "root@[::ffff:10.0.0.5]", true)]
        [InlineData("prod", "root@10.0.0.9", false)]
        // Un destino que se llama como la entrada y que la configuración no cambia (p. ej. un Match user): por su texto.
        [InlineData("prod", "root@prod", true)]
        [InlineData("prod", "root@otro.ejemplo", false)]
        [InlineData("deploy@prod", "deploy@10.0.0.5", true)]
        [InlineData("deploy@prod", "root@10.0.0.5", false)]
        [InlineData("prod:2222", "root@10.0.0.5:2222", true)]
        [InlineData("prod:2222", "root@10.0.0.5", false)]
        [InlineData("interno", "root@prod.ejemplo", true)]
        [InlineData("interno", "root@10.0.0.5", true)]
        [InlineData("interno", "root@otro.ejemplo", false)]
        [InlineData("fantasma", "root@noexiste.ejemplo", true)]
        [InlineData("*", "root@otro.ejemplo", true)]
        public async Task EntradaQueEsAlias_SeComparaPorSuHostName(string configuracion, string destino, bool isSoloLectura)
        {
            // Arrange
            DestinosSoloLectura destinos = ConConfiguracion(configuracion);

            // Act
            ComprobacionSoloLectura comprobacion = await destinos.ComprobarAsync(Destino(destino), CancellationToken.None);

            // Assert
            _ = comprobacion.Should().Be(new ComprobacionSoloLectura(isSoloLectura, null));
        }

        // El alias como texto pedido cuenta aunque ahora resuelva a otra máquina; uno que no está en la lista, no.
        [Theory]
        [InlineData("prod", "prod", true)]
        [InlineData("fantasma", "fantasma", true)]
        [InlineData("prod", "web", false)]
        public async Task EntradaQueEsAlias_ElTextoPedidoCuenta(string configuracion, string alias, bool isSoloLectura)
        {
            // Arrange
            DestinosSoloLectura destinos = ConConfiguracion(configuracion);

            // Act
            ComprobacionSoloLectura comprobacion = await destinos.ComprobarAsync(DestinoSsh.Resuelto("ana", "10.0.0.7", 22, null, alias), CancellationToken.None);

            // Assert
            _ = comprobacion.Should().Be(new ComprobacionSoloLectura(isSoloLectura, null));
        }

        // A ssh -G le llega la entrada como se escribió, con su usuario y su puerto si los lleva (como -l y -p); '*' no se
        // resuelve.
        [Fact]
        public async Task EntradaQueEsAlias_SeResuelveConSuUsuarioYPuerto()
        {
            // Arrange
            List<string> llamadas = [];
            string? Registrar(string? usuario, string servidor, int? puerto)
            {
                lock (llamadas)
                    llamadas.Add($"{usuario}|{servidor}|{puerto}");
                return null;
            }

            DestinosSoloLectura destinos = ConConfiguracion("deploy@Prod:2222, web, *", Registrar);

            // Act
            _ = await destinos.ComprobarAsync(Destino("root@10.0.0.9"), CancellationToken.None);

            // Assert
            _ = llamadas.Should().BeEquivalentTo("deploy|Prod|2222", "|web|");
        }

        // Si ssh -G no puede leer una entrada (plazo, salida repetida...), no se sabe a qué máquina lleva: falla cerrado con un
        // mensaje que lo dice, salvo que el destino coincida con otra entrada.
        [Fact]
        public async Task EntradaQueSshNoPuedeLeer_FallaCerrado()
        {
            // Arrange
            DestinosSoloLectura destinos = ConConfiguracion("mal, prod");

            // Act
            ComprobacionSoloLectura otro = await destinos.ComprobarAsync(Destino("root@10.0.0.9"), CancellationToken.None);
            ComprobacionSoloLectura laOtraEntrada = await destinos.ComprobarAsync(Destino("root@10.0.0.5"), CancellationToken.None);
            string mensaje = DestinosSoloLectura.MensajeRechazo(otro, "root@10.0.0.9:22", "ssh_exec")!;

            // Assert
            _ = otro.Should().Be(new ComprobacionSoloLectura(true, "mal", IsErrorConfiguracion: true));
            _ = laOtraEntrada.Should().Be(new ComprobacionSoloLectura(true, null));
            _ = mensaje.Should().Contain("no se pudo leer la entrada mal con la configuración de ssh (ssh -G)");
        }

        // La comprobación de la sesión justo antes de enviar (la dirección conectada) y ssh_status usan el mismo HostName.
        [Fact]
        public async Task EntradaQueEsAlias_ConexionYSinRed()
        {
            // Arrange
            DestinosSoloLectura destinos = ConConfiguracion("prod");

            // Act
            ComprobacionSoloLectura conexion = await destinos.ComprobarConexionAsync(Destino("root@ventana.ejemplo"), new ExtremoConectado(IPAddress.Parse("10.0.0.5")), CancellationToken.None);
            ComprobacionSoloLectura otraConexion = await destinos.ComprobarConexionAsync(Destino("root@ventana.ejemplo"), new ExtremoConectado(IPAddress.Parse("10.0.0.9")), CancellationToken.None);
            bool? isSinRed = destinos.IsSoloLecturaSinRed(Destino("root@10.0.0.5"));
            bool? isSinRedOtro = destinos.IsSoloLecturaSinRed(Destino("root@10.0.0.9"));

            // Assert
            _ = conexion.Should().Be(new ComprobacionSoloLectura(true, null, DireccionConectada: IPAddress.Parse("10.0.0.5")));
            _ = otraConexion.Should().Be(new ComprobacionSoloLectura(false, null));
            _ = isSinRed.Should().BeTrue();
            _ = isSinRedOtro.Should().BeFalse();
        }

        // Entrada prod.ejemplo resuelta por familia con 'porFamilia' (y, si se da, un /etc/hosts con 'hosts'); el destino es una
        // IP escrita tal cual.
        private static async Task<ComprobacionSoloLectura> ComprobarPorFamiliaAsync(DireccionesPorFamilia porFamilia, string destino, string? hosts = null)
        {
            string? rutaHosts = hosts is null ? null : Path.GetTempFileName();
            try
            {
                if (rutaHosts is not null)
                    await File.WriteAllTextAsync(rutaHosts, hosts);

                _ = DestinosSoloLectura.TryParsear("prod.ejemplo", out DestinosSoloLectura destinos, out _, ResolverFalso, (_, _) => Task.FromResult(porFamilia), rutaHosts).Should().BeTrue();
                return await destinos.ComprobarAsync(Destino(destino), CancellationToken.None);
            }
            finally
            {
                if (rutaHosts is not null)
                    File.Delete(rutaHosts);
            }
        }

        private static readonly ComprobacionSoloLectura NoResuelta = new(true, "prod.ejemplo", Entrada: "prod.ejemplo");

        // B1 de la prefusión-ter: si el DNS falla en una sola familia (SERVFAIL, REFUSED, NXDOMAIN o sin respuesta solo para
        // AAAA, o solo para A), la entrada no se resuelve y se rechaza, también la dirección de la familia que falló (que con
        // AF_UNSPEC no se comparaba: getaddrinfo daba la otra como si fuera todo) y cualquier otro destino que haya que
        // comparar por dirección.
        [Theory]
        [InlineData("10.0.0.5", null, "root@[fd00::5]:22")]
        [InlineData("10.0.0.5", null, "root@10.0.0.6")]
        [InlineData(null, "fd00::5", "root@10.0.0.5")]
        [InlineData(null, "fd00::5", "root@[fd00::6]:22")]
        [InlineData(null, null, "root@10.0.0.5")]
        public async Task FalloEnUnaFamilia_LaEntradaNoSeResuelve(string? ipv4, string? ipv6, string destino)
        {
            // Arrange
            DireccionesPorFamilia porFamilia = new(ipv4 is null ? null : [IPAddress.Parse(ipv4)], ipv6 is null ? null : [IPAddress.Parse(ipv6)]);

            // Act
            ComprobacionSoloLectura comprobacion = await ComprobarPorFamiliaAsync(porFamilia, destino);

            // Assert
            _ = comprobacion.Should().Be(NoResuelta);
        }

        // B1 fuera de Linux (prefusión-quater), con un resolutor por familia que simula cada sistema: en Windows y en macOS
        // la entrada también se resuelve por familia y la AAAA que falla lanza (o da null); la IPv6 de la máquina protegida
        // se rechaza (con las dos familias juntas, AF_UNSPEC daba solo la A como si fuera todo y la IPv6 escribía). NoData:
        // en Windows es «sin direcciones» (vacío) y solo cuenta la IPv4; en macOS se junta con «no existe» (fallo).
        [Theory]
        [InlineData("lanza", "root@[fd00::5]:22", true)]
        [InlineData("lanza", "root@10.0.0.6", true)]
        [InlineData("null", "root@[fd00::5]:22", true)]
        [InlineData("nodata-windows", "root@[fd00::5]:22", false)]
        [InlineData("nodata-windows", "root@10.0.0.5", true)]
        [InlineData("nodata-windows", "root@10.0.0.6", false)]
        public async Task SistemaSimulado_FalloSoloEnAaaa(string aaaa, string destino, bool isSoloLectura)
        {
            // Arrange
            IPAddress[]? Familia(string nombre, AddressFamily familia) =>
                familia == AddressFamily.InterNetwork ? [IPAddress.Parse("10.0.0.5")]
                : aaaa == "lanza" ? throw new SocketException((int)SocketError.TryAgain)
                : aaaa == "null" ? null
                : [];
            _ = DestinosSoloLectura.TryParsear("prod.ejemplo", out DestinosSoloLectura destinos, out _, ResolverFalso, (nombre, plazo) => ResolucionPorFamilia.ResolverAsync(nombre, Familia, plazo)).Should().BeTrue();

            // Act
            ComprobacionSoloLectura comprobacion = await destinos.ComprobarAsync(Destino(destino), CancellationToken.None);

            // Assert
            _ = comprobacion.Should().Be(isSoloLectura && aaaa != "nodata-windows" ? NoResuelta : new ComprobacionSoloLectura(isSoloLectura, null));
        }

        // Una familia sin direcciones (NODATA: un host solo IPv4 o solo IPv6) es legítima: la entrada vale por la otra y los
        // demás destinos no se bloquean. Sin direcciones en ninguna de las dos, no se resuelve.
        [Theory]
        [InlineData("10.0.0.5", "", "root@10.0.0.5", true)]
        [InlineData("10.0.0.5", "", "root@10.0.0.6", false)]
        [InlineData("10.0.0.5", "", "root@[fd00::5]:22", false)]
        [InlineData("", "fd00::5", "root@[fd00::5]:22", true)]
        [InlineData("", "fd00::5", "root@10.0.0.5", false)]
        [InlineData("10.0.0.5", "fd00::5", "root@[fd00::5]:22", true)]
        public async Task FamiliaSinDirecciones_EsLegitima(string ipv4, string ipv6, string destino, bool isSoloLectura)
        {
            // Arrange
            DireccionesPorFamilia porFamilia = new(ipv4.Length == 0 ? [] : [IPAddress.Parse(ipv4)], ipv6.Length == 0 ? [] : [IPAddress.Parse(ipv6)]);

            // Act
            ComprobacionSoloLectura comprobacion = await ComprobarPorFamiliaAsync(porFamilia, destino);

            // Assert
            _ = comprobacion.Should().Be(new ComprobacionSoloLectura(isSoloLectura, null));
        }

        [Fact]
        public async Task SinDireccionesEnNingunaFamilia_NoSeResuelve()
        {
            // Act
            ComprobacionSoloLectura comprobacion = await ComprobarPorFamiliaAsync(new DireccionesPorFamilia([], []), "root@10.0.0.6");

            // Assert
            _ = comprobacion.Should().Be(NoResuelta);
        }

        // Un nombre que está en /etc/hosts: con 'files dns' la familia que no está en el fichero la pregunta el DNS, que dice
        // que no existe (medido: getaddrinfo AF_INET6 da EAI_NONAME para un nombre solo IPv4 de /etc/hosts). Ese fallo no
        // bloquea: la máquina usa lo de /etc/hosts. Las direcciones del fichero cuentan siempre, también si el DNS da otras.
        [Theory]
        [InlineData("10.0.0.5 prod.ejemplo\n", "10.0.0.5", null, "root@10.0.0.5", true)]
        [InlineData("10.0.0.5 prod.ejemplo\n", "10.0.0.5", null, "root@10.0.0.6", false)]
        [InlineData("10.0.0.5\tlocal  PROD.ejemplo # maquina protegida\n", null, null, "root@10.0.0.6", false)]
        [InlineData("10.0.0.5\tlocal  PROD.ejemplo # maquina protegida\n", null, null, "root@10.0.0.5", true)]
        [InlineData("fd00::5 prod.ejemplo\n", null, null, "root@[fd00::5]:22", true)]
        [InlineData("10.0.0.7 prod.ejemplo\n", "10.0.0.5", "", "root@10.0.0.7", true)]
        [InlineData("10.0.0.7 prod.ejemplo\n", "10.0.0.5", "", "root@10.0.0.5", true)]
        public async Task NombreEnHosts_ValenSusDireccionesYElFalloDeUnaFamiliaNoBloquea(string hosts, string? ipv4, string? ipv6, string destino, bool isSoloLectura)
        {
            // Arrange
            DireccionesPorFamilia porFamilia = new(ipv4 is null ? null : [IPAddress.Parse(ipv4)], ipv6 is null ? null : ipv6.Length == 0 ? [] : [IPAddress.Parse(ipv6)]);

            // Act
            ComprobacionSoloLectura comprobacion = await ComprobarPorFamiliaAsync(porFamilia, destino, hosts);

            // Assert
            _ = comprobacion.Should().Be(new ComprobacionSoloLectura(isSoloLectura, null));
        }

        // Solo se recuerdan las resoluciones completas: lo tolerado por estar el nombre en /etc/hosts (una familia falló) se
        // vuelve a resolver en cada comprobación, como un fallo; lo completo, no (5 minutos).
        [Theory]
        [InlineData(true, 2)]
        [InlineData(false, 1)]
        public async Task ResueltoConTolerancia_NoSeRecuerda(bool isFalloIpv6, int consultasEsperadas)
        {
            // Arrange
            int consultas = 0;
            Task<DireccionesPorFamilia> Contador(string nombre, CancellationToken plazo)
            {
                _ = Interlocked.Increment(ref consultas);
                return Task.FromResult(new DireccionesPorFamilia([IPAddress.Parse("10.0.0.5")], isFalloIpv6 ? null : []));
            }

            string ruta = Path.GetTempFileName();
            await File.WriteAllTextAsync(ruta, "10.0.0.5 prod.ejemplo\n");
            _ = DestinosSoloLectura.TryParsear("prod.ejemplo", out DestinosSoloLectura destinos, out _, ResolverFalso, Contador, ruta).Should().BeTrue();

            try
            {
                // Act
                ComprobacionSoloLectura primera = await destinos.ComprobarAsync(Destino("root@10.0.0.6"), CancellationToken.None);
                bool? isSinRed = destinos.IsSoloLecturaSinRed(Destino("root@10.0.0.6"));
                ComprobacionSoloLectura segunda = await destinos.ComprobarAsync(Destino("root@10.0.0.6"), CancellationToken.None);

                // Assert
                _ = primera.Should().Be(new ComprobacionSoloLectura(false, null));
                _ = segunda.Should().Be(primera);
                _ = isSinRed.Should().Be(isFalloIpv6 ? null : false);
                _ = consultas.Should().Be(consultasEsperadas);
            }
            finally
            {
                File.Delete(ruta);
            }
        }

        // Una familia que no responde (getaddrinfo colgado, no se puede cancelar) cuenta como fallida al vencer el plazo de
        // 3 s, que cubre las dos consultas: la entrada falla cerrado, salvo que el nombre esté en /etc/hosts (entonces vale
        // lo resuelto más lo del fichero). En los dos casos la comprobación no espera a la familia colgada.
        [Theory]
        [InlineData(true, "root@10.0.0.6", false)]
        [InlineData(true, "root@10.0.0.5", true)]
        [InlineData(false, "root@10.0.0.6", true)]
        public async Task FamiliaQueNoResponde_FallaAlVencerElPlazoSalvoEnHosts(bool isEnHosts, string destino, bool isSoloLectura)
        {
            // Arrange
            using ManualResetEventSlim soltar = new(false);
            IPAddress[]? Ipv6Colgada(string nombre, AddressFamily familia)
            {
                if (familia == AddressFamily.InterNetworkV6)
                    _ = soltar.Wait(TimeSpan.FromSeconds(30));
                return familia == AddressFamily.InterNetwork ? [IPAddress.Parse("10.0.0.5")] : null;
            }

            string ruta = Path.GetTempFileName();
            await File.WriteAllTextAsync(ruta, isEnHosts ? "10.0.0.5 prod.ejemplo\n" : "10.0.0.8 otro.ejemplo\n");
            _ = DestinosSoloLectura.TryParsear("prod.ejemplo", out DestinosSoloLectura destinos, out _, ResolverFalso, (nombre, plazo) => ResolucionPorFamilia.ResolverAsync(nombre, Ipv6Colgada, plazo), ruta).Should().BeTrue();
            Stopwatch reloj = Stopwatch.StartNew();

            try
            {
                // Act
                ComprobacionSoloLectura comprobacion = await destinos.ComprobarAsync(Destino(destino), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

                // Assert
                _ = comprobacion.Should().Be(isSoloLectura && !isEnHosts ? NoResuelta : new ComprobacionSoloLectura(isSoloLectura, null));
                _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(6));
            }
            finally
            {
                soltar.Set();
                File.Delete(ruta);
            }
        }

        // Un comentario, otro nombre que lo contiene o un fichero que no existe no son estar en /etc/hosts: el fallo de una
        // familia sigue fallando cerrado.
        [Theory]
        [InlineData("# 10.0.0.5 prod.ejemplo\n10.0.0.8 prodx.ejemplo prod.ejemplo.com\n")]
        [InlineData("10.0.0.8 otro.ejemplo #prod.ejemplo\n")]
        [InlineData("10.0.0.8 otro.ejemplo # prod.ejemplo\n")]
        [InlineData("prod.ejemplo 10.0.0.5\n")]
        [InlineData("10.1 prod.ejemplo\n")]
        [InlineData("10.0.0.5 prod.ejemplo.\n")]
        [InlineData("fe80::1%lo prod.ejemplo\n")]
        [InlineData("10.0.0.5 prod.ejemplo")]
        [InlineData(" 10.0.0.5 prod.ejemplo\n")]
        [InlineData("10.0.0.5 prod.ejemplo # máquina protegida\n")]
        [InlineData("10.0.0.5 prod.ejemplo\n fd00::6 prod.ejemplo\n")]
        public async Task NombreQueNoEstaEnHosts_ElFalloDeUnaFamiliaFallaCerrado(string hosts)
        {
            // Act
            ComprobacionSoloLectura comprobacion = await ComprobarPorFamiliaAsync(new DireccionesPorFamilia([IPAddress.Parse("10.0.0.5")], null), "root@10.0.0.6", hosts);

            // Assert
            _ = comprobacion.Should().Be(NoResuelta);
        }

        // B1 de la prefusión-quinquies: un fichero hosts que empieza por la marca UTF-8 (o es UTF-16) no da la tolerancia; con
        // la AAAA fallida, la IPv6 de la máquina protegida escrita tal cual se rechaza.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task HostsConMarcaDeOrden_AaaaFallida_LaIpv6SeRechaza(bool isUtf16)
        {
            // Arrange
            byte[] contenido = isUtf16 ? [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("10.0.0.5 prod.ejemplo\n")] : [0xEF, 0xBB, 0xBF, .. "10.0.0.5 prod.ejemplo\n"u8];
            string ruta = Path.GetTempFileName();
            await File.WriteAllBytesAsync(ruta, contenido);
            _ = DestinosSoloLectura.TryParsear("prod.ejemplo", out DestinosSoloLectura destinos, out _, ResolverFalso, (_, _) => Task.FromResult(new DireccionesPorFamilia([IPAddress.Parse("10.0.0.5")], null)), ruta).Should().BeTrue();

            try
            {
                // Act
                ComprobacionSoloLectura comprobacion = await destinos.ComprobarAsync(Destino("root@[fd00::5]:22"), CancellationToken.None);

                // Assert
                _ = comprobacion.Should().Be(NoResuelta);
            }
            finally
            {
                File.Delete(ruta);
            }
        }

        // Con DNS real (/etc/hosts): localhost cubre 127.0.0.1.
        [Fact]
        public async Task Localhost_CubreSuDireccion()
        {
            // Arrange
            _ = DestinosSoloLectura.TryParsear("localhost", out DestinosSoloLectura destinos, out _).Should().BeTrue();

            // Act & Assert
            _ = (await destinos.IsSoloLecturaAsync(Destino("ana@127.0.0.1:22042"), CancellationToken.None)).Should().BeTrue();
        }
    }
}
