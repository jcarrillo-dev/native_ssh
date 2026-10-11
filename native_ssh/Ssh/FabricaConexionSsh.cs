using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Renci.SshNet;
using Renci.SshNet.Common;
using NativeSsh.Comun;
using NativeSsh.Sesion;
// El SDK Web importa Microsoft.AspNetCore.Http (usings implícitos), que también define ConnectionInfo.
using ConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace NativeSsh.Ssh
{
    // Claves privadas (CI3): las de destino.Identidades (IdentityFile de la configuración de ssh) o, si no las hay, las de
    // NATIVE_SSH_IDENTIDADES. Una clave cifrada se abre con la passphrase de NATIVE_SSH_PASSPHRASE o del fichero de
    // NATIVE_SSH_PASSPHRASE_FICHERO (con las mismas comprobaciones que el del token HTTP: FicheroSecreto), nunca con algo que
    // venga del LLM. La passphrase solo se lee cuando hace falta (una clave sin cifrar no la necesita), no se guarda y no
    // aparece en ningún mensaje ni log. Cada clave abierta se guarda por su ruta y se reutiliza; la que no se pudo abrir se
    // reintenta en la siguiente conexión (arreglar la passphrase no exige reiniciar native_ssh).
    public sealed class FabricaConexionSsh(OpcionesNativeSsh opciones, VerificadorHostConocido verificador, ILogger<FabricaConexionSsh> logger) : IFabricaConexion, IDisposable
    {
        public const string VariablePassphrase = "NATIVE_SSH_PASSPHRASE";
        public const string VariableFicheroPassphrase = "NATIVE_SSH_PASSPHRASE_FICHERO";

        private static readonly DescripcionSecreto DescripcionPassphrase = new("de la passphrase de las claves", "la passphrase", "una passphrase", VariableFicheroPassphrase, VariablePassphrase);

        private readonly Lock _candado = new();
        private readonly Dictionary<string, PrivateKeyFile> _claves = new(StringComparer.Ordinal);

        public async Task<IConexionSsh> ConectarAsync(DestinoSsh destino, CancellationToken cancellationToken)
        {
            bool isDeConfiguracion = destino.Identidades is not null;
            IReadOnlyList<string> rutas = destino.Identidades ?? opciones.RutasIdentidad;
            List<string> motivos = [];
            List<AuthenticationMethod> metodos = CrearMetodos(destino.Usuario, rutas, motivos);
            string origen = isDeConfiguracion ? "IdentityFile de la configuración de ssh" : "NATIVE_SSH_IDENTIDADES";
            string detalle = motivos.Count == 0 ? "" : $" {string.Join(" ", motivos)}";
            if (metodos.Count == 0)
                throw new NativeSshException($"No hay credenciales para {destino.Clave}: ninguna clave de {origen} ({string.Join(", ", rutas)}) se pudo cargar y NATIVE_SSH_PASSWORD no está definida.{detalle}");

            ConnectionInfo info = new(destino.Servidor, destino.Puerto, destino.Usuario, [.. metodos])
            {
                Timeout = opciones.TimeoutConexion,
                RetryAttempts = 1
            };
            IReadOnlySet<string> tiposConocidos = verificador.ObtenerTiposClave(destino.Servidor, destino.Puerto);
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
                return new ConexionSshNet(cliente);
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
            }
        }

        private List<AuthenticationMethod> CrearMetodos(string usuario, IReadOnlyList<string> rutas, List<string> motivos)
        {
            List<AuthenticationMethod> metodos = new(2);
            IReadOnlyList<PrivateKeyFile> claves = CargarClaves(rutas, motivos);
            if (claves.Count > 0)
                metodos.Add(new PrivateKeyAuthenticationMethod(usuario, [.. claves]));
            if (opciones.Contrasena is { Length: > 0 } contrasena)
                metodos.Add(new PasswordAuthenticationMethod(usuario, contrasena));
            return metodos;
        }

        // Ruta fría (una vez por conexión). Una ruta que no existe se salta en silencio, como hace ssh con las claves por
        // defecto; una que existe y no se pudo abrir deja su motivo (una frase con la ruta) en motivos y un aviso en el log.
        internal IReadOnlyList<PrivateKeyFile> CargarClaves(IReadOnlyList<string> rutas, List<string> motivos)
        {
            lock (_candado)
            {
                List<PrivateKeyFile> claves = new(rutas.Count);
                foreach (string ruta in rutas)
                {
                    if (_claves.TryGetValue(ruta, out PrivateKeyFile? guardada))
                    {
                        claves.Add(guardada);
                        continue;
                    }

                    if (!File.Exists(ruta))
                        continue;

                    if (TryAbrirClave(ruta, out PrivateKeyFile? clave, out string? motivo))
                    {
                        _claves[ruta] = clave;
                        claves.Add(clave);
                        continue;
                    }

                    logger.ClaveNoCargada(ruta, motivo);
                    motivos.Add($"La clave {ruta} no se pudo cargar: {motivo}");
                }

                return claves;
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
            if (!OperatingSystem.IsWindows())
                _ = FicheroSecreto.TryLeer(opciones.FicheroPassphrase!, OperatingSystem.IsLinux() ? InteropLinux.UsuarioEfectivo() : 0, DescripcionPassphrase, out leida, out errorFichero);
            if (leida is null)
            {
                error = $"no se pudo leer: {errorFichero}";
                return false;
            }

            passphrase = leida;
            origen = $"{VariableFicheroPassphrase} ({opciones.FicheroPassphrase})";
            error = null;
            return true;
        }

        private string DescribirRechazo(DestinoSsh destino, ResultadoHostEnum resultado, string tipo, string huella)
        {
            string archivos = string.Join(", ", verificador.Rutas);
            string puerto = destino.Puerto == DestinoSsh.PuertoPorDefecto ? "" : $"-p {destino.Puerto} ";
            return resultado switch
            {
                ResultadoHostEnum.ClaveDistinta => $"La clave {tipo} de {destino.Servidor} (SHA256:{huella}) NO coincide con la guardada en {archivos}. Posible suplantación: no se ha conectado. Si el cambio es legítimo, actualiza known_hosts manualmente.",
                ResultadoHostEnum.Revocado => $"La clave {tipo} de {destino.Servidor} (SHA256:{huella}) está marcada como @revoked en {archivos}. No se ha conectado.",
                ResultadoHostEnum.Desconocido => $"{destino.Servidor}:{destino.Puerto} no está en {archivos} (clave {tipo} SHA256:{huella}). Comprueba la huella y añádelo con: ssh-keyscan {puerto}{destino.Servidor} >> ~/.ssh/known_hosts",
                ResultadoHostEnum.Conocido => throw new UnreachableException("Un host conocido no se rechaza."),
                _ => throw new ArgumentOutOfRangeException(nameof(resultado), resultado, null)
            };
        }

        public static void FiltrarAlgoritmosHost(ConnectionInfo info, IReadOnlySet<string> tiposConocidos)
        {
            if (tiposConocidos.Count == 0)
                return;

            List<string> noCompatibles = [];
            foreach (string algo in info.HostKeyAlgorithms.Keys)
                if (!EsAlgoritmoCompatible(algo, tiposConocidos))
                    noCompatibles.Add(algo);

            foreach (string algo in noCompatibles)
                _ = info.HostKeyAlgorithms.Remove(algo);
        }

        private static bool EsAlgoritmoCompatible(string algo, IReadOnlySet<string> tiposConocidos)
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
