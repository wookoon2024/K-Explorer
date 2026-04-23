namespace WorkFileExplorer.App.Models;

public sealed class FindFilesOptions
{
    public string StartDirectory { get; init; } = string.Empty;
    public bool SearchSubdirectories { get; init; } = true;
    public int? MaxDepth { get; init; }
    public string FileMasks { get; init; } = "*";
    public string ExcludedDirectories { get; init; } = string.Empty;
    public string ExcludedFiles { get; init; } = string.Empty;
    public string TextQuery { get; init; } = string.Empty;
    public string EncodingName { get; init; } = "Default";
    public bool CaseSensitive { get; init; }
    public bool UseRegex { get; init; }
    public long? MinSizeKb { get; init; }
    public long? MaxSizeKb { get; init; }
    public DateTime? DateFrom { get; init; }
    public DateTime? DateTo { get; init; }
}

