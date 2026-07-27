using System.Text;

namespace DnSpyXDX.Host.Mcp;

internal static class McpNodeIds
{
    public static string Encode(Guid moduleMvid, string value) => EncodeText($"{moduleMvid:D}\n{value}");

    public static bool TryDecode(string encoded, out Guid moduleMvid, out string value)
    {
        moduleMvid = default;
        value = "";
        try
        {
            var decoded = DecodeText(encoded);
            var separator = decoded.IndexOf('\n');
            if (separator <= 0 || !Guid.TryParse(decoded[..separator], out moduleMvid)) return false;
            value = decoded[(separator + 1)..];
            return value.Length > 0;
        }
        catch (FormatException) { return false; }
    }

    internal static string EncodeText(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string DecodeText(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}

public sealed class McpCursorCodec
{
    private readonly string generation = Guid.NewGuid().ToString("N");

    public string Encode(string nodeId, int offset) => McpNodeIds.EncodeText($"{generation}\n{offset}\n{nodeId}");

    public bool TryDecode(string cursor, string nodeId, out int offset)
    {
        offset = 0;
        try
        {
            var parts = McpNodeIds.DecodeText(cursor).Split('\n', 3);
            return parts.Length == 3 && parts[0] == generation && int.TryParse(parts[1], out offset) && offset >= 0 && parts[2] == nodeId;
        }
        catch (FormatException) { return false; }
    }
}
