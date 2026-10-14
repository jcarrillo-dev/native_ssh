using System.Buffers;
using System.Text;
using NativeSsh.Buffers;

namespace NativeSsh.Protocolo
{
    // Delante de la salida de un comando con guardarSalida (SesionShell.Procesar, detrás del escáner del marcador de fin):
    // quita del flujo del terminal las marcas del envoltorio de esta petición (SalidaGuardada.Envolver) antes de que lleguen
    // al búfer, y las aplica al informe. Así ninguna página ni ningún desdeByte puede enseñar un trozo de una marca, aunque
    // llegue partida en cualquier byte. Una marca: "__NSG_" + el nonce (32 hexadecimales en minúscula) + "_" + el tipo
    // (A, F o E) + sus campos (cada uno "_" + [A-Za-z0-9+/=-]*, tantos como dice SalidaGuardada.CamposDe) + "__" y el salto
    // de línea que la sigue ("\n", o "\r\n" si el terminal convierte los saltos). Lo que al final de un trozo puede ser el
    // principio de una marca se retiene hasta el trozo siguiente; lo que deja de cuadrar (otro nonce, otro carácter, más de
    // MaxLongitudMarca) es texto del comando y pasa tal cual. Solo se aplican las marcas en el orden del envoltorio
    // (IsAceptada). Vaciar suelta lo retenido al terminar el comando: si ya llevaba el nonce entero (una marca de esta
    // petición cortada, p. ej. por el cierre del shell) se descarta; si no, es texto.
    // No es seguro entre hilos: Escribir y Vaciar van bajo el candado de proceso de la sesión. Informe sí se lee desde
    // cualquier hilo.
    public sealed class FiltroMarcasGuardado : IDestinoSalida
    {
        public const int MaxLongitudMarca = 8192;
        private const int LongitudCabecera = 6;
        private const int LongitudNonce = 32;

        private static ReadOnlySpan<byte> Cabecera => "__NSG_"u8;

        private readonly byte[] _nonce = new byte[LongitudNonce];
        private readonly Lock _candadoInforme = new();
        private byte[] _retenido = [];
        private int _retenidos;
        private int _saltarSalto;
        private InformeGuardado _informe = new(null, null, null, null, null);
        private bool _isVistaA;
        private bool _isVistaF;
        private bool _isVistaE;

        public FiltroMarcasGuardado(Guid nonce, BufferSalida salida)
        {
            _ = nonce.TryFormat(_nonce, out _, "N");
            Salida = salida;
        }

        public BufferSalida Salida { get; }

        // Se llama una vez, bajo el candado de proceso de la sesión, al llegar la marca A (el archivo ya está creado): la sesión
        // lo cuenta en su shell (SesionShell.ContarArchivoGuardado).
        public Action? AlCrearArchivo { get; set; }

        public InformeGuardado Informe
        {
            get
            {
                lock (_candadoInforme)
                    return _informe;
            }
        }

        // La sesión, tras leer la vista previa por un canal exec (o al no poder leerla).
        public void FijarVistaPrevia(byte[]? vista, string? motivo)
        {
            lock (_candadoInforme)
                _informe = _informe with { VistaPrevia = vista, MotivoSinVistaPrevia = motivo };
        }

        public void Escribir(ReadOnlySpan<byte> datos)
        {
            datos = SaltarSaltoPendiente(datos);
            if (datos.IsEmpty)
                return;

            if (_retenidos == 0)
            {
                Procesar(datos);
                return;
            }

            int total = _retenidos + datos.Length;
            byte[] ventana = ArrayPool<byte>.Shared.Rent(total);
            try
            {
                _retenido.AsSpan(0, _retenidos).CopyTo(ventana);
                datos.CopyTo(ventana.AsSpan(_retenidos));
                _retenidos = 0;
                Procesar(ventana.AsSpan(0, total));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(ventana, clearArray: true);
            }
        }

        // El comando terminó (o se interrumpió): lo retenido ya no se completará.
        public void Vaciar()
        {
            if (_retenidos >= LongitudCabecera + LongitudNonce)
                _retenidos = 0;
            if (_retenidos > 0)
                Salida.Escribir(_retenido.AsSpan(0, _retenidos));
            _retenidos = 0;
            _saltarSalto = 0;
        }

        private ReadOnlySpan<byte> SaltarSaltoPendiente(ReadOnlySpan<byte> datos)
        {
            if (_saltarSalto == 1 && !datos.IsEmpty && datos[0] == (byte)'\r')
            {
                datos = datos[1..];
                _saltarSalto = 2;
            }

            if (_saltarSalto != 0 && !datos.IsEmpty)
            {
                if (datos[0] == (byte)'\n')
                    datos = datos[1..];
                _saltarSalto = 0;
            }

            return datos;
        }

