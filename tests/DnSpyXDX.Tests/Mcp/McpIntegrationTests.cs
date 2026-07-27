using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DnSpyXDX.Application;
using DnSpyXDX.Decompilation;
using DnSpyXDX.Host.Mcp;
using DnSpyXDX.Host.Mcp.Resources;
using DnSpyXDX.Host.Mcp.Tools;
using DnSpyXDX.UI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DnSpyXDX.Tests.Mcp;

public sealed class McpIntegrationTests
{
    [Fact]
    public async Task Advertised_descriptor_resources_and_tree_pages_are_readable()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(McpIntegrationTests).Assembly.Location);
        var activity = new McpActivityLog();
        var resources = new DescriptorResources(backend, activity);
        var tree = new TreeTools(backend, activity, new McpCursorCodec());

        var assemblyJson = await resources.ReadAssemblyAsync(assembly.ModuleMvid);
        using var assemblyDocument = JsonDocument.Parse(assemblyJson);
        var rootNodeId = assemblyDocument.RootElement.GetProperty("rootNodeId").GetString()!;
        var firstPage = await tree.ListChildrenAsync(rootNodeId, pageSize: 1);
        var secondPage = await tree.ListChildrenAsync(rootNodeId, firstPage.NextCursor, pageSize: 1);

        Assert.Single(firstPage.Nodes);
        Assert.NotNull(firstPage.NextCursor);
        Assert.Single(secondPage.Nodes);
        Assert.NotEqual(firstPage.Nodes[0].NodeId, secondPage.Nodes[0].NodeId);
        Assert.False(new McpCursorCodec().TryDecode(firstPage.NextCursor!, rootNodeId, out _));
        var staleCursor = await Assert.ThrowsAnyAsync<Exception>(() => tree.ListChildrenAsync(rootNodeId, "invalid"));
        Assert.StartsWith("stale_cursor:", staleCursor.Message, StringComparison.Ordinal);

        var symbol = Assert.Single(await backend.SearchAsync(nameof(McpIntegrationTests)), result =>
            result.Kind == "Type" && result.QualifiedName == typeof(McpIntegrationTests).FullName);
        var symbolJson = await resources.ReadSymbolAsync(symbol.Symbol.ModuleMvid, symbol.Symbol.MetadataToken);
        using var symbolDocument = JsonDocument.Parse(symbolJson);
        Assert.Equal(nameof(McpIntegrationTests), symbolDocument.RootElement.GetProperty("name").GetString());
        Assert.EndsWith("/source/csharp", symbolDocument.RootElement.GetProperty("cSharpResourceUri").GetString(), StringComparison.Ordinal);

        await backend.CloseAsync(assembly.SessionId);
        var closedResource = await Assert.ThrowsAnyAsync<Exception>(() => resources.ReadAssemblyAsync(assembly.ModuleMvid));
        Assert.StartsWith("assembly_not_open:", closedResource.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Endpoint_rejects_bad_credentials_and_non_loopback_origins()
    {
        await using var backend = new DecompilerBackend();
        var workspace = new WorkspaceState();
        var settings = new McpServerSettings { Enabled = true, Port = 0 };
        var activity = new McpActivityLog();
        var cache = new SourcePresentationCache(NullLogger<SourcePresentationCache>.Instance);
        var assemblies = new WorkspaceAssemblyService(backend, workspace, new SourceViewStateStore(), cache);
        await using var server = new McpServerService(backend, workspace, assemblies, settings, activity, NullLogger<McpServerService>.Instance);
        await server.StartAsync();
        using var client = new HttpClient();

        using var badToken = Request(server.Endpoint!, "invalid", "http://127.0.0.1");
        using var badOrigin = Request(server.Endpoint!, settings.BearerToken, "https://example.com");
        using var valid = Request(server.Endpoint!, settings.BearerToken, "http://127.0.0.1");
        var badTokenResponse = await client.SendAsync(badToken);
        var badOriginResponse = await client.SendAsync(badOrigin);
        var validResponse = await client.SendAsync(valid);

        Assert.Equal(HttpStatusCode.Unauthorized, badTokenResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, badOriginResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
        Assert.Contains("\"protocolVersion\":\"2025-11-25\"", await validResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var initialize = Assert.Single(activity.Entries, entry => entry.Operation == "initialize");
        Assert.Equal("test", initialize.ClientName);
        Assert.Equal("succeeded", initialize.State);
        Assert.Equal(0, activity.ActiveCalls);
    }

    [Fact]
    public async Task Shared_close_path_removes_documents_and_notifies_desktop_consumers()
    {
        await using var backend = new DecompilerBackend();
        var assembly = await backend.OpenAsync(typeof(McpIntegrationTests).Assembly.Location);
        var symbol = Assert.Single(await backend.SearchAsync(nameof(McpIntegrationTests)), result =>
            result.Kind == "Type" && result.QualifiedName == typeof(McpIntegrationTests).FullName);
        var workspace = new WorkspaceState();
        workspace.Open(await backend.DecompileAsync(symbol.Symbol, DecompilerLanguage.CSharp), assembly.Name);
        var service = new WorkspaceAssemblyService(
            backend, workspace, new SourceViewStateStore(), new SourcePresentationCache(NullLogger<SourcePresentationCache>.Instance));
        AssemblyDescriptor? closing = null;
        service.Closing += descriptor => closing = descriptor;

        var closed = await service.CloseAsync(assembly.ModuleMvid, "test");

        Assert.True(closed);
        Assert.Equal(assembly, closing);
        Assert.Empty(backend.Assemblies);
        Assert.Empty(workspace.Tabs);
    }

    private static HttpRequestMessage Request(Uri endpoint, string token, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            Encoding.UTF8, "application/json");
        return request;
    }
}
