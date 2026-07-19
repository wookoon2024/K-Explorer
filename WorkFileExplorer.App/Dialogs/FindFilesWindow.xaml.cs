using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using WorkFileExplorer.App.Models;
using WorkFileExplorer.App.ViewModels;

namespace WorkFileExplorer.App.Dialogs;

public partial class FindFilesWindow : Window
{
    private readonly Stopwatch _stopwatch = new();
    private readonly System.Windows.Threading.DispatcherTimer _elapsedTimer;
    private CancellationTokenSource? _searchCts;
    private string? _resultSortMember;
    private ListSortDirection _resultSortDirection = ListSortDirection.Ascending;

    public FindFilesWindow()
    {
        InitializeComponent();
        Loaded += OnFindFilesWindowLoaded;
        _elapsedTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _elapsedTimer.Tick += (_, _) =>
        {
            if (Vm is not null)
            {
                Vm.FindElapsedText = $"경과 시간: {_stopwatch.Elapsed:hh\\:mm\\:ss}";
            }
        };
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnFindFilesWindowLoaded(object sender, RoutedEventArgs e)
    {
        AdjustResultColumns();
        TopMenu.Visibility = Visibility.Collapsed;

        // Keep features wired, but hide placeholder tabs from UI.
        if (FindTabs.Items.Count > 2 && FindTabs.Items[2] is TabItem pluginTab)
        {
            pluginTab.Visibility = Visibility.Collapsed;
        }

        if (FindTabs.Items.Count > 3 && FindTabs.Items[3] is TabItem loadSaveTab)
        {
            loadSaveTab.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        // While a search is running the start button acts as a stop button.
        if (_searchCts is not null)
        {
            _searchCts.Cancel();
            return;
        }

        _searchCts = new CancellationTokenSource();
        _stopwatch.Restart();
        Vm.FindResultSummary = "검색 중...";
        Vm.FindElapsedText = string.Empty;
        Vm.SearchResults.Clear();
        StartSearchButton.Content = "검색 중지(S)";
        SearchProgressBar.Visibility = Visibility.Visible;
        _elapsedTimer.Start();

        try
        {
            if (FindTabs.SelectedIndex != 4)
            {
                FindTabs.SelectedIndex = 4;
            }

            var options = BuildOptionsFromVm(Vm);
            Vm.FindSearchConditionText = DescribeSearchConditions(options);
            await Vm.RecordFindFilesSearchHistoryAsync();
            var progress = new Progress<IReadOnlyList<FileSystemItem>>(batch =>
            {
                foreach (var item in batch)
                {
                    Vm.SearchResults.Add(item);
                }

                Vm.FindResultSummary = $"검색 중... 찾음: {Vm.SearchResults.Count}개";
                AdjustResultColumns();
            });

            var results = await Vm.FindFilesAsync(options, _searchCts.Token, progress);
            Vm.FindResultSummary = options.MaxResults is { } maxResultCount && results.Count >= maxResultCount
                ? $"검색 완료: {results.Count}개 찾음 (최대 개수 도달, 더 있을 수 있음)"
                : $"검색 완료: {results.Count}개 찾음";
        }
        catch (OperationCanceledException)
        {
            Vm.FindResultSummary = $"검색 취소됨 (찾음: {Vm.SearchResults.Count}개)";
        }
        catch (Exception ex)
        {
            Vm.FindResultSummary = "검색 실패";
            StyledDialogWindow.ShowInfo(this, "검색 오류", ex.Message);
        }
        finally
        {
            _elapsedTimer.Stop();
            _stopwatch.Stop();
            StartSearchButton.Content = "시작(S)";
            SearchProgressBar.Visibility = Visibility.Collapsed;
            AdjustResultColumns();
            Vm.FindElapsedText = $"검색 시간: {_stopwatch.Elapsed:hh\\:mm\\:ss\\.fff}";
            _searchCts.Dispose();
            _searchCts = null;
        }
    }

    private void OnResultsListViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        AdjustResultColumns();
    }

    private void OnResultsHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header ||
            header.Role == GridViewColumnHeaderRole.Padding)
        {
            return;
        }

        var sortMember = (header.Column?.Header as string) switch
        {
            "경로" => nameof(FileSystemItem.FullPath),
            "크기" => nameof(FileSystemItem.SizeBytes),
            "날짜" => nameof(FileSystemItem.LastModified),
            _ => null
        };
        if (sortMember is null)
        {
            return;
        }

