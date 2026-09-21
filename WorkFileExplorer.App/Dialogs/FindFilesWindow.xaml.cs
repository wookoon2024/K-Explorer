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
    private sealed class SearchRun
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Stopwatch Stopwatch { get; } = new();
    }

    private readonly System.Windows.Threading.DispatcherTimer _elapsedTimer;
    private readonly Dictionary<SearchResultTabViewModel, SearchRun> _runs = new();
    private int _resultTabCount;

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
            foreach (var (session, run) in _runs)
            {
                session.ElapsedText = $"경과 시간: {run.Stopwatch.Elapsed:hh\\:mm\\:ss}";
            }
        };
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnFindFilesWindowLoaded(object sender, RoutedEventArgs e)
    {
        AdjustVisibleResultColumns();
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

        UpdateActionPanels();
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Vm.SearchStartDirectory))
        {
            StyledDialogWindow.ShowInfo(this, "알림", "시작 디렉터리를 입력하거나 선택한 뒤 검색하세요.");
            return;
        }

        // Every search gets its own result tab and cancellation token so several
        // searches can run side by side.
        var session = new SearchResultTabViewModel(NextResultTabTitle());
        var run = new SearchRun();
        _runs[session] = run;
        run.Stopwatch.Start();
        session.IsSearching = true;
        session.Summary = "검색 중...";

        var options = BuildOptionsFromVm(Vm);
        session.ConditionText = DescribeSearchConditions(options);

        AddResultTab(session);
        StartElapsedTimer();

        try
        {
            await Vm.RecordFindFilesSearchHistoryAsync();
            var progress = new Progress<IReadOnlyList<FileSystemItem>>(batch =>
            {
                foreach (var item in batch)
                {
                    session.Results.Add(item);
                }

                session.Summary = $"검색 중... 찾음: {session.Results.Count}개";
                AdjustVisibleResultColumns();
            });

            var results = await Vm.FindFilesAsync(options, run.Cancellation.Token, progress);
            session.Summary = options.MaxResults is { } maxResultCount && results.Count >= maxResultCount
                ? $"검색 완료: {results.Count}개 찾음 (최대 개수 도달, 더 있을 수 있음)"
                : $"검색 완료: {results.Count}개 찾음";
            session.Title = $"{session.BaseTitle}(완료)";
        }
        catch (OperationCanceledException)
        {
            session.Summary = $"검색 취소됨 (찾음: {session.Results.Count}개)";
            session.Title = $"{session.BaseTitle}(취소)";
        }
        catch (Exception ex)
        {
            session.Summary = "검색 실패";
            session.Title = $"{session.BaseTitle}(실패)";
            StyledDialogWindow.ShowInfo(this, "검색 오류", ex.Message);
        }
        finally
        {
            run.Stopwatch.Stop();
            session.IsSearching = false;
            session.ElapsedText = $"검색 시간: {run.Stopwatch.Elapsed:hh\\:mm\\:ss\\.fff}";
            run.Cancellation.Dispose();
            _runs.Remove(session);
            StopElapsedTimerIfIdle();
            AdjustVisibleResultColumns();
        }
    }

    private void OnStopSearchClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchResultTabViewModel session } &&
            _runs.TryGetValue(session, out var run))
        {
            run.Cancellation.Cancel();
        }
    }

    private string NextResultTabTitle()
    {
        _resultTabCount++;
        return $"결과{_resultTabCount}";
    }

    private void AddResultTab(SearchResultTabViewModel session)
    {
        var tab = new TabItem
        {
            Content = session,
            ContentTemplate = (DataTemplate)FindResource("ResultTabContentTemplate")
        };
        tab.SetBinding(TabItem.HeaderProperty, new Binding(nameof(SearchResultTabViewModel.Title)) { Source = session });

        var menu = new ContextMenu();

        var closeItem = new MenuItem { Header = "탭 닫기" };
        closeItem.Click += (_, _) => CloseResultTab(tab, session);
        menu.Items.Add(closeItem);

        menu.Items.Add(new Separator());

        var closeOthersItem = new MenuItem { Header = "이 탭만 남기고 닫기" };
        closeOthersItem.Click += (_, _) => CloseOtherResultTabs(tab);
        menu.Items.Add(closeOthersItem);

        menu.Items.Add(new Separator());

        var closeAllItem = new MenuItem { Header = "모든 탭 닫기" };
        closeAllItem.Click += (_, _) => CloseAllResultTabs();
        menu.Items.Add(closeAllItem);

        tab.ContextMenu = menu;
        // Right-clicking a tab acts on that tab, matching the usual tab behaviour.
        tab.PreviewMouseRightButtonDown += (_, _) => tab.IsSelected = true;

        FindTabs.Items.Add(tab);
        FindTabs.SelectedItem = tab;
    }

    private void CloseResultTab(TabItem tab, SearchResultTabViewModel session)
    {
        // Closing a tab stops the search it owns instead of letting it run unseen.
        if (_runs.TryGetValue(session, out var run))
        {
            run.Cancellation.Cancel();
        }

        FindTabs.Items.Remove(tab);
    }

    private void CloseOtherResultTabs(TabItem keep)
    {
        var others = FindTabs.Items
            .OfType<TabItem>()
            .Where(item => !ReferenceEquals(item, keep) && item.Content is SearchResultTabViewModel)
            .ToList();

        foreach (var tab in others)
        {
            if (tab.Content is SearchResultTabViewModel session)
            {
                CloseResultTab(tab, session);
            }
        }
    }

    private void CloseAllResultTabs()
    {
        var resultTabs = FindTabs.Items
            .OfType<TabItem>()
            .Where(item => item.Content is SearchResultTabViewModel)
            .ToList();

        foreach (var tab in resultTabs)
        {
            if (tab.Content is SearchResultTabViewModel session)
            {
                CloseResultTab(tab, session);
            }
        }

        if (!FindTabs.Items.OfType<TabItem>().Any(item => item.Content is SearchResultTabViewModel))
        {
            _resultTabCount = 0;
        }
    }

    private void StartElapsedTimer()
    {
        if (!_elapsedTimer.IsEnabled)
        {
            _elapsedTimer.Start();
        }
    }

    private void StopElapsedTimerIfIdle()
    {
        if (_runs.Count == 0)
        {
            _elapsedTimer.Stop();
        }
    }

    private void OnFindTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateActionPanels();
    }

    private void UpdateActionPanels()
    {
        // SelectionChanged fires while the visual tree is still being built.
        if (InputActionsPanel is null || ResultActionsPanel is null)
        {
            return;
        }

        var isResultTab = FindTabs.SelectedItem is TabItem { Content: SearchResultTabViewModel };
        InputActionsPanel.Visibility = isResultTab ? Visibility.Collapsed : Visibility.Visible;
        ResultActionsPanel.Visibility = isResultTab ? Visibility.Visible : Visibility.Collapsed;
    }

    private FileSystemItem? GetActiveResultItem()
    {
        return FindDescendant<ListView>(FindTabs) is { } listView
            ? listView.SelectedItem as FileSystemItem
            : null;
    }

    private List<FileSystemItem> GetSelectedResultItems()
    {
        return FindDescendant<ListView>(FindTabs) is { } listView
            ? listView.SelectedItems.Cast<FileSystemItem>().ToList()
            : new List<FileSystemItem>();
    }

    private void OnFindWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null || IsTextInputFocused())
        {
            return;
        }

        // "편집(외부 편집기)" shortcut (F4 by default) works on the result list too.
        var gesture = Vm.GetShortcutGesture(ShortcutCatalog.EditWithExternalEditor);
        if (!gesture.IsAssigned ||
            gesture.Key != ShortcutInput.ResolveKey(e) ||
            gesture.Modifiers != Keyboard.Modifiers)
        {
            return;
        }

        if (GetActiveResultItem() is not { IsDirectory: false } item)
        {
            return;
        }

        e.Handled = true;
        if (!Vm.TryOpenWithExternalEditor(item.FullPath, out var error))
        {
            StyledDialogWindow.ShowInfo(this, "오류", $"편집기를 실행할 수 없습니다.\n경로: {Vm.ExternalEditorPath}\n사유: {error}");
        }
    }

    private static bool IsTextInputFocused() =>
        Keyboard.FocusedElement is TextBox ||
        Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase ||
        Keyboard.FocusedElement is ComboBox;

    private async void OnOpenResultFolderClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || GetActiveResultItem() is not { } item)
        {
            return;
        }

        await Vm.OpenSearchResultAsync(item);
    }

    private async void OnDeleteResultClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var selected = GetSelectedResultItems();
        if (selected.Count == 0)
        {
            return;
        }

        var message = $"선택한 {selected.Count}개 항목을 삭제하시겠습니까?";
        if (Vm.ConfirmBeforeDelete && !StyledDialogWindow.ShowConfirm(this, "삭제 확인", message))
        {
            return;
        }

        await Vm.DeleteSelectedAsync(selected);
    }

    private async void OnFavoriteResultClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.AddFavoriteItemsAsync(GetSelectedResultItems());
    }

    private async void OnPinResultClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.TogglePinForItemsAsync(GetSelectedResultItems());
    }

    private void OnResultListViewLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView listView)
        {
            AdjustResultColumns(listView);
        }
    }

    private void OnResultsListViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is ListView listView)
        {
            AdjustResultColumns(listView);
        }
    }

    private void OnResultsHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header ||
            header.Role == GridViewColumnHeaderRole.Padding)
        {
            return;
        }

        if (header.Column?.Header is not string headerText)
        {
            return;
        }

        var sortMember = headerText switch
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

        var listView = FindAncestor<ListView>(header);
        var view = listView is null ? null : CollectionViewSource.GetDefaultView(listView.ItemsSource);
        if (view is null)
        {
            return;
        }

        // Toggle direction based on what this column's view currently shows so each
        // result tab keeps its own sort state.
        var direction = view.SortDescriptions.Count > 0 &&
                        view.SortDescriptions[0].PropertyName == sortMember &&
                        view.SortDescriptions[0].Direction == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(sortMember, direction));
        view.Refresh();
    }

    private void AdjustVisibleResultColumns()
    {
        if (FindDescendant<ListView>(FindTabs) is { IsLoaded: true, IsVisible: true } listView)
        {
            AdjustResultColumns(listView);
        }
    }

    private void AdjustResultColumns(ListView listView)
    {
        if (listView.View is not GridView gridView || gridView.Columns.Count < 3)
        {
            return;
        }

        // Keep the path column inside the viewport so long paths are trimmed with an
        // ellipsis (the full value stays available in the tooltip) instead of forcing
        // a horizontal scrollbar.
        var available = listView.ActualWidth
            - gridView.Columns[1].Width
            - gridView.Columns[2].Width
            - SystemParameters.VerticalScrollBarWidth
            - 14;
        if (available <= 200)
        {
            return;
        }

        gridView.Columns[0].Width = available;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? source) where T : DependencyObject
    {
        if (source is null)
        {
            return null;
        }

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(source);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(source, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        foreach (var run in _runs.Values.ToList())
        {
            run.Cancellation.Cancel();
        }

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

        // Reset only the input form; existing result tabs keep running/keep their results.
        if (FindTabs.SelectedIndex != 0)
        {
            FindTabs.SelectedIndex = 0;
        }
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
