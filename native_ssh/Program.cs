using NativeSsh;
using NativeSsh.Comun;

// El modo interno de la resolución en un proceso aparte (en Linux: ResolutorEnProceso), antes que nada más.
if (ResolucionPorFamilia.TryEjecutarModoInterno(args, out int codigoInterno))
    return codigoInterno;

Arranque.PrepararSalidaDeErrores();

if (!OpcionesArranque.TryParsear(args, Environment.GetEnvironmentVariable("NATIVE_SSH_PUERTO_HTTP"), out OpcionesArranque arranque, out string? error))
{
    Console.Error.WriteLine(error);
    return 2;
}

// Una entrada de NATIVE_SSH_SOLO_LECTURA que no se entiende impide arrancar: se comprueba aquí para salir con el mensaje,
// sin el volcado de pila de la excepción que lanzaría OpcionesNativeSsh.DesdeEntorno al registrar los servicios.
if (!DestinosSoloLectura.TryParsear(Environment.GetEnvironmentVariable(DestinosSoloLectura.Variable), out _, out string? errorSoloLectura))
{
    Console.Error.WriteLine(errorSoloLectura);
    return 2;
}

if (!arranque.IsHttp)
{
    // stdout es el canal MCP: nada más puede escribir en él. Los logs van a stderr.
    HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });
    Arranque.ConfigurarLogs(builder.Logging);
    _ = Arranque.RegistrarServicios(builder.Services).WithStdioServerTransport();
    await builder.Build().RunAsync();
    return 0;
}

// Sin token no se arranca el modo HTTP (TokenHttp); el modo stdio no lo lee. En Windows no se puede comprobar que el fichero
// sea solo de su dueño: allí el modo HTTP no arranca.
TokenHttp? token = null;
string? errorToken = "El modo HTTP solo está disponible en Linux y macOS (el fichero del token se comprueba con permisos Unix).";
if (!OperatingSystem.IsWindows() && !TokenHttp.TryCargar(Environment.GetEnvironmentVariable, out token, out errorToken))
    token = null;

if (token is null)
{
    Console.Error.WriteLine(errorToken);
    return 2;
}

TokenHttp tokenHttp = token;

// Todos los clientes HTTP comparten el mismo GestorSesiones: mismas sesiones SSH y mismo cupo de ufw.
WebApplicationBuilder web = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
Arranque.ConfigurarLogs(web.Logging);
web.WebHost.ConfigureKestrel(kestrel =>
{
    if (arranque.Modo == ModoTransporteEnum.HttpTodasLasInterfaces)
        kestrel.ListenAnyIP(arranque.Puerto);
    else
        kestrel.ListenLocalhost(arranque.Puerto);
});
_ = Arranque.RegistrarServicios(web.Services).WithHttpTransport();

WebApplication app = web.Build();
if (arranque.Modo == ModoTransporteEnum.HttpLoopback)
{
    _ = app.Use(async (context, next) =>
    {
        string host = context.Request.Host.Host;
        if (!EsHostLoopbackValido(host))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Bad Request: Host no permitido.").ConfigureAwait(false);
            return;
        }

        await next().ConfigureAwait(false);
    });
}

// Todas las peticiones (POST, GET del flujo SSE, DELETE de la sesión) llevan el token. El rechazo no dice nada del token
// presentado, ni en la respuesta ni en el log (solo si faltaba la cabecera o no coincidía, y desde dónde). El log de rechazos
// tiene tope (LimitadorAvisos): una ráfaga de peticiones no lo llena.
LimitadorAvisos avisosRechazo = new(10, TimeSpan.FromMinutes(1), TimeProvider.System);
_ = app.Use(async (context, next) =>
{
    string? cabecera = context.Request.Headers.Authorization;
    if (!tokenHttp.IsValido(cabecera))
    {
        bool isSinCabecera = string.IsNullOrEmpty(cabecera);
        if (avisosRechazo.TryRegistrar(out int omitidos))
        {
            if (omitidos > 0)
                app.Logger.PeticionesHttpRechazadasOmitidas(omitidos, avisosRechazo.Maximo);
            app.Logger.PeticionHttpRechazada(context.Connection.RemoteIpAddress?.ToString() ?? "desconocido", isSinCabecera ? "sin cabecera Authorization" : "token incorrecto");
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer realm=\"native_ssh\"";
        await context.Response.WriteAsync(isSinCabecera
            ? "Unauthorized: falta la cabecera 'Authorization: Bearer <token>' (el token de NATIVE_SSH_TOKEN_HTTP o NATIVE_SSH_TOKEN_HTTP_FICHERO)."
            : "Unauthorized: el token de la cabecera Authorization no es el configurado en el servidor.").ConfigureAwait(false);
        return;
    }

    await next().ConfigureAwait(false);
});

_ = app.MapMcp("/mcp");

try
{
    await app.StartAsync();
    if (arranque.Modo == ModoTransporteEnum.HttpTodasLasInterfaces)
        app.Logger.HttpInseguro(arranque.Puerto);
    else
        app.Logger.HttpLoopback(arranque.Puerto);

    await app.WaitForShutdownAsync();
    return 0;
}
catch (IOException ex)
{
    Console.Error.WriteLine($"Error al iniciar el servidor HTTP en el puerto {arranque.Puerto}: {ex.Message}");
    return 1;
}

static bool EsHostLoopbackValido(string? host) =>
    host is not null && (
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
        string.Equals(host, "[::1]", StringComparison.Ordinal) ||
        string.Equals(host, "::1", StringComparison.Ordinal));