        _resultSortDirection = string.Equals(_resultSortMember, sortMember, StringComparison.Ordinal) &&
                               _resultSortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        _resultSortMember = sortMember;

        var view = CollectionViewSource.GetDefaultView(ResultsListView.ItemsSource);
        if (view is null)
        {
            return;
        }

        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(sortMember, _resultSortDirection));
        view.Refresh();
    }

    private void AdjustResultColumns()
    {
        if (ResultsListView.View is not GridView gridView || gridView.Columns.Count < 3)
        {
            return;
        }

        var available = ResultsListView.ActualWidth
            - gridView.Columns[1].Width
            - gridView.Columns[2].Width
            - SystemParameters.VerticalScrollBarWidth
            - 14;
        if (available <= 200)
        {
            return;
        }

        // Long paths get the full width they need so the horizontal scrollbar can
        // reach them; short results keep the column stretched to the window width.
        var needed = MeasureWidestResultPathWidth();
        gridView.Columns[0].Width = Math.Max(available, needed + 18);
    }

    private double MeasureWidestResultPathWidth()
    {
        if (Vm is null || Vm.SearchResults.Count == 0)
        {
            return 0;
        }

        // Measuring every row is too expensive for large result sets; measure only the
        // few longest strings (character count is a close proxy for pixel width).
        var candidates = Vm.SearchResults
            .Select(item => item.FullPath)
            .Where(path => !string.IsNullOrEmpty(path))
            .OrderByDescending(path => path.Length)
            .Take(10);

        var typeface = new System.Windows.Media.Typeface(
            ResultsListView.FontFamily, ResultsListView.FontStyle, ResultsListView.FontWeight, ResultsListView.FontStretch);
        var pixelsPerDip = System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double widest = 0;
        foreach (var path in candidates)
        {
            var formatted = new System.Windows.Media.FormattedText(
                path,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                ResultsListView.FontSize,
                System.Windows.Media.Brushes.Black,
                pixelsPerDip);
            widest = Math.Max(widest, formatted.WidthIncludingTrailingWhitespace);
        }

        return widest;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _searchCts?.Cancel();
        Close();
    }

    private void OnNewSearchClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        Vm.SearchText = string.Empty;
        Vm.SearchFileMasks = "*";
        Vm.SearchExcludedDirectories = string.Empty;
        Vm.SearchExcludedFiles = string.Empty;
        Vm.SearchDepthOption = "모두 (무제한 깊이)";
        Vm.SearchTextQuery = string.Empty;
        Vm.SearchCaseSensitive = false;
        Vm.SearchUseRegex = false;
        Vm.SearchUseTextQuery = false;
        Vm.SearchRecursive = true;
        Vm.SearchMaxDepthText = string.Empty;
        Vm.SearchMinSizeKb = string.Empty;
        Vm.SearchMaxSizeKb = string.Empty;
        Vm.SearchMinSizeUnit = "KB";
        Vm.SearchMaxSizeUnit = "KB";
        Vm.SearchUseMinSize = false;
        Vm.SearchUseMaxSize = false;
        Vm.SearchExcludeHidden = false;
        Vm.SearchIncludeDirectories = false;
        Vm.SearchUseMaxResults = false;
        Vm.SearchMaxResultsText = string.Empty;
        Vm.SearchUseDateFrom = false;
        Vm.SearchUseDateTo = false;
        Vm.SearchDateFrom = DateTime.Today;
        Vm.SearchDateTo = DateTime.Today;
        Vm.SearchResults.Clear();
        Vm.FindResultSummary = "검색 준비";
        Vm.FindElapsedText = string.Empty;
        Vm.FindSearchConditionText = string.Empty;
    }

    private async void OnLastSearchClick(object sender, RoutedEventArgs e)
    {
        await ExecuteLastSearchAsync();
    }

    private async void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || sender is not ListView listView || listView.SelectedItem is not FileSystemItem item)
        {
            return;
        }

        await Vm.OpenSearchResultAsync(item);
    }

    private void OnMaskPresetClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not Button button)
        {
            return;
        }

        var preset = (button.Tag as string)?.Trim();
        Vm.SearchFileMasks = string.IsNullOrWhiteSpace(preset) ? "*" : preset;
    }

    private async Task ExecuteLastSearchAsync()
    {
        if (Vm is null)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() => OnStartClick(this, new RoutedEventArgs()));
    }

    private static string DescribeSearchConditions(FindFilesOptions options)
    {
        var startDirectory = string.IsNullOrWhiteSpace(options.StartDirectory)
            ? "(활성 패널 경로)"
            : options.StartDirectory;
        var condition = $"검색 조건: {startDirectory} | 마스크: {options.FileMasks}";
        if (options.ExactMatch)
        {
            condition += " (정확히 일치)";
        }

        if (!string.IsNullOrWhiteSpace(options.TextQuery))
        {
            condition += $" | 내용: {options.TextQuery}";
        }

        if (options.IncludeDirectories)
        {
            condition += " | 폴더 포함";
        }

        if (options.MaxResults is { } maxResults)
        {
            condition += $" | 최대 {maxResults}개";
        }

        return condition;
    }

    private static FindFilesOptions BuildOptionsFromVm(MainWindowViewModel vm)
    {
        var (searchSubdirectories, maxDepth) = ParseDepthOption(vm);

        long? minSize = null;
        if (vm.SearchUseMinSize && long.TryParse(vm.SearchMinSizeKb, out var minValue))
        {
            minSize = minValue * SizeUnitToKbMultiplier(vm.SearchMinSizeUnit);
        }

        long? maxSize = null;
        if (vm.SearchUseMaxSize && long.TryParse(vm.SearchMaxSizeKb, out var maxValue))
        {
            maxSize = maxValue * SizeUnitToKbMultiplier(vm.SearchMaxSizeUnit);
        }

        int? maxResults = null;
        if (vm.SearchUseMaxResults && int.TryParse(vm.SearchMaxResultsText, out var maxResultsValue) && maxResultsValue > 0)
        {
            maxResults = maxResultsValue;
        }

        return new FindFilesOptions
        {
            StartDirectory = vm.SearchStartDirectory,
            SearchSubdirectories = searchSubdirectories,
            MaxDepth = maxDepth,
            FileMasks = string.IsNullOrWhiteSpace(vm.SearchFileMasks) ? "*" : vm.SearchFileMasks,
            ExcludedDirectories = vm.SearchExcludedDirectories,
            ExcludedFiles = vm.SearchExcludedFiles,
            TextQuery = vm.SearchUseTextQuery ? vm.SearchTextQuery : string.Empty,
            EncodingName = vm.SearchEncoding,
            CaseSensitive = vm.SearchCaseSensitive,
            ExactMatch = vm.SearchExactMatch,
            UseRegex = vm.SearchUseRegex,
            MinSizeKb = minSize,
            MaxSizeKb = maxSize,
            DateFrom = vm.SearchUseDateFrom ? vm.SearchDateFrom : null,
            DateTo = vm.SearchUseDateTo ? vm.SearchDateTo : null,
            ExcludeHidden = vm.SearchExcludeHidden,
            IncludeDirectories = vm.SearchIncludeDirectories,
            MaxResults = maxResults
        };
    }

    private static long SizeUnitToKbMultiplier(string? unit) => unit switch
    {
        "MB" => 1024L,
        "GB" => 1024L * 1024L,
        _ => 1L
    };

    private static (bool SearchSubdirectories, int? MaxDepth) ParseDepthOption(MainWindowViewModel vm)
    {
        var option = (vm.SearchDepthOption ?? string.Empty).Trim();
        if (string.Equals(option, "현재 디렉터리만", StringComparison.Ordinal))
        {
            return (false, null);
        }

        // "모두 (무제한 깊이)" must always mean a full recursive search; without this it
        // fell through to the legacy SearchRecursive setting, which can silently turn
        // the search into a loaded-panel-items-only scan.
        if (option.StartsWith("모두", StringComparison.Ordinal))
        {
            return (true, null);
        }

        var parts = option.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && int.TryParse(parts[0], out var level) && level >= 0)
        {
            return (true, level);
        }

        // Legacy fallback for old persisted setting.
        if (int.TryParse(vm.SearchMaxDepthText, out var depth) && depth >= 0)
        {
            return (true, depth);
        }

        return (vm.SearchRecursive, null);
    }
}
