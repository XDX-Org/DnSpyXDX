using DnSpyXDX.Application;
using DnSpyXDX.Export;
using System.Collections.Concurrent;
using Xunit;

namespace DnSpyXDX.Tests;

public sealed class ProjectExportServiceTests
{
    [Fact]
    public async Task Exports_sdk_project_solution_and_report()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dnspyxdx-export-{Guid.NewGuid():N}");
        var destination = Path.Combine(root, "output");
        Directory.CreateDirectory(root);
        try
        {
            var id = Guid.NewGuid();
            var descriptor = new AssemblyDescriptor(id, Guid.NewGuid(), "DnSpyXDX.Tests", typeof(ProjectExportServiceTests).Assembly.Location, ".NETCoreApp,Version=v10.0", "Amd64", new NodeId(id, "root"));
            await using var backend = new ExportBackendStub(descriptor);
            var exporter = new ProjectExportService(backend);
            var progress = new RecordingProgress();
            var report = await exporter.ExportAsync(new ExportRequest([id], destination), progress);

            Assert.True(report.Success);
            Assert.True(File.Exists(Path.Combine(destination, "DnSpyXDXExport.slnx")));
            Assert.True(File.Exists(Path.Combine(destination, "export-report.json")));
            Assert.NotEmpty(Directory.EnumerateFiles(destination, "*.csproj", SearchOption.AllDirectories));
            Assert.NotEmpty(Directory.EnumerateFiles(destination, "*.cs", SearchOption.AllDirectories));
            Assert.Contains(progress.Updates, update => update.Total > 0 && update.Completed > 0 && update.Completed <= update.Total);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class RecordingProgress : IProgress<ExportProgress>
    {
        private readonly ConcurrentQueue<ExportProgress> updates = new();
        public IReadOnlyCollection<ExportProgress> Updates => updates.ToArray();
        public void Report(ExportProgress value) => updates.Enqueue(value);
    }

    private sealed class ExportBackendStub(AssemblyDescriptor descriptor) : IDecompilerBackend
    {
        public IReadOnlyList<AssemblyDescriptor> Assemblies => [descriptor];
        public bool TryGetAssembly(Guid sessionId, out AssemblyDescriptor? assembly) { assembly = sessionId == descriptor.SessionId ? descriptor : null; return assembly is not null; }
        public Task<AssemblyDescriptor> OpenAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssemblyDescriptor> OpenReferenceAsync(NodeId reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseAsync(Guid sessionId) => Task.CompletedTask;
        public Task<IReadOnlyList<TreeNodeDescriptor>> GetChildrenAsync(NodeId parent, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DecompilerDocument> DecompileAsync(SymbolId symbol, DecompilerLanguage language, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SymbolId> GetDeclaringTypeAsync(SymbolId symbol, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NodeId>> GetPathAsync(SymbolId symbol, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default, IProgress<IReadOnlyList<SearchResult>>? progress = null) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
