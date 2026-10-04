using NativeSsh;
using NativeSsh.Comun;

if (!OpcionesArranque.TryParsear(args, Environment.GetEnvironmentVariable("NATIVE_SSH_PUERTO_HTTP"), out OpcionesArranque arranque, out string? error))
{
    Console.Error.WriteLine(error);
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
