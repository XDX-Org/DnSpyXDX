using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using DnSpyXDX.Application;

namespace DnSpyXDX.Debugging;

public sealed partial class UnityMonoEndpointDiscovery(
    TimeSpan? discoveryWindow = null) : IUnityMonoEndpointDiscovery
{
    private const int DiscoveryPort = 54997;
    private static readonly IPAddress DiscoveryGroup = IPAddress.Parse("225.0.0.222");
    private readonly TimeSpan window = discoveryWindow ?? TimeSpan.FromMilliseconds(750);

    public async Task<IReadOnlyList<UnityMonoEndpoint>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        if (window <= TimeSpan.Zero || window > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(discoveryWindow));
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            socket.JoinMulticastGroup(DiscoveryGroup);
        }
        catch (SocketException)
        {
            return [];
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(window);
        var endpoints = new Dictionary<(string Host, int Port), UnityMonoEndpoint>();
        try
        {
            while (true)
            {
                var packet = await socket.ReceiveAsync(timeout.Token);
                var endpoint = ParsePacket(
                    Encoding.UTF8.GetString(packet.Buffer),
                    packet.RemoteEndPoint.Address);
                if (endpoint is not null)
                    endpoints[(endpoint.Host, endpoint.Port)] = endpoint;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            try { socket.DropMulticastGroup(DiscoveryGroup); }
            catch (SocketException) { }
        }
        return endpoints.Values.OrderBy(value => value.PlayerName).ToArray();
    }

    internal static UnityMonoEndpoint? ParsePacket(string packet, IPAddress sender)
    {
        if (packet.Length > 4096) return null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in FieldPattern().Matches(packet))
            fields.TryAdd(match.Groups[1].Value, match.Groups[2].Value.Trim());
        if (!fields.TryGetValue("Port", out var portText) ||
            !int.TryParse(portText, out var port) || port is <= 0 or > 65535 ||
            fields.GetValueOrDefault("Debug") != "1")
            return null;
        var host = fields.GetValueOrDefault("IP");
        if (!IPAddress.TryParse(host, out var address)) address = sender;
        var id = fields.GetValueOrDefault("Id");
        var idMatch = IdPattern().Match(id ?? string.Empty);
        var player = idMatch.Success ? idMatch.Groups[1].Value : id;
        var project = idMatch.Success ? idMatch.Groups[2].Value : null;
        _ = int.TryParse(fields.GetValueOrDefault("Version"), out var protocolVersion);
        return new(
            address.ToString(),
            port,
            player,
            project,
            DebuggerProtocolVersion: protocolVersion > 0 ? protocolVersion : null,
            IsEditor: player?.Contains("Editor", StringComparison.OrdinalIgnoreCase) == true,
            IsLoopback: IPAddress.IsLoopback(address));
    }

    [GeneratedRegex(@"\[([^\]]+)\]\s*([^\[]*)", RegexOptions.CultureInvariant)]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"^([^:(]+)\(([^)]*)\)(?::\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
