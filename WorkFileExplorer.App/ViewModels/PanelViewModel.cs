using WorkFileExplorer.App.Helpers;
using WorkFileExplorer.App.Models;

namespace WorkFileExplorer.App.ViewModels;

public sealed class PanelViewModel : ObservableObject
{
    private readonly List<FileSystemItem> _allItems = new();
    private int _directoryCount;
    private int _fileCount;
    private long _totalFileBytes;
    private string _currentPath = string.Empty;
    private FileSystemItem? _selectedItem;
    private string? _lastNonParentSelectedPath;
    private DateTime _suppressParentSelectionUntilUtc;
    private string _quickFilterText = string.Empty;
    private bool _quickFilterStartsWith;
    private bool _quickFilterExactMatch;
    private bool _quickFilterCaseSensitive;
    private bool _quickFilterIncludeFiles = true;
    private bool _quickFilterIncludeDirectories = true;

    public string CurrentPath
    {
        get => _currentPath;
        set => SetProperty(ref _currentPath, value);
    }

    public RangeObservableCollection<FileSystemItem> Items { get; } = new();

    public FileSystemItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (value is not null &&
                value.IsParentDirectory &&
                DateTime.UtcNow < _suppressParentSelectionUntilUtc)
            {
                LiveTrace.Write($"Panel.SelectedItem parent-attempt blocked path='{CurrentPath}' parent='{value.FullPath}' lastNonParent='{_lastNonParentSelectedPath ?? "(null)"}'");
                var fallback = ResolveNonParentSelectionCandidate();
                if (fallback is not null)
                {
                    LiveTrace.Write($"Panel.SelectedItem fallback path='{CurrentPath}' selected='{fallback.FullPath}'");
                    value = fallback;
                }
            }
            else if (value is not null && value.IsParentDirectory)
            {
                LiveTrace.Write($"Panel.SelectedItem parent-set path='{CurrentPath}' parent='{value.FullPath}' guardActive={DateTime.UtcNow < _suppressParentSelectionUntilUtc} lastNonParent='{_lastNonParentSelectedPath ?? "(null)"}'");
            }

            if (value is not null &&
                !value.IsParentDirectory &&
                !string.IsNullOrWhiteSpace(value.FullPath))
            {
                _lastNonParentSelectedPath = value.FullPath;
            }

