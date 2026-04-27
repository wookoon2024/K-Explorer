using WorkFileExplorer.App.Models;
using WorkFileExplorer.App.Services.Interfaces;

namespace WorkFileExplorer.App.Services;

public sealed class FileSystemService : IFileSystemService
{
    private static readonly IComparer<FileSystemItem> FileSystemItemComparer = Comparer<FileSystemItem>.Create(static (x, y) =>
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return 1;
        }

        if (y is null)
        {
            return -1;
        }

        // Parent directory row should always stay on top.
        var parentCompare = y.IsParentDirectory.CompareTo(x.IsParentDirectory);
        if (parentCompare != 0)
        {
            return parentCompare;
        }

        // Then directories first.
        var directoryCompare = y.IsDirectory.CompareTo(x.IsDirectory);
        if (directoryCompare != 0)
        {
            return directoryCompare;
        }

        return StringComparer.CurrentCultureIgnoreCase.Compare(x.Name, y.Name);
    });

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
            var items = new List<FileSystemItem>(1024);

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

                var directoryInfo = new DirectoryInfo(normalizedPath);
                foreach (var entry in directoryInfo.EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        if ((entry.Attributes & FileAttributes.Directory) == FileAttributes.Directory)
                        {
                            items.Add(new FileSystemItem
                            {
                                Name = entry.Name,
                                Extension = string.Empty,
                                FullPath = entry.FullName,
                                IsDirectory = true,
                                SizeBytes = 0,
                                SizeDisplay = "<디렉터리>",
                                LastModified = entry.LastWriteTime,
                                TypeDisplay = "Folder"
                            });
                            continue;
                        }

                        var fileInfo = entry as FileInfo ?? new FileInfo(entry.FullName);
                        var extension = fileInfo.Extension;
                        var size = fileInfo.Length;

                        items.Add(new FileSystemItem
                        {
                            Name = fileInfo.Name,
                            Extension = extension,
                            FullPath = fileInfo.FullName,
                            IsDirectory = false,
                            SizeBytes = size,
                            SizeDisplay = ToReadableSize(size),
                            LastModified = fileInfo.LastWriteTime,
                            TypeDisplay = string.IsNullOrWhiteSpace(extension)
                                ? "File"
                                : $"{extension.ToUpperInvariant()} File"
                        });
                    }
                    catch
                    {
                        // Skip inaccessible/broken entries and keep loading the rest.
                    }
                }
            }
            catch
            {
                return Array.Empty<FileSystemItem>();
            }

            items.Sort(FileSystemItemComparer);
            return items;
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
