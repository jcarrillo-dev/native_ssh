using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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

        private static async Task<bool> EsSoloLecturaAsync(string configuracion, string destino)
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
            _ = (await EsSoloLecturaAsync(configuracion, destino)).Should().Be(isSoloLectura);

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
            bool? sinRed = destinos.IsSoloLecturaSinRed(Destino(destino));

            // Assert
            _ = comprobacion.Should().Be(new ComprobacionSoloLectura(true, null), $"{destino} es la misma dirección que '{configuracion}'");
            _ = sinRed.Should().BeTrue("ssh_status no debe decir que es escribible ni que no se sabe");
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
            bool primera = await destinos.IsSoloLecturaAsync(Destino("ana@otro.ejemplo"), CancellationToken.None);
            bool segunda = await destinos.IsSoloLecturaAsync(Destino("ana@otro.ejemplo"), CancellationToken.None);

            // Assert
            _ = primera.Should().BeFalse();
            _ = segunda.Should().BeFalse();
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
            bool? sinRed = destinos.IsSoloLecturaSinRed(Destino("root@cambia.ejemplo"));
            ComprobacionSoloLectura despues = await destinos.ComprobarAsync(Destino("root@cambia.ejemplo"), CancellationToken.None);

            // Assert
            _ = antes.Should().Be(new ComprobacionSoloLectura(false, null));
            _ = sinRed.Should().BeNull("lo resuelto del destino no se recuerda: ssh_status no puede saberlo sin red");
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
            bool? sinRed = destinos.IsSoloLecturaSinRed(Destino("root@10.0.0.5"));
            ComprobacionSoloLectura segunda = await destinos.ComprobarAsync(Destino("root@10.0.0.5"), CancellationToken.None);

            // Assert
            _ = primera.Should().Be(new ComprobacionSoloLectura(true, "prod.ejemplo"));
            _ = sinRed.Should().BeNull("no se recordó nada");
            _ = segunda.Should().Be(new ComprobacionSoloLectura(true, "prod.ejemplo"));
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
            _ = durante.Should().Be(new ComprobacionSoloLectura(true, "prod.ejemplo"));
            _ = despues.Should().Be(new ComprobacionSoloLectura(false, null));
            _ = laIp.Should().Be(new ComprobacionSoloLectura(true, null));
            _ = consultas.Should().Be(2, "el fallo no se recuerda; el éxito sí");
        }

        // El plazo de 3 s se cumple aunque la resolución no haga caso de la cancelación (getaddrinfo no se puede cancelar):
        // se deja de esperar y se rechaza. Las llamadas siguientes al mismo nombre esperan esa misma resolución en lugar de
        // empezar otra (no se acumulan hilos bloqueados).
        [Fact]
        public async Task ResolucionQueNoTermina_SeRechazaAlVencerElPlazoSinLanzarOtra()
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
            _ = resultados.Should().AllBeEquivalentTo(new ComprobacionSoloLectura(true, "prod.ejemplo"));
            _ = otraVez.Should().Be(new ComprobacionSoloLectura(true, "prod.ejemplo"));
            _ = reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(9), "cada llamada espera como mucho 3 s");
            _ = consultas.Should().Be(1, "una sola resolución en curso por nombre");
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

        // Con el cupo de resoluciones lleno, lo que no se llegó a resolver se distingue de lo que no se resolvió (fallo o
        // plazo): el rechazo puede decir que se vuelva a intentar en unos segundos en lugar de culpar al DNS.
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
            ComprobacionSoloLectura sinCupo = await destinos.ComprobarAsync(Destino("root@otro.ejemplo"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            ComprobacionSoloLectura entradaSinCupo = await destinos.ComprobarAsync(Destino("deploy@10.0.0.9"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
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
            bool? porTexto = destinos.IsSoloLecturaSinRed(Destino("root@PROD.ejemplo"));
            bool? otroUsuario = destinos.IsSoloLecturaSinRed(Destino("root@10.0.0.7:2222"));
            bool? loopback = destinos.IsSoloLecturaSinRed(Destino("root@127.0.0.1"));
            bool? sinResolver = destinos.IsSoloLecturaSinRed(Destino("root@10.0.0.5"));
            _ = await destinos.ComprobarAsync(Destino("root@10.0.0.5"), CancellationToken.None);
            bool? yaResuelto = destinos.IsSoloLecturaSinRed(Destino("root@10.0.0.5"));
            _ = await destinos.ComprobarAsync(Destino("root@otro.ejemplo"), CancellationToken.None);
            bool? resueltoDistinto = destinos.IsSoloLecturaSinRed(Destino("root@otro.ejemplo"));

            // Assert
            _ = porTexto.Should().BeTrue();
            _ = loopback.Should().BeTrue();
            _ = sinResolver.Should().BeNull();
            _ = otroUsuario.Should().BeNull("prod.ejemplo aún no se ha resuelto");
            _ = yaResuelto.Should().BeTrue();
            _ = resueltoDistinto.Should().BeNull("el destino se resuelve en cada llamada: no se recuerda");
            _ = destinos.Entradas.Should().Equal("prod.ejemplo", "deploy@10.0.0.7:2222", "localhost");
        }

        // El DNS dice que el nombre de una entrada no existe (NXDOMAIN, o un nombre sin direcciones: en Linux los dos llegan
        // como HostNotFound, medido): la entrada no es ninguna dirección, así que solo coincide como texto y no hace fallar a
        // los demás destinos. Antes se rechazaba todo destino con su usuario y puerto («no se resolvió …»).
        [Theory]
        [InlineData("noexiste.ejemplo", "root@otro.ejemplo", false)]
        [InlineData("noexiste.ejemplo", "root@10.0.0.5", false)]
        [InlineData("noexiste.ejemplo", "root@NOEXISTE.ejemplo.", true)]
        [InlineData("noexiste.ejemplo, prod.ejemplo", "root@10.0.0.5", true)]
        [InlineData("noexiste.ejemplo, nadie.ejemplo", "root@10.0.0.5", true)]
        public async Task EntradaQueNoExiste_SoloCoincideComoTexto(string configuracion, string destino, bool isSoloLectura)
        {
            // Arrange
            _ = DestinosSoloLectura.TryParsear(configuracion, out DestinosSoloLectura destinos, out string? error, ResolverFalso).Should().BeTrue(error);

            // Act
            ComprobacionSoloLectura comprobacion = await destinos.ComprobarAsync(Destino(destino), CancellationToken.None);

            // Assert
            _ = comprobacion.IsSoloLectura.Should().Be(isSoloLectura);
            _ = comprobacion.NoResuelto.Should().Be(configuracion.Contains("nadie", StringComparison.Ordinal) ? "nadie.ejemplo" : null);
        }

        // Cualquier otro fallo del DNS sigue fallando cerrado: TryAgain (SERVFAIL, REFUSED, sin respuesta o sin servidor, medido
        // en Linux), NoData (en Linux no llega: un nombre sin direcciones da HostNotFound; donde llegue, no consta que el
        // nombre no exista), NoRecovery o un error genérico. No se sabe adónde apunta el nombre.
        [Theory]
        [InlineData(SocketError.TryAgain)]
        [InlineData(SocketError.NoData)]
        [InlineData(SocketError.NoRecovery)]
        [InlineData(SocketError.SocketError)]
        public async Task EntradaConFalloQueNoDiceQueNoExiste_FallaCerrado(SocketError codigo)
        {
            // Arrange
            Task<IPAddress[]> Fallo(string nombre, CancellationToken _) => Task.FromException<IPAddress[]>(new SocketException((int)codigo));
            _ = DestinosSoloLectura.TryParsear("prod.ejemplo", out DestinosSoloLectura destinos, out _, Fallo).Should().BeTrue();

            // Act
            ComprobacionSoloLectura comprobacion = await destinos.ComprobarAsync(Destino("root@10.0.0.9"), CancellationToken.None);

            // Assert
            _ = comprobacion.Should().Be(new ComprobacionSoloLectura(true, "prod.ejemplo"));
        }

        // Que el nombre no exista no se recuerda: si se crea, la llamada siguiente ya lo compara por sus direcciones.
        [Fact]
        public async Task EntradaQueNoExiste_NoSeRecuerda()
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
            _ = antes.Should().Be(new ComprobacionSoloLectura(false, null));
            _ = despues.Should().Be(new ComprobacionSoloLectura(true, null));
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
        [InlineData("fantasma", "root@otro.ejemplo", false)]
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
            bool? sinRed = destinos.IsSoloLecturaSinRed(Destino("root@10.0.0.5"));
            bool? sinRedOtro = destinos.IsSoloLecturaSinRed(Destino("root@10.0.0.9"));

            // Assert
            _ = conexion.Should().Be(new ComprobacionSoloLectura(true, null, DireccionConectada: IPAddress.Parse("10.0.0.5")));
            _ = otraConexion.Should().Be(new ComprobacionSoloLectura(false, null));
            _ = sinRed.Should().BeTrue();
            _ = sinRedOtro.Should().BeFalse();
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
