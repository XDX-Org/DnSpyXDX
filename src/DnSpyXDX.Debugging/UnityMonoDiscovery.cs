namespace DnSpyXDX.Debugging;

public sealed record UnityMonoEndpoint(
    string Host,
    int Port,
    string? PlayerName = null,
    string? ProjectName = null,
    int? ProcessId = null,
    string? UnityVersion = null,
    bool IsEditor = false,
    bool IsLoopback = true);

public interface IUnityMonoEndpointDiscovery
{
    Task<IReadOnlyList<UnityMonoEndpoint>> DiscoverAsync(
        CancellationToken cancellationToken = default);
}

public sealed class UnityMonoEndpointDiscovery : IUnityMonoEndpointDiscovery
{
    public Task<IReadOnlyList<UnityMonoEndpoint>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<UnityMonoEndpoint>>([]);
    }
}
