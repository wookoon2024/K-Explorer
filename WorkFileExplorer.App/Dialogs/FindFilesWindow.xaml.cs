using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WorkFileExplorer.App.Models;
using WorkFileExplorer.App.ViewModels;

namespace WorkFileExplorer.App.Dialogs;

public partial class FindFilesWindow : Window
{
    private readonly Stopwatch _stopwatch = new();
    private CancellationTokenSource? _searchCts;

    public FindFilesWindow()
    {
        InitializeComponent();
        Loaded += OnFindFilesWindowLoaded;
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnFindFilesWindowLoaded(object sender, RoutedEventArgs e)
    {
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
        if (Vm is null || _searchCts is not null)
        {
            return;
        }

        _searchCts = new CancellationTokenSource();
        _stopwatch.Restart();
        Vm.FindResultSummary = "검색 중...";
        Vm.FindElapsedText = string.Empty;
        Vm.SearchResults.Clear();

        try
        {
            if (FindTabs.SelectedIndex != 4)
            {
                FindTabs.SelectedIndex = 4;
            }

            var options = BuildOptionsFromVm(Vm);
            var progress = new Progress<FileSystemItem>(item =>
            {
                Vm.SearchResults.Add(item);
                Vm.FindResultSummary = $"찾음: {Vm.SearchResults.Count}";
            });

            var results = await Vm.FindFilesAsync(options, _searchCts.Token, progress);
            Vm.FindResultSummary = $"찾음: {results.Count}";
        }
        catch (OperationCanceledException)
        {
            Vm.FindResultSummary = "검색 취소됨";
        }
        catch (Exception ex)
        {
            Vm.FindResultSummary = "검색 실패";
            StyledDialogWindow.ShowInfo(this, "검색 오류", ex.Message);
        }
        finally
        {
            _stopwatch.Stop();
            Vm.FindElapsedText = $"검색 시간: {_stopwatch.Elapsed:hh\\:mm\\:ss\\.fff}";
            _searchCts.Dispose();
            _searchCts = null;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _searchCts?.Cancel();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
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
        Vm.SearchUseMinSize = false;
        Vm.SearchUseMaxSize = false;
        Vm.SearchUseDateFrom = false;
        Vm.SearchUseDateTo = false;
        Vm.SearchDateFrom = DateTime.Today;
        Vm.SearchDateTo = DateTime.Today;
        Vm.SearchResults.Clear();
        Vm.FindResultSummary = "검색 준비";
        Vm.FindElapsedText = string.Empty;
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

    private static FindFilesOptions BuildOptionsFromVm(MainWindowViewModel vm)
    {
        var (searchSubdirectories, maxDepth) = ParseDepthOption(vm);

        long? minSize = null;
        if (vm.SearchUseMinSize && long.TryParse(vm.SearchMinSizeKb, out var minKb))
        {
            minSize = minKb;
        }

        long? maxSize = null;
        if (vm.SearchUseMaxSize && long.TryParse(vm.SearchMaxSizeKb, out var maxKb))
        {
            maxSize = maxKb;
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
            UseRegex = vm.SearchUseRegex,
            MinSizeKb = minSize,
            MaxSizeKb = maxSize,
            DateFrom = vm.SearchUseDateFrom ? vm.SearchDateFrom : null,
            DateTo = vm.SearchUseDateTo ? vm.SearchDateTo : null
        };
    }

    private static (bool SearchSubdirectories, int? MaxDepth) ParseDepthOption(MainWindowViewModel vm)
    {
        var option = (vm.SearchDepthOption ?? string.Empty).Trim();
        if (string.Equals(option, "현재 디렉터리만", StringComparison.Ordinal))
        {
            return (false, null);
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
