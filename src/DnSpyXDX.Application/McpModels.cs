using System.Security.Cryptography;

namespace DnSpyXDX.Application;

public sealed class McpServerSettings
{
    private readonly object gate = new();
    private string[] allowedRoots = [];

    public event Action? Changed;
    public bool Enabled { get; set; }
    public int Port { get; set; }
    public string BearerToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public long MaximumAssemblyBytes { get; set; } = 512L * 1024 * 1024;
    public int MaximumOpenAssemblies { get; set; } = 32;
    public int MaximumConcurrentRequests { get; set; } = 2;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public IReadOnlyList<string> AllowedRoots { get { lock (gate) return allowedRoots; } }

    public void SetAllowedRoots(IEnumerable<string> roots)
    {
        var normalized = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root =>
            {
                if (!Path.IsPathFullyQualified(root))
                    throw new ArgumentException("MCP roots must be absolute paths.", nameof(roots));
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            })
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        lock (gate) allowedRoots = normalized;
        Changed?.Invoke();
    }
}

public enum McpServerStatus { Stopped, Starting, Listening, Stopping, Error }

public interface IMcpServerService : IAsyncDisposable
{
    event Action? Changed;
    McpServerStatus Status { get; }
    Uri? Endpoint { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed record McpActivityEntry(
    DateTimeOffset Timestamp,
    string Operation,
    string? Target,
    string State,
    TimeSpan Duration,
    string? Error = null,
    Guid Id = default,
    string? ClientName = null);

public sealed class McpActivityLog(int capacity = 500)
{
    private readonly object gate = new();
    private readonly List<McpActivityEntry> entries = [];
    private readonly AsyncLocal<Stack<Guid>?> current = new();
    private readonly AsyncLocal<string?> currentClient = new();
    private int requestCount;
    private int activeCalls;

    public event Action? Changed;
    public event Action<McpActivityEntry>? Completed;
    public IReadOnlyList<McpActivityEntry> Entries { get { lock (gate) return entries.ToArray(); } }
    public int RequestCount => Volatile.Read(ref requestCount);
    public int ActiveCalls => Volatile.Read(ref activeCalls);

    public void Begin(string operation, string? target = null, string? clientName = null, bool countRequest = true)
    {
        var id = Guid.NewGuid();
        (current.Value ??= new()).Push(id);
        lock (gate)
        {
            entries.Add(new(DateTimeOffset.UtcNow, operation, target, "running", TimeSpan.Zero, Id: id, ClientName: clientName ?? currentClient.Value));
            Trim();
        }
        if (countRequest) Interlocked.Increment(ref requestCount);
        Interlocked.Increment(ref activeCalls);
        Changed?.Invoke();
    }

    public void Add(McpActivityEntry entry)
    {
        McpActivityEntry completed;
        lock (gate)
        {
            var id = current.Value is { Count: > 0 } pending ? pending.Pop() : (Guid?)null;
            var index = id is null ? -1 : entries.FindIndex(candidate => candidate.Id == id);
            completed = entry with { Id = id ?? entry.Id, ClientName = index >= 0 ? entries[index].ClientName : entry.ClientName };
            if (index >= 0) entries[index] = completed;
            else entries.Add(completed);
            Trim();
        }
        Interlocked.Decrement(ref activeCalls);
        Completed?.Invoke(completed);
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (gate) entries.Clear();
        Interlocked.Exchange(ref requestCount, 0);
        Changed?.Invoke();
    }

    public IDisposable UseClient(string? clientName)
    {
        var previous = currentClient.Value;
        currentClient.Value = clientName;
        return new ClientScope(() => currentClient.Value = previous);
    }

    private void Trim()
    {
        while (entries.Count > capacity)
        {
            var completed = entries.FindIndex(entry => entry.State != "running");
            entries.RemoveAt(completed >= 0 ? completed : 0);
        }
    }

    private sealed class ClientScope(Action dispose) : IDisposable
    {
        private Action? callback = dispose;
        public void Dispose() => Interlocked.Exchange(ref callback, null)?.Invoke();
    }
}
