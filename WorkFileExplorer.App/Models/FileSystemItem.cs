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
    public static bool UseLightTheme { get; set; }
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
        ".txt", ".log", ".rtf", ".doc", ".docx", ".pdf", ".csv", ".hwp", ".hwpx", ".xls", ".xlsx", ".ppt", ".pptx"
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

    // Column display: the name already contains ".zip", so show just "zip".
    public string ExtensionDisplay => ExtensionLower.TrimStart('.');

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
                return $"{IconBasePath}icon_folder_up.png";
            }

            if (IsDirectory)
            {
                return $"{IconBasePath}icon_folder.png";
            }

            var extension = ExtensionLower;
            if (ImageExtensions.Contains(extension))
            {
                return $"{IconBasePath}icon_image.png";
            }

            if (VideoExtensions.Contains(extension))
            {
                return $"{IconBasePath}icon_video.png";
            }

            if (AudioExtensions.Contains(extension))
            {
                return $"{IconBasePath}icon_audio.png";
            }

            if (ArchiveExtensions.Contains(extension))
            {
                return $"{IconBasePath}icon_archive.png";
            }

            if (BinaryExtensions.Contains(extension))
            {
                return $"{IconBasePath}icon_exe.png";
            }

            if (TerminalExtensions.Contains(extension))
            {
                return $"{IconBasePath}icon_terminal.png";
            }

            if (TextExtensions.Contains(extension))
            {
                return string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase)
                    ? $"{IconBasePath}icon_file.png"
                    : $"{IconBasePath}icon_doc.png";
            }

            if (CodeExtensions.Contains(extension))
            {
                return $"{IconBasePath}icon_code.png";
            }

            return $"{IconBasePath}icon_file.png";
        }
    }

    public bool IsImageFile => !IsDirectory && !IsParentDirectory && ImageExtensions.Contains(ExtensionLower);

    public bool IsVideoFile => !IsDirectory && !IsParentDirectory && VideoExtensions.Contains(ExtensionLower);

    public bool HasTileThumbnail => IsImageFile || IsVideoFile;

    public string TileImageSource => HasTileThumbnail ? FullPath : IconPath;

    public static bool IsVideoExtension(string? extension) =>
        !string.IsNullOrEmpty(extension) && VideoExtensions.Contains(extension);

    public string NameColor
    {
        get
        {
            if (IsParentDirectory)
            {
                return UseLightTheme ? "#17212F" : "#E8E8E8";
            }

            if (IsDirectory)
            {
                return UseLightTheme ? "#17212F" : "#E8E8E8";
            }

            if (!UseExtensionColors)
            {
                return UseLightTheme ? "#17212F" : "#E8E8E8";
            }

            var extension = ExtensionLower;
            if (ExtensionColorOverrides.TryGetValue(extension, out var overrideColor))
            {
                return overrideColor;
            }

            if (NeutralExtensions.Contains(extension))
            {
                return UseLightTheme ? "#17212F" : "#E8E8E8";
            }

            if (ImageExtensions.Contains(extension))
            {
                return UseLightTheme ? "#7C3AED" : "#C89BF5";
            }

            if (VideoExtensions.Contains(extension))
            {
                return UseLightTheme ? "#C25E00" : "#FF9E5E";
            }

            if (AudioExtensions.Contains(extension))
            {
                return UseLightTheme ? "#C2255C" : "#F783AC";
            }

            if (ArchiveExtensions.Contains(extension))
            {
                return UseLightTheme ? "#B07D10" : "#E8B04B";
            }

            if (BinaryExtensions.Contains(extension))
            {
                return UseLightTheme ? "#C82222" : "#FF6B6B";
            }

            if (TerminalExtensions.Contains(extension))
            {
                return UseLightTheme ? "#2B8A3E" : "#69DB7C";
            }

            if (string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
            {
                return UseLightTheme ? "#5A6B7D" : "#C3CCD5";
            }

            if (TextExtensions.Contains(extension))
            {
                return UseLightTheme ? "#1267CC" : "#6CB6FF";
            }

            if (CodeExtensions.Contains(extension))
            {
                return UseLightTheme ? "#0E7490" : "#4DD4E8";
            }

            return UseLightTheme ? "#17212F" : "#E8E8E8";
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

    public static void SetThemeMode(string? themeMode)
    {
        UseLightTheme = string.Equals(themeMode, "White", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyDictionary<string, string> GetBuiltInExtensionColors(string? themeMode = null)
    {
        var lightTheme = string.Equals(themeMode, "White", StringComparison.OrdinalIgnoreCase);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static void AddRange(Dictionary<string, string> target, IEnumerable<string> extensions, string color)
        {
            foreach (var extension in extensions)
            {
                target[extension] = color;
            }
        }

        AddRange(map, ImageExtensions, lightTheme ? "#7C3AED" : "#C89BF5");
        AddRange(map, VideoExtensions, lightTheme ? "#C25E00" : "#FF9E5E");
        AddRange(map, AudioExtensions, lightTheme ? "#C2255C" : "#F783AC");
        AddRange(map, ArchiveExtensions, lightTheme ? "#B07D10" : "#E8B04B");
        AddRange(map, BinaryExtensions, lightTheme ? "#C82222" : "#FF6B6B");
        AddRange(map, TerminalExtensions, lightTheme ? "#2B8A3E" : "#69DB7C");
        AddRange(map, CodeExtensions, lightTheme ? "#0E7490" : "#4DD4E8");
        AddRange(map, TextExtensions, lightTheme ? "#1267CC" : "#6CB6FF");
        map[".txt"] = lightTheme ? "#5A6B7D" : "#C3CCD5";
        AddRange(map, NeutralExtensions, lightTheme ? "#0F172A" : "#E8E8E8");

        return map;
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