            SetProperty(ref _selectedItem, value);
        }
    }

    public string? LastNonParentSelectedPath => _lastNonParentSelectedPath;
    public bool IsParentSelectionSuppressionActive => DateTime.UtcNow < _suppressParentSelectionUntilUtc;

    public void SuppressParentSelectionFor(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        if (until > _suppressParentSelectionUntilUtc)
        {
            _suppressParentSelectionUntilUtc = until;
            LiveTrace.Write($"Panel.SuppressParentSelection path='{CurrentPath}' untilUtc={_suppressParentSelectionUntilUtc:O}");
        }
    }

    public void ClearParentSelectionSuppression()
    {
        _suppressParentSelectionUntilUtc = DateTime.MinValue;
    }

    public bool TryRestoreNonParentSelection()
    {
        if (_selectedItem is null || !_selectedItem.IsParentDirectory)
        {
            return false;
        }

        var fallback = ResolveNonParentSelectionCandidate();
        if (fallback is null)
        {
            return false;
        }

        SelectedItem = fallback;
        return true;
    }

    public string QuickFilterText
    {
        get => _quickFilterText;
        set
        {
            if (!SetProperty(ref _quickFilterText, value))
            {
                return;
            }

            ApplyFilterInternal();
        }
    }

    public bool QuickFilterStartsWith
    {
        get => _quickFilterStartsWith;
        set
        {
            if (!SetProperty(ref _quickFilterStartsWith, value))
            {
                return;
            }

            if (value && _quickFilterExactMatch)
            {
                _quickFilterExactMatch = false;
                OnPropertyChanged(nameof(QuickFilterExactMatch));
            }

            ApplyFilterInternal();
        }
    }

    public bool QuickFilterExactMatch
    {
        get => _quickFilterExactMatch;
        set
        {
            if (!SetProperty(ref _quickFilterExactMatch, value))
            {
                return;
            }

            if (value && _quickFilterStartsWith)
            {
                _quickFilterStartsWith = false;
                OnPropertyChanged(nameof(QuickFilterStartsWith));
            }

            ApplyFilterInternal();
        }
    }

    public bool QuickFilterCaseSensitive
    {
        get => _quickFilterCaseSensitive;
        set
        {
            if (!SetProperty(ref _quickFilterCaseSensitive, value))
            {
                return;
            }

            ApplyFilterInternal();
        }
    }

    public bool QuickFilterIncludeFiles
    {
        get => _quickFilterIncludeFiles;
        set
        {
            if (!SetProperty(ref _quickFilterIncludeFiles, value))
            {
                return;
            }

            ApplyFilterInternal();
        }
    }

    public bool QuickFilterIncludeDirectories
    {
        get => _quickFilterIncludeDirectories;
        set
        {
            if (!SetProperty(ref _quickFilterIncludeDirectories, value))
            {
                return;
            }

            ApplyFilterInternal();
        }
    }

    public void SetItems(IEnumerable<FileSystemItem> items)
    {
        var incoming = items as IReadOnlyList<FileSystemItem> ?? items.ToList();
        var reconciled = ReconcileItems(incoming, _allItems);
        if (SameReferenceSequence(_allItems, reconciled))
        {
            return;
        }

        _allItems.Clear();
        _allItems.AddRange(reconciled);
        ApplyFilterInternal();
    }

    private static List<FileSystemItem> ReconcileItems(
        IReadOnlyList<FileSystemItem> incoming,
        IReadOnlyList<FileSystemItem> existing)
    {
        var byPath = new Dictionary<string, FileSystemItem>(StringComparer.OrdinalIgnoreCase);
        var duplicatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in existing)
        {
            if (string.IsNullOrWhiteSpace(item.FullPath))
            {
                continue;
            }

            if (!byPath.TryAdd(item.FullPath, item))
            {
                duplicatePaths.Add(item.FullPath);
            }
        }

        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FileSystemItem>(incoming.Count);
        foreach (var item in incoming)
        {
            if (!string.IsNullOrWhiteSpace(item.FullPath) &&
                !duplicatePaths.Contains(item.FullPath) &&
                usedPaths.Add(item.FullPath) &&
                byPath.TryGetValue(item.FullPath, out var old) &&
                FileSystemItem.HasSameRenderedState(old, item))
            {
                result.Add(old);
            }
            else
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static bool SameReferenceSequence(
        IReadOnlyList<FileSystemItem> left,
        IReadOnlyList<FileSystemItem> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!ReferenceEquals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    public IReadOnlyList<FileSystemItem> GetAllItems() => _allItems;

    public (int DirectoryCount, int FileCount, long TotalFileBytes) GetStats() =>
        (_directoryCount, _fileCount, _totalFileBytes);

    public void ApplyFilter(string keyword)
    {
        keyword ??= string.Empty;
        if (!string.Equals(_quickFilterText, keyword, StringComparison.Ordinal))
        {
            _quickFilterText = keyword;
            OnPropertyChanged(nameof(QuickFilterText));
            ApplyFilterInternal();
        }
    }

    public void ResetQuickFilter()
    {
        _quickFilterText = string.Empty;
        _quickFilterStartsWith = false;
        _quickFilterExactMatch = false;
        _quickFilterCaseSensitive = false;
        _quickFilterIncludeFiles = true;
        _quickFilterIncludeDirectories = true;

        OnPropertyChanged(nameof(QuickFilterText));
        OnPropertyChanged(nameof(QuickFilterStartsWith));
        OnPropertyChanged(nameof(QuickFilterExactMatch));
        OnPropertyChanged(nameof(QuickFilterCaseSensitive));
        OnPropertyChanged(nameof(QuickFilterIncludeFiles));
        OnPropertyChanged(nameof(QuickFilterIncludeDirectories));
        ApplyFilterInternal();
    }

    private void ApplyFilterInternal()
    {
        var previousSelectedPath = _selectedItem?.FullPath;
        var keyword = _quickFilterText ?? string.Empty;
        var hasKeyword = !string.IsNullOrWhiteSpace(keyword);
        var comparison = _quickFilterCaseSensitive
            ? StringComparison.CurrentCulture
            : StringComparison.CurrentCultureIgnoreCase;

        var filteredItems = new List<FileSystemItem>(_allItems.Count);
        FileSystemItem? selectedByPath = null;
        FileSystemItem? firstNonParent = null;

        foreach (var item in _allItems)
        {
            if (item.IsParentDirectory)
            {
                filteredItems.Add(item);
                continue;
            }

            if (!item.IsDirectory && !_quickFilterIncludeFiles)
            {
                continue;
            }

            if (item.IsDirectory && !_quickFilterIncludeDirectories)
            {
                continue;
            }

            if (hasKeyword)
            {
                var matches = _quickFilterExactMatch
                    ? string.Equals(item.Name, keyword, comparison)
                    : _quickFilterStartsWith
                        ? item.Name.StartsWith(keyword, comparison)
                        : item.Name.Contains(keyword, comparison);
                if (!matches)
                {
                    continue;
                }
            }

            filteredItems.Add(item);
            firstNonParent ??= item;
            if (selectedByPath is null &&
                !string.IsNullOrWhiteSpace(previousSelectedPath) &&
                string.Equals(item.FullPath, previousSelectedPath, StringComparison.OrdinalIgnoreCase))
            {
                selectedByPath = item;
            }
        }

        _directoryCount = 0;
        _fileCount = 0;
        _totalFileBytes = 0;
        foreach (var item in filteredItems)
        {
            if (item.IsDirectory)
            {
                _directoryCount++;
                continue;
            }

            _fileCount++;
            _totalFileBytes += item.SizeBytes;
        }

        Items.ReplaceRange(filteredItems);

        var nextSelected = selectedByPath
            ?? firstNonParent
            ?? filteredItems.FirstOrDefault();

        SelectedItem = nextSelected;
    }

    private FileSystemItem? ResolveNonParentSelectionCandidate()
    {
        if (!string.IsNullOrWhiteSpace(_lastNonParentSelectedPath))
        {
            var byPath = Items.FirstOrDefault(item =>
                !item.IsParentDirectory &&
                string.Equals(item.FullPath, _lastNonParentSelectedPath, StringComparison.OrdinalIgnoreCase));
            if (byPath is not null)
            {
                return byPath;
            }
        }

        return Items.FirstOrDefault(item => !item.IsParentDirectory);
    }
}


