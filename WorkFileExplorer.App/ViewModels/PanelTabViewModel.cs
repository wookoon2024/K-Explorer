using System.Collections.ObjectModel;

namespace WorkFileExplorer.App.ViewModels;

public sealed class PanelTabViewModel : ObservableObject
{
    private string _title = string.Empty;
    private PanelViewMode _viewMode;
    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private readonly ObservableCollection<string> _historyCandidates = new();

    public PanelTabViewModel(string title, PanelViewModel panel)
    {
        _title = title;
        Panel = panel;
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public PanelViewModel Panel { get; }

    public bool IsTileViewEnabled
    {
        get => _viewMode != PanelViewMode.Details;
        set => ViewMode = value ? PanelViewMode.Tiles : PanelViewMode.Details;
    }

    public PanelViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (!SetProperty(ref _viewMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsTileViewEnabled));
            OnPropertyChanged(nameof(IsCompactListViewEnabled));
        }
    }

    public bool IsCompactListViewEnabled => _viewMode == PanelViewMode.CompactList;

    public IReadOnlyList<string> HistoryCandidates => _historyCandidates;

    public bool CanGoBack => _backStack.Count > 0;

    public bool CanGoForward => _forwardStack.Count > 0;

    public void RecordVisitedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Panel.CurrentPath) &&
            !string.Equals(Panel.CurrentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _backStack.Push(Panel.CurrentPath);
        }

        _forwardStack.Clear();
        AppendCandidate(path);
        RaiseHistoryChanged();
    }

    public void RecordPathWithoutHistoryShift(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        AppendCandidate(path);
        RaiseHistoryChanged();
    }

    public string? GoBack(string currentPath)
    {
        if (_backStack.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            _forwardStack.Push(currentPath);
        }

        var target = _backStack.Pop();
        AppendCandidate(target);
        RaiseHistoryChanged();
        return target;
    }

    public string? GoForward(string currentPath)
    {
        if (_forwardStack.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            _backStack.Push(currentPath);
        }

        var target = _forwardStack.Pop();
        AppendCandidate(target);
        RaiseHistoryChanged();
        return target;
    }

    public void InitializeHistory(IEnumerable<string> paths)
    {
        _backStack.Clear();
        _forwardStack.Clear();
        _historyCandidates.Clear();
        var orderedPaths = paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();

        foreach (var path in orderedPaths)
        {
            _historyCandidates.Add(path);
        }

        if (orderedPaths.Count > 0)
        {
            var currentPath = Panel.CurrentPath;
            var currentIndex = string.IsNullOrWhiteSpace(currentPath)
                ? 0
                : orderedPaths.FindIndex(path => string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase));

            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            // History candidates are stored from newest -> oldest.
            // Rebuild stack order so Back/Forward works immediately after restore.
            for (var i = orderedPaths.Count - 1; i > currentIndex; i--)
            {
                _backStack.Push(orderedPaths[i]);
            }

            for (var i = 0; i < currentIndex; i++)
            {
                _forwardStack.Push(orderedPaths[i]);
            }
        }

        RaiseHistoryChanged();
    }

    private void AppendCandidate(string path)
    {
        var existing = _historyCandidates.FirstOrDefault(item =>
            string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _historyCandidates.Remove(existing);
        }

        _historyCandidates.Insert(0, path);
        while (_historyCandidates.Count > 40)
        {
            _historyCandidates.RemoveAt(_historyCandidates.Count - 1);
        }
    }

    private void RaiseHistoryChanged()
    {
        OnPropertyChanged(nameof(HistoryCandidates));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }
}
