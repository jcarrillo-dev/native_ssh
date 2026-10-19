namespace NativeSsh.Comun
{
    // Nombre y versión del servidor: los da serverInfo en la respuesta a initialize (Arranque.RegistrarServicios) y los
    // imprime '--version' (Program.cs), de este único sitio.
    public static class IdentidadServidor
    {
        public const string Nombre = "native_ssh";

        public const string Version = "2.0.0";

        // La línea de '--version'.
        public const string TextoVersion = Nombre + " " + Version;
    }
}