        private void Procesar(ReadOnlySpan<byte> ventana)
        {
            int emitido = 0;
            int busqueda = 0;
            while (true)
            {
                int relativo = ventana[busqueda..].IndexOf(Cabecera);
                if (relativo < 0)
                    break;

                int inicio = busqueda + relativo;
                ResultadoMarcadorEnum resultado = Analizar(ventana[inicio..], out int longitud, out byte tipo, out string[] campos);
                if (resultado == ResultadoMarcadorEnum.Incompleta)
                {
                    Emitir(ventana[emitido..inicio]);
                    Retener(ventana[inicio..]);
                    return;
                }

                if (resultado == ResultadoMarcadorEnum.Invalida)
                {
                    busqueda = inicio + 1;
                    continue;
                }

                Emitir(ventana[emitido..inicio]);
                bool isPrimerArchivo;
                lock (_candadoInforme)
                {
                    bool isSinArchivo = _informe.Archivo is null;
                    if (IsAceptada(tipo))
                        _informe = SalidaGuardada.Aplicar(_informe, (char)tipo, campos);
                    isPrimerArchivo = isSinArchivo && _informe.Archivo is not null;
                }

                if (isPrimerArchivo)
                    AlCrearArchivo?.Invoke();
                emitido = SaltarSalto(ventana, inicio + longitud);
                busqueda = emitido;
            }

            int retenidos = LongitudPrefijoFinal(ventana[emitido..]);
            Emitir(ventana[emitido..^retenidos]);
            Retener(ventana[^retenidos..]);
        }

        // El envoltorio deja como mucho una A, después una F, o, si no llegó a ejecutar el comando, una E antes de la A (la del
        // motivo y, si la preparación se cortó sin darlo, la genérica 'prep': cuenta solo la primera). Otra marca con el nonce
        // (una segunda A, una E tras la A, una F antes de la A o una segunda F) solo la pudo imprimir el propio comando o un
        // proceso suyo: se quita igual de la salida, pero no cambia el archivo, el recuento ni el estado. Bajo _candadoInforme.
        private bool IsAceptada(byte tipo)
        {
            bool isAceptada;
            switch (tipo)
            {
                case (byte)'A':
                    isAceptada = !_isVistaA;
                    _isVistaA = true;
                    break;
                case (byte)'F':
                    isAceptada = _isVistaA && !_isVistaF;
                    _isVistaF |= isAceptada;
                    break;
                default:
                    isAceptada = !_isVistaA && !_isVistaE;
                    _isVistaE |= isAceptada;
                    break;
            }

            return isAceptada;
        }

        // El salto de línea que sigue a una marca; si el trozo se acaba antes, se salta al principio del siguiente.
        private int SaltarSalto(ReadOnlySpan<byte> ventana, int i)
        {
            if (i == ventana.Length)
            {
                _saltarSalto = 1;
                return i;
            }

            if (ventana[i] == (byte)'\r')
            {
                i++;
                if (i == ventana.Length)
                {
                    _saltarSalto = 2;
                    return i;
                }
            }

            return ventana[i] == (byte)'\n' ? i + 1 : i;
        }

        private ResultadoMarcadorEnum Analizar(ReadOnlySpan<byte> datos, out int longitud, out byte tipo, out string[] campos)
        {
            longitud = 0;
            tipo = 0;
            campos = [];
            int i = LongitudCabecera;
            for (; i < LongitudCabecera + LongitudNonce; i++)
            {
                if (i >= datos.Length)
                    return ResultadoMarcadorEnum.Incompleta;
                if (datos[i] != _nonce[i - LongitudCabecera])
                    return ResultadoMarcadorEnum.Invalida;
            }

            if (i >= datos.Length)
                return ResultadoMarcadorEnum.Incompleta;
            if (datos[i++] != (byte)'_')
                return ResultadoMarcadorEnum.Invalida;
            if (i >= datos.Length)
                return ResultadoMarcadorEnum.Incompleta;

            tipo = datos[i++];
            int numeroCampos = SalidaGuardada.CamposDe(tipo);
            if (numeroCampos < 0)
                return ResultadoMarcadorEnum.Invalida;

            string[] leidos = new string[numeroCampos];
            for (int campo = 0; campo < numeroCampos; campo++)
            {
                if (i >= datos.Length)
                    return ResultadoMarcadorEnum.Incompleta;
                if (datos[i++] != (byte)'_')
                    return ResultadoMarcadorEnum.Invalida;

                int inicioCampo = i;
                while (i < datos.Length && IsCaracterCampo(datos[i]))
                    if (++i > MaxLongitudMarca)
                        return ResultadoMarcadorEnum.Invalida;

                if (i >= datos.Length)
                    return ResultadoMarcadorEnum.Incompleta;
                leidos[campo] = Encoding.ASCII.GetString(datos[inicioCampo..i]);
            }

            for (int guion = 0; guion < 2; guion++)
            {
                if (i >= datos.Length)
                    return ResultadoMarcadorEnum.Incompleta;
                if (datos[i++] != (byte)'_')
                    return ResultadoMarcadorEnum.Invalida;
            }

            longitud = i;
            campos = leidos;
            return ResultadoMarcadorEnum.Completa;
        }

        private static bool IsCaracterCampo(byte c) => char.IsAsciiLetterOrDigit((char)c) || c is (byte)'+' or (byte)'/' or (byte)'=' or (byte)'-';

        // Sufijo más largo (hasta 5 bytes) que coincide con el principio de la cabecera.
        private static int LongitudPrefijoFinal(ReadOnlySpan<byte> datos)
        {
            for (int k = Math.Min(Cabecera.Length - 1, datos.Length); k > 0; k--)
                if (datos[^k..].SequenceEqual(Cabecera[..k]))
                    return k;

            return 0;
        }

        private void Emitir(ReadOnlySpan<byte> datos)
        {
            if (!datos.IsEmpty)
                Salida.Escribir(datos);
        }

        private void Retener(ReadOnlySpan<byte> datos)
        {
            if (datos.Length > _retenido.Length)
                _retenido = new byte[Math.Max(datos.Length, Math.Min(2 * _retenido.Length, MaxLongitudMarca + 64))];
            datos.CopyTo(_retenido);
            _retenidos = datos.Length;
        }
    }
}
