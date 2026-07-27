using DnSpyXDX.Application;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class McpServerSettingsTests
{
    [Fact]
    public void SetAllowedRootsRejectsRelativePaths()
    {
        var settings = new McpServerSettings();

        Assert.Throws<ArgumentException>(() => settings.SetAllowedRoots(["relative/path"]));
    }

    [Fact]
    public void AllowedRootsIsAnImmutableSnapshot()
    {
        var settings = new McpServerSettings();
        var first = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "first"));
        var second = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "second"));
        settings.SetAllowedRoots([first]);
        var snapshot = settings.AllowedRoots;

        settings.SetAllowedRoots([second]);

        Assert.Equal([Path.TrimEndingDirectorySeparator(first)], snapshot);
        Assert.Equal([Path.TrimEndingDirectorySeparator(second)], settings.AllowedRoots);
    }
}
