using WorkFileExplorer.App.Models;

namespace WorkFileExplorer.App.ViewModels;

public sealed class SearchResultTabViewModel : ObservableObject
{
    private string _title;
    private string _summary = "검색 준비";
    private string _elapsedText = string.Empty;
    private string _conditionText = string.Empty;
    private bool _isSearching;

    public SearchResultTabViewModel(string baseTitle)
    {
        BaseTitle = baseTitle;
        _title = baseTitle;
    }

    public string BaseTitle { get; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public RangeObservableCollection<FileSystemItem> Results { get; } = new();

    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        set => SetProperty(ref _elapsedText, value);
    }

    public string ConditionText
    {
        get => _conditionText;
        set => SetProperty(ref _conditionText, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        set => SetProperty(ref _isSearching, value);
    }
}
