namespace NativeSsh.Comun
{
    public enum EstadoPeticionEnum
    {
        Encolado,
        Ejecutando,
        Completado,
        Cancelado,
        TiempoAgotado,
        ShellPerdido,
        Error
    }

    public static class EstadoPeticionExtensiones
    {
        public static bool IsFinal(this EstadoPeticionEnum estado) => estado >= EstadoPeticionEnum.Completado;
    }
}
