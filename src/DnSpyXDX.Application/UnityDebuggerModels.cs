namespace DnSpyXDX.Application;

public sealed record UnityMonoEndpoint(
    string Host,
    int Port,
    string? PlayerName = null,
    string? ProjectName = null,
    int? ProcessId = null,
    string? UnityVersion = null,
    int? DebuggerProtocolVersion = null,
    bool IsEditor = false,
    bool IsLoopback = true);

public interface IUnityMonoEndpointDiscovery
{
    Task<IReadOnlyList<UnityMonoEndpoint>> DiscoverAsync(
        CancellationToken cancellationToken = default);
}
