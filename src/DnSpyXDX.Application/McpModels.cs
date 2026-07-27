using System.Security.Cryptography;

namespace DnSpyXDX.Application;

public sealed class McpServerSettings
{
    private readonly List<string> allowedRoots = [];

    public event Action? Changed;
    public bool Enabled { get; set; }
    public int Port { get; set; }
    public string BearerToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public IReadOnlyList<string> AllowedRoots => allowedRoots;

    public void SetAllowedRoots(IEnumerable<string> roots)
    {
        allowedRoots.Clear();
        allowedRoots.AddRange(roots.Where(root => !string.IsNullOrWhiteSpace(root)));
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
    string? Error = null);

public sealed class McpActivityLog(int capacity = 500)
{
    private readonly object gate = new();
    private readonly Queue<McpActivityEntry> entries = new();
    private int requestCount;
    private int activeCalls;

    public event Action? Changed;
    public IReadOnlyList<McpActivityEntry> Entries { get { lock (gate) return entries.ToArray(); } }
    public int RequestCount => Volatile.Read(ref requestCount);
    public int ActiveCalls => Volatile.Read(ref activeCalls);

    public void Begin()
    {
        Interlocked.Increment(ref requestCount);
        Interlocked.Increment(ref activeCalls);
        Changed?.Invoke();
    }

    public void Add(McpActivityEntry entry)
    {
        lock (gate)
        {
            entries.Enqueue(entry);
            while (entries.Count > capacity) entries.Dequeue();
        }
        Interlocked.Decrement(ref activeCalls);
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (gate) entries.Clear();
        Interlocked.Exchange(ref requestCount, 0);
        Changed?.Invoke();
    }
}
