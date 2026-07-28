using DnSpyXDX.Application;
using PhotinoEx.Core;
using PhotinoEx.Core.Models;

namespace DnSpyXDX.Host;

public sealed class PhotinoFileDialogService(PhotinoExWindow window) : IFileDialogService
{
    public async Task<string?> OpenAssemblyAsync()
    {
        var files = await window.ShowOpenFileDialogAsync(
            "Open .NET assembly",
            string.Empty,
            multiSelect: false,
            filterPatterns: [new FileFilter(".NET assemblies (*.dll;*.exe)", "*.dll;*.exe")]
        );
        return files?.FirstOrDefault();
    }

    public async Task<string?> SelectExportFolderAsync() =>
        (await window.ShowOpenFolderDialogAsync(
            "Choose an empty export folder",
            string.Empty,
            multiSelect: false
        ))?.FirstOrDefault();
}
