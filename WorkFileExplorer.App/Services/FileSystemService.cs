using WorkFileExplorer.App.Models;
using WorkFileExplorer.App.Services.Interfaces;

namespace WorkFileExplorer.App.Services;

public sealed class FileSystemService : IFileSystemService
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    public Task<IReadOnlyList<FileSystemItem>> GetDirectoryItemsAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<FileSystemItem>>(() =>
        {
            var normalizedPath = NormalizePath(path);
            var items = new List<FileSystemItem>();

            try
            {
                var parent = Directory.GetParent(normalizedPath);
                if (parent is not null)
                {
                    items.Add(new FileSystemItem
                    {
                        Name = "..",
                        Extension = string.Empty,
                        FullPath = parent.FullName,
                        IsParentDirectory = true,
                        IsDirectory = true,
                        SizeBytes = 0,
                        SizeDisplay = "<디렉터리>",
                        LastModified = parent.LastWriteTime,
                        TypeDisplay = "Parent"
                    });
                }

                foreach (var directory in Directory.EnumerateDirectories(normalizedPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dirInfo = new DirectoryInfo(directory);
                    items.Add(new FileSystemItem
                    {
                        Name = dirInfo.Name,
                        Extension = string.Empty,
                        FullPath = dirInfo.FullName,
                        IsDirectory = true,
                        SizeBytes = 0,
                        SizeDisplay = "<디렉터리>",
                        LastModified = dirInfo.LastWriteTime,
                        TypeDisplay = "Folder"
                    });
                }

                foreach (var file in Directory.EnumerateFiles(normalizedPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fileInfo = new FileInfo(file);
                    items.Add(new FileSystemItem
                    {
                        Name = fileInfo.Name,
                        Extension = fileInfo.Extension,
                        FullPath = fileInfo.FullName,
                        IsDirectory = false,
                        SizeBytes = fileInfo.Length,
                        SizeDisplay = ToReadableSize(fileInfo.Length),
                        LastModified = fileInfo.LastWriteTime,
                        TypeDisplay = string.IsNullOrWhiteSpace(fileInfo.Extension)
                            ? "File"
                            : $"{fileInfo.Extension.ToUpperInvariant()} File"
                    });
                }
            }
            catch
            {
                return Array.Empty<FileSystemItem>();
            }

            return items
                .OrderByDescending(static item => item.IsParentDirectory)
                .ThenByDescending(static item => item.IsDirectory)
                .ThenBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }

    private static string ToReadableSize(long size)
    {
        if (size <= 0)
        {
            return "0 B";
        }

        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var index = 0;
        double value = size;

        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {suffixes[index]}";
    }
}
