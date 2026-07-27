using System.Net;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text.Json;
using DnSpyXDX.Application;
using DnSpyXDX.Host.Mcp.Tools;
using DnSpyXDX.Host.Mcp.Resources;
using DnSpyXDX.UI;
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
    WorkspaceAssemblyService assemblies,
    McpServerSettings settings,
    McpActivityLog activity,
    ILogger<McpServerService> logger) : IMcpServerService
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<string, string> clients = new();
    private WebApplication? application;
    private McpServerStatus status;
    private bool activityLoggingAttached;

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
            if (!activityLoggingAttached)
            {
                activity.Completed += LogActivity;
                activityLoggingAttached = true;
            }
            Status = McpServerStatus.Starting;
            var builder = WebApplication.CreateSlimBuilder();
            // The SDK logs expected McpException tool results as unhandled errors after converting them to isError responses.
            builder.Logging.AddFilter("ModelContextProtocol.Server.McpServer", LogLevel.Critical);
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, settings.Port));
            builder.Services.AddSingleton(backend);
            builder.Services.AddSingleton(workspace);
            builder.Services.AddSingleton(assemblies);
            builder.Services.AddSingleton(settings);
            builder.Services.AddSingleton(activity);
            builder.Services.AddSingleton<McpCursorCodec>();
            builder.Services.AddMcpServer().WithHttpTransport()
                .WithTools<AssemblyTools>().WithTools<SymbolTools>().WithTools<TreeTools>()
                .WithResources<SourceResources>().WithResources<DescriptorResources>();
            var app = builder.Build();
            startingApplication = app;
            app.Use(async (context, next) =>
            {
                if (!IsAuthorized(context))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                var request = await ReadActivityRequestAsync(context.Request);
                var sessionId = context.Request.Headers["Mcp-Session-Id"].ToString();
                var clientName = request?.ClientName ?? (sessionId.Length > 0 && clients.TryGetValue(sessionId, out var knownClient) ? knownClient : null);
                using var clientScope = activity.UseClient(clientName);
                var track = request is not null;
                var started = DateTimeOffset.UtcNow;
                if (track) activity.Begin(request!.Operation, request.Target, clientName);
                try
                {
                    await next(context);
                    if (request?.ClientName is { } initializedClient && context.Response.Headers["Mcp-Session-Id"] is { Count: > 0 } responseSession)
                        clients[responseSession.ToString()] = initializedClient;
                    if (track)
                    {
                        var failed = context.Response.StatusCode >= StatusCodes.Status400BadRequest;
                        activity.Add(new(started, request!.Operation, request.Target, failed ? "failed" : "succeeded",
                            DateTimeOffset.UtcNow - started, failed ? $"HTTP {context.Response.StatusCode}." : null));
                    }
                }
                catch (Exception exception)
                {
                    if (track) activity.Add(new(started, request!.Operation, request.Target,
                        exception is OperationCanceledException ? "cancelled" : "failed", DateTimeOffset.UtcNow - started, "MCP request failed."));
                    throw;
                }
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
            clients.Clear();
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

    private static async Task<ActivityRequest?> ReadActivityRequestAsync(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method) || request.ContentLength is null or 0) return null;
        request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var methodElement)) return null;
            var method = methodElement.GetString() ?? "request";
            string? target = null;
            string? clientName = null;
            if (root.TryGetProperty("params", out var parameters))
            {
                if (method == "tools/call" && parameters.TryGetProperty("name", out var tool)) target = tool.GetString();
                else if (method == "resources/read" && parameters.TryGetProperty("uri", out var uri)) target = uri.GetString();
                else if (method == "initialize" && parameters.TryGetProperty("clientInfo", out var client) && client.TryGetProperty("name", out var name)) clientName = name.GetString();
            }
            return new(method, target, clientName);
        }
        catch (JsonException) { return new("malformed_request", null, null); }
        finally { request.Body.Position = 0; }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (activityLoggingAttached) activity.Completed -= LogActivity;
        lifecycle.Dispose();
    }

    private void LogActivity(McpActivityEntry entry)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["McpOperation"] = entry.Operation,
            ["McpClient"] = entry.ClientName,
            ["McpTarget"] = entry.Target
        });
        logger.LogInformation(new EventId(4100, "McpOperation"), "MCP {Operation} {State} in {DurationMs} ms",
            entry.Operation, entry.State, entry.Duration.TotalMilliseconds);
    }

    private sealed record ActivityRequest(string Operation, string? Target, string? ClientName);
}
