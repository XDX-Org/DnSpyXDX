using System.Text.RegularExpressions;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed partial class OfflineAssetsTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string HostRoot = Path.Combine(RepositoryRoot, "src", "DnSpyXDX.Host", "wwwroot");
    private static readonly string UiRoot = Path.Combine(RepositoryRoot, "src", "DnSpyXDX.UI", "wwwroot");

    [Fact]
    public void HostPageUsesLocalAssetReferences()
    {
        var html = File.ReadAllText(Path.Combine(HostRoot, "index.html"));

        Assert.DoesNotMatch(RemoteUrl(), html);

        foreach (Match match in HtmlAssetReference().Matches(html))
            AssertLocalReference(match.Groups[1].Value);
    }

    [Fact]
    public void StylesAndScriptsContainNoRemoteUrls()
    {
        foreach (var path in Directory.EnumerateFiles(UiRoot, "*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                                    || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                                    || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.DoesNotMatch(RemoteUrl(), File.ReadAllText(path));
        }
    }

    [Fact]
    public void EveryCssAssetIsBundled()
    {
        var cssPath = Path.Combine(UiRoot, "css", "app.css");
        var css = File.ReadAllText(cssPath);

        foreach (Match match in CssUrl().Matches(css))
        {
            var reference = match.Groups[1].Value.Trim('"', '\'');
            AssertLocalReference(reference);
            Assert.True(File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cssPath)!, reference))),
                $"CSS asset is missing: {reference}");
        }
    }

    [Fact]
    public void BundledFontsAreValidWoff2Files()
    {
        var fonts = Directory.GetFiles(Path.Combine(UiRoot, "fonts"), "*.woff2");
        Assert.Equal(6, fonts.Length);

        foreach (var font in fonts)
            Assert.Equal("wOF2", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(font), 0, 4));
    }

    private static void AssertLocalReference(string reference)
    {
        Assert.False(reference.StartsWith("//", StringComparison.Ordinal), $"Protocol-relative asset reference: {reference}");
        if (reference.StartsWith('/')) return;
        Assert.False(Uri.TryCreate(reference, UriKind.Absolute, out _), $"Remote asset reference: {reference}");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DnSpyXDX.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    [GeneratedRegex(@"https?:|[""']\s*//", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteUrl();

    [GeneratedRegex(@"(?:src|href)=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlAssetReference();

    [GeneratedRegex(@"url\(([^)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrl();
}
