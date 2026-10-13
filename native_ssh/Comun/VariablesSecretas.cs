namespace NativeSsh.Comun
{
    // Variables de entorno cuyo valor es un secreto de native_ssh (no las de rutas a ficheros). Lista única: se quitan del
    // entorno de 'ssh -G' (un Match exec las vería) y ${VAR} en un IdentityFile no las expande (su valor acabaría en los
    // errores, en los mensajes de credenciales y en el log). Toda variable secreta nueva se añade aquí.
    public static class VariablesSecretas
    {
        public const string Contrasena = "NATIVE_SSH_PASSWORD";
        public const string Passphrase = "NATIVE_SSH_PASSPHRASE";

        public static readonly string[] Nombres = [Contrasena, Passphrase, TokenHttp.VariableToken];

        public static bool IsSecreta(string nombre) => Array.IndexOf(Nombres, nombre) >= 0;
    }
}
