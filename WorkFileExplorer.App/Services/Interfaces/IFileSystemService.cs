using WorkFileExplorer.App.Models;

namespace WorkFileExplorer.App.Services.Interfaces;

public interface IFileSystemService
{
    Task<IReadOnlyList<FileSystemItem>> GetDirectoryItemsAsync(string path, CancellationToken cancellationToken = default);
    bool DirectoryExists(string path);
    string NormalizePath(string? path);
}