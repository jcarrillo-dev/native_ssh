using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using NativeSsh.Comun;
using NativeSsh.Sesion;
using JCarrillo.AOT.Core.Colecciones.Pooled.Ref;
// El SDK Web importa Microsoft.AspNetCore.Http (usings implícitos), que también define ConnectionInfo.
using ConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace NativeSsh.Ssh
{
    // Claves privadas (CI3): las de destino.Identidades (IdentityFile de la configuración de ssh) o, si no las hay, las de
    // NATIVE_SSH_IDENTIDADES. Una clave cifrada se abre con la passphrase de NATIVE_SSH_PASSPHRASE o del fichero de
    // NATIVE_SSH_PASSPHRASE_FICHERO (con las mismas comprobaciones que el del token HTTP: FicheroSecreto), nunca con algo que
    // venga del cliente. La passphrase solo se lee cuando hace falta (una clave sin cifrar no la necesita), no se guarda y no
    // aparece en ningún mensaje ni log. Cada clave abierta se guarda por su ruta y se reutiliza. La que no se pudo abrir se
    // reintenta cuando cambia su fichero o el de la passphrase (fecha, tamaño o permisos): arreglarla no exige reiniciar
    // native_ssh, y mientras nada cambie su motivo se repite sin volver a abrirla (bcrypt cuesta casi un segundo) ni a
    // avisar en el log. Las claves se abren fuera del candado: una lenta no retiene las conexiones a otros destinos.
    public sealed class FabricaConexionSsh(OpcionesNativeSsh opciones, VerificadorHostConocido verificador, ILogger<FabricaConexionSsh> logger) : IFabricaConexion, IDisposable
    {
        public const string VariablePassphrase = VariablesSecretas.Passphrase;
        public const string VariableFicheroPassphrase = "NATIVE_SSH_PASSPHRASE_FICHERO";

        private static readonly DescripcionSecreto DescripcionPassphrase = new("de la passphrase de las claves", "la passphrase", "una passphrase", VariableFicheroPassphrase, VariablePassphrase);

        private readonly Lock _candado = new();
        private readonly Dictionary<string, PrivateKeyFile> _claves = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FalloClave> _fallos = new(StringComparer.Ordinal);

        // Lo que identifica una versión de un fichero sin leerlo: si cambia, se vuelve a intentar.
        private readonly record struct FirmaFichero(DateTime Modificacion, long Longitud, int Permisos);

        private sealed record FalloClave(FirmaFichero Clave, FirmaFichero? Passphrase, string Motivo);

        public async Task<IConexionSsh> ConectarAsync(DestinoSsh destino, CancellationToken cancellationToken)
        {
            AuthenticationMethod[] metodos = CrearMetodos(destino, out string origen, out string detalle);

            // El servidor se resuelve aquí y SSH.NET recibe la dirección ya resuelta (un literal, que no vuelve a preguntar al
            // DNS): la dirección de cada intento es exactamente el extremo remoto del socket (IConexionSsh.DireccionRemota, que
            // el modo solo lectura compara con sus entradas aunque el nombre pase después a apuntar a otra máquina). Se prueban
            // en el orden del resolutor, como hacía SSH.NET con el nombre (DnsEndPoint): si una no acepta la conexión TCP
            // (SocketException), la siguiente; cualquier otro fallo (clave del host, autenticación, plazo) termina ahí. El
            // known_hosts se sigue comprobando por el nombre del destino.
            IPAddress[] direcciones = await ResolverServidorAsync(destino.Servidor, cancellationToken).ConfigureAwait(false);
            for (int i = 0; ; i++)
            {
                bool isUltima = i == direcciones.Length - 1;
                try
                {
                    return await ConectarADireccionAsync(destino, direcciones[i], metodos, origen, detalle, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException) when (!isUltima)
                {
                    // Esa dirección no acepta la conexión: la siguiente.
                }
            }
        }

        // Un literal se usa tal cual (IPv6 con zona incluida); un nombre, con el resolutor del sistema (DNS o /etc/hosts), como
        // lo resolvía SSH.NET: si no se resuelve, la SocketException de siempre.
        private static async Task<IPAddress[]> ResolverServidorAsync(string servidor, CancellationToken cancellationToken)
        {
            if (IPAddress.TryParse(servidor, out IPAddress? literal))
                return [literal];

            IPAddress[] direcciones = await Dns.GetHostAddressesAsync(servidor, cancellationToken).ConfigureAwait(false);
            return direcciones.Length > 0 ? direcciones : throw new SocketException((int)SocketError.HostNotFound);
        }

        private async Task<IConexionSsh> ConectarADireccionAsync(DestinoSsh destino, IPAddress direccion, AuthenticationMethod[] metodos, string origen, string detalle, CancellationToken cancellationToken)
        {
            ConnectionInfo info = new(direccion.ToString(), destino.Puerto, destino.Usuario, metodos)
            {
                Timeout = opciones.TimeoutConexion,
                RetryAttempts = 1
            };
            HashSet<string> tiposConocidos = verificador.ObtenerTiposClave(destino.Servidor, destino.Puerto);
            FiltrarAlgoritmosHost(info, tiposConocidos);
            SshClient cliente = new(info) { KeepAliveInterval = opciones.IntervaloKeepAlive };

            string? rechazoHost = null;
            cliente.HostKeyReceived += (_, e) =>
            {
                string tipo = VerificadorHostConocido.LeerTipo(e.HostKey) ?? e.HostKeyName;
                ResultadoHostEnum resultado = verificador.Verificar(destino.Servidor, destino.Puerto, tipo, e.HostKey);
                e.CanTrust = resultado == ResultadoHostEnum.Conocido;
                if (!e.CanTrust)
                    rechazoHost = DescribirRechazo(destino, resultado, tipo, e.FingerPrintSHA256);
            };

            try
            {
                await cliente.ConnectAsync(cancellationToken).ConfigureAwait(false);
                logger.Conectado(destino.Clave);
                return new ConexionSshNet(cliente, direccion);
            }
            catch (Exception ex)
            {
                cliente.Dispose();
                if (rechazoHost is not null)
                    throw new NativeSshException(rechazoHost, ex);
                if (ex is SshAuthenticationException)
                    throw new NativeSshException($"Autenticación rechazada en {destino.Clave} ({ex.Message}). Revisa {origen} o NATIVE_SSH_PASSWORD.{detalle}", ex);
                throw;
            }
        }

        public void Dispose()
        {
            lock (_candado)
            {
                foreach (PrivateKeyFile clave in _claves.Values)
                    clave.Dispose();
                _claves.Clear();
                _fallos.Clear();
            }
        }

        // Los métodos de autenticación del destino (clave y contraseña), con el origen de las claves y los motivos de las que
        // no se pudieron cargar (detalle) para los mensajes de error; lanza si no hay ninguno. Síncrono, antes del primer
        // await de ConectarAsync: las claves y los motivos van en listas rentadas (con try/finally, porque se pasan por ref).
        private AuthenticationMethod[] CrearMetodos(DestinoSsh destino, out string origen, out string detalle)
        {
            bool isDeConfiguracion = destino.Identidades is not null;
            string[] rutas = destino.Identidades ?? opciones.RutasIdentidad;
            origen = isDeConfiguracion ? "IdentityFile de la configuración de ssh" : "NATIVE_SSH_IDENTIDADES";
            using PooledListRef<AuthenticationMethod> metodos = new();
            PooledListRef<PrivateKeyFile> claves = new();
            PooledListRef<string> motivos = new();
            try
            {
                CargarClaves(rutas, ref claves, ref motivos);
                if (claves.Tamaño > 0)
                    metodos.Add(new PrivateKeyAuthenticationMethod(destino.Usuario, [.. claves.Span]));
                detalle = motivos.Tamaño == 0 ? "" : $" {string.Join(" ", motivos.Span)}";
            }
            finally
            {
                motivos.Dispose();
                claves.Dispose();
            }

            if (opciones.Contrasena is { Length: > 0 } contrasena)
                metodos.Add(new PasswordAuthenticationMethod(destino.Usuario, contrasena));
            return metodos.Tamaño == 0
                ? throw new NativeSshException($"No hay credenciales para {destino.Clave}: ninguna clave de {origen} ({string.Join(", ", rutas)}) se pudo cargar y NATIVE_SSH_PASSWORD no está definida.{detalle}")
                : metodos.Span.ToArray();
        }

        // Ruta fría (una vez por conexión). Una ruta que no existe se salta en silencio, como hace ssh con las claves por
        // defecto; una que existe y no se pudo abrir deja su motivo (una frase con la ruta) en motivos y un aviso en el log.
        // Las cargadas se añaden a 'claves', en el orden de 'rutas'.
        internal void CargarClaves(scoped ReadOnlySpan<string> rutas, scoped ref PooledListRef<PrivateKeyFile> claves, scoped ref PooledListRef<string> motivos)
        {
            foreach (string ruta in rutas)
            {
                FalloClave? fallo;
                lock (_candado)
                {
                    if (_claves.TryGetValue(ruta, out PrivateKeyFile? guardada))
                    {
                        claves.Add(guardada);
                        continue;
                    }

                    _ = _fallos.TryGetValue(ruta, out fallo);
                }

                if (!File.Exists(ruta))
                    continue;

                // Firmas tomadas antes de abrir: si el fichero cambia mientras tanto, la siguiente conexión lo reintenta.
                FirmaFichero firmaClave = Firmar(ruta);
                FirmaFichero? firmaPassphrase = opciones.FicheroPassphrase is { Length: > 0 } fichero ? Firmar(fichero) : null;
                if (fallo is not null && fallo.Clave == firmaClave && fallo.Passphrase == firmaPassphrase)
                {
                    motivos.Add(fallo.Motivo);
                    continue;
                }

                if (TryAbrirClave(ruta, out PrivateKeyFile? clave, out string? motivo))
                {
                    claves.Add(Guardar(ruta, clave));
                    continue;
                }

                string frase = $"La clave {ruta} no se pudo cargar: {motivo}";
                lock (_candado)
                    _fallos[ruta] = new FalloClave(firmaClave, firmaPassphrase, frase);
                logger.ClaveNoCargada(ruta, motivo);
                motivos.Add(frase);
            }
        }

        // Otra conexión pudo abrir la misma clave a la vez: se queda la primera guardada.
        private PrivateKeyFile Guardar(string ruta, PrivateKeyFile clave)
        {
            lock (_candado)
            {
                _ = _fallos.Remove(ruta);
                if (_claves.TryAdd(ruta, clave))
                    return clave;

                clave.Dispose();
                return _claves[ruta];
            }
        }

        // Fecha de modificación, tamaño y permisos; si no se puede leer, una firma vacía (el fallo se repite hasta que se
        // pueda y cambie).
        private static FirmaFichero Firmar(string ruta)
        {
            try
            {
                FileInfo info = new(ruta);
                return info.Exists ? new FirmaFichero(info.LastWriteTimeUtc, info.Length, OperatingSystem.IsWindows() ? 0 : (int)info.UnixFileMode) : default;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return default;
            }
        }

        // Primero sin passphrase: si la clave no está cifrada, la passphrase ni se lee. Cifrada se reconoce por la excepción
        // de SSH.NET (OpenSSH, PEM, PuTTY, SSH.COM) o por la cabecera del fichero (PKCS#8: SSH.NET no avisa de la falta).
        private bool TryAbrirClave(string ruta, [NotNullWhen(true)] out PrivateKeyFile? clave, [NotNullWhen(false)] out string? motivo)
        {
            clave = null;
            try
            {
                clave = new PrivateKeyFile(ruta);
                motivo = null;
                return true;
            }
            catch (Exception ex) when (ex is not SshPassPhraseNullOrEmptyException && !IsCabeceraCifrada(ruta))
            {
                motivo = ex.Message;
                return false;
            }
            catch (Exception)
            {
                // Cifrada: se abre con la passphrase configurada.
            }

            if (!TryObtenerPassphrase(out string? passphrase, out string? origen, out string? error))
            {
                motivo = $"está cifrada con passphrase y {error}";
                return false;
            }

            try
            {
                clave = new PrivateKeyFile(ruta, passphrase);
                motivo = null;
                return true;
            }
            catch (Exception ex)
            {
                motivo = $"está cifrada y la passphrase de {origen} no la abre: es incorrecta o el cifrado de la clave no está admitido ({ex.Message}).";
                return false;
            }
        }

        private static bool IsCabeceraCifrada(string ruta)
        {
            try
            {
                using StreamReader lector = new(ruta);
                for (int i = 0; i < 3 && lector.ReadLine() is { } linea; i++)
                    if (linea.Contains("ENCRYPTED", StringComparison.Ordinal))
                        return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            return false;
        }

        // error: el resto de la frase «La clave X no se pudo cargar: está cifrada con passphrase y …».
        private bool TryObtenerPassphrase([NotNullWhen(true)] out string? passphrase, [NotNullWhen(true)] out string? origen, [NotNullWhen(false)] out string? error)
        {
            passphrase = null;
            origen = null;
            bool hasValor = opciones.Passphrase is { Length: > 0 };
            bool hasFichero = opciones.FicheroPassphrase is { Length: > 0 };
            if (hasValor && hasFichero)
            {
                error = $"hay dos configuradas: define solo una de {VariablePassphrase} o {VariableFicheroPassphrase}.";
                return false;
            }

            if (!hasValor && !hasFichero)
            {
                error = $"no hay ninguna configurada: define {VariableFicheroPassphrase} con la ruta de un fichero que la contenga y que solo pueda leer su dueño (chmod 600), o {VariablePassphrase}. native_ssh no la pide ni la acepta en las herramientas.";
                return false;
            }

            if (hasValor)
            {
                passphrase = opciones.Passphrase!;
                origen = VariablePassphrase;
                error = null;
                return true;
            }

            string? leida = null;
            string? errorFichero = $"{VariableFicheroPassphrase} solo se admite en Linux: en este sistema usa {VariablePassphrase}.";
            // El bool, no 'leida': un fichero vacío deja leida = "" y el error que lo dice.
            bool isLeida = !OperatingSystem.IsWindows() && FicheroSecreto.TryLeer(opciones.FicheroPassphrase!, OperatingSystem.IsLinux() ? InteropLinux.UsuarioEfectivo() : 0, DescripcionPassphrase, out leida, out errorFichero);
            if (!isLeida || leida is null)
            {
                error = $"no se pudo leer: {errorFichero}";
                return false;
            }

            // Ya sin los saltos finales: un salto en medio son varias líneas, no una passphrase (ssh-keygen la pide en una).
            if (leida.AsSpan().ContainsAny('\n', '\r'))
            {
                error = $"no se pudo leer: El fichero {DescripcionPassphrase.Del} ({opciones.FicheroPassphrase}) tiene varias líneas: debe contener solo la passphrase, en una línea.";
                return false;
            }

            passphrase = leida;
            origen = $"{VariableFicheroPassphrase} ({opciones.FicheroPassphrase})";
            error = null;
            return true;
        }

        private string DescribirRechazo(DestinoSsh destino, ResultadoHostEnum resultado, string tipo, string huella) =>
            DescribirRechazo(destino, resultado, tipo, huella, verificador.Rutas);

        // La clave del host la guarda o la actualiza siempre el usuario, nunca el cliente: aunque diga que comprobó la huella, el
        // cliente no puede saber si la comprobó contra la buena. Con varios known_hosts, la que falta no está en ninguno y basta
        // con guardarla en uno.
        internal static string DescribirRechazo(DestinoSsh destino, ResultadoHostEnum resultado, string tipo, string huella, string[] rutas)
        {
            string archivos = string.Join(", ", rutas);
            bool isVarios = rutas.Length > 1;
            string puerto = destino.Puerto == DestinoSsh.PuertoPorDefecto ? "" : $"-p {destino.Puerto} ";
            return resultado switch
            {
                ResultadoHostEnum.ClaveDistinta => $"La clave {tipo} de {destino.Servidor} (SHA256:{huella}) NO coincide con la guardada en {archivos}. Posible suplantación: no se ha conectado. Díselo al usuario: si el cambio es legítimo, es él quien debe comprobar la huella nueva y actualizar known_hosts (no lo actualices tú); no desactives la comprobación de la clave del host.",
                ResultadoHostEnum.Revocado => $"La clave {tipo} de {destino.Servidor} (SHA256:{huella}) está marcada como @revoked en {archivos}. No se ha conectado.",
                ResultadoHostEnum.Desconocido => $"{destino.Servidor}:{destino.Puerto} no está en {(isVarios ? $"ninguno de estos known_hosts: {archivos}" : archivos)} (clave {tipo} SHA256:{huella}): no se ha conectado, porque native_ssh no acepta claves de host nuevas. Pide al usuario que compruebe esa huella y la guarde él en {(isVarios ? "uno de ellos" : archivos)} (p. ej. conectándose una vez a mano con 'ssh {puerto}{destino.Usuario}@{destino.Servidor}', que la guarda en el known_hosts de su usuario: si no es {(isVarios ? "ninguno de ellos" : "ese fichero")}, hay que copiarla). No la añadas tú, ni aunque el usuario diga que ya la comprobó: la guarda el usuario; y no desactives la comprobación de la clave del host.",
                ResultadoHostEnum.Conocido => throw new UnreachableException("Un host conocido no se rechaza."),
                _ => throw new ArgumentOutOfRangeException(nameof(resultado), resultado, null)
            };
        }

        public static void FiltrarAlgoritmosHost(ConnectionInfo info, HashSet<string> tiposConocidos)
        {
            if (tiposConocidos.Count == 0)
                return;

            // HostKeyAlgorithms es un IDictionary de SSH.NET: su recorrido sigue siendo por la interfaz.
            using PooledListRef<string> noCompatibles = new();
            foreach (string algo in info.HostKeyAlgorithms.Keys)
                if (!IsAlgoritmoCompatible(algo, tiposConocidos))
                    noCompatibles.Add(algo);

            foreach (string algo in noCompatibles)
                _ = info.HostKeyAlgorithms.Remove(algo);
        }

        private static bool IsAlgoritmoCompatible(string algo, HashSet<string> tiposConocidos)
        {
            foreach (string tipo in tiposConocidos)
            {
                if (tipo == "ssh-ed25519" && algo == "ssh-ed25519")
                    return true;
                if (tipo.StartsWith("ecdsa-sha2-", StringComparison.Ordinal) && algo.StartsWith("ecdsa-sha2-", StringComparison.Ordinal))
                    return true;
                if (tipo == "ssh-rsa" && algo is "rsa-sha2-512" or "rsa-sha2-256" or "ssh-rsa")
                    return true;
                if (string.Equals(tipo, algo, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}
