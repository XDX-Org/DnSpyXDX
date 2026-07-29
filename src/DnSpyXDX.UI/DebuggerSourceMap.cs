using DnSpyXDX.Application;

namespace DnSpyXDX.UI;

public static class DebuggerSourceMap
{
    public static DebugDocumentSequencePoint? FindForLine(
        DebugDocumentMap? map,
        SourceDocumentModel document,
        int line)
    {
        if (map is null) return null;
        var slice = document.GetLine(line);
        var end = slice.StartOffset + Math.Max(1, slice.Length);
        return map.SequencePoints
            .Where(point =>
                slice.StartOffset <= point.StartOffset &&
                point.StartOffset < end)
            .OrderBy(point => point.StartOffset)
            .ThenBy(point => point.Length)
            .FirstOrDefault();
    }

    public static int? FindLine(
        DebugDocumentMap? map,
        SourceDocumentModel document,
        DebugCodeLocation? location)
    {
        if (map is null || location is null) return null;
        var point = map.FindByRuntimeLocation(location.Value);
        return point is null
            ? null
            : document.GetPosition(point.StartOffset).Line;
    }
}
