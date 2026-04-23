using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WorkFileExplorer.App.Models;

public sealed class FileSystemItem : INotifyPropertyChanged
{
    private string? _renameCandidate;
    private bool _isInlineRenaming;
    private const string IconBasePath = "/Assets/Icons/";
    public static bool UseExtensionColors { get; set; } = true;
    public static bool UsePinnedHighlightColor { get; set; } = true;
    private static readonly Dictionary<string, string> ExtensionColorOverrides = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico", ".svg"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".avi", ".mov", ".mkv", ".wmv", ".webm", ".flv", ".mpeg", ".mpg"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".aac", ".flac", ".ogg", ".m4a", ".wma"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".rtf", ".doc", ".docx", ".pdf", ".csv", ".hwp", ".hwpx", ".xls", ".xlsx"
    };

    private static readonly HashSet<string> TerminalExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cmd", ".bat", ".ps1", ".sh"
    };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".xml", ".json", ".js", ".jsx", ".ts", ".tsx", ".py", ".java", ".c", ".cpp", ".h", ".hpp",
        ".sql", ".yaml", ".yml", ".html", ".css", ".scss", ".md", ".ini", ".config"
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".msi", ".lnk", ".bin", ".iso"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2"
    };

    private static readonly HashSet<string> NeutralExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ini"
    };

    public string Name { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsParentDirectory { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsPinned { get; init; }
    public bool IsFavorite { get; init; }
    public string Memo { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string SizeDisplay { get; init; } = string.Empty;
    public DateTime LastModified { get; init; }
    public string TypeDisplay { get; init; } = string.Empty;
    public string ExtensionLower => (Extension ?? string.Empty).ToLowerInvariant();

    public string DisplayName => IsParentDirectory
        ? "[..]"
        : IsDirectory
            ? $"[{Name}]"
            : Name;

    public string IconPath
    {
        get
        {
            if (IsParentDirectory)
            {
                return $"{IconBasePath}3.png";
            }

            if (IsDirectory)
            {
                return $"{IconBasePath}1.png";
            }

            var extension = ExtensionLower;
            if (ImageExtensions.Contains(extension))
            {
                return $"{IconBasePath}5.png";
            }

            if (VideoExtensions.Contains(extension))
            {
                return $"{IconBasePath}7.png";
            }

            if (AudioExtensions.Contains(extension))
            {
                return $"{IconBasePath}8.png";
            }

            if (ArchiveExtensions.Contains(extension))
            {
                return $"{IconBasePath}17.png";
            }

            if (BinaryExtensions.Contains(extension))
            {
                return $"{IconBasePath}20.png";
            }

            if (TerminalExtensions.Contains(extension))
            {
                return $"{IconBasePath}10.png";
            }

            if (TextExtensions.Contains(extension))
            {
                return string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase)
                    ? $"{IconBasePath}4.png"
                    : $"{IconBasePath}11.png";
            }

            if (CodeExtensions.Contains(extension))
            {
                return $"{IconBasePath}11.png";
            }

            return $"{IconBasePath}4.png";
        }
    }

    public bool IsImageFile => !IsDirectory && !IsParentDirectory && ImageExtensions.Contains(ExtensionLower);

    public string TileImageSource => IsImageFile ? FullPath : IconPath;

    public string NameColor
    {
        get
        {
            if (IsParentDirectory)
            {
                return "#E8E8E8";
            }

            if (IsDirectory)
            {
                return "#E8E8E8";
            }

            if (!UseExtensionColors)
            {
                return "#E8E8E8";
            }

            var extension = ExtensionLower;
            if (ExtensionColorOverrides.TryGetValue(extension, out var overrideColor))
            {
                return overrideColor;
            }

            if (NeutralExtensions.Contains(extension))
            {
                return "#E8E8E8";
            }

            if (ImageExtensions.Contains(extension))
            {
                return "#A0A0A0";
            }

            if (VideoExtensions.Contains(extension))
            {
                return "#FFD54A";
            }

            if (AudioExtensions.Contains(extension))
            {
                return "#FFD54A";
            }

            if (ArchiveExtensions.Contains(extension))
            {
                return "#8FD66B";
            }

            if (BinaryExtensions.Contains(extension))
            {
                return "#FF4D4D";
            }

            if (TerminalExtensions.Contains(extension))
            {
                return "#FFA347";
            }

            if (string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
            {
                return "#A0A0A0";
            }

            if (TextExtensions.Contains(extension))
            {
                return "#66B3FF";
            }

            return "#E8E8E8";
        }
    }

    public string HighlightColor => IsPinned && UsePinnedHighlightColor ? "#FFD54A" : NameColor;
    public bool HasMemo => !string.IsNullOrWhiteSpace(Memo);

    public static void SetExtensionColorOverrides(IEnumerable<KeyValuePair<string, string>>? overrides)
    {
        ExtensionColorOverrides.Clear();
        if (overrides is null)
        {
            return;
        }

        foreach (var pair in overrides)
        {
            var key = pair.Key?.Trim().ToLowerInvariant();
            var value = pair.Value?.Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            ExtensionColorOverrides[key] = value;
        }
    }

    public string PropertyDisplay
    {
        get
        {
            if (IsParentDirectory)
            {
                return string.Empty;
            }

            var tags = new List<string>(3);
            if (IsFavorite)
            {
                tags.Add("즐겨찾기");
            }

            if (IsPinned)
            {
                tags.Add("핀고정");
            }

            if (HasMemo)
            {
                tags.Add("메모");
            }

            return tags.Count == 0 ? string.Empty : string.Join(",", tags);
        }
    }

    public string RenameCandidate
    {
        get => string.IsNullOrWhiteSpace(_renameCandidate) ? Name : _renameCandidate!;
        set
        {
            if (string.Equals(_renameCandidate, value, StringComparison.Ordinal))
            {
                return;
            }

            _renameCandidate = value;
            OnPropertyChanged();
        }
    }

    public bool IsInlineRenaming
    {
        get => _isInlineRenaming;
        set
        {
            if (_isInlineRenaming == value)
            {
                return;
            }

            _isInlineRenaming = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

