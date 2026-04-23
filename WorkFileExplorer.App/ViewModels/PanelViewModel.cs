using System.Collections.ObjectModel;
using WorkFileExplorer.App.Models;

namespace WorkFileExplorer.App.ViewModels;

public sealed class PanelViewModel : ObservableObject
{
    private readonly List<FileSystemItem> _allItems = new();
    private string _currentPath = string.Empty;
    private FileSystemItem? _selectedItem;
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

    public ObservableCollection<FileSystemItem> Items { get; } = new();

    public FileSystemItem? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
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
        _allItems.Clear();
        _allItems.AddRange(items);
        ApplyFilterInternal();
    }

    public IReadOnlyList<FileSystemItem> GetAllItems() => _allItems;

    public void ApplyFilter(string keyword)
    {
        keyword ??= string.Empty;
        if (!string.Equals(_quickFilterText, keyword, StringComparison.Ordinal))
        {
            _quickFilterText = keyword;
            OnPropertyChanged(nameof(QuickFilterText));
        }

        ApplyFilterInternal();
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
        var comparison = _quickFilterCaseSensitive
            ? StringComparison.CurrentCulture
            : StringComparison.CurrentCultureIgnoreCase;

        IEnumerable<FileSystemItem> query = _allItems.Where(item =>
        {
            if (item.IsParentDirectory)
            {
                return true;
            }

            if (!item.IsDirectory && !_quickFilterIncludeFiles)
            {
                return false;
            }

            if (item.IsDirectory && !_quickFilterIncludeDirectories)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(keyword))
            {
                return true;
            }

            if (_quickFilterExactMatch)
            {
                return string.Equals(item.Name, keyword, comparison);
            }

            if (_quickFilterStartsWith)
            {
                return item.Name.StartsWith(keyword, comparison);
            }

            return item.Name.Contains(keyword, comparison);
        });

        Items.Clear();
        foreach (var item in query)
        {
            Items.Add(item);
        }

        var nextSelected = !string.IsNullOrWhiteSpace(previousSelectedPath)
            ? Items.FirstOrDefault(item => string.Equals(item.FullPath, previousSelectedPath, StringComparison.OrdinalIgnoreCase))
            : null;

        nextSelected ??= Items.FirstOrDefault(item => !item.IsParentDirectory)
            ?? Items.FirstOrDefault();

        SelectedItem = nextSelected;
    }
}
