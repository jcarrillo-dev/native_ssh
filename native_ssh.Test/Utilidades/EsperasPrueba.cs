namespace NativeSsh.Test.Utilidades
{
    public static class EsperasPrueba
    {
        public static async Task EsperarHastaAsync(Func<bool> condicion, TimeSpan? limite = null)
        {
            DateTime tope = DateTime.UtcNow + (limite ?? TimeSpan.FromSeconds(5));
            while (!condicion())
            {
                if (DateTime.UtcNow > tope)
                    throw new TimeoutException("La condición no se cumplió a tiempo.");
                await Task.Delay(10);
            }
        }
    }
}
