using System.Net;
using System.Security.Cryptography;
using DnSpyXDX.Application;
using DnSpyXDX.Host.Mcp.Tools;
using DnSpyXDX.Host.Mcp.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DnSpyXDX.Host.Mcp;

public sealed class McpServerService(
    IDecompilerBackend backend,
    WorkspaceState workspace,
    McpServerSettings settings,
    McpActivityLog activity,
    ILogger<McpServerService> logger) : IMcpServerService
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private WebApplication? application;
    private McpServerStatus status;

    public event Action? Changed;
    public McpServerStatus Status
    {
        get => status;
        private set
        {
            if (status == value) return;
            status = value;
            Changed?.Invoke();
        }
    }
    public Uri? Endpoint { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        WebApplication? startingApplication = null;
        try
        {
            if (application is not null) return;
            Status = McpServerStatus.Starting;
            var builder = WebApplication.CreateSlimBuilder();
            // The SDK logs expected McpException tool results as unhandled errors after converting them to isError responses.
            builder.Logging.AddFilter("ModelContextProtocol.Server.McpServer", LogLevel.Critical);
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, settings.Port));
            builder.Services.AddSingleton(backend);
            builder.Services.AddSingleton(workspace);
            builder.Services.AddSingleton(settings);
            builder.Services.AddSingleton(activity);
            builder.Services.AddMcpServer().WithHttpTransport().WithTools<AssemblyTools>().WithTools<SymbolTools>().WithResources<SourceResources>();
            var app = builder.Build();
            startingApplication = app;
            app.Use(async (context, next) =>
            {
                if (!IsAuthorized(context))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                await next(context);
            });
            app.MapMcp("/mcp");
            await app.StartAsync(cancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single();
            Endpoint = address is null ? null : new Uri(new Uri(address), "/mcp");
            application = app;
            startingApplication = null;
            Status = McpServerStatus.Listening;
            logger.LogInformation("MCP server listening on {Endpoint}", Endpoint);
        }
        catch
        {
            if (startingApplication is not null) await startingApplication.DisposeAsync();
            Status = McpServerStatus.Error;
            throw;
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (application is null) return;
            Status = McpServerStatus.Stopping;
            var stoppingApplication = application;
            application = null;
            Endpoint = null;
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            shutdown.CancelAfter(TimeSpan.FromSeconds(2));
            try { await stoppingApplication.StopAsync(shutdown.Token); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                logger.LogWarning("MCP server shutdown timed out; aborting active connections");
            }
            try { await stoppingApplication.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (TimeoutException)
            {
                logger.LogWarning("MCP server disposal exceeded the shutdown deadline");
            }
            Status = McpServerStatus.Stopped;
            logger.LogInformation("MCP server stopped");
        }
        finally { lifecycle.Release(); }
    }

    private bool IsAuthorized(HttpContext context)
    {
        if (!settings.Enabled) return false;
        if (context.Request.Headers.Origin is { Count: > 0 } origins && origins.Any(origin => !IsLoopbackOrigin(origin)))
            return false;
        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var supplied = authorization[prefix.Length..];
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(supplied),
            System.Text.Encoding.UTF8.GetBytes(settings.BearerToken));
    }

    private static bool IsLoopbackOrigin(string? origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        lifecycle.Dispose();
    }
}
