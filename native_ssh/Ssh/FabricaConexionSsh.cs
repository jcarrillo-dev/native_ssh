using System.Diagnostics;
using Renci.SshNet;
using Renci.SshNet.Common;
using NativeSsh.Comun;
using NativeSsh.Sesion;
// El SDK Web importa Microsoft.AspNetCore.Http (usings implícitos), que también define ConnectionInfo.
using ConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace NativeSsh.Ssh
{
    public sealed class FabricaConexionSsh(OpcionesNativeSsh opciones, VerificadorHostConocido verificador, ILogger<FabricaConexionSsh> logger) : IFabricaConexion, IDisposable
    {
        private readonly Lock _candado = new();
        private List<PrivateKeyFile>? _claves;

        public async Task<IConexionSsh> ConectarAsync(DestinoSsh destino, CancellationToken cancellationToken)
        {
            List<AuthenticationMethod> metodos = CrearMetodos(destino.Usuario);
            if (metodos.Count == 0)
                throw new NativeSshException($"No hay credenciales para {destino.Clave}: ninguna clave de NATIVE_SSH_IDENTIDADES ({string.Join(", ", opciones.RutasIdentidad)}) se pudo cargar y NATIVE_SSH_PASSWORD no está definida. Las claves con frase de paso no están soportadas.");

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
                    throw new NativeSshException($"Autenticación rechazada en {destino.Clave} ({ex.Message}). Revisa NATIVE_SSH_IDENTIDADES o NATIVE_SSH_PASSWORD.", ex);
                throw;
            }
        }

        public void Dispose()
        {
            lock (_candado)
            {
                foreach (PrivateKeyFile clave in _claves ?? [])
                    clave.Dispose();
                _claves = null;
            }
        }

        private List<AuthenticationMethod> CrearMetodos(string usuario)
        {
            List<AuthenticationMethod> metodos = new(2);
            List<PrivateKeyFile> claves = CargarClaves();
            if (claves.Count > 0)
                metodos.Add(new PrivateKeyAuthenticationMethod(usuario, [.. claves]));
            if (opciones.Contrasena is { Length: > 0 } contrasena)
                metodos.Add(new PasswordAuthenticationMethod(usuario, contrasena));
            return metodos;
        }

        // Ruta fría: las claves se leen una vez y se reutilizan en todas las conexiones.
        private List<PrivateKeyFile> CargarClaves()
        {
            lock (_candado)
            {
                if (_claves is not null)
                    return _claves;

                _claves = [];
                foreach (string ruta in opciones.RutasIdentidad)
                {
                    if (!File.Exists(ruta))
                        continue;

                    try
                    {
                        _claves.Add(new PrivateKeyFile(ruta));
                    }
                    catch (Exception ex)
                    {
                        logger.ClaveNoCargada(ruta, ex.Message);
                    }
                }

                return _claves;
            }
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
