using DnSpyXDX.Application;
using Photino.NET;

namespace DnSpyXDX.Host;

public sealed class PhotinoFileDialogService(PhotinoWindow window) : IFileDialogService
{
    public Task<string?> OpenAssemblyAsync(string? initialDirectory = null)
    {
        var files = window.ShowOpenFile(
            "Open .NET assembly",
            defaultPath: string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory) ? null : initialDirectory,
            multiSelect: false,
            filters:
            [
                ("All files (*.*)", ["*.*"])
            ]);
        return Task.FromResult(files.FirstOrDefault());
    }

    public Task<string?> SelectExportFolderAsync() => Task.FromResult(window.ShowOpenFolder("Choose an empty export folder").FirstOrDefault());
}
