using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using WorkFileExplorer.App.Commands;
using WorkFileExplorer.App.Dialogs;
using WorkFileExplorer.App.Helpers;
using WorkFileExplorer.App.Models;
using WorkFileExplorer.App.Services.Interfaces;

namespace WorkFileExplorer.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    public sealed class UiSettingsSnapshot
    {
        public bool UseFourPanels { get; init; }
        public bool UseGridLayout { get; init; }
        public bool RememberSessionTabs { get; init; }
        public bool DefaultTileViewEnabled { get; init; }
        public bool UseExtensionColors { get; init; }
        public bool UsePinnedHighlightColor { get; init; }
        public bool ConfirmBeforeDelete { get; init; }
        public string ConflictPolicyDisplay { get; init; } = "Rename new";
        public string SearchScope { get; init; } = "Active panel";
        public bool SearchRecursive { get; init; } = true;
        public List<string> ExtensionColorOverrides { get; init; } = new();
    }

    private sealed class FourPanelTabsState
    {
        public List<FourPanelSlotTabsState> Slots { get; set; } = new();
    }

    private sealed class FourPanelSlotTabsState
    {
        public List<string> TabPaths { get; set; } = new();
        public int SelectedIndex { get; set; }
    }

    private sealed class CachedDirectoryItems
    {
        public DateTime CachedAtUtc { get; init; }
        public IReadOnlyList<FileSystemItem> Items { get; init; } = Array.Empty<FileSystemItem>();
    }

    private sealed class CachedFreeSpaceInfo
    {
        public DateTime CachedAtUtc { get; init; }
        public string Text { get; init; } = "-";
    }

    private const string DriveRootVirtualPath = "::DRIVES::";
    private const string MemoListVirtualPath = "::MEMO_LIST::";
    private const string FrequentFoldersVirtualPath = "::FREQUENT_FOLDERS::";
    private const string FrequentFilesVirtualPath = "::FREQUENT_FILES::";
    private const string DefaultFavoriteFileCategory = "기본";
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"
    };
    private readonly IFileSystemService _fileSystemService;
    private readonly ISettingsStorageService _settingsStorageService;
    private readonly IUsageTrackingService _usageTrackingService;
    private readonly IQuickAccessService _quickAccessService;
    private readonly IPathHistoryStoreService _pathHistoryStoreService;
    private readonly ObservableCollection<FourPanelSlotViewModel> _fourPanels = new();
    private readonly PanelViewModel _fallbackLeft = new();
    private readonly PanelViewModel _fallbackRight = new();

    private AppSettings _settings = new();
    private bool _isLeftPanelActive = true;
    private string _statusText = "Ready";
    private string _searchText = string.Empty;
    private string _searchExtension = string.Empty;
    private string _searchMinSizeKb = string.Empty;
    private string _searchMaxSizeKb = string.Empty;
    private string _searchFileMasks = "*";
    private string _searchExcludedDirectories = string.Empty;
    private string _searchExcludedFiles = string.Empty;
    private string _searchTextQuery = string.Empty;
    private string _searchEncoding = "Default";
    private string _searchStartDirectory = string.Empty;
    private string _searchMaxDepthText = string.Empty;
    private string _searchDepthOption = "모두 (무제한 깊이)";
    private bool _searchCaseSensitive;
    private bool _searchUseRegex;
    private bool _searchUseTextQuery;
    private bool _searchUseMinSize;
    private bool _searchUseMaxSize;
    private bool _searchUseDateFrom;
    private bool _searchUseDateTo;
    private DateTime _searchDateFrom = DateTime.Today;
    private DateTime _searchDateTo = DateTime.Today;
    private string _findResultSummary = "검색 준비";
    private string _findElapsedText = string.Empty;
    private string _searchScope = "Active panel";
    private string _selectedConflictPolicyDisplay = "Rename new";
    private bool _searchRecursive = true;
    private PanelTabViewModel? _selectedLeftTab;
    private PanelTabViewModel? _selectedRightTab;
    private string _leftSelectionSummary = "Sel 0";
    private string _rightSelectionSummary = "Sel 0";
    private string _leftFreeSpaceText = "-";
    private string _rightFreeSpaceText = "-";
    private string _selectedLeftDrive = "C:";
    private string _selectedRightDrive = "C:";
    private bool _isFourPanelMode;
    private bool _isFourPanelGridLayout;
    private bool _usageRefreshQueued;
    private bool _postMutationRefreshQueued;
    private readonly List<ClipboardTransferItem> _clipboardItems = new();
    private bool _clipboardCutMode;
    private readonly Dictionary<string, CachedDirectoryItems> _directoryItemsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedFreeSpaceInfo> _freeSpaceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _freeSpaceRefreshInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _directoryWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingWatcherRefreshPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _freeSpaceSync = new();
    private readonly object _watcherSync = new();
    private bool _watcherRefreshQueued;

    private static readonly TimeSpan DirectoryItemsCacheDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan FreeSpaceCacheDuration = TimeSpan.FromSeconds(3);
    private const int MaxDirectoryItemsCacheEntries = 24;

    public MainWindowViewModel(
        IFileSystemService fileSystemService,
        ISettingsStorageService settingsStorageService,
        IUsageTrackingService usageTrackingService,
        IQuickAccessService quickAccessService,
        IPathHistoryStoreService pathHistoryStoreService)
    {
        _fileSystemService = fileSystemService;
        _settingsStorageService = settingsStorageService;
        _usageTrackingService = usageTrackingService;
        _quickAccessService = quickAccessService;
        _pathHistoryStoreService = pathHistoryStoreService;

        LeftTabs.Add(new PanelTabViewModel("L1", new PanelViewModel()));
        RightTabs.Add(new PanelTabViewModel("R1", new PanelViewModel()));
        SelectedLeftTab = LeftTabs[0];
        SelectedRightTab = RightTabs[0];

        NavigatePanelPathCommand = new RelayCommand(p => RunCommandSafely(() => NavigatePanelPathAsync(p as string)));
        GoUpCommand = new RelayCommand(_ => RunCommandSafely(GoUpAsync));
        GoRootUpCommand = new RelayCommand(_ => RunCommandSafely(GoRootUpAsync));
        RefreshCommand = new RelayCommand(_ => RunCommandSafely(RefreshActivePanelAsync));
        OpenSelectedItemCommand = new RelayCommand(_ => RunCommandSafely(OpenSelectedItemAsync));
        SetActivePanelCommand = new RelayCommand(p => SetActivePanel(p as string));
        NavigateQuickAccessCommand = new RelayCommand(p => RunCommandSafely(() => NavigateQuickAccessAsync(p as QuickAccessItem)));
        OpenTrackedFileCommand = new RelayCommand(p => RunCommandSafely(() => OpenTrackedFileAsync(p as TrackedFileRecord)));
        SearchCommand = new RelayCommand(_ => RunCommandSafely(ApplySearchAsync));
        AddFavoriteCommand = new RelayCommand(_ => RunCommandSafely(AddFavoriteFromActiveSelectionAsync));
        SetPanelCountCommand = new RelayCommand(p => RunCommandSafely(() => SetPanelCountAsync(ParsePanelCount(p))));
        TogglePanelLayoutCommand = new RelayCommand(_ => RunCommandSafely(TogglePanelLayoutAsync));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        for (var index = 0; index < 4; index++)
        {
            _fourPanels.Add(new FourPanelSlotViewModel($"P{index + 1}", home));
        }

        _fourPanels[0].IsActive = true;
    }

    public ObservableCollection<PanelTabViewModel> LeftTabs { get; } = new();
    public ObservableCollection<PanelTabViewModel> RightTabs { get; } = new();
    public ObservableCollection<FourPanelSlotViewModel> FourPanels => _fourPanels;

    public PanelTabViewModel? SelectedLeftTab
    {
        get => _selectedLeftTab;
        set
        {
            var sw = Stopwatch.StartNew();
            var previousPath = _selectedLeftTab?.Panel.CurrentPath ?? string.Empty;
            var nextPath = value?.Panel.CurrentPath ?? string.Empty;
            if (!SetProperty(ref _selectedLeftTab, value))
            {
                return;
            }
            LiveTrace.Write($"TabSwitch[L] start '{previousPath}' -> '{nextPath}'");
            LiveTrace.WriteProcessSnapshot("TabSwitch[L] start");

            var step = Stopwatch.StartNew();
            OnPropertyChanged(nameof(LeftPanel));
            OnPropertyChanged(nameof(LeftCurrentPath));
            OnPropertyChanged(nameof(LeftPathHistory));
            OnPropertyChanged(nameof(LeftCanGoBack));
            OnPropertyChanged(nameof(LeftCanGoForward));
            OnPropertyChanged(nameof(LeftPanelIsTileViewEnabled));
            OnPropertyChanged(nameof(LeftPanelIsCompactListViewEnabled));
            LiveTrace.Write($"TabSwitch[L] notify-primary {step.ElapsedMilliseconds}ms");

            step.Restart();
            RefreshPanelFreeSpaceTexts();
            LiveTrace.Write($"TabSwitch[L] refresh-free-space {step.ElapsedMilliseconds}ms");

            step.Restart();
            OnPropertyChanged(nameof(LeftFolderInfo));
            OnPropertyChanged(nameof(StatusBarText));
            LiveTrace.Write($"TabSwitch[L] notify-summary {step.ElapsedMilliseconds}ms");
            if (IsLeftPanelActive)
            {
                step.Restart();
                OnPropertyChanged(nameof(IsTileViewEnabled));
                OnPropertyChanged(nameof(IsCompactListViewEnabled));
                LiveTrace.Write($"TabSwitch[L] notify-active-view {step.ElapsedMilliseconds}ms");
            }

            LiveTrace.Write($"TabSwitch[L] done {sw.ElapsedMilliseconds}ms");
            LiveTrace.WriteProcessSnapshot("TabSwitch[L] done");
        }
    }

    public PanelTabViewModel? SelectedRightTab
    {
        get => _selectedRightTab;
        set
        {
            var sw = Stopwatch.StartNew();
            var previousPath = _selectedRightTab?.Panel.CurrentPath ?? string.Empty;
            var nextPath = value?.Panel.CurrentPath ?? string.Empty;
            if (!SetProperty(ref _selectedRightTab, value))
            {
                return;
            }
            LiveTrace.Write($"TabSwitch[R] start '{previousPath}' -> '{nextPath}'");
            LiveTrace.WriteProcessSnapshot("TabSwitch[R] start");

            var step = Stopwatch.StartNew();
            OnPropertyChanged(nameof(RightPanel));
            OnPropertyChanged(nameof(RightCurrentPath));
            OnPropertyChanged(nameof(RightPathHistory));
            OnPropertyChanged(nameof(RightCanGoBack));
            OnPropertyChanged(nameof(RightCanGoForward));
            OnPropertyChanged(nameof(RightPanelIsTileViewEnabled));
            OnPropertyChanged(nameof(RightPanelIsCompactListViewEnabled));
            LiveTrace.Write($"TabSwitch[R] notify-primary {step.ElapsedMilliseconds}ms");

            step.Restart();
            RefreshPanelFreeSpaceTexts();
            LiveTrace.Write($"TabSwitch[R] refresh-free-space {step.ElapsedMilliseconds}ms");

            step.Restart();
            OnPropertyChanged(nameof(RightFolderInfo));
            OnPropertyChanged(nameof(StatusBarText));
            LiveTrace.Write($"TabSwitch[R] notify-summary {step.ElapsedMilliseconds}ms");
            if (!IsLeftPanelActive)
            {
                step.Restart();
                OnPropertyChanged(nameof(IsTileViewEnabled));
                OnPropertyChanged(nameof(IsCompactListViewEnabled));
                LiveTrace.Write($"TabSwitch[R] notify-active-view {step.ElapsedMilliseconds}ms");
            }

            LiveTrace.Write($"TabSwitch[R] done {sw.ElapsedMilliseconds}ms");
            LiveTrace.WriteProcessSnapshot("TabSwitch[R] done");
        }
    }

    public PanelViewModel LeftPanel => SelectedLeftTab?.Panel ?? _fallbackLeft;
    public PanelViewModel RightPanel => SelectedRightTab?.Panel ?? _fallbackRight;

    public string LeftCurrentPath
    {
        get => LeftPanel.CurrentPath;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            LeftPanel.CurrentPath = value;
            OnPropertyChanged(nameof(LeftFolderInfo));
            OnPropertyChanged(nameof(StatusBarText));
        }
    }

    public string RightCurrentPath
    {
        get => RightPanel.CurrentPath;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            RightPanel.CurrentPath = value;
            OnPropertyChanged(nameof(RightFolderInfo));
            OnPropertyChanged(nameof(StatusBarText));
        }
    }

    public IReadOnlyList<string> LeftPathHistory => SelectedLeftTab?.HistoryCandidates ?? Array.Empty<string>();

    public IReadOnlyList<string> RightPathHistory => SelectedRightTab?.HistoryCandidates ?? Array.Empty<string>();

    public bool LeftCanGoBack => SelectedLeftTab?.CanGoBack ?? false;

    public bool LeftCanGoForward => SelectedLeftTab?.CanGoForward ?? false;

    public bool RightCanGoBack => SelectedRightTab?.CanGoBack ?? false;

    public bool RightCanGoForward => SelectedRightTab?.CanGoForward ?? false;

    public string LeftSelectionSummary
    {
        get => _leftSelectionSummary;
        set
        {
            if (!SetProperty(ref _leftSelectionSummary, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StatusBarText));
        }
    }

    public string RightSelectionSummary
    {
        get => _rightSelectionSummary;
        set
        {
            if (!SetProperty(ref _rightSelectionSummary, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StatusBarText));
        }
    }

    public string StatusBarText => $"{StatusText} | {BuildPanelSummary(LeftPanel, "L")} {LeftSelectionSummary} | {BuildPanelSummary(RightPanel, "R")} {RightSelectionSummary}";

    public IReadOnlyList<string> AvailableDrives { get; } = Environment.GetLogicalDrives()
        .Select(static d => d.TrimEnd('\\'))
        .Where(static d => !string.IsNullOrWhiteSpace(d))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(static d => d, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public string SelectedLeftDrive
    {
        get => _selectedLeftDrive;
        set => SetProperty(ref _selectedLeftDrive, value);
    }

    public string SelectedRightDrive
    {
        get => _selectedRightDrive;
        set => SetProperty(ref _selectedRightDrive, value);
    }

    public string LeftFreeSpaceText
    {
        get => _leftFreeSpaceText;
        set => SetProperty(ref _leftFreeSpaceText, value);
    }

    public string RightFreeSpaceText
    {
        get => _rightFreeSpaceText;
        set => SetProperty(ref _rightFreeSpaceText, value);
    }

    public string LeftFolderInfo => BuildFolderInfo(LeftPanel);
    public string RightFolderInfo => BuildFolderInfo(RightPanel);

    public ObservableCollection<QuickAccessItem> QuickAccessItems { get; } = new();
    public ObservableCollection<QuickAccessItem> FavoriteToolbarFolders { get; } = new();
    public ObservableCollection<TrackedFileRecord> FavoriteToolbarFiles { get; } = new();
    public ObservableCollection<TrackedFileRecord> RecentFiles { get; } = new();
    public ObservableCollection<TrackedFileRecord> FrequentFiles { get; } = new();
    public ObservableCollection<QuickAccessItem> PinnedFolders { get; } = new();
    public ObservableCollection<TrackedFileRecord> PinnedFiles { get; } = new();
    public ObservableCollection<FileSystemItem> SearchResults { get; } = new();
    public ObservableCollection<string> WorkLogs { get; } = new();

    public IReadOnlyList<string> SearchScopes { get; } = ["Active panel", "Both panels"];
    public IReadOnlyList<string> SearchDepthOptions { get; } =
    [
        "모두 (무제한 깊이)",
        "현재 디렉터리만",
        "1 수준",
        "2 수준",
        "3 수준",
        "4 수준",
        "5 수준",
        "6 수준"
    ];
    public IReadOnlyList<string> SearchEncodings { get; } = ["Default", "UTF-8", "ANSI", "OEM", "cp1250", "cp1251", "cp1252", "cp1253"];
    public IReadOnlyList<string> ConflictPolicyOptions { get; } = ["Rename new", "Overwrite", "Skip"];

    public RelayCommand NavigatePanelPathCommand { get; }
    public RelayCommand GoUpCommand { get; }
    public RelayCommand GoRootUpCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenSelectedItemCommand { get; }
    public RelayCommand SetActivePanelCommand { get; }
    public RelayCommand NavigateQuickAccessCommand { get; }
    public RelayCommand OpenTrackedFileCommand { get; }
    public RelayCommand SearchCommand { get; }
    public RelayCommand AddFavoriteCommand { get; }
    public RelayCommand SetPanelCountCommand { get; }
    public RelayCommand TogglePanelLayoutCommand { get; }

    public bool IsLeftPanelActive
    {
        get => _isLeftPanelActive;
        set => SetProperty(ref _isLeftPanelActive, value);
    }

    public bool IsTileViewEnabled
    {
        get => IsLeftPanelActive
            ? (SelectedLeftTab?.IsTileViewEnabled ?? false)
            : (SelectedRightTab?.IsTileViewEnabled ?? false);
        set => SetTileViewForActivePanel(value);
    }

    public bool IsCompactListViewEnabled => IsLeftPanelActive
        ? (SelectedLeftTab?.IsCompactListViewEnabled ?? false)
        : (SelectedRightTab?.IsCompactListViewEnabled ?? false);

    public bool LeftPanelIsTileViewEnabled => SelectedLeftTab?.IsTileViewEnabled ?? false;

    public bool RightPanelIsTileViewEnabled => SelectedRightTab?.IsTileViewEnabled ?? false;

    public bool LeftPanelIsCompactListViewEnabled => SelectedLeftTab?.IsCompactListViewEnabled ?? false;

    public bool RightPanelIsCompactListViewEnabled => SelectedRightTab?.IsCompactListViewEnabled ?? false;

    public bool IsFourPanelMode
    {
        get => _isFourPanelMode;
        set
        {
            if (!SetProperty(ref _isFourPanelMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsTwoPanelMode));
            OnPropertyChanged(nameof(PanelModeLabel));
            OnPropertyChanged(nameof(IsFourPanelHorizontalLayout));
        }
    }

    public bool IsTwoPanelMode => !IsFourPanelMode;

    public bool IsFourPanelGridLayout
    {
        get => _isFourPanelGridLayout;
        set
        {
            if (!SetProperty(ref _isFourPanelGridLayout, value))
            {
                return;
            }

            OnPropertyChanged(nameof(PanelModeLabel));
            OnPropertyChanged(nameof(IsFourPanelHorizontalLayout));
        }
    }

    public string PanelModeLabel => IsFourPanelMode
        ? (IsFourPanelGridLayout ? "4P 2x2" : "4P 1x4")
        : "2P";

    public bool IsFourPanelHorizontalLayout => IsFourPanelMode && !IsFourPanelGridLayout;

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (!SetProperty(ref _statusText, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StatusBarText));
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string SearchExtension
    {
        get => _searchExtension;
        set => SetProperty(ref _searchExtension, value);
    }

    public string SearchMinSizeKb
    {
        get => _searchMinSizeKb;
        set => SetProperty(ref _searchMinSizeKb, value);
    }

    public string SearchMaxSizeKb
    {
        get => _searchMaxSizeKb;
        set => SetProperty(ref _searchMaxSizeKb, value);
    }

    public string SearchScope
    {
        get => _searchScope;
        set => SetProperty(ref _searchScope, value);
    }

    public string SearchFileMasks
    {
        get => _searchFileMasks;
        set => SetProperty(ref _searchFileMasks, value);
    }

    public string SearchExcludedDirectories
    {
        get => _searchExcludedDirectories;
        set => SetProperty(ref _searchExcludedDirectories, value);
    }

    public string SearchExcludedFiles
    {
        get => _searchExcludedFiles;
        set => SetProperty(ref _searchExcludedFiles, value);
    }

    public string SearchTextQuery
    {
        get => _searchTextQuery;
        set => SetProperty(ref _searchTextQuery, value);
    }

    public string SearchEncoding
    {
        get => _searchEncoding;
        set => SetProperty(ref _searchEncoding, value);
    }

    public bool SearchCaseSensitive
    {
        get => _searchCaseSensitive;
        set => SetProperty(ref _searchCaseSensitive, value);
    }

    public bool SearchUseRegex
    {
        get => _searchUseRegex;
        set => SetProperty(ref _searchUseRegex, value);
    }

    public string SearchStartDirectory
    {
        get => _searchStartDirectory;
        set => SetProperty(ref _searchStartDirectory, value);
    }

    public string SearchMaxDepthText
    {
        get => _searchMaxDepthText;
        set => SetProperty(ref _searchMaxDepthText, value);
    }

    public string SearchDepthOption
    {
        get => _searchDepthOption;
        set => SetProperty(ref _searchDepthOption, value);
    }

    public bool SearchUseTextQuery
    {
        get => _searchUseTextQuery;
        set => SetProperty(ref _searchUseTextQuery, value);
    }

    public bool SearchUseMinSize
    {
        get => _searchUseMinSize;
        set => SetProperty(ref _searchUseMinSize, value);
    }

    public bool SearchUseMaxSize
    {
        get => _searchUseMaxSize;
        set => SetProperty(ref _searchUseMaxSize, value);
    }

    public bool SearchUseDateFrom
    {
        get => _searchUseDateFrom;
        set => SetProperty(ref _searchUseDateFrom, value);
    }

    public bool SearchUseDateTo
    {
        get => _searchUseDateTo;
        set => SetProperty(ref _searchUseDateTo, value);
    }

    public DateTime SearchDateFrom
    {
        get => _searchDateFrom;
        set => SetProperty(ref _searchDateFrom, value);
    }

    public DateTime SearchDateTo
    {
        get => _searchDateTo;
        set => SetProperty(ref _searchDateTo, value);
    }

    public string FindResultSummary
    {
        get => _findResultSummary;
        set => SetProperty(ref _findResultSummary, value);
    }

    public string FindElapsedText
    {
        get => _findElapsedText;
        set => SetProperty(ref _findElapsedText, value);
    }

    public string SelectedConflictPolicyDisplay
    {
        get => _selectedConflictPolicyDisplay;
        set => SetProperty(ref _selectedConflictPolicyDisplay, value);
    }

    public bool SearchRecursive
    {
        get => _searchRecursive;
        set => SetProperty(ref _searchRecursive, value);
    }

    public bool ConfirmBeforeDelete
    {
        get => _settings.ConfirmBeforeDelete;
        set => _settings.ConfirmBeforeDelete = value;
    }

    public async Task InitializeAsync()
    {
        LiveTrace.Write("InitializeAsync begin");
        try
        {
            _settings = await _settingsStorageService.LoadSettingsAsync();
            LiveTrace.Write("Settings loaded");
        }
        catch
        {
            _settings = new AppSettings();
            LiveTrace.Write("Settings load failed; fallback defaults");
        }

        IsFourPanelMode = _settings.PanelCount >= 4;
        IsFourPanelGridLayout = string.Equals(_settings.PanelLayout, "Grid", StringComparison.OrdinalIgnoreCase);
        SearchScope = SearchScopes.Contains(_settings.DefaultSearchScope, StringComparer.OrdinalIgnoreCase)
            ? _settings.DefaultSearchScope
            : "Active panel";
        SearchRecursive = _settings.DefaultSearchRecursive;
        SelectedConflictPolicyDisplay = ConflictPolicyOptions.Contains(_settings.ConflictPolicyDisplay, StringComparer.OrdinalIgnoreCase)
            ? _settings.ConflictPolicyDisplay
            : "Rename new";
        FileSystemItem.UseExtensionColors = _settings.UseExtensionColors;
        FileSystemItem.UsePinnedHighlightColor = _settings.UsePinnedHighlightColor;
        _settings.ExtensionColorOverrides = NormalizeExtensionColorOverrides(_settings.ExtensionColorOverrides);
        FileSystemItem.SetExtensionColorOverrides(BuildExtensionColorMap(_settings.ExtensionColorOverrides));
        NormalizeFavoriteFileCategorySettings();
        RestoreSessionTabsFromSettings();
        ApplyDefaultViewToAllTabs(_settings.DefaultTileViewEnabled);
        SearchStartDirectory = LeftCurrentPath;
        LiveTrace.Write($"Initial paths: left='{LeftCurrentPath}', right='{RightCurrentPath}'");

        await RestorePathHistoryAsync();

        foreach (var tab in LeftTabs)
        {
            await LoadPanelAsync(tab.Panel, tab.Panel.CurrentPath, false);
        }

        foreach (var tab in RightTabs)
        {
            await LoadPanelAsync(tab.Panel, tab.Panel.CurrentPath, false);
        }

        await InitializeFourPanelsAsync();
        LiveTrace.Write("Panels initialized");
        try
        {
            await RefreshSidebarAndDashboardAsync();
            LiveTrace.Write("Sidebar/dashboard initialized");
        }
        catch
        {
            LiveTrace.Write("Sidebar/dashboard init failed");
        }

        LiveTrace.Write("InitializeAsync end");
    }

    public async Task SaveSessionStateAsync()
    {
        try
        {
            CaptureSessionState();
            await PersistPathHistoryAsync();
            await _settingsStorageService.SaveSettingsAsync(_settings);
        }
        catch (Exception ex)
        {
            LiveTrace.Write($"SaveSessionStateAsync failed: {ex}");
        }
    }

    public void UpdateWindowPlacement(double left, double top, double width, double height, bool isMaximized)
    {
        _settings.WindowLeft = left;
        _settings.WindowTop = top;
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        _settings.WindowMaximized = isMaximized;
    }

    public async Task AddPanelTabAsync(bool left, string? explicitSourcePath = null)
    {
        var tabs = left ? LeftTabs : RightTabs;
        var sourcePath = string.IsNullOrWhiteSpace(explicitSourcePath)
            ? (left ? LeftPanel.CurrentPath : RightPanel.CurrentPath)
            : explicitSourcePath;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            sourcePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        var tab = new PanelTabViewModel(left ? $"L{tabs.Count + 1}" : $"R{tabs.Count + 1}", new PanelViewModel())
        {
            IsTileViewEnabled = _settings.DefaultTileViewEnabled
        };
        tabs.Add(tab);
        if (left) SelectedLeftTab = tab; else SelectedRightTab = tab;
        await LoadPanelAsync(tab.Panel, sourcePath, false);
    }

    public void CloseCurrentPanelTab(bool left)
    {
        var tabs = left ? LeftTabs : RightTabs;
        var selected = left ? SelectedLeftTab : SelectedRightTab;
        if (tabs.Count <= 1 || selected is null)
        {
            return;
        }

        var idx = tabs.IndexOf(selected);
        tabs.Remove(selected);
        var next = Math.Clamp(idx - 1, 0, tabs.Count - 1);
        if (left) SelectedLeftTab = tabs[next]; else SelectedRightTab = tabs[next];
    }

    public async Task DuplicateCurrentPanelTabAsync(bool left)
    {
        var currentPath = left ? LeftPanel.CurrentPath : RightPanel.CurrentPath;
        await AddPanelTabAsync(left);
        await LoadPanelAsync(left ? LeftPanel : RightPanel, currentPath, false);
    }

    public void CloseOtherTabs(bool left)
    {
        var tabs = left ? LeftTabs : RightTabs;
        var selected = left ? SelectedLeftTab : SelectedRightTab;
        if (selected is null)
        {
            return;
        }

        for (var i = tabs.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(tabs[i], selected))
            {
                tabs.RemoveAt(i);
            }
        }
    }

    public void CloseTabsToLeft(bool left)
    {
        var tabs = left ? LeftTabs : RightTabs;
        var selected = left ? SelectedLeftTab : SelectedRightTab;
        if (selected is null)
        {
            return;
        }

        var idx = tabs.IndexOf(selected);
        for (var i = idx - 1; i >= 0; i--)
        {
            tabs.RemoveAt(i);
        }
    }

    public void CloseTabsToRight(bool left)
    {
        var tabs = left ? LeftTabs : RightTabs;
        var selected = left ? SelectedLeftTab : SelectedRightTab;
        if (selected is null)
        {
            return;
        }

        var idx = tabs.IndexOf(selected);
        for (var i = tabs.Count - 1; i > idx; i--)
        {
            tabs.RemoveAt(i);
        }
    }

    public void MoveTab(bool left, int fromIndex, int toIndex)
    {
        var tabs = left ? LeftTabs : RightTabs;
        if (fromIndex < 0 || fromIndex >= tabs.Count || toIndex < 0 || toIndex >= tabs.Count || fromIndex == toIndex)
        {
            return;
        }

        tabs.Move(fromIndex, toIndex);
    }

    public void UpdateSelectionSummary(bool left, IReadOnlyList<FileSystemItem> selectedItems)
    {
        string summary;
        if (selectedItems.Count == 0)
        {
            summary = "Sel 0";
        }
        else
        {
            var files = selectedItems.Where(item => !item.IsDirectory).ToArray();
            var total = files.Sum(item => item.SizeBytes);
            var avg = files.Length > 0 ? total / files.Length : 0;
            var extTop = files
                .GroupBy(item => string.IsNullOrWhiteSpace(item.Extension) ? "(none)" : item.Extension.ToLowerInvariant())
                .OrderByDescending(group => group.Count())
                .Take(2)
                .Select(group => $"{group.Key}x{group.Count()}");

            var extText = string.Join(",", extTop);
            summary = files.Length == 0
                ? $"Sel {selectedItems.Count}"
                : $"Sel {selectedItems.Count} ({ToReadableSize(total)}, avg {ToReadableSize(avg)}) [{extText}]";
        }

        if (left)
        {
            LeftSelectionSummary = summary;
        }
        else
        {
            RightSelectionSummary = summary;
        }
    }

    public async Task NavigatePanelBackAsync(bool left)
    {
        var tab = left ? SelectedLeftTab : SelectedRightTab;
        var panel = left ? LeftPanel : RightPanel;
        if (tab is null)
        {
            return;
        }

        var target = tab.GoBack(panel.CurrentPath);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        await LoadPanelAsync(panel, target, false, suppressHistoryRecord: true);
    }

    public async Task NavigatePanelForwardAsync(bool left)
    {
        var tab = left ? SelectedLeftTab : SelectedRightTab;
        var panel = left ? LeftPanel : RightPanel;
        if (tab is null)
        {
            return;
        }

        var target = tab.GoForward(panel.CurrentPath);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        await LoadPanelAsync(panel, target, false, suppressHistoryRecord: true);
    }

    public async Task OpenSelectedItemAsync()
    {
        var panel = GetActivePanel();
        if (panel.SelectedItem is null) return;
        await OpenItemAsync(panel, panel.SelectedItem);
    }

    public async Task OpenSearchResultAsync(FileSystemItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.FullPath))
        {
            return;
        }

        var activeLeft = IsLeftPanelActive;
        var targetPath = item.FullPath;
        var openFolderPath = item.IsDirectory
            ? Path.GetDirectoryName(item.FullPath)
            : Path.GetDirectoryName(item.FullPath);

        if (string.IsNullOrWhiteSpace(openFolderPath))
        {
            // Root folder search result fallback: open itself when no parent exists.
            openFolderPath = item.IsDirectory ? item.FullPath : null;
        }

        if (string.IsNullOrWhiteSpace(openFolderPath) || !_fileSystemService.DirectoryExists(openFolderPath))
        {
            StatusText = "검색 결과 경로를 열 수 없습니다.";
            return;
        }

        await AddPanelTabAsync(activeLeft, explicitSourcePath: openFolderPath);
        var panel = activeLeft ? LeftPanel : RightPanel;

        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            panel.SelectedItem = panel.Items.FirstOrDefault(entry =>
                string.Equals(entry.FullPath, targetPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task OpenMemoListTabAsync()
    {
        await OpenVirtualTabAsync(MemoListVirtualPath);
    }

    public async Task OpenFrequentFoldersTabAsync()
    {
        await OpenVirtualTabAsync(FrequentFoldersVirtualPath);
    }

    public async Task OpenFrequentFilesTabAsync()
    {
        await OpenVirtualTabAsync(FrequentFilesVirtualPath);
    }

    public bool IsMemoListPanel(bool leftPanel)
    {
        var path = leftPanel ? LeftPanel.CurrentPath : RightPanel.CurrentPath;
        return string.Equals(path, MemoListVirtualPath, StringComparison.Ordinal);
    }

    public async Task OpenMemoListItemAsync(bool leftPanel, FileSystemItem? item)
    {
        if (item is null)
        {
            return;
        }

        var sourcePanel = leftPanel ? LeftPanel : RightPanel;
        await OpenMemoListEntryAsync(sourcePanel, item);
    }

    public async Task OpenContainingFolderAsync(FileSystemItem? item)
    {
        if (item is null) return;
        var folderPath = item.IsDirectory ? item.FullPath : Path.GetDirectoryName(item.FullPath);
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;
        await LoadPanelAsync(GetActivePanel(), folderPath, true);
    }

    public async Task OpenItemFromPanelAsync(bool leftPanel)
    {
        SetActivePanel(leftPanel ? "Left" : "Right");
        var panel = leftPanel ? LeftPanel : RightPanel;
        if (panel.SelectedItem is null) return;
        await OpenItemAsync(panel, panel.SelectedItem);
    }

    public async Task CopySelectedToOtherPanelAsync(IReadOnlyList<FileSystemItem>? selectedItems = null)
    {
        var sourcePanel = GetActivePanel();
        var targetPanel = GetPassivePanel();
        await CopyOrMoveBetweenPanelsAsync(sourcePanel, targetPanel, selectedItems, move: false);
    }

    public async Task MoveSelectedToOtherPanelAsync(IReadOnlyList<FileSystemItem>? selectedItems = null)
    {
        var sourcePanel = GetActivePanel();
        var targetPanel = GetPassivePanel();
        await CopyOrMoveBetweenPanelsAsync(sourcePanel, targetPanel, selectedItems, move: true);
    }

    public bool HasClipboardItems => _clipboardItems.Count > 0;

    public void CopySelectionToClipboard(IReadOnlyList<FileSystemItem> selectedItems)
    {
        SetClipboardItems(selectedItems, cutMode: false);
    }

    public void CutSelectionToClipboard(IReadOnlyList<FileSystemItem> selectedItems)
    {
        SetClipboardItems(selectedItems, cutMode: true);
    }

    public async Task PasteClipboardToActivePanelAsync()
    {
        if (_clipboardItems.Count == 0)
        {
            return;
        }

        var targetPanel = GetActivePanel();
        var policy = GetTransferConflictPolicy();
        var items = _clipboardItems.ToArray();
        var movedItems = new List<ClipboardTransferItem>();
        var movedPathEntries = new List<(string OldPath, string NewPath, bool IsDirectory)>();

        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                if (!_fileSystemService.DirectoryExists(targetPanel.CurrentPath))
                {
                    continue;
                }

                if (item.IsDirectory)
                {
                    if (!Directory.Exists(item.Path))
                    {
                        continue;
                    }
                }
                else if (!File.Exists(item.Path))
                {
                    continue;
                }

                if (_clipboardCutMode &&
                    string.Equals(Path.GetDirectoryName(item.Path)?.TrimEnd('\\'), targetPanel.CurrentPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var transferItem = CreateTransferItem(item);
                if (transferItem is null)
                {
                    continue;
                }

                if (!TryResolveTransferDestination(transferItem, targetPanel.CurrentPath, policy, out var destinationPath, out var exists))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(destinationPath) ||
                    string.Equals(item.Path, destinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (_clipboardCutMode)
                {
                    if (item.IsDirectory)
                    {
                        if (exists)
                        {
                            CopyDirectory(item.Path, destinationPath, overwrite: true);
                            Directory.Delete(item.Path, true);
                        }
                        else
                        {
                            Directory.Move(item.Path, destinationPath);
                        }
                    }
                    else
                    {
                        File.Move(item.Path, destinationPath, overwrite: policy == TransferConflictPolicy.Overwrite);
                    }

                    movedItems.Add(item);
                    movedPathEntries.Add((item.Path, destinationPath, item.IsDirectory));
                    continue;
                }

                if (item.IsDirectory)
                {
                    CopyDirectory(item.Path, destinationPath, policy == TransferConflictPolicy.Overwrite || !exists);
                }
                else
                {
                    File.Copy(item.Path, destinationPath, overwrite: policy == TransferConflictPolicy.Overwrite);
                }
            }
        });

        if (_clipboardCutMode && movedItems.Count > 0)
        {
            foreach (var moved in movedItems)
            {
                _clipboardItems.RemoveAll(existing => string.Equals(existing.Path, moved.Path, StringComparison.OrdinalIgnoreCase));
            }

            if (_clipboardItems.Count == 0)
            {
                _clipboardCutMode = false;
            }

            foreach (var entry in movedPathEntries)
            {
                ReplacePathInSettings(entry.OldPath, entry.NewPath, entry.IsDirectory);
            }

            await PersistSettingsAsync();
        }

        var affectedPaths = new List<string?> { targetPanel.CurrentPath };
        if (_clipboardCutMode)
        {
            affectedPaths.AddRange(movedPathEntries.Select(entry => Path.GetDirectoryName(entry.OldPath)));
        }

        await ReloadPanelsForPathsAndDashboardAsync(affectedPaths);
    }

    public async Task DeleteSelectedAsync(IReadOnlyList<FileSystemItem>? selectedItems = null)
    {
        var panel = GetActivePanel();
        var items = ResolveSelection(panel, selectedItems);
        if (items.Count == 0) return;
        var nextSelectionIndex = FindDeletionAnchorIndex(panel, items);
        var deletingPaths = items
            .Where(item => !item.IsParentDirectory && !string.IsNullOrWhiteSpace(item.FullPath))
            .Select(item => item.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedSelectionPath = FindNearestSurvivingPath(panel, deletingPaths, nextSelectionIndex);
        var memoChanged = false;
        foreach (var item in items.Where(item => !item.IsParentDirectory))
        {
            memoChanged |= RemoveMemoEntriesForPath(item.FullPath, item.IsDirectory);
        }
        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                if (item.IsDirectory) Directory.Delete(item.FullPath, true);
                else File.Delete(item.FullPath);
            }
        });
        if (memoChanged)
        {
            await PersistSettingsAsync();
        }

        if (!string.IsNullOrWhiteSpace(expectedSelectionPath))
        {
            panel.SelectedItem = panel.Items.FirstOrDefault(entry =>
                !entry.IsParentDirectory &&
                string.Equals(entry.FullPath, expectedSelectionPath, StringComparison.OrdinalIgnoreCase));
        }

        await ReloadPanelsForPathsAsync([panel.CurrentPath]);
        QueuePostMutationRefresh();
        SelectNearestItemAfterDeletion(panel, nextSelectionIndex);
    }

    public async Task RenameSelectedAsync(string newName)
    {
        var panel = GetActivePanel();
        var activePanelIsLeft = IsLeftPanelActive;
        var item = panel.SelectedItem;
        if (item is null) return;
        newName = (newName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(newName)) return;
        var parent = Path.GetDirectoryName(item.FullPath);
        if (string.IsNullOrWhiteSpace(parent)) return;
        var dest = Path.Combine(parent, newName);
        if (string.Equals(dest, item.FullPath, StringComparison.OrdinalIgnoreCase)) return;
        if (item.IsDirectory) Directory.Move(item.FullPath, dest); else File.Move(item.FullPath, dest);
        ReplacePathInSettings(item.FullPath, dest, item.IsDirectory);
        await ReloadPanelsForPathAndDashboardAsync(panel.CurrentPath);

        var targetPanel = activePanelIsLeft ? LeftPanel : RightPanel;
        var renamedItem = targetPanel.Items.FirstOrDefault(entry =>
            string.Equals(entry.FullPath, dest, StringComparison.OrdinalIgnoreCase));
        if (renamedItem is not null)
        {
            targetPanel.SelectedItem = renamedItem;
        }
    }

    public async Task CreateNewFolderAsync(string folderName)
    {
        var panel = GetActivePanel();
        var name = (folderName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        var dest = EnsureUniquePath(panel.CurrentPath, name, true);
        Directory.CreateDirectory(dest);
        await ReloadPanelsAndDashboardAsync();
    }

    public async Task TogglePinForActiveSelectionAsync(IReadOnlyList<FileSystemItem>? selectedItems = null)
    {
        var panel = GetActivePanel();
        var items = ResolveSelection(panel, selectedItems);
        if (items.Count == 0) return;
        await TogglePinForItemsAsync(items, panel);
    }

    public async Task TogglePinForItemsAsync(IReadOnlyList<FileSystemItem> items, PanelViewModel? selectionPanel = null)
    {
        var validItems = items
            .Where(item => !item.IsParentDirectory && !string.IsNullOrWhiteSpace(item.FullPath))
            .ToArray();
        if (validItems.Length == 0)
        {
            return;
        }

        var selectedPaths = validItems
            .Select(item => item.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hasUnpinned = validItems.Any(item => !IsPinned(item));
        foreach (var item in validItems)
        {
            if (item.IsDirectory) TogglePinPath(_settings.PinnedFolders, item.FullPath, hasUnpinned);
            else TogglePinPath(_settings.PinnedFiles, item.FullPath, hasUnpinned);
        }

        await PersistSettingsAsync();
        await ReloadPanelsAndDashboardAsync();
        RestorePanelSelectionByPaths(selectionPanel, selectedPaths);
    }

    public async Task AddFavoriteItemsAsync(IReadOnlyList<FileSystemItem> items)
    {
        if (items.Count == 0) return;
        foreach (var item in items)
        {
            if (item.IsDirectory)
            {
                if (!_settings.FavoriteFolders.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase))
                    _settings.FavoriteFolders.Add(item.FullPath);
            }
            else
            {
                if (!_settings.FavoriteFiles.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase))
                {
                    _settings.FavoriteFiles.Add(item.FullPath);
                }

                SetFavoriteFileCategory(item.FullPath, ResolveFallbackFavoriteFileCategory(_settings.FavoriteFileCategoryFolders));
            }
        }

        await PersistSettingsAsync();
        await RefreshSidebarAndDashboardAsync();
    }

    public async Task ToggleFavoriteForActiveSelectionAsync(IReadOnlyList<FileSystemItem>? selectedItems = null)
    {
        var panel = GetActivePanel();
        var items = ResolveSelection(panel, selectedItems);
        await ToggleFavoriteForItemsAsync(items, panel);
    }

    public async Task ToggleFavoriteForItemsAsync(IReadOnlyList<FileSystemItem> items, PanelViewModel? selectionPanel = null)
    {
        var validItems = items
            .Where(item => !item.IsParentDirectory && !string.IsNullOrWhiteSpace(item.FullPath))
            .ToArray();
        if (validItems.Length == 0)
        {
            return;
        }

        var selectedPaths = validItems
            .Select(item => item.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hasUnfavorited = validItems.Any(item => item.IsDirectory
            ? !_settings.FavoriteFolders.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase)
            : !_settings.FavoriteFiles.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase));

        foreach (var item in validItems)
        {
            if (item.IsDirectory)
            {
                ToggleFavoritePath(_settings.FavoriteFolders, item.FullPath, hasUnfavorited);
            }
            else
            {
                ToggleFavoritePath(_settings.FavoriteFiles, item.FullPath, hasUnfavorited);
                if (hasUnfavorited)
                {
                    SetFavoriteFileCategory(item.FullPath, ResolveFallbackFavoriteFileCategory(_settings.FavoriteFileCategoryFolders));
                }
                else
                {
                    RemoveFavoriteFileCategory(item.FullPath);
                }
            }
        }

        await PersistSettingsAsync();
        await ReloadPanelsAndDashboardAsync();
        RestorePanelSelectionByPaths(selectionPanel, selectedPaths);
    }

    public string GetItemMemoText(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || _settings.ItemMemos is null)
        {
            return string.Empty;
        }

        return _settings.ItemMemos.TryGetValue(path, out var memo) ? memo : string.Empty;
    }

    public async Task SetItemMemoAsync(PanelViewModel panel, FileSystemItem item, string? memoText, bool deleteRequested = false)
    {
        if (item.IsParentDirectory || string.IsNullOrWhiteSpace(item.FullPath))
        {
            return;
        }

        _settings.ItemMemos ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalizedMemo = (memoText ?? string.Empty).Trim();
        if (deleteRequested || string.IsNullOrWhiteSpace(normalizedMemo))
        {
            RemoveMemoEntriesForPath(item.FullPath, isDirectory: false);
        }
        else
        {
            _settings.ItemMemos[item.FullPath] = normalizedMemo;
        }

        await PersistSettingsAsync();
        await ReloadPanelsAndDashboardAsync();
        panel.SelectedItem = panel.Items.FirstOrDefault(entry =>
            string.Equals(entry.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase));
    }

    public async Task AddCurrentPanelPathToFavoritesAsync(bool leftPanel)
    {
        var panel = leftPanel ? LeftPanel : RightPanel;
        var path = panel.CurrentPath;
        if (string.IsNullOrWhiteSpace(path) ||
            string.Equals(path, DriveRootVirtualPath, StringComparison.Ordinal) ||
            !_fileSystemService.DirectoryExists(path))
        {
            return;
        }

        if (!_settings.FavoriteFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            _settings.FavoriteFolders.Add(path);
            await PersistSettingsAsync();
        }

        await RefreshSidebarAndDashboardAsync();
    }

    public async Task ToggleCurrentPanelPathFavoriteAsync(bool leftPanel)
    {
        var panel = leftPanel ? LeftPanel : RightPanel;
        await ToggleFavoriteFolderPathAsync(panel.CurrentPath);
    }

    public async Task MoveFavoriteToolbarFolderAsync(string? sourcePath, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(targetPath) ||
            string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var sourceIndex = _settings.FavoriteFolders.FindIndex(path =>
            string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase));
        var targetIndex = _settings.FavoriteFolders.FindIndex(path =>
            string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            return;
        }

        var sourceValue = _settings.FavoriteFolders[sourceIndex];
        _settings.FavoriteFolders.RemoveAt(sourceIndex);
        if (sourceIndex < targetIndex)
        {
            targetIndex--;
        }

        _settings.FavoriteFolders.Insert(targetIndex, sourceValue);
        await PersistSettingsAsync();
        await RefreshSidebarAndDashboardAsync();
    }

    public async Task NavigateQuickAccessAsync(QuickAccessItem? item)
    {
        if (item is null) return;
        await LoadPanelAsync(GetCommandTargetPanel(), item.Path, true);
    }

    public async Task OpenTrackedFileAsync(TrackedFileRecord? record)
    {
        if (record is null || !File.Exists(record.Path)) return;
        try
        {
            if (IsImageFile(record.Path))
            {
                if (!TryShowImageViewer(record.Path))
                {
                    Process.Start(new ProcessStartInfo { FileName = record.Path, UseShellExecute = true });
                }
            }
            else
            {
                Process.Start(new ProcessStartInfo { FileName = record.Path, UseShellExecute = true });
            }
            _usageTrackingService.RecordFileOpen(record.Path, _settings.PinnedFiles.Contains(record.Path, StringComparer.OrdinalIgnoreCase));
            await _usageTrackingService.PersistAsync();
            await RefreshSidebarAndDashboardAsync();
        }
        catch
        {
            StatusText = "Failed to open file";
        }
    }

    public async Task NavigateToDriveAsync(bool leftPanel, string? driveLabel)
    {
        var drivePath = NormalizeDrivePath(driveLabel);
        if (string.IsNullOrWhiteSpace(drivePath) || !_fileSystemService.DirectoryExists(drivePath))
        {
            return;
        }

        SetActivePanel(leftPanel ? "Left" : "Right");
        await LoadPanelAsync(leftPanel ? LeftPanel : RightPanel, drivePath, true);
    }

    public async Task NavigatePanelToPathAsync(bool leftPanel, string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!TryResolveNavigableDirectoryPath(path, out var resolvedPath))
            {
                StatusText = $"없는 폴더: {path}";
                return;
            }

            SetActivePanel(leftPanel ? "Left" : "Right");
            await LoadPanelAsync(leftPanel ? LeftPanel : RightPanel, resolvedPath, true);
        }
        catch
        {
            StatusText = "Navigation failed";
        }
    }

    public async Task GoUpFromPanelAsync(bool leftPanel)
    {
        try
        {
            var panel = leftPanel ? LeftPanel : RightPanel;
            SetActivePanel(leftPanel ? "Left" : "Right");

            if (string.Equals(panel.CurrentPath, DriveRootVirtualPath, StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(panel.CurrentPath))
            {
                return;
            }

            var parent = Directory.GetParent(panel.CurrentPath);
            if (parent is null)
            {
                await LoadPanelAsync(panel, DriveRootVirtualPath, true);
                return;
            }

            await LoadPanelAsync(panel, parent.FullName, true);
        }
        catch
        {
            StatusText = "Failed to move to parent folder";
        }
    }

    private async Task NavigatePanelPathAsync(string? panelKey)
    {
        var right = string.Equals(panelKey, "Right", StringComparison.OrdinalIgnoreCase);
        SetActivePanel(right ? "Right" : "Left");
        var panel = right ? RightPanel : LeftPanel;
        await LoadPanelAsync(panel, panel.CurrentPath, true);
    }

    private async Task GoUpAsync()
    {
        try
        {
            var panel = GetActivePanel();
            if (string.Equals(panel.CurrentPath, DriveRootVirtualPath, StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(panel.CurrentPath))
            {
                return;
            }

            var parent = Directory.GetParent(panel.CurrentPath);
            if (parent is null)
            {
                await LoadPanelAsync(panel, DriveRootVirtualPath, true);
                return;
            }

            await LoadPanelAsync(panel, parent.FullName, true);
        }
        catch
        {
            StatusText = "Failed to move to parent folder";
        }
    }

    private async Task GoRootUpAsync()
    {
        await LoadPanelAsync(GetActivePanel(), DriveRootVirtualPath, true);
    }

    private async Task RefreshActivePanelAsync()
    {
        var panel = GetActivePanel();
        InvalidateDirectoryItemsCache(panel.CurrentPath);
        await LoadPanelAsync(panel, panel.CurrentPath, false);
    }

    private async Task ApplySearchAsync()
    {
        var hasMin = long.TryParse(SearchMinSizeKb, out var minKb);
        var hasMax = long.TryParse(SearchMaxSizeKb, out var maxKb);
        var roots = string.Equals(SearchScope, "Both panels", StringComparison.OrdinalIgnoreCase)
            ? new[] { LeftPanel.CurrentPath, RightPanel.CurrentPath }
            : new[] { GetActivePanel().CurrentPath };

        var options = new FindFilesOptions
        {
            StartDirectory = roots.FirstOrDefault() ?? GetActivePanel().CurrentPath,
            SearchSubdirectories = SearchRecursive,
            FileMasks = string.IsNullOrWhiteSpace(SearchFileMasks)
                ? (string.IsNullOrWhiteSpace(SearchExtension) ? "*" : $"*{NormalizeExtensionFilter(SearchExtension)}")
                : SearchFileMasks,
            ExcludedDirectories = SearchExcludedDirectories,
            ExcludedFiles = SearchExcludedFiles,
            TextQuery = SearchTextQuery,
            EncodingName = SearchEncoding,
            CaseSensitive = SearchCaseSensitive,
            UseRegex = SearchUseRegex,
            MinSizeKb = hasMin ? minKb : null,
            MaxSizeKb = hasMax ? maxKb : null
        };

        var results = await FindFilesAsync(options, roots, CancellationToken.None, progress: null);

        ResetCollection(SearchResults, results);
        var keyword = (SearchText ?? string.Empty).Trim();
        GetActivePanel().ApplyFilter(keyword);
        StatusText = $"검색 완료: {SearchResults.Count}개";
    }

    public async Task<IReadOnlyList<FileSystemItem>> FindFilesAsync(FindFilesOptions options, CancellationToken cancellationToken)
    {
        IEnumerable<string> roots = string.IsNullOrWhiteSpace(options.StartDirectory)
            ? new[] { GetActivePanel().CurrentPath }
            : new[] { options.StartDirectory };

        return await FindFilesAsync(options, roots, cancellationToken, progress: null);
    }

    public async Task<IReadOnlyList<FileSystemItem>> FindFilesAsync(
        FindFilesOptions options,
        CancellationToken cancellationToken,
        IProgress<FileSystemItem>? progress)
    {
        IEnumerable<string> roots = string.IsNullOrWhiteSpace(options.StartDirectory)
            ? new[] { GetActivePanel().CurrentPath }
            : new[] { options.StartDirectory };

        return await FindFilesAsync(options, roots, cancellationToken, progress);
    }

    private async Task<IReadOnlyList<FileSystemItem>> FindFilesAsync(
        FindFilesOptions options,
        IEnumerable<string> roots,
        CancellationToken cancellationToken,
        IProgress<FileSystemItem>? progress)
    {
        var masks = ParsePatternList(options.FileMasks, "*");
        var excludedDirectories = ParsePatternList(options.ExcludedDirectories);
        var excludedFiles = ParsePatternList(options.ExcludedFiles);

        return await Task.Run(() =>
        {
            IEnumerable<FileSystemItem> items = options.SearchSubdirectories
                ? EnumerateSearchCandidates(roots, options.MaxDepth, excludedDirectories, cancellationToken)
                : (string.Equals(SearchScope, "Both panels", StringComparison.OrdinalIgnoreCase)
                    ? LeftPanel.GetAllItems().Concat(RightPanel.GetAllItems())
                    : GetActivePanel().GetAllItems());

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<FileSystemItem>(capacity: 256);

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!MatchesFileMasks(item, masks))
                {
                    continue;
                }

                if (MatchesAnyPattern(item.Name, excludedFiles))
                {
                    continue;
                }

                if (!FilterSize(item, options.MinSizeKb.HasValue, options.MinSizeKb ?? 0, options.MaxSizeKb.HasValue, options.MaxSizeKb ?? 0))
                {
                    continue;
                }

                if (!FilterDate(item, options.DateFrom, options.DateTo))
                {
                    continue;
                }

                if (!FilterTextContent(item, options, cancellationToken))
                {
                    continue;
                }

                if (!seen.Add(item.FullPath))
                {
                    continue;
                }

                result.Add(item);
                progress?.Report(item);

                if (result.Count >= 500)
                {
                    break;
                }
            }

            return (IReadOnlyList<FileSystemItem>)result;
        }, cancellationToken);
    }

    private IEnumerable<FileSystemItem> EnumerateSearchCandidates(IEnumerable<string> roots, int? maxDepth, IReadOnlyList<string> excludedDirectories, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, ReturnSpecialDirectories = false, AttributesToSkip = FileAttributes.System };
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            foreach (var item in EnumerateFromRoot(root, 0, maxDepth, excludedDirectories, options, cancellationToken))
            {
                yield return item;
            }
        }
    }

    public async Task RevealFileInActivePanelAsync(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        var folderPath = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        var activePanel = GetCommandTargetPanel();
        await LoadPanelAsync(activePanel, folderPath, true);

        var target = activePanel.Items.FirstOrDefault(item =>
            !item.IsDirectory &&
            string.Equals(item.FullPath, filePath, StringComparison.OrdinalIgnoreCase));
        if (target is not null)
        {
            activePanel.SelectedItem = target;
        }
    }

    private static IEnumerable<FileSystemItem> EnumerateFromRoot(string root, int depth, int? maxDepth, IReadOnlyList<string> excludedDirectories, EnumerationOptions options, CancellationToken cancellationToken)
    {
        if (maxDepth.HasValue && depth > maxDepth.Value)
        {
            yield break;
        }

        DirectoryInfo rootInfo;
        try { rootInfo = new DirectoryInfo(root); } catch { yield break; }

        yield return new FileSystemItem
        {
            Name = rootInfo.Name,
            FullPath = rootInfo.FullName,
            IsDirectory = true,
            LastModified = rootInfo.LastWriteTime,
            TypeDisplay = "Folder"
        };

        IEnumerable<string> directories;
        try { directories = Directory.EnumerateDirectories(root, "*", options); } catch { directories = Array.Empty<string>(); }
        foreach (var d in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dirName = Path.GetFileName(d);
            if (MatchesAnyPattern(dirName, excludedDirectories))
            {
                continue;
            }

            DirectoryInfo info;
            try { info = new DirectoryInfo(d); } catch { continue; }

            yield return new FileSystemItem
            {
                Name = info.Name,
                FullPath = info.FullName,
                IsDirectory = true,
                LastModified = info.LastWriteTime,
                TypeDisplay = "Folder"
            };

            foreach (var nested in EnumerateFromRoot(d, depth + 1, maxDepth, excludedDirectories, options, cancellationToken))
            {
                yield return nested;
            }
        }

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", options); } catch { files = Array.Empty<string>(); }
        foreach (var f in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info;
            try { info = new FileInfo(f); } catch { continue; }
            yield return new FileSystemItem
            {
                Name = info.Name,
                Extension = info.Extension,
                FullPath = info.FullName,
                IsDirectory = false,
                SizeBytes = info.Length,
                SizeDisplay = ToReadableSize(info.Length),
                LastModified = info.LastWriteTime,
                TypeDisplay = string.IsNullOrWhiteSpace(info.Extension) ? "File" : $"{info.Extension.ToUpperInvariant()} File"
            };
        }
    }

    private async Task AddFavoriteFromActiveSelectionAsync()
    {
        var selected = GetActivePanel().SelectedItem;
        if (selected is null) return;
        await AddFavoriteItemsAsync([selected]);
    }

    private void SetActivePanel(string? panelKey)
    {
        IsLeftPanelActive = !string.Equals(panelKey, "Right", StringComparison.OrdinalIgnoreCase);
        SearchStartDirectory = GetActivePanel().CurrentPath;
        OnPropertyChanged(nameof(IsTileViewEnabled));
        OnPropertyChanged(nameof(IsCompactListViewEnabled));
    }

    public bool IsTileViewEnabledForPanel(bool leftPanel) => leftPanel
        ? LeftPanelIsTileViewEnabled
        : RightPanelIsTileViewEnabled;

    public bool IsCompactListViewEnabledForPanel(bool leftPanel) => leftPanel
        ? LeftPanelIsCompactListViewEnabled
        : RightPanelIsCompactListViewEnabled;

    public void SetTileViewForActivePanel(bool enabled)
    {
        SetViewModeForActivePanel(enabled ? PanelViewMode.Tiles : PanelViewMode.Details);
    }

    public void SetCompactListViewForActivePanel()
    {
        SetViewModeForActivePanel(PanelViewMode.CompactList);
    }

    public void SetViewModeForActivePanel(PanelViewMode mode)
    {
        var targetTab = IsLeftPanelActive ? SelectedLeftTab : SelectedRightTab;
        if (targetTab is null || targetTab.ViewMode == mode)
        {
            return;
        }

        targetTab.ViewMode = mode;
        OnPropertyChanged(nameof(IsTileViewEnabled));
        OnPropertyChanged(nameof(IsCompactListViewEnabled));
        if (IsLeftPanelActive)
        {
            OnPropertyChanged(nameof(LeftPanelIsTileViewEnabled));
            OnPropertyChanged(nameof(LeftPanelIsCompactListViewEnabled));
            return;
        }

        OnPropertyChanged(nameof(RightPanelIsTileViewEnabled));
        OnPropertyChanged(nameof(RightPanelIsCompactListViewEnabled));
    }

    public async Task SetPanelCountAsync(int count)
    {
        var useFourPanels = count >= 4;
        if (useFourPanels)
        {
            if (!IsFourPanelMode)
            {
                await SyncTwoPanelsToFourPanelsAsync();
            }

            var changed = !IsFourPanelMode || IsFourPanelGridLayout;
            IsFourPanelMode = true;
            IsFourPanelGridLayout = false; // 4P 버튼은 기본 배치(1x4)로 복귀
            StatusText = changed
                ? "4패널 모드(1x4)로 전환됨"
                : "이미 4패널 모드(1x4)입니다.";
            await PersistSettingsAsync();
            return;
        }

        if (!IsFourPanelMode)
        {
            StatusText = "이미 2패널 모드입니다.";
            return;
        }

        IsFourPanelMode = false;
        IsFourPanelGridLayout = false;
        StatusText = "2패널 모드로 전환됨";
        await PersistSettingsAsync();
    }

    private async Task SyncTwoPanelsToFourPanelsAsync()
    {
        if (_fourPanels.Count == 0)
        {
            return;
        }

        if (_fourPanels.Count >= 1)
        {
            await SyncFourPanelSlotFromTabsAsync(_fourPanels[0], LeftTabs, SelectedLeftTab);
        }

        if (_fourPanels.Count >= 2)
        {
            await SyncFourPanelSlotFromTabsAsync(_fourPanels[1], RightTabs, SelectedRightTab);
        }

        SetActiveFourPanel(0);
    }

    private async Task SyncFourPanelSlotFromTabsAsync(
        FourPanelSlotViewModel slot,
        IReadOnlyList<PanelTabViewModel> sourceTabs,
        PanelTabViewModel? selectedSourceTab)
    {
        if (slot is null || sourceTabs.Count == 0)
        {
            return;
        }

        var selectedIndex = 0;
        if (selectedSourceTab is not null)
        {
            for (var index = 0; index < sourceTabs.Count; index++)
            {
                if (ReferenceEquals(sourceTabs[index], selectedSourceTab))
                {
                    selectedIndex = index;
                    break;
                }
            }
        }

        slot.Tabs.Clear();
        var copiedTabs = new List<PanelTabViewModel>(sourceTabs.Count);
        for (var index = 0; index < sourceTabs.Count; index++)
        {
            var source = sourceTabs[index];
            var path = string.IsNullOrWhiteSpace(source.Panel.CurrentPath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : source.Panel.CurrentPath;
            var copied = new PanelTabViewModel($"{slot.SlotKey}{index + 1}", new PanelViewModel
            {
                CurrentPath = path
            })
            {
                ViewMode = source.ViewMode
            };

            slot.Tabs.Add(copied);
            copiedTabs.Add(copied);
        }

        selectedIndex = Math.Clamp(selectedIndex, 0, copiedTabs.Count - 1);
        slot.SelectedTab = copiedTabs[selectedIndex];

        foreach (var tab in copiedTabs)
        {
            await LoadPanelAsync(tab.Panel, tab.Panel.CurrentPath, false);
        }
    }

    public async Task TogglePanelLayoutAsync()
    {
        if (!IsFourPanelMode)
        {
            IsFourPanelMode = true;
        }

        IsFourPanelGridLayout = !IsFourPanelGridLayout;
        StatusText = IsFourPanelGridLayout
            ? "4패널 배치: 2x2"
            : "4패널 배치: 1x4";
        await PersistSettingsAsync();
    }

    public async Task NavigateFourPanelToPathAsync(PanelViewModel? panel, string? path)
    {
        if (panel is null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!TryResolveNavigableDirectoryPath(path, out var resolvedPath))
        {
            StatusText = $"없는 폴더: {path}";
            return;
        }

        await LoadPanelAsync(panel, resolvedPath, false);
    }

    public async Task NavigateFourPanelBackAsync(FourPanelSlotViewModel? slot)
    {
        if (slot?.SelectedTab is null)
        {
            return;
        }

        var panel = slot.Panel;
        var target = slot.SelectedTab.GoBack(panel.CurrentPath);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        await LoadPanelAsync(panel, target, false, suppressHistoryRecord: true);
    }

    public async Task NavigateFourPanelForwardAsync(FourPanelSlotViewModel? slot)
    {
        if (slot?.SelectedTab is null)
        {
            return;
        }

        var panel = slot.Panel;
        var target = slot.SelectedTab.GoForward(panel.CurrentPath);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        await LoadPanelAsync(panel, target, false, suppressHistoryRecord: true);
    }

    public void SetActiveFourPanel(int index)
    {
        var normalized = Math.Clamp(index, 0, _fourPanels.Count - 1);
        for (var i = 0; i < _fourPanels.Count; i++)
        {
            _fourPanels[i].IsActive = i == normalized;
        }
    }

    public async Task AddFourPanelTabAsync(FourPanelSlotViewModel? slot, string? explicitSourcePath = null)
    {
        if (slot is null)
        {
            return;
        }

        var path = string.IsNullOrWhiteSpace(explicitSourcePath)
            ? (string.IsNullOrWhiteSpace(slot.Panel.CurrentPath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : slot.Panel.CurrentPath)
            : explicitSourcePath;
        var tab = slot.AddTab(path);
        tab.IsTileViewEnabled = _settings.DefaultTileViewEnabled;
        await LoadPanelAsync(tab.Panel, path, false);
        await PersistSettingsAsync();
    }

    public async Task CloseFourPanelTabAsync(FourPanelSlotViewModel? slot)
    {
        if (slot is null)
        {
            return;
        }

        if (slot.CloseCurrentTab())
        {
            await PersistSettingsAsync();
        }
    }

    public UiSettingsSnapshot CaptureUiSettings()
    {
        return new UiSettingsSnapshot
        {
            UseFourPanels = IsFourPanelMode,
            UseGridLayout = IsFourPanelGridLayout,
            RememberSessionTabs = _settings.RememberSessionTabs,
            DefaultTileViewEnabled = _settings.DefaultTileViewEnabled,
            UseExtensionColors = _settings.UseExtensionColors,
            UsePinnedHighlightColor = _settings.UsePinnedHighlightColor,
            ConfirmBeforeDelete = _settings.ConfirmBeforeDelete,
            ConflictPolicyDisplay = SelectedConflictPolicyDisplay,
            SearchScope = SearchScope,
            SearchRecursive = SearchRecursive,
            ExtensionColorOverrides = _settings.ExtensionColorOverrides.ToList()
        };
    }

    public async Task ApplyUiSettingsAsync(UiSettingsSnapshot snapshot)
    {
        var previousUseExtensionColors = _settings.UseExtensionColors;
        var previousUsePinnedHighlight = _settings.UsePinnedHighlightColor;
        var previousDefaultTile = _settings.DefaultTileViewEnabled;
        var previousExtensionColorOverrides = NormalizeExtensionColorOverrides(_settings.ExtensionColorOverrides);

        IsFourPanelMode = snapshot.UseFourPanels;
        IsFourPanelGridLayout = snapshot.UseFourPanels && snapshot.UseGridLayout;
        _settings.PanelCount = snapshot.UseFourPanels ? 4 : 2;
        _settings.PanelLayout = IsFourPanelGridLayout ? "Grid" : "Horizontal";
        _settings.RememberSessionTabs = snapshot.RememberSessionTabs;
        _settings.DefaultTileViewEnabled = snapshot.DefaultTileViewEnabled;
        _settings.UseExtensionColors = snapshot.UseExtensionColors;
        _settings.UsePinnedHighlightColor = snapshot.UsePinnedHighlightColor;
        _settings.ConfirmBeforeDelete = snapshot.ConfirmBeforeDelete;
        _settings.ConflictPolicyDisplay = ConflictPolicyOptions.Contains(snapshot.ConflictPolicyDisplay, StringComparer.OrdinalIgnoreCase)
            ? snapshot.ConflictPolicyDisplay
            : "Rename new";
        _settings.DefaultSearchScope = SearchScopes.Contains(snapshot.SearchScope, StringComparer.OrdinalIgnoreCase)
            ? snapshot.SearchScope
            : "Active panel";
        _settings.DefaultSearchRecursive = snapshot.SearchRecursive;
        _settings.ExtensionColorOverrides = NormalizeExtensionColorOverrides(snapshot.ExtensionColorOverrides);

        SelectedConflictPolicyDisplay = _settings.ConflictPolicyDisplay;
        SearchScope = _settings.DefaultSearchScope;
        SearchRecursive = _settings.DefaultSearchRecursive;
        FileSystemItem.UseExtensionColors = _settings.UseExtensionColors;
        FileSystemItem.UsePinnedHighlightColor = _settings.UsePinnedHighlightColor;
        FileSystemItem.SetExtensionColorOverrides(BuildExtensionColorMap(_settings.ExtensionColorOverrides));
        var extensionColorsChanged = !previousExtensionColorOverrides.SequenceEqual(_settings.ExtensionColorOverrides, StringComparer.OrdinalIgnoreCase);

        if (previousDefaultTile != _settings.DefaultTileViewEnabled)
        {
            ApplyDefaultViewToAllTabs(_settings.DefaultTileViewEnabled);
        }

        if (previousUseExtensionColors != _settings.UseExtensionColors ||
            previousUsePinnedHighlight != _settings.UsePinnedHighlightColor ||
            previousDefaultTile != _settings.DefaultTileViewEnabled ||
            extensionColorsChanged)
        {
            await ReloadPanelsAndDashboardAsync();
        }
        else
        {
            await RefreshSidebarAndDashboardAsync();
        }

        await PersistSettingsAsync();
        StatusText = "환경설정이 적용되었습니다.";
    }

    private static List<string> NormalizeExtensionColorOverrides(IEnumerable<string>? overrides)
    {
        if (overrides is null)
        {
            return new List<string>();
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in overrides)
        {
            if (!TryParseExtensionColorOverride(entry, out var ext, out var color))
            {
                continue;
            }

            normalized[ext] = color;
        }

        return normalized
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}")
            .ToList();
    }

    private static Dictionary<string, string> BuildExtensionColorMap(IEnumerable<string>? overrides)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (overrides is null)
        {
            return result;
        }

        foreach (var entry in overrides)
        {
            if (!TryParseExtensionColorOverride(entry, out var ext, out var color))
            {
                continue;
            }

            result[ext] = color;
        }

        return result;
    }

    private static bool TryParseExtensionColorOverride(string? entry, out string extension, out string color)
    {
        extension = string.Empty;
        color = string.Empty;
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var splitIndex = entry.IndexOf('=');
        if (splitIndex <= 0 || splitIndex >= entry.Length - 1)
        {
            return false;
        }

        extension = NormalizeExtensionKey(entry[..splitIndex]);
        color = NormalizeHexColor(entry[(splitIndex + 1)..]);
        return !string.IsNullOrWhiteSpace(extension) && !string.IsNullOrWhiteSpace(color);
    }

    private static string NormalizeExtensionKey(string? extension)
    {
        var value = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.StartsWith('.') ? value : $".{value}";
    }

    private static string NormalizeHexColor(string? color)
    {
        var value = (color ?? string.Empty).Trim().TrimStart('#');
        if (!Regex.IsMatch(value, "^[0-9a-fA-F]{6}$"))
        {
            return string.Empty;
        }

        return $"#{value.ToUpperInvariant()}";
    }

    public async Task GoParentInFourPanelAsync(PanelViewModel? panel)
    {
        if (panel is null)
        {
            return;
        }

        try
        {
            var current = panel.CurrentPath;
            if (string.IsNullOrWhiteSpace(current) || string.Equals(current, DriveRootVirtualPath, StringComparison.Ordinal))
            {
                return;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                return;
            }

            await LoadPanelAsync(panel, parent.FullName, false);
        }
        catch
        {
        }
    }

    public async Task RefreshFourPanelAsync(PanelViewModel? panel)
    {
        if (panel is null || string.IsNullOrWhiteSpace(panel.CurrentPath))
        {
            return;
        }

        await LoadPanelAsync(panel, panel.CurrentPath, false);
    }

    public async Task OpenItemFromFourPanelAsync(PanelViewModel? panel)
    {
        if (panel?.SelectedItem is null)
        {
            return;
        }

        await OpenItemAsync(panel, panel.SelectedItem);
    }

    public async Task NavigateFourPanelToDriveAsync(PanelViewModel? panel, string? driveLabel)
    {
        if (panel is null)
        {
            return;
        }

        var drivePath = NormalizeDrivePath(driveLabel);
        if (string.IsNullOrWhiteSpace(drivePath) || !_fileSystemService.DirectoryExists(drivePath))
        {
            return;
        }

        await LoadPanelAsync(panel, drivePath, false);
    }

    public async Task NavigateFourPanelHomeAsync(PanelViewModel? panel)
    {
        if (panel is null)
        {
            return;
        }

        await LoadPanelAsync(panel, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), false);
    }

    public async Task AddFavoriteFolderPathAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            string.Equals(path, DriveRootVirtualPath, StringComparison.Ordinal) ||
            !_fileSystemService.DirectoryExists(path))
        {
            return;
        }

        if (!_settings.FavoriteFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            _settings.FavoriteFolders.Add(path);
            await PersistSettingsAsync();
        }

        await RefreshSidebarAndDashboardAsync();
    }

    public async Task ToggleFavoriteFolderPathAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            string.Equals(path, DriveRootVirtualPath, StringComparison.Ordinal) ||
            !_fileSystemService.DirectoryExists(path))
        {
            return;
        }

        var isFavorite = _settings.FavoriteFolders.Contains(path, StringComparer.OrdinalIgnoreCase);
        if (isFavorite)
        {
            _settings.FavoriteFolders.RemoveAll(existing =>
                string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            _settings.FavoriteFolders.Add(path);
        }

        await PersistSettingsAsync();
        await RefreshSidebarAndDashboardAsync();
    }

    public async Task ApplyFavoriteFileCategoryFoldersAsync(IReadOnlyList<string>? categoryPaths)
    {
        NormalizeFavoriteFileCategorySettings();

        var normalized = (categoryPaths ?? Array.Empty<string>())
            .Select(NormalizeFavoriteFileCategoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            normalized.Add(DefaultFavoriteFileCategory);
        }

        _settings.FavoriteFileCategoryFolders = normalized;
        var fallbackCategory = ResolveFallbackFavoriteFileCategory(_settings.FavoriteFileCategoryFolders);

        var mappings = ParseFavoriteFileCategoryMappings(_settings.FavoriteFileCategoryMappings);
        var remapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in mappings)
        {
            var mappedCategory = NormalizeFavoriteFileCategoryPath(pair.Value);
            if (!_settings.FavoriteFileCategoryFolders.Contains(mappedCategory, StringComparer.OrdinalIgnoreCase))
            {
                mappedCategory = fallbackCategory;
            }

            remapped[pair.Key] = mappedCategory;
        }

        _settings.FavoriteFileCategoryMappings = SerializeFavoriteFileCategoryMappings(remapped);
        await PersistSettingsAsync();
        await RefreshSidebarAndDashboardAsync();
    }

    public IReadOnlyList<string> GetFavoriteFileCategoryFolders()
    {
        NormalizeFavoriteFileCategorySettings();
        return _settings.FavoriteFileCategoryFolders
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public string GetFavoriteFileCategoryForPath(string? filePath)
    {
        NormalizeFavoriteFileCategorySettings();
        var fallbackCategory = ResolveFallbackFavoriteFileCategory(_settings.FavoriteFileCategoryFolders);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return fallbackCategory;
        }

        var mapping = ParseFavoriteFileCategoryMappings(_settings.FavoriteFileCategoryMappings);
        return mapping.TryGetValue(filePath, out var category) && !string.IsNullOrWhiteSpace(category)
            ? category
            : fallbackCategory;
    }

    public async Task AssignFavoriteFileToCategoryAsync(string filePath, string? categoryPath, PanelViewModel? selectionPanel = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var normalizedCategory = NormalizeFavoriteFileCategoryPath(categoryPath);
        var selectedPaths = new[] { filePath };

        if (!_settings.FavoriteFiles.Contains(filePath, StringComparer.OrdinalIgnoreCase))
        {
            _settings.FavoriteFiles.Add(filePath);
        }

        EnsureFavoriteFileCategoryFolderExists(normalizedCategory);
        SetFavoriteFileCategory(filePath, normalizedCategory);
        await PersistSettingsAsync();
        await ReloadPanelsAndDashboardAsync();
        RestorePanelSelectionByPaths(selectionPanel, selectedPaths);
    }

    public async Task RemoveFavoriteFileAsync(string filePath, PanelViewModel? selectionPanel = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var removed = _settings.FavoriteFiles.RemoveAll(existing =>
            string.Equals(existing, filePath, StringComparison.OrdinalIgnoreCase)) > 0;
        RemoveFavoriteFileCategory(filePath);
        if (!removed)
        {
            return;
        }

        await PersistSettingsAsync();
        await ReloadPanelsAndDashboardAsync();
        RestorePanelSelectionByPaths(selectionPanel, new[] { filePath });
    }

    public async Task<string?> AddFavoriteFileCategoryFolderAsync(string? parentCategoryPath, string? folderName)
    {
        NormalizeFavoriteFileCategorySettings();
        var normalizedName = NormalizeFavoriteFileCategorySegment(folderName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return null;
        }

        var normalizedParent = NormalizeFavoriteFileCategoryPath(parentCategoryPath);
        var newPath = string.Equals(normalizedParent, DefaultFavoriteFileCategory, StringComparison.OrdinalIgnoreCase)
            ? normalizedName
            : $"{normalizedParent}/{normalizedName}";
        newPath = NormalizeFavoriteFileCategoryPath(newPath);
        EnsureFavoriteFileCategoryFolderExists(newPath);
        await PersistSettingsAsync();
        await RefreshSidebarAndDashboardAsync();
        return newPath;
    }

    public async Task<string?> RenameFavoriteFileCategoryFolderAsync(string? categoryPath, string? newName)
    {
        NormalizeFavoriteFileCategorySettings();
        var sourcePath = NormalizeFavoriteFileCategoryPath(categoryPath);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        var normalizedName = NormalizeFavoriteFileCategorySegment(newName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return null;
        }

        var parentPath = sourcePath.Contains('/')
            ? sourcePath[..sourcePath.LastIndexOf('/')]
            : string.Empty;
        var targetPath = string.IsNullOrWhiteSpace(parentPath)
            ? normalizedName
            : $"{parentPath}/{normalizedName}";
        targetPath = NormalizeFavoriteFileCategoryPath(targetPath);
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath;
        }

        var folders = _settings.FavoriteFileCategoryFolders
            .Where(path => !string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                if (path.StartsWith(sourcePath + "/", StringComparison.OrdinalIgnoreCase))
                {
                    var suffix = path[sourcePath.Length..];
                    return targetPath + suffix;
                }

                return path;
            })
            .ToList();
        folders.Add(targetPath);
        _settings.FavoriteFileCategoryFolders = folders;

        var mappings = ParseFavoriteFileCategoryMappings(_settings.FavoriteFileCategoryMappings);
        var updatedMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in mappings)
        {
            var mapped = pair.Value;
            if (string.Equals(mapped, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                updatedMappings[pair.Key] = targetPath;
            }
            else if (mapped.StartsWith(sourcePath + "/", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = mapped[sourcePath.Length..];
                updatedMappings[pair.Key] = targetPath + suffix;
            }
            else
            {
                updatedMappings[pair.Key] = mapped;
            }
        }

        _settings.FavoriteFileCategoryMappings = SerializeFavoriteFileCategoryMappings(updatedMappings);
        NormalizeFavoriteFileCategorySettings();
        await PersistSettingsAsync();
        await RefreshSidebarAndDashboardAsync();
        return targetPath;
    }

    public async Task DeleteFavoriteFileCategoryFolderAsync(string? categoryPath)
    {
        NormalizeFavoriteFileCategorySettings();
        var sourcePath = NormalizeFavoriteFileCategoryPath(categoryPath);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        _settings.FavoriteFileCategoryFolders = _settings.FavoriteFileCategoryFolders
            .Where(path =>
                !string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith(sourcePath + "/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var mappings = ParseFavoriteFileCategoryMappings(_settings.FavoriteFileCategoryMappings);
        var updatedMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in mappings)
        {
            var mapped = pair.Value;
            if (string.Equals(mapped, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                mapped.StartsWith(sourcePath + "/", StringComparison.OrdinalIgnoreCase))
            {
                updatedMappings[pair.Key] = ResolveFallbackFavoriteFileCategory(_settings.FavoriteFileCategoryFolders);
            }
            else
            {
                updatedMappings[pair.Key] = mapped;
            }
        }

        _settings.FavoriteFileCategoryMappings = SerializeFavoriteFileCategoryMappings(updatedMappings);
        NormalizeFavoriteFileCategorySettings();
        await PersistSettingsAsync();
        await RefreshSidebarAndDashboardAsync();
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetFavoriteFileCategoriesWithFiles()
    {
        NormalizeFavoriteFileCategorySettings();
        var mappings = ParseFavoriteFileCategoryMappings(_settings.FavoriteFileCategoryMappings);
        var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var category in _settings.FavoriteFileCategoryFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            grouped[category] = new List<string>();
        }

        foreach (var filePath in _settings.FavoriteFiles
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!mappings.TryGetValue(filePath, out var category) || string.IsNullOrWhiteSpace(category))
            {
                category = ResolveFallbackFavoriteFileCategory(_settings.FavoriteFileCategoryFolders);
            }

            category = NormalizeFavoriteFileCategoryPath(category);
            if (!grouped.TryGetValue(category, out var list))
            {
                list = new List<string>();
                grouped[category] = list;
            }

            list.Add(filePath);
        }

        return grouped.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task CopyOrMoveBetweenPanelsAsync(
        PanelViewModel sourcePanel,
        PanelViewModel targetPanel,
        IReadOnlyList<FileSystemItem>? selectedItems,
        bool move,
        string? targetDirectoryOverride = null)
    {
        var destinationDirectory = string.IsNullOrWhiteSpace(targetDirectoryOverride)
            ? targetPanel.CurrentPath
            : targetDirectoryOverride;

        if (string.IsNullOrWhiteSpace(destinationDirectory) ||
            string.Equals(destinationDirectory, DriveRootVirtualPath, StringComparison.Ordinal) ||
            !_fileSystemService.DirectoryExists(destinationDirectory))
        {
            StatusText = "대상 패널 경로가 유효하지 않아 작업을 건너뜁니다.";
            return;
        }

        var items = ResolveSelection(sourcePanel, selectedItems)
            .Where(item => !item.IsParentDirectory)
            .ToArray();
        if (items.Length == 0)
        {
            return;
        }

        var policy = GetTransferConflictPolicy();
        var usedElevation = false;
        var movedPathEntries = new List<(string OldPath, string NewPath, bool IsDirectory)>();
        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                if (!TryResolveTransferDestination(item, destinationDirectory, policy, out var dest, out var exists))
                {
                    continue;
                }

                try
                {
                    if (move)
                    {
                        if (item.IsDirectory)
                        {
                            if (exists)
                            {
                                CopyDirectory(item.FullPath, dest!, overwrite: true);
                                Directory.Delete(item.FullPath, true);
                            }
                            else
                            {
                                Directory.Move(item.FullPath, dest!);
                            }
                        }
                        else
                        {
                            File.Move(item.FullPath, dest!, overwrite: policy == TransferConflictPolicy.Overwrite);
                        }

                        movedPathEntries.Add((item.FullPath, dest!, item.IsDirectory));
                    }
                    else
                    {
                        if (item.IsDirectory)
                        {
                            CopyDirectory(item.FullPath, dest!, policy == TransferConflictPolicy.Overwrite || !exists);
                        }
                        else
                        {
                            File.Copy(item.FullPath, dest!, overwrite: policy == TransferConflictPolicy.Overwrite);
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    if (!TryTransferWithElevation(item, dest!, move, policy, exists))
                    {
                        throw;
                    }

                    usedElevation = true;
                }
            }
        });

        if (move && movedPathEntries.Count > 0)
        {
            foreach (var entry in movedPathEntries)
            {
                ReplacePathInSettings(entry.OldPath, entry.NewPath, entry.IsDirectory);
            }

            await PersistSettingsAsync();
        }

        var affectedPaths = new List<string?> { destinationDirectory };
        if (move)
        {
            affectedPaths.Add(sourcePanel.CurrentPath);
            affectedPaths.AddRange(movedPathEntries.Select(entry => Path.GetDirectoryName(entry.OldPath)));
        }

        await ReloadPanelsForPathsAndDashboardAsync(affectedPaths);
        if (usedElevation)
        {
            StatusText = "보호 경로 작업을 관리자 권한으로 처리했습니다.";
        }
    }

    public async Task DeleteItemsFromPanelAsync(PanelViewModel panel, IReadOnlyList<FileSystemItem>? selectedItems)
    {
        var items = ResolveSelection(panel, selectedItems)
            .Where(item => !item.IsParentDirectory)
            .ToArray();
        if (items.Length == 0)
        {
            return;
        }

        var nextSelectionIndex = FindDeletionAnchorIndex(panel, items);
        var deletingPaths = items
            .Where(item => !string.IsNullOrWhiteSpace(item.FullPath))
            .Select(item => item.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedSelectionPath = FindNearestSurvivingPath(panel, deletingPaths, nextSelectionIndex);
        var memoChanged = false;
        foreach (var item in items)
        {
            memoChanged |= RemoveMemoEntriesForPath(item.FullPath, item.IsDirectory);
        }
        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                if (item.IsDirectory)
                {
                    Directory.Delete(item.FullPath, true);
                }
                else
                {
                    File.Delete(item.FullPath);
                }
            }
        });

        if (memoChanged)
        {
            await PersistSettingsAsync();
        }

        if (!string.IsNullOrWhiteSpace(expectedSelectionPath))
        {
            panel.SelectedItem = panel.Items.FirstOrDefault(entry =>
                !entry.IsParentDirectory &&
                string.Equals(entry.FullPath, expectedSelectionPath, StringComparison.OrdinalIgnoreCase));
        }

        await ReloadPanelsForPathsAsync([panel.CurrentPath]);
        QueuePostMutationRefresh();
        SelectNearestItemAfterDeletion(panel, nextSelectionIndex);
    }

    public async Task RenameItemInPanelAsync(PanelViewModel panel, FileSystemItem item, string newName)
    {
        var normalizedName = (newName ?? string.Empty).Trim();
        if (item.IsParentDirectory || string.IsNullOrWhiteSpace(normalizedName))
        {
            return;
        }

        var parent = Path.GetDirectoryName(item.FullPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return;
        }

        var destination = Path.Combine(parent, normalizedName);
        if (string.Equals(destination, item.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (item.IsDirectory)
        {
            Directory.Move(item.FullPath, destination);
        }
        else
        {
            File.Move(item.FullPath, destination);
        }

        ReplacePathInSettings(item.FullPath, destination, item.IsDirectory);
        await ReloadPanelsForPathAndDashboardAsync(panel.CurrentPath);
        panel.SelectedItem = panel.Items.FirstOrDefault(entry =>
            string.Equals(entry.FullPath, destination, StringComparison.OrdinalIgnoreCase));
    }

    public async Task CreateNewFolderInPanelAsync(PanelViewModel panel, string folderName)
    {
        var name = (folderName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var destination = EnsureUniquePath(panel.CurrentPath, name, true);
        Directory.CreateDirectory(destination);
        await ReloadPanelsForPathAndDashboardAsync(panel.CurrentPath);
    }

    public async Task PasteClipboardToPanelAsync(PanelViewModel panel)
    {
        if (_clipboardItems.Count == 0)
        {
            return;
        }

        var policy = GetTransferConflictPolicy();
        var items = _clipboardItems.ToArray();
        var movedItems = new List<ClipboardTransferItem>();

        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                if (!_fileSystemService.DirectoryExists(panel.CurrentPath))
                {
                    continue;
                }

                if (item.IsDirectory)
                {
                    if (!Directory.Exists(item.Path))
                    {
                        continue;
                    }
                }
                else if (!File.Exists(item.Path))
                {
                    continue;
                }

                if (_clipboardCutMode &&
                    string.Equals(Path.GetDirectoryName(item.Path)?.TrimEnd('\\'), panel.CurrentPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var transferItem = CreateTransferItem(item);
                if (transferItem is null)
                {
                    continue;
                }

                if (!TryResolveTransferDestination(transferItem, panel.CurrentPath, policy, out var destinationPath, out var exists))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(destinationPath) ||
                    string.Equals(item.Path, destinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (_clipboardCutMode)
                {
                    if (item.IsDirectory)
                    {
                        if (exists)
                        {
                            CopyDirectory(item.Path, destinationPath, overwrite: true);
                            Directory.Delete(item.Path, true);
                        }
                        else
                        {
                            Directory.Move(item.Path, destinationPath);
                        }
                    }
                    else
                    {
                        File.Move(item.Path, destinationPath, overwrite: policy == TransferConflictPolicy.Overwrite);
                    }

                    movedItems.Add(item);
                    continue;
                }

                if (item.IsDirectory)
                {
                    CopyDirectory(item.Path, destinationPath, policy == TransferConflictPolicy.Overwrite || !exists);
                }
                else
                {
                    File.Copy(item.Path, destinationPath, overwrite: policy == TransferConflictPolicy.Overwrite);
                }
            }
        });

        if (_clipboardCutMode && movedItems.Count > 0)
        {
            foreach (var moved in movedItems)
            {
                _clipboardItems.RemoveAll(existing => string.Equals(existing.Path, moved.Path, StringComparison.OrdinalIgnoreCase));
            }

            if (_clipboardItems.Count == 0)
            {
                _clipboardCutMode = false;
            }
        }

        var affectedPaths = new List<string?> { panel.CurrentPath };
        if (_clipboardCutMode)
        {
            affectedPaths.AddRange(movedItems.Select(item => Path.GetDirectoryName(item.Path)));
        }

        await ReloadPanelsForPathsAndDashboardAsync(affectedPaths);
    }

    private async Task OpenItemAsync(PanelViewModel panel, FileSystemItem item)
    {
        if (string.Equals(panel.CurrentPath, MemoListVirtualPath, StringComparison.Ordinal))
        {
            await OpenMemoListEntryAsync(panel, item);
            return;
        }

        if (item.IsDirectory)
        {
            await LoadPanelAsync(panel, item.FullPath, true);
            return;
        }

        try
        {
            if (IsImageFile(item.FullPath))
            {
                if (!TryShowImageViewer(item.FullPath))
                {
                    Process.Start(new ProcessStartInfo { FileName = item.FullPath, UseShellExecute = true });
                }
            }
            else
            {
                Process.Start(new ProcessStartInfo { FileName = item.FullPath, UseShellExecute = true });
            }
            _usageTrackingService.RecordFileOpen(item.FullPath, _settings.PinnedFiles.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase));
            await _usageTrackingService.PersistAsync();
            await RefreshSidebarAndDashboardAsync();
        }
        catch
        {
            StatusText = "Failed to open file";
        }
    }

    private async Task LoadPanelAsync(PanelViewModel panel, string path, bool track, bool suppressHistoryRecord = false)
    {
        var sw = Stopwatch.StartNew();
        var side = ReferenceEquals(panel, LeftPanel) ? "L" : ReferenceEquals(panel, RightPanel) ? "R" : "?";
        LiveTrace.Write($"LoadPanelAsync[{side}] start path='{path}', track={track}, suppressHistory={suppressHistoryRecord}");
        try
        {
            if (string.Equals(path, MemoListVirtualPath, StringComparison.Ordinal))
            {
                UpdateHistoryForPanel(panel, MemoListVirtualPath, suppressHistoryRecord);
                panel.CurrentPath = MemoListVirtualPath;
                panel.SetItems(BuildMemoListItems());
                panel.ApplyFilter(SearchText);
                // Keep memo list unselected by default to prevent unintended auto-open loops
                // when users switch back to the memo-list tab.
                panel.SelectedItem = null;
                UpdateTabTitleForPanel(panel);
                if (ReferenceEquals(panel, LeftPanel)) OnPropertyChanged(nameof(LeftCurrentPath));
                if (ReferenceEquals(panel, RightPanel)) OnPropertyChanged(nameof(RightCurrentPath));
                SyncSelectedDrivesFromPaths();
                RefreshPanelFreeSpaceTexts();
                OnPropertyChanged(nameof(LeftFolderInfo));
                OnPropertyChanged(nameof(RightFolderInfo));
                OnPropertyChanged(nameof(StatusBarText));
                StatusText = "메모목록";
                LiveTrace.Write($"LoadPanelAsync[{side}] memo-list done in {sw.ElapsedMilliseconds}ms");
                return;
            }

            if (string.Equals(path, FrequentFoldersVirtualPath, StringComparison.Ordinal))
            {
                var snapshot = await _usageTrackingService.GetSnapshotAsync();
                UpdateHistoryForPanel(panel, FrequentFoldersVirtualPath, suppressHistoryRecord);
                panel.CurrentPath = FrequentFoldersVirtualPath;
                panel.SetItems(BuildFrequentFolderItems(snapshot));
                panel.ApplyFilter(SearchText);
                SelectTopItemAfterLoad(panel);
                UpdateTabTitleForPanel(panel);
                if (ReferenceEquals(panel, LeftPanel)) OnPropertyChanged(nameof(LeftCurrentPath));
                if (ReferenceEquals(panel, RightPanel)) OnPropertyChanged(nameof(RightCurrentPath));
                SyncSelectedDrivesFromPaths();
                RefreshPanelFreeSpaceTexts();
                OnPropertyChanged(nameof(LeftFolderInfo));
                OnPropertyChanged(nameof(RightFolderInfo));
                OnPropertyChanged(nameof(StatusBarText));
                StatusText = "자주가는폴더";
                LiveTrace.Write($"LoadPanelAsync[{side}] frequent-folders done in {sw.ElapsedMilliseconds}ms");
                return;
            }

            if (string.Equals(path, FrequentFilesVirtualPath, StringComparison.Ordinal))
            {
                var snapshot = await _usageTrackingService.GetSnapshotAsync();
                UpdateHistoryForPanel(panel, FrequentFilesVirtualPath, suppressHistoryRecord);
                panel.CurrentPath = FrequentFilesVirtualPath;
                panel.SetItems(BuildFrequentFileItems(snapshot));
                panel.ApplyFilter(SearchText);
                SelectTopItemAfterLoad(panel);
                UpdateTabTitleForPanel(panel);
                if (ReferenceEquals(panel, LeftPanel)) OnPropertyChanged(nameof(LeftCurrentPath));
                if (ReferenceEquals(panel, RightPanel)) OnPropertyChanged(nameof(RightCurrentPath));
                SyncSelectedDrivesFromPaths();
                RefreshPanelFreeSpaceTexts();
                OnPropertyChanged(nameof(LeftFolderInfo));
                OnPropertyChanged(nameof(RightFolderInfo));
                OnPropertyChanged(nameof(StatusBarText));
                StatusText = "자주사용한파일";
                LiveTrace.Write($"LoadPanelAsync[{side}] frequent-files done in {sw.ElapsedMilliseconds}ms");
                return;
            }

            if (string.Equals(path, DriveRootVirtualPath, StringComparison.Ordinal))
            {
                UpdateHistoryForPanel(panel, DriveRootVirtualPath, suppressHistoryRecord);
                panel.CurrentPath = DriveRootVirtualPath;
                panel.SetItems(GetDriveRootItems());
                panel.ApplyFilter(SearchText);
                SelectTopItemAfterLoad(panel);
                UpdateTabTitleForPanel(panel);
                SyncSelectedDrivesFromPaths();
                RefreshPanelFreeSpaceTexts();
                OnPropertyChanged(nameof(LeftFolderInfo));
                OnPropertyChanged(nameof(RightFolderInfo));
                OnPropertyChanged(nameof(StatusBarText));
                StatusText = "Drive list";
                LiveTrace.Write($"LoadPanelAsync[{side}] drive-root done in {sw.ElapsedMilliseconds}ms");
                return;
            }

            var normalized = _fileSystemService.NormalizePath(path);
            LiveTrace.Write($"LoadPanelAsync[{side}] normalized='{normalized}'");
            IReadOnlyList<FileSystemItem> sourceItems;
            if (!TryGetDirectoryItemsFromCache(normalized, out sourceItems))
            {
                var loadTask = _fileSystemService.GetDirectoryItemsAsync(normalized);
                var completed = await Task.WhenAny(loadTask, Task.Delay(TimeSpan.FromSeconds(3)));
                if (!ReferenceEquals(completed, loadTask))
                {
                    UpdateHistoryForPanel(panel, normalized, suppressHistoryRecord);
                    panel.CurrentPath = normalized;
                    panel.SetItems(Array.Empty<FileSystemItem>());
                    panel.ApplyFilter(SearchText);
                    SelectTopItemAfterLoad(panel);
                    UpdateTabTitleForPanel(panel);
                    if (ReferenceEquals(panel, LeftPanel)) OnPropertyChanged(nameof(LeftCurrentPath));
                    if (ReferenceEquals(panel, RightPanel)) OnPropertyChanged(nameof(RightCurrentPath));
                    SyncSelectedDrivesFromPaths();
                    RefreshPanelFreeSpaceTexts();
                    OnPropertyChanged(nameof(LeftFolderInfo));
                    OnPropertyChanged(nameof(RightFolderInfo));
                    OnPropertyChanged(nameof(StatusBarText));
                    StatusText = "Folder load timeout";
                    LiveTrace.Write($"LoadPanelAsync[{side}] timeout after {sw.ElapsedMilliseconds}ms");
                    return;
                }

                sourceItems = await loadTask;
                SetDirectoryItemsCache(normalized, sourceItems);
                LiveTrace.Write($"LoadPanelAsync[{side}] cache-miss");
            }
            else
            {
                LiveTrace.Write($"LoadPanelAsync[{side}] cache-hit");
            }

            var items = ApplyPinnedPriority(sourceItems);
            LiveTrace.Write($"LoadPanelAsync[{side}] items={items.Count}");
            UpdateHistoryForPanel(panel, normalized, suppressHistoryRecord);
            panel.CurrentPath = normalized;
            panel.SetItems(items);
            panel.ApplyFilter(SearchText);
            SelectTopItemAfterLoad(panel);
            UpdateTabTitleForPanel(panel);

            if (track)
            {
                try
                {
                    _usageTrackingService.RecordFolderAccess(normalized, _settings.PinnedFolders.Contains(normalized, StringComparer.OrdinalIgnoreCase));
                    QueueUsageRefreshAfterNavigation();
                }
                catch
                {
                }
            }

            if (ReferenceEquals(panel, LeftPanel)) OnPropertyChanged(nameof(LeftCurrentPath));
            if (ReferenceEquals(panel, RightPanel)) OnPropertyChanged(nameof(RightCurrentPath));
            SyncSelectedDrivesFromPaths();
            RefreshPanelFreeSpaceTexts();
            OnPropertyChanged(nameof(LeftFolderInfo));
            OnPropertyChanged(nameof(RightFolderInfo));
            OnPropertyChanged(nameof(StatusBarText));
            StatusText = normalized;
            LiveTrace.Write($"LoadPanelAsync[{side}] done in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            StatusText = "Failed to load folder";
            LiveTrace.Write($"LoadPanelAsync[{side}] exception after {sw.ElapsedMilliseconds}ms: {ex}");
        }
        finally
        {
            TryUpdateDirectoryWatchers();
        }
    }

    private void TryUpdateDirectoryWatchers()
    {
        try
        {
            UpdateDirectoryWatchers();
        }
        catch (Exception ex)
        {
            LiveTrace.Write($"Watcher update failed: {ex.Message}");
        }
    }

    private void UpdateDirectoryWatchers()
    {
        var requiredPaths = CollectWatchablePanelPaths();
        var stalePaths = _directoryWatchers.Keys
            .Where(path => !requiredPaths.Contains(path))
            .ToArray();

        foreach (var stalePath in stalePaths)
        {
            if (!_directoryWatchers.TryGetValue(stalePath, out var staleWatcher))
            {
                continue;
            }

            staleWatcher.EnableRaisingEvents = false;
            staleWatcher.Created -= OnDirectoryWatcherChanged;
            staleWatcher.Changed -= OnDirectoryWatcherChanged;
            staleWatcher.Deleted -= OnDirectoryWatcherChanged;
            staleWatcher.Renamed -= OnDirectoryWatcherRenamed;
            staleWatcher.Dispose();
            _directoryWatchers.Remove(stalePath);
        }

        foreach (var path in requiredPaths)
        {
            if (_directoryWatchers.ContainsKey(path))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = false,
                    Filter = "*",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024
                };
                watcher.Created += OnDirectoryWatcherChanged;
                watcher.Changed += OnDirectoryWatcherChanged;
                watcher.Deleted += OnDirectoryWatcherChanged;
                watcher.Renamed += OnDirectoryWatcherRenamed;
                watcher.EnableRaisingEvents = true;
                _directoryWatchers[path] = watcher;
            }
            catch (Exception ex)
            {
                LiveTrace.Write($"Watcher attach failed for '{path}': {ex.Message}");
            }
        }
    }

    private HashSet<string> CollectWatchablePanelPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfWatchable(string? path)
        {
            if (!TryNormalizeWatchableDirectory(path, out var normalized))
            {
                return;
            }

            paths.Add(normalized);
        }

        foreach (var tab in LeftTabs)
        {
            AddIfWatchable(tab.Panel.CurrentPath);
        }

        foreach (var tab in RightTabs)
        {
            AddIfWatchable(tab.Panel.CurrentPath);
        }

        foreach (var slot in _fourPanels)
        {
            AddIfWatchable(slot.Panel.CurrentPath);
        }

        return paths;
    }

    private bool TryNormalizeWatchableDirectory(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (string.Equals(path, DriveRootVirtualPath, StringComparison.Ordinal) ||
            string.Equals(path, MemoListVirtualPath, StringComparison.Ordinal) ||
            string.Equals(path, FrequentFoldersVirtualPath, StringComparison.Ordinal) ||
            string.Equals(path, FrequentFilesVirtualPath, StringComparison.Ordinal))
        {
            return false;
        }

        normalized = _fileSystemService.NormalizePath(path);
        return _fileSystemService.DirectoryExists(normalized);
    }

    private void OnDirectoryWatcherChanged(object sender, FileSystemEventArgs e)
    {
        if (sender is not FileSystemWatcher watcher)
        {
            return;
        }

        QueueWatcherRefresh(watcher.Path);
    }

    private void OnDirectoryWatcherRenamed(object sender, RenamedEventArgs e)
    {
        if (sender is not FileSystemWatcher watcher)
        {
            return;
        }

        QueueWatcherRefresh(watcher.Path);
    }

    private void QueueWatcherRefresh(string? path)
    {
        if (!TryNormalizeWatchableDirectory(path, out var normalized))
        {
            return;
        }

        lock (_watcherSync)
        {
            _pendingWatcherRefreshPaths.Add(normalized);
            if (_watcherRefreshQueued)
            {
                return;
            }

            _watcherRefreshQueued = true;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            lock (_watcherSync)
            {
                _pendingWatcherRefreshPaths.Clear();
                _watcherRefreshQueued = false;
            }

            return;
        }

        _ = dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(250);

            string[] targets;
            lock (_watcherSync)
            {
                targets = _pendingWatcherRefreshPaths.ToArray();
                _pendingWatcherRefreshPaths.Clear();
                _watcherRefreshQueued = false;
            }

            if (targets.Length == 0)
            {
                return;
            }

            try
            {
                LiveTrace.Write($"Watcher refresh: {string.Join(", ", targets)}");
                await ReloadPanelsForPathsAsync(targets);
            }
            catch (Exception ex)
            {
                LiveTrace.Write($"Watcher refresh failed: {ex.Message}");
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SyncSelectedDrivesFromPaths()
    {
        var left = GetDriveLabel(LeftPanel.CurrentPath);
        if (!string.IsNullOrWhiteSpace(left))
        {
            SelectedLeftDrive = left;
        }

        var right = GetDriveLabel(RightPanel.CurrentPath);
        if (!string.IsNullOrWhiteSpace(right))
        {
            SelectedRightDrive = right;
        }
    }

    private static string? NormalizeDrivePath(string? driveLabel)
    {
        if (string.IsNullOrWhiteSpace(driveLabel))
        {
            return null;
        }

        var token = driveLabel.Trim().TrimEnd('\\');
        if (token.Length == 2 && token[1] == ':')
        {
            return $"{char.ToUpperInvariant(token[0])}:\\";
        }

        return null;
    }

    private bool TryResolveNavigableDirectoryPath(string path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();
        if (string.Equals(trimmed, DriveRootVirtualPath, StringComparison.Ordinal) ||
            string.Equals(trimmed, MemoListVirtualPath, StringComparison.Ordinal) ||
            string.Equals(trimmed, FrequentFoldersVirtualPath, StringComparison.Ordinal) ||
            string.Equals(trimmed, FrequentFilesVirtualPath, StringComparison.Ordinal))
        {
            resolvedPath = trimmed;
            return true;
        }

        var drivePath = NormalizeDrivePath(trimmed);
        if (!string.IsNullOrWhiteSpace(drivePath) && _fileSystemService.DirectoryExists(drivePath))
        {
            resolvedPath = drivePath;
            return true;
        }

        var normalized = _fileSystemService.NormalizePath(trimmed);
        if (_fileSystemService.DirectoryExists(normalized))
        {
            resolvedPath = normalized;
            return true;
        }

        return false;
    }

    private static string GetDriveLabel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        if (string.Equals(path, DriveRootVirtualPath, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        try
        {
            var root = Path.GetPathRoot(path)?.TrimEnd('\\');
            return string.IsNullOrWhiteSpace(root) ? string.Empty : root;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<FileSystemItem> GetDriveRootItems()
    {
        var now = DateTime.Now;
        return Environment.GetLogicalDrives()
            .Where(static drive => !string.IsNullOrWhiteSpace(drive))
            .Select(drive => new FileSystemItem
            {
                Name = drive.TrimEnd('\\'),
                Extension = string.Empty,
                FullPath = drive,
                IsDirectory = true,
                SizeBytes = 0,
                SizeDisplay = "<?붾젆?곕━>",
                LastModified = now,
                TypeDisplay = "Drive"
            })
            .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void RefreshPanelFreeSpaceTexts(bool allowQueueRefresh = true)
    {
        LeftFreeSpaceText = GetFreeSpaceText(LeftPanel.CurrentPath, SelectedLeftDrive, allowQueueRefresh);
        RightFreeSpaceText = GetFreeSpaceText(RightPanel.CurrentPath, SelectedRightDrive, allowQueueRefresh);
        foreach (var slot in _fourPanels)
        {
            slot.FreeSpaceText = GetFreeSpaceText(slot.Panel.CurrentPath, fallbackDrive: null, allowQueueRefresh);
        }
        OnPropertyChanged(nameof(LeftFolderInfo));
        OnPropertyChanged(nameof(RightFolderInfo));
    }

    public void RefreshFreeSpaceIndicators()
    {
        RefreshPanelFreeSpaceTexts();
    }

    private string GetFreeSpaceText(string? path, string? fallbackDrive, bool allowQueueRefresh)
    {
        var root = TryResolveDriveRoot(path, fallbackDrive);
        if (string.IsNullOrWhiteSpace(root))
        {
            return "-";
        }

        if (TryGetCachedFreeSpaceText(root, requireFresh: true, out var fresh))
        {
            return fresh;
        }

        if (allowQueueRefresh)
        {
            QueueFreeSpaceRefresh(root);
        }

        if (TryGetCachedFreeSpaceText(root, requireFresh: false, out var stale))
        {
            return stale;
        }

        return "-";
    }

    private bool TryGetCachedFreeSpaceText(string root, bool requireFresh, out string text)
    {
        lock (_freeSpaceSync)
        {
            if (_freeSpaceCache.TryGetValue(root, out var cached))
            {
                if (!requireFresh || DateTime.UtcNow - cached.CachedAtUtc <= FreeSpaceCacheDuration)
                {
                    text = cached.Text;
                    return true;
                }
            }
        }

        text = "-";
        return false;
    }

    private void QueueFreeSpaceRefresh(string root)
    {
        lock (_freeSpaceSync)
        {
            if (_freeSpaceRefreshInFlight.Contains(root))
            {
                return;
            }

            _freeSpaceRefreshInFlight.Add(root);
        }

        _ = Task.Run(() => ComputeFreeSpaceText(root)).ContinueWith(task =>
        {
            var resolved = task.Status == TaskStatus.RanToCompletion
                ? task.Result
                : "-";

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                lock (_freeSpaceSync)
                {
                    _freeSpaceCache[root] = new CachedFreeSpaceInfo
                    {
                        CachedAtUtc = DateTime.UtcNow,
                        Text = resolved
                    };
                    _freeSpaceRefreshInFlight.Remove(root);
                }
                return;
            }

            _ = dispatcher.BeginInvoke(() =>
            {
                lock (_freeSpaceSync)
                {
                    _freeSpaceCache[root] = new CachedFreeSpaceInfo
                    {
                        CachedAtUtc = DateTime.UtcNow,
                        Text = resolved
                    };
                    _freeSpaceRefreshInFlight.Remove(root);
                }

                RefreshPanelFreeSpaceTexts(allowQueueRefresh: false);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private static string ComputeFreeSpaceText(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            if (drive.TotalSize <= 0)
            {
                return $"{(drive.AvailableFreeSpace / 1024d / 1024d / 1024d):0.00}GB free";
            }

            var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
            var freePercent = Math.Round((drive.AvailableFreeSpace * 100d) / drive.TotalSize);
            return $"{freeGb:0.00}GB({freePercent:0}%) free";
        }
        catch
        {
            return "-";
        }
    }

    private static string? TryResolveDriveRoot(string? path, string? fallbackDrive)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && !string.Equals(path, DriveRootVirtualPath, StringComparison.Ordinal))
            {
                var root = Path.GetPathRoot(path);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    return root;
                }
            }
        }
        catch
        {
        }

        var fallback = NormalizeDrivePath(fallbackDrive);
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    private void UpdateTabTitleForPanel(PanelViewModel panel)
    {
        static string Title(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "-";
            if (string.Equals(path, MemoListVirtualPath, StringComparison.Ordinal)) return "메모목록";
            if (string.Equals(path, FrequentFoldersVirtualPath, StringComparison.Ordinal)) return "자주가는폴더";
            if (string.Equals(path, FrequentFilesVirtualPath, StringComparison.Ordinal)) return "자주사용한파일";
            if (string.Equals(path, DriveRootVirtualPath, StringComparison.Ordinal)) return "Drives";
            var root = Path.GetPathRoot(path);
            if (string.Equals(path.TrimEnd('\\'), root?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return path;
            return Path.GetFileName(path.TrimEnd('\\'));
        }

        var left = LeftTabs.FirstOrDefault(t => ReferenceEquals(t.Panel, panel));
        if (left is not null) { left.Title = Title(panel.CurrentPath); return; }
        var right = RightTabs.FirstOrDefault(t => ReferenceEquals(t.Panel, panel));
        if (right is not null) { right.Title = Title(panel.CurrentPath); return; }

        foreach (var slot in _fourPanels)
        {
            var tab = slot.Tabs.FirstOrDefault(t => ReferenceEquals(t.Panel, panel));
            if (tab is not null)
            {
                tab.Title = Title(panel.CurrentPath);
                if (ReferenceEquals(slot.SelectedTab, tab))
                {
                    OnPropertyChanged(nameof(FourPanels));
                }

                return;
            }
        }
    }

    private IReadOnlyList<FileSystemItem> BuildMemoListItems()
    {
        if (_settings.ItemMemos is null || _settings.ItemMemos.Count == 0)
        {
            return Array.Empty<FileSystemItem>();
        }

        var items = new List<FileSystemItem>();
        foreach (var pair in _settings.ItemMemos
                     .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                     .OrderBy(pair => Path.GetFileName(pair.Key), StringComparer.CurrentCultureIgnoreCase))
        {
            var fullPath = pair.Key;
            var isDirectory = Directory.Exists(fullPath);
            var isFile = File.Exists(fullPath);
            if (!isDirectory && !isFile)
            {
                continue;
            }

            var name = isDirectory
                ? Path.GetFileName(fullPath.TrimEnd('\\'))
                : Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = fullPath;
            }

            if (isDirectory)
            {
                var info = new DirectoryInfo(fullPath);
                items.Add(WithPinnedState(new FileSystemItem
                {
                    Name = name,
                    FullPath = fullPath,
                    IsDirectory = true,
                    SizeDisplay = "<디렉터리>",
                    LastModified = info.Exists ? info.LastWriteTime : DateTime.MinValue,
                    TypeDisplay = "Folder",
                    Memo = pair.Value
                }));
                continue;
            }

            var fileInfo = new FileInfo(fullPath);
            items.Add(WithPinnedState(new FileSystemItem
            {
                Name = fileInfo.Name,
                Extension = fileInfo.Extension,
                FullPath = fullPath,
                IsDirectory = false,
                SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
                SizeDisplay = fileInfo.Exists ? ToReadableSize(fileInfo.Length) : "0 B",
                LastModified = fileInfo.Exists ? fileInfo.LastWriteTime : DateTime.MinValue,
                TypeDisplay = string.IsNullOrWhiteSpace(fileInfo.Extension)
                    ? "File"
                    : $"{fileInfo.Extension.ToUpperInvariant()} File",
                Memo = pair.Value
            }));
        }

        return items
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<FileSystemItem> BuildFrequentFolderItems(UsageSnapshot snapshot)
    {
        return snapshot.FrequentFolders
            .Where(folder =>
                !string.IsNullOrWhiteSpace(folder.Path) &&
                _fileSystemService.DirectoryExists(folder.Path) &&
                !IsDriveRootPath(folder.Path))
            .Take(50)
            .Select(folder => WithPinnedState(new FileSystemItem
            {
                Name = string.IsNullOrWhiteSpace(folder.Name)
                    ? Path.GetFileName(folder.Path.TrimEnd('\\'))
                    : folder.Name,
                FullPath = folder.Path,
                IsDirectory = true,
                SizeDisplay = "<디렉터리>",
                LastModified = folder.LastAccessUtc.ToLocalTime(),
                TypeDisplay = "Frequent Folder"
            }))
            .ToArray();
    }

    private IReadOnlyList<FileSystemItem> BuildFrequentFileItems(UsageSnapshot snapshot)
    {
        return snapshot.FrequentFiles
            .Where(file => !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path))
            .Take(80)
            .Select(file =>
            {
                var info = new FileInfo(file.Path);
                return WithPinnedState(new FileSystemItem
                {
                    Name = string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileName(file.Path) : file.Name,
                    Extension = info.Extension,
                    FullPath = file.Path,
                    IsDirectory = false,
                    SizeBytes = info.Exists ? info.Length : 0,
                    SizeDisplay = info.Exists ? ToReadableSize(info.Length) : "0 B",
                    LastModified = info.Exists ? info.LastWriteTime : file.LastAccessUtc.ToLocalTime(),
                    TypeDisplay = string.IsNullOrWhiteSpace(info.Extension)
                        ? "Frequent File"
                        : $"{info.Extension.TrimStart('.').ToUpperInvariant()} File"
                });
            })
            .ToArray();
    }

    private static bool IsDriveRootPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var trimmed = path.Trim().TrimEnd('\\', '/');
            var root = Path.GetPathRoot(path)?.TrimEnd('\\', '/');
            return !string.IsNullOrWhiteSpace(root) &&
                   string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task OpenMemoListEntryAsync(PanelViewModel sourcePanel, FileSystemItem item)
    {
        if (item.IsParentDirectory || string.IsNullOrWhiteSpace(item.FullPath))
        {
            return;
        }

        var targetFolder = item.IsDirectory
            ? item.FullPath
            : Path.GetDirectoryName(item.FullPath);

        if (string.IsNullOrWhiteSpace(targetFolder) || !_fileSystemService.DirectoryExists(targetFolder))
        {
            StatusText = "메모 항목의 폴더를 찾을 수 없습니다.";
            return;
        }

        // Clear selection in memo-list source panel so returning to that tab
        // does not immediately retrigger open-on-selection behavior.
        sourcePanel.SelectedItem = null;

        var targetLeft = LeftTabs.Any(tab => ReferenceEquals(tab.Panel, sourcePanel));
        await AddPanelTabAsync(targetLeft, explicitSourcePath: targetFolder);
        var panel = targetLeft ? LeftPanel : RightPanel;
        if (!item.IsDirectory)
        {
            panel.SelectedItem = panel.Items.FirstOrDefault(entry =>
                string.Equals(entry.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool IsPersistablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return !IsVirtualListPath(path);
    }

    private string GetPersistableStartPath(string? currentPath, IEnumerable<string> tabPaths)
    {
        if (IsPersistablePath(currentPath))
        {
            return currentPath!;
        }

        var fallback = tabPaths.FirstOrDefault(IsPersistablePath);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private void UpdateHistoryForPanel(PanelViewModel panel, string path, bool suppressHistoryRecord)
    {
        var leftTab = LeftTabs.FirstOrDefault(tab => ReferenceEquals(tab.Panel, panel));
        if (leftTab is not null)
        {
            if (suppressHistoryRecord) leftTab.RecordPathWithoutHistoryShift(path);
            else leftTab.RecordVisitedPath(path);
            OnPropertyChanged(nameof(LeftPathHistory));
            OnPropertyChanged(nameof(LeftCanGoBack));
            OnPropertyChanged(nameof(LeftCanGoForward));
            _ = PersistPathHistoryAsync();
            return;
        }

        var rightTab = RightTabs.FirstOrDefault(tab => ReferenceEquals(tab.Panel, panel));
        if (rightTab is not null)
        {
            if (suppressHistoryRecord) rightTab.RecordPathWithoutHistoryShift(path);
            else rightTab.RecordVisitedPath(path);
            OnPropertyChanged(nameof(RightPathHistory));
            OnPropertyChanged(nameof(RightCanGoBack));
            OnPropertyChanged(nameof(RightCanGoForward));
            _ = PersistPathHistoryAsync();
            return;
        }

        foreach (var slot in _fourPanels)
        {
            var tab = slot.Tabs.FirstOrDefault(t => ReferenceEquals(t.Panel, panel));
            if (tab is null)
            {
                continue;
            }

            if (suppressHistoryRecord) tab.RecordPathWithoutHistoryShift(path);
            else tab.RecordVisitedPath(path);
            return;
        }
    }

    private async Task RestorePathHistoryAsync()
    {
        try
        {
            var snapshot = await _pathHistoryStoreService.LoadAsync();
            SelectedLeftTab?.InitializeHistory(snapshot.LeftPaths);
            SelectedRightTab?.InitializeHistory(snapshot.RightPaths);
            OnPropertyChanged(nameof(LeftPathHistory));
            OnPropertyChanged(nameof(RightPathHistory));
            OnPropertyChanged(nameof(LeftCanGoBack));
            OnPropertyChanged(nameof(LeftCanGoForward));
            OnPropertyChanged(nameof(RightCanGoBack));
            OnPropertyChanged(nameof(RightCanGoForward));
        }
        catch (Exception ex)
        {
            LiveTrace.Write($"RestorePathHistoryAsync failed: {ex}");
        }
    }

    private async Task PersistPathHistoryAsync()
    {
        try
        {
            var left = SelectedLeftTab?.HistoryCandidates ?? Array.Empty<string>();
            var right = SelectedRightTab?.HistoryCandidates ?? Array.Empty<string>();
            await _pathHistoryStoreService.SaveAsync(left, right);
        }
        catch (Exception ex)
        {
            LiveTrace.Write($"PersistPathHistoryAsync failed: {ex}");
        }
    }

    private async Task PersistSettingsAsync()
    {
        CaptureSessionState();
        await _settingsStorageService.SaveSettingsAsync(_settings);
    }

    private async Task InitializeFourPanelsAsync()
    {
        var defaults = new[]
        {
            LeftPanel.CurrentPath,
            RightPanel.CurrentPath,
            LeftPanel.CurrentPath,
            RightPanel.CurrentPath
        };

        var savedPaths = NormalizeFourPanelPaths(_settings.FourPanelPaths, defaults);
        var savedTabsState = DeserializeFourPanelTabsState(_settings.FourPanelTabStateJson);
        for (var index = 0; index < _fourPanels.Count; index++)
        {
            var slot = _fourPanels[index];
            var defaultPath = savedPaths[index];
            var slotState = savedTabsState is not null && index < savedTabsState.Slots.Count
                ? savedTabsState.Slots[index]
                : null;
            var tabPaths = NormalizeTabPaths(slotState?.TabPaths, defaultPath);

            slot.Tabs.Clear();
            for (var tabIndex = 0; tabIndex < tabPaths.Count; tabIndex++)
            {
                var tab = new PanelTabViewModel($"{slot.SlotKey}{tabIndex + 1}", new PanelViewModel
                {
                    CurrentPath = tabPaths[tabIndex]
                })
                {
                    IsTileViewEnabled = _settings.DefaultTileViewEnabled
                };
                slot.Tabs.Add(tab);
            }

            var selectedIndex = slotState?.SelectedIndex ?? 0;
            selectedIndex = Math.Clamp(selectedIndex, 0, slot.Tabs.Count - 1);
            slot.SelectedTab = slot.Tabs[selectedIndex];

            foreach (var tab in slot.Tabs)
            {
                await LoadPanelAsync(tab.Panel, tab.Panel.CurrentPath, false);
            }
        }
    }

    private void CaptureSessionState()
    {
        NormalizeFavoriteFileCategorySettings();
        _settings.LeftStartPath = GetPersistableStartPath(LeftPanel.CurrentPath, LeftTabs.Select(tab => tab.Panel.CurrentPath));
        _settings.RightStartPath = GetPersistableStartPath(RightPanel.CurrentPath, RightTabs.Select(tab => tab.Panel.CurrentPath));
        _settings.PanelCount = IsFourPanelMode ? 4 : 2;
        _settings.PanelLayout = IsFourPanelGridLayout ? "Grid" : "Horizontal";
        _settings.FourPanelTabStateJson = SerializeFourPanelTabsState();
        _settings.FourPanelPaths = _fourPanels
            .Select(slot => slot.Panel.CurrentPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        _settings.LeftOpenTabPaths = LeftTabs
            .Select(tab => tab.Panel.CurrentPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        _settings.RightOpenTabPaths = RightTabs
            .Select(tab => tab.Panel.CurrentPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        _settings.SelectedLeftTabIndex = LeftTabs.Count == 0 ? 0 : Math.Max(0, LeftTabs.IndexOf(SelectedLeftTab ?? LeftTabs[0]));
        _settings.SelectedRightTabIndex = RightTabs.Count == 0 ? 0 : Math.Max(0, RightTabs.IndexOf(SelectedRightTab ?? RightTabs[0]));
        _settings.DefaultSearchScope = SearchScope;
        _settings.DefaultSearchRecursive = SearchRecursive;
        _settings.ConflictPolicyDisplay = SelectedConflictPolicyDisplay;
    }

    private string SerializeFourPanelTabsState()
    {
        var state = new FourPanelTabsState();
        foreach (var slot in _fourPanels)
        {
            var tabPaths = slot.Tabs
                .Select(tab => tab.Panel.CurrentPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();

            if (tabPaths.Count == 0)
            {
                tabPaths.Add(_fileSystemService.NormalizePath(slot.Panel.CurrentPath));
            }

            var selectedIndex = slot.SelectedTab is null
                ? 0
                : Math.Max(0, slot.Tabs.IndexOf(slot.SelectedTab));
            selectedIndex = Math.Clamp(selectedIndex, 0, tabPaths.Count - 1);

            state.Slots.Add(new FourPanelSlotTabsState
            {
                TabPaths = tabPaths,
                SelectedIndex = selectedIndex
            });
        }

        return JsonSerializer.Serialize(state);
    }

    private static FourPanelTabsState? DeserializeFourPanelTabsState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<FourPanelTabsState>(json);
            if (state is null)
            {
                return null;
            }

            state.Slots ??= new List<FourPanelSlotTabsState>();
            foreach (var slot in state.Slots)
            {
                slot.TabPaths ??= new List<string>();
            }

            return state;
        }
        catch
        {
            return null;
        }
    }

    private void RestoreSessionTabsFromSettings()
    {
        var defaultLeft = _fileSystemService.NormalizePath(_settings.LeftStartPath);
        var defaultRight = _fileSystemService.NormalizePath(_settings.RightStartPath);

        var leftPaths = _settings.RememberSessionTabs
            ? NormalizeTabPaths(_settings.LeftOpenTabPaths, defaultLeft)
            : NormalizeTabPaths(Array.Empty<string>(), defaultLeft);
        var rightPaths = _settings.RememberSessionTabs
            ? NormalizeTabPaths(_settings.RightOpenTabPaths, defaultRight)
            : NormalizeTabPaths(Array.Empty<string>(), defaultRight);

        LeftTabs.Clear();
        RightTabs.Clear();

        for (var index = 0; index < leftPaths.Count; index++)
        {
            var tab = new PanelTabViewModel($"L{index + 1}", new PanelViewModel
            {
                CurrentPath = leftPaths[index]
            })
            {
                IsTileViewEnabled = _settings.DefaultTileViewEnabled
            };
            LeftTabs.Add(tab);
        }

        for (var index = 0; index < rightPaths.Count; index++)
        {
            var tab = new PanelTabViewModel($"R{index + 1}", new PanelViewModel
            {
                CurrentPath = rightPaths[index]
            })
            {
                IsTileViewEnabled = _settings.DefaultTileViewEnabled
            };
            RightTabs.Add(tab);
        }

        var leftIndex = Math.Clamp(_settings.SelectedLeftTabIndex, 0, LeftTabs.Count - 1);
        var rightIndex = Math.Clamp(_settings.SelectedRightTabIndex, 0, RightTabs.Count - 1);
        SelectedLeftTab = LeftTabs[leftIndex];
        SelectedRightTab = RightTabs[rightIndex];
        LeftCurrentPath = SelectedLeftTab.Panel.CurrentPath;
        RightCurrentPath = SelectedRightTab.Panel.CurrentPath;
    }

    private void ApplyDefaultViewToAllTabs(bool enabled)
    {
        foreach (var tab in LeftTabs)
        {
            tab.IsTileViewEnabled = enabled;
        }

        foreach (var tab in RightTabs)
        {
            tab.IsTileViewEnabled = enabled;
        }

        foreach (var slot in _fourPanels)
        {
            foreach (var tab in slot.Tabs)
            {
                tab.IsTileViewEnabled = enabled;
            }
        }

        OnPropertyChanged(nameof(IsTileViewEnabled));
        OnPropertyChanged(nameof(IsCompactListViewEnabled));
        OnPropertyChanged(nameof(LeftPanelIsTileViewEnabled));
        OnPropertyChanged(nameof(LeftPanelIsCompactListViewEnabled));
        OnPropertyChanged(nameof(RightPanelIsTileViewEnabled));
        OnPropertyChanged(nameof(RightPanelIsCompactListViewEnabled));
    }

    private IReadOnlyList<string> NormalizeTabPaths(IReadOnlyList<string>? paths, string fallbackPath)
    {
        var fallback = string.IsNullOrWhiteSpace(fallbackPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : fallbackPath;

        var normalized = (paths ?? Array.Empty<string>())
            .Select(path => IsVirtualListPath(path)
                ? path
                : _fileSystemService.NormalizePath(path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        if (normalized.Count == 0)
        {
            normalized.Add(_fileSystemService.NormalizePath(fallback));
        }

        return normalized;
    }

    private IReadOnlyList<string> NormalizeFourPanelPaths(IReadOnlyList<string>? paths, IReadOnlyList<string> defaults)
    {
        var result = new List<string>(4);
        var source = (paths ?? Array.Empty<string>()).ToArray();
        for (var index = 0; index < 4; index++)
        {
            var candidate = index < source.Length ? source[index] : defaults[index];
            var normalized = IsVirtualListPath(candidate)
                ? candidate
                : _fileSystemService.NormalizePath(candidate);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = _fileSystemService.NormalizePath(defaults[index]);
            }

            result.Add(normalized);
        }

        return result;
    }

    private async Task OpenVirtualTabAsync(string virtualPath)
    {
        if (IsFourPanelMode)
        {
            var activeSlot = _fourPanels.FirstOrDefault(slot => slot.IsActive) ?? _fourPanels.FirstOrDefault();
            if (activeSlot is not null)
            {
                var existingInActiveSlot = activeSlot.Tabs.FirstOrDefault(tab =>
                    string.Equals(tab.Panel.CurrentPath, virtualPath, StringComparison.Ordinal));
                if (existingInActiveSlot is not null)
                {
                    activeSlot.SelectedTab = existingInActiveSlot;
                    SetActiveFourPanel(_fourPanels.IndexOf(activeSlot));
                    return;
                }

                await AddFourPanelTabAsync(activeSlot, explicitSourcePath: virtualPath);
            }

            return;
        }

        var activeTabs = IsLeftPanelActive ? LeftTabs : RightTabs;
        var existingInActivePanel = activeTabs.FirstOrDefault(tab =>
            string.Equals(tab.Panel.CurrentPath, virtualPath, StringComparison.Ordinal));
        if (existingInActivePanel is not null)
        {
            if (IsLeftPanelActive)
            {
                SelectedLeftTab = existingInActivePanel;
            }
            else
            {
                SelectedRightTab = existingInActivePanel;
            }
            return;
        }

        await AddPanelTabAsync(IsLeftPanelActive, explicitSourcePath: virtualPath);
    }

    private static bool IsVirtualListPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return string.Equals(path, MemoListVirtualPath, StringComparison.Ordinal) ||
               string.Equals(path, FrequentFoldersVirtualPath, StringComparison.Ordinal) ||
               string.Equals(path, FrequentFilesVirtualPath, StringComparison.Ordinal);
    }

    private static int ParsePanelCount(object? parameter)
    {
        if (parameter is int count)
        {
            return count;
        }

        if (parameter is string text && int.TryParse(text, out var parsed))
        {
            return parsed;
        }

        return 2;
    }

    private static string BuildPanelSummary(PanelViewModel panel, string prefix)
    {
        var dirs = panel.Items.Count(item => item.IsDirectory);
        var files = panel.Items.Count - dirs;
        var totalBytes = panel.Items.Where(item => !item.IsDirectory).Sum(item => item.SizeBytes);
        return $"{prefix}: {dirs}D/{files}F {ToReadableSize(totalBytes)}";
    }

    private static string BuildFolderInfo(PanelViewModel panel)
    {
        var dirs = panel.Items.Count(item => item.IsDirectory);
        var files = panel.Items.Count - dirs;
        var totalBytes = panel.Items.Where(item => !item.IsDirectory).Sum(item => item.SizeBytes);
        return $"폴더 {dirs} / 파일 {files} / 용량 {ToReadableSize(totalBytes)}";
    }

    private async Task RefreshSidebarAndDashboardAsync()
    {
        var snapshot = await _usageTrackingService.GetSnapshotAsync();
        var quickAccess = await _quickAccessService.GetItemsAsync(_settings, snapshot);
        ResetCollection(QuickAccessItems, quickAccess);
        ResetCollection(FavoriteToolbarFolders, BuildFavoriteToolbarItems(_settings.FavoriteFolders));
        ResetCollection(FavoriteToolbarFiles, BuildFavoriteToolbarFiles(_settings.FavoriteFiles));
        ResetCollection(RecentFiles, snapshot.RecentFiles.Where(r => File.Exists(r.Path)).Take(30));
        ResetCollection(FrequentFiles, snapshot.FrequentFiles.Where(r => File.Exists(r.Path)).Take(30));
        ResetCollection(PinnedFolders, _settings.PinnedFolders.Where(Directory.Exists).Select(p => new QuickAccessItem { Name = Path.GetFileName(p), Path = p, Category = "怨좎젙 ?대뜑", IsPinned = true }));
        ResetCollection(PinnedFiles, _settings.PinnedFiles.Where(File.Exists).Select(p => new TrackedFileRecord { Name = Path.GetFileName(p), Path = p, LastAccessUtc = File.GetLastWriteTimeUtc(p), IsPinned = true }));
    }

    private void QueueUsageRefreshAfterNavigation()
    {
        if (_usageRefreshQueued)
        {
            return;
        }

        _usageRefreshQueued = true;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _usageRefreshQueued = false;
            return;
        }

        _ = dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await _usageTrackingService.PersistAsync();
                await RefreshSidebarAndDashboardAsync();
            }
            catch
            {
            }
            finally
            {
                _usageRefreshQueued = false;
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static IReadOnlyList<QuickAccessItem> BuildFavoriteToolbarItems(IEnumerable<string> favoriteFolders)
    {
        var items = new List<QuickAccessItem>();
        foreach (var path in favoriteFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            items.Add(new QuickAccessItem
            {
                Name = GetFavoriteToolbarLabel(path),
                Path = path,
                Category = "즐겨찾기",
                IsPinned = true
            });
        }

        return items;
    }

    private static string GetFavoriteToolbarLabel(string path)
    {
        var trimmed = (path ?? string.Empty).Trim().TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(trimmed);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var root = Path.GetPathRoot(trimmed)?.TrimEnd('\\');
        return string.IsNullOrWhiteSpace(root) ? trimmed : root;
    }

    private static IReadOnlyList<TrackedFileRecord> BuildFavoriteToolbarFiles(IEnumerable<string> favoriteFiles)
    {
        var records = favoriteFiles
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new TrackedFileRecord
            {
                Name = Path.GetFileName(path),
                Path = path,
                LastAccessUtc = File.GetLastWriteTimeUtc(path),
                IsPinned = true
            })
            .OrderBy(record => record.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        records.Insert(0, new TrackedFileRecord
        {
            Name = $"즐겨찾기 파일[{records.Count}개]",
            Path = string.Empty,
            LastAccessUtc = DateTime.UtcNow,
            IsPinned = false
        });

        return records;
    }

    private void NormalizeFavoriteFileCategorySettings()
    {
        _settings.FavoriteFileCategoryFolders ??= new List<string>();
        _settings.FavoriteFileCategoryMappings ??= new List<string>();
        _settings.FavoriteFiles ??= new List<string>();

        var normalizedFolders = _settings.FavoriteFileCategoryFolders
            .Select(NormalizeFavoriteFileCategoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedFolders.Count == 0)
        {
            normalizedFolders.Add(DefaultFavoriteFileCategory);
        }

        var mappings = ParseFavoriteFileCategoryMappings(_settings.FavoriteFileCategoryMappings);
        var normalizedMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in _settings.FavoriteFiles
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var category = mappings.TryGetValue(filePath, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
                ? NormalizeFavoriteFileCategoryPath(mapped)
                : ResolveFallbackFavoriteFileCategory(normalizedFolders);

            if (!normalizedFolders.Contains(category, StringComparer.OrdinalIgnoreCase))
            {
                normalizedFolders.Add(category);
            }

            normalizedMappings[filePath] = category;
        }

        _settings.FavoriteFileCategoryFolders = normalizedFolders
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _settings.FavoriteFileCategoryMappings = SerializeFavoriteFileCategoryMappings(normalizedMappings);
    }

    private static Dictionary<string, string> ParseFavoriteFileCategoryMappings(IEnumerable<string>? entries)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (entries is null)
        {
            return result;
        }

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var separator = entry.IndexOf('\t');
            if (separator <= 0 || separator >= entry.Length - 1)
            {
                continue;
            }

            var filePath = entry[..separator].Trim();
            var category = entry[(separator + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            result[filePath] = NormalizeFavoriteFileCategoryPath(category);
        }

        return result;
    }

    private static List<string> SerializeFavoriteFileCategoryMappings(IReadOnlyDictionary<string, string> mappings)
    {
        return mappings
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .Select(pair =>
            {
                var category = NormalizeFavoriteFileCategoryPath(pair.Value);
                return $"{pair.Key}\t{category}";
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string NormalizeFavoriteFileCategoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DefaultFavoriteFileCategory;
        }

        var normalized = path
            .Replace('\\', '/')
            .Split(['/', '>'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeFavoriteFileCategorySegment)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();

        return normalized.Length == 0
            ? DefaultFavoriteFileCategory
            : string.Join("/", normalized);
    }

    private static string NormalizeFavoriteFileCategorySegment(string? name)
    {
        var segment = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(segment))
        {
            return string.Empty;
        }

        return segment
            .Replace("/", string.Empty, StringComparison.Ordinal)
            .Replace("\\", string.Empty, StringComparison.Ordinal)
            .Replace("\t", string.Empty, StringComparison.Ordinal);
    }

    private static string ResolveFallbackFavoriteFileCategory(IReadOnlyList<string>? folders)
    {
        var candidates = (folders ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
        {
            return DefaultFavoriteFileCategory;
        }

        var explicitDefault = candidates.FirstOrDefault(path =>
            string.Equals(path, DefaultFavoriteFileCategory, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(explicitDefault) ? candidates[0] : explicitDefault;
    }

    private void EnsureFavoriteFileCategoryFolderExists(string? folderPath)
    {
        var normalized = NormalizeFavoriteFileCategoryPath(folderPath);
        if (!_settings.FavoriteFileCategoryFolders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            _settings.FavoriteFileCategoryFolders.Add(normalized);
        }
    }

    private void SetFavoriteFileCategory(string filePath, string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var normalizedFolder = NormalizeFavoriteFileCategoryPath(folderPath);
        EnsureFavoriteFileCategoryFolderExists(normalizedFolder);
        var mappings = ParseFavoriteFileCategoryMappings(_settings.FavoriteFileCategoryMappings);
        mappings[filePath] = normalizedFolder;
        _settings.FavoriteFileCategoryMappings = SerializeFavoriteFileCategoryMappings(mappings);
    }

    private void RemoveFavoriteFileCategory(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var mappings = ParseFavoriteFileCategoryMappings(_settings.FavoriteFileCategoryMappings);
        if (!mappings.Remove(filePath))
        {
            return;
        }

        _settings.FavoriteFileCategoryMappings = SerializeFavoriteFileCategoryMappings(mappings);
    }

    private async Task ReloadPanelsAndDashboardAsync()
    {
        _directoryItemsCache.Clear();
        foreach (var tab in LeftTabs) await LoadPanelAsync(tab.Panel, tab.Panel.CurrentPath, false);
        foreach (var tab in RightTabs) await LoadPanelAsync(tab.Panel, tab.Panel.CurrentPath, false);
        foreach (var slot in _fourPanels) await LoadPanelAsync(slot.Panel, slot.Panel.CurrentPath, false);
        await RefreshSidebarAndDashboardAsync();
        await PersistSettingsAsync();
    }

    private async Task ReloadPanelsForPathAndDashboardAsync(string? path)
    {
        await ReloadPanelsForPathsAndDashboardAsync([path]);
    }

    private async Task ReloadPanelsForPathsAndDashboardAsync(IEnumerable<string?> paths)
    {
        await ReloadPanelsForPathsAsync(paths);
        await RefreshSidebarAndDashboardAsync();
        await PersistSettingsAsync();
    }

    private async Task ReloadPanelsForPathsAsync(IEnumerable<string?> paths)
    {
        var targetPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targetPaths.Length == 0)
        {
            _directoryItemsCache.Clear();
            foreach (var tab in LeftTabs) await LoadPanelAsync(tab.Panel, tab.Panel.CurrentPath, false);
            foreach (var tab in RightTabs) await LoadPanelAsync(tab.Panel, tab.Panel.CurrentPath, false);
            foreach (var slot in _fourPanels) await LoadPanelAsync(slot.Panel, slot.Panel.CurrentPath, false);
            return;
        }

        InvalidateDirectoryItemsCaches(targetPaths);

        bool IsTargetPath(string candidatePath)
            => targetPaths.Contains(candidatePath.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase);

        foreach (var tab in LeftTabs.Where(tab => IsTargetPath(tab.Panel.CurrentPath)))
        {
            await LoadPanelAsync(tab.Panel, tab.Panel.CurrentPath, false);
        }

        foreach (var tab in RightTabs.Where(tab => IsTargetPath(tab.Panel.CurrentPath)))
        {
            await LoadPanelAsync(tab.Panel, tab.Panel.CurrentPath, false);
        }

        foreach (var slot in _fourPanels.Where(slot => IsTargetPath(slot.Panel.CurrentPath)))
        {
            await LoadPanelAsync(slot.Panel, slot.Panel.CurrentPath, false);
        }
    }

    private void QueuePostMutationRefresh()
    {
        if (_postMutationRefreshQueued)
        {
            return;
        }

        _postMutationRefreshQueued = true;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _postMutationRefreshQueued = false;
            return;
        }

        _ = dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await RefreshSidebarAndDashboardAsync();
                await PersistSettingsAsync();
            }
            catch
            {
            }
            finally
            {
                _postMutationRefreshQueued = false;
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private bool TryGetDirectoryItemsFromCache(string normalizedPath, out IReadOnlyList<FileSystemItem> items)
    {
        items = Array.Empty<FileSystemItem>();
        if (!_directoryItemsCache.TryGetValue(normalizedPath, out var cached))
        {
            return false;
        }

        if (DateTime.UtcNow - cached.CachedAtUtc > DirectoryItemsCacheDuration)
        {
            _directoryItemsCache.Remove(normalizedPath);
            return false;
        }

        items = cached.Items;
        return true;
    }

    private void SetDirectoryItemsCache(string normalizedPath, IReadOnlyList<FileSystemItem> items)
    {
        _directoryItemsCache[normalizedPath] = new CachedDirectoryItems
        {
            CachedAtUtc = DateTime.UtcNow,
            Items = items
        };

        if (_directoryItemsCache.Count <= MaxDirectoryItemsCacheEntries)
        {
            return;
        }

        var trimTargets = _directoryItemsCache
            .OrderBy(pair => pair.Value.CachedAtUtc)
            .Take(_directoryItemsCache.Count - MaxDirectoryItemsCacheEntries)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in trimTargets)
        {
            _directoryItemsCache.Remove(key);
        }
    }

    private void InvalidateDirectoryItemsCache(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (string.Equals(path, DriveRootVirtualPath, StringComparison.Ordinal) ||
            string.Equals(path, MemoListVirtualPath, StringComparison.Ordinal) ||
            string.Equals(path, FrequentFoldersVirtualPath, StringComparison.Ordinal) ||
            string.Equals(path, FrequentFilesVirtualPath, StringComparison.Ordinal))
        {
            return;
        }

        var normalized = _fileSystemService.NormalizePath(path);
        _directoryItemsCache.Remove(normalized);
    }

    private void InvalidateDirectoryItemsCaches(IEnumerable<string?> paths)
    {
        foreach (var path in paths)
        {
            InvalidateDirectoryItemsCache(path);
        }
    }

    private static void CopyDirectory(string source, string dest, bool overwrite)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite);
        foreach (var dir in Directory.EnumerateDirectories(source)) CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)), overwrite);
    }

    private static string EnsureUniquePath(string targetDirectory, string name, bool isDirectory)
    {
        var basePath = Path.Combine(targetDirectory, name);
        if (!File.Exists(basePath) && !Directory.Exists(basePath)) return basePath;
        var fileName = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isDirectory ? string.Empty : Path.GetExtension(name);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(targetDirectory, $"{fileName} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(targetDirectory, $"{fileName} ({Guid.NewGuid():N}){extension}");
    }

    private static IReadOnlyList<FileSystemItem> ResolveSelection(PanelViewModel panel, IReadOnlyList<FileSystemItem>? selectedItems)
    {
        if (selectedItems is { Count: > 0 }) return selectedItems;
        return panel.SelectedItem is null ? Array.Empty<FileSystemItem>() : [panel.SelectedItem];
    }

    private static void SelectTopItemAfterLoad(PanelViewModel panel)
    {
        var parentDirectoryItem = panel.Items.FirstOrDefault(item => item.IsParentDirectory);
        if (parentDirectoryItem is not null)
        {
            panel.SelectedItem = parentDirectoryItem;
            return;
        }

        panel.SelectedItem = panel.Items.FirstOrDefault();
    }

    private static string? FindNearestSurvivingPath(
        PanelViewModel panel,
        HashSet<string> deletingPaths,
        int preferredIndex)
    {
        if (panel.Items.Count == 0)
        {
            return null;
        }

        var baseIndex = preferredIndex < 0
            ? 0
            : Math.Clamp(preferredIndex, 0, panel.Items.Count - 1);

        bool IsCandidate(FileSystemItem item) =>
            !item.IsParentDirectory &&
            !string.IsNullOrWhiteSpace(item.FullPath) &&
            !deletingPaths.Contains(item.FullPath);

        var center = panel.Items[baseIndex];
        if (IsCandidate(center))
        {
            return center.FullPath;
        }

        for (var offset = 1; offset < panel.Items.Count; offset++)
        {
            var forward = baseIndex + offset;
            if (forward < panel.Items.Count)
            {
                var forwardItem = panel.Items[forward];
                if (IsCandidate(forwardItem))
                {
                    return forwardItem.FullPath;
                }
            }

            var backward = baseIndex - offset;
            if (backward >= 0)
            {
                var backwardItem = panel.Items[backward];
                if (IsCandidate(backwardItem))
                {
                    return backwardItem.FullPath;
                }
            }
        }

        return null;
    }

    private static void RestorePanelSelectionByPaths(PanelViewModel? panel, IReadOnlyList<string> selectedPaths)
    {
        if (panel is null || selectedPaths.Count == 0 || panel.Items.Count == 0)
        {
            return;
        }

        foreach (var path in selectedPaths)
        {
            var matchedItem = panel.Items.FirstOrDefault(item =>
                !item.IsParentDirectory &&
                string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase));
            if (matchedItem is not null)
            {
                panel.SelectedItem = matchedItem;
                return;
            }
        }
    }

    private static int FindDeletionAnchorIndex(PanelViewModel panel, IReadOnlyList<FileSystemItem> selectedItems)
    {
        var indices = selectedItems
            .Where(item => !item.IsParentDirectory)
            .Select(item => panel.Items.IndexOf(item))
            .Where(index => index >= 0)
            .OrderBy(index => index)
            .ToArray();

        if (indices.Length > 0)
        {
            return indices[0];
        }

        if (panel.SelectedItem is not null)
        {
            var selectedIndex = panel.Items.IndexOf(panel.SelectedItem);
            if (selectedIndex >= 0)
            {
                return selectedIndex;
            }
        }

        return -1;
    }

    private static void SelectNearestItemAfterDeletion(PanelViewModel panel, int preferredIndex)
    {
        if (panel.Items.Count == 0)
        {
            panel.SelectedItem = null;
            return;
        }

        var baseIndex = preferredIndex < 0
            ? 0
            : Math.Clamp(preferredIndex, 0, panel.Items.Count - 1);

        var candidate = panel.Items[baseIndex];
        if (!candidate.IsParentDirectory)
        {
            panel.SelectedItem = candidate;
            return;
        }

        for (var offset = 1; offset < panel.Items.Count; offset++)
        {
            var forward = baseIndex + offset;
            if (forward < panel.Items.Count && !panel.Items[forward].IsParentDirectory)
            {
                panel.SelectedItem = panel.Items[forward];
                return;
            }

            var backward = baseIndex - offset;
            if (backward >= 0 && !panel.Items[backward].IsParentDirectory)
            {
                panel.SelectedItem = panel.Items[backward];
                return;
            }
        }

        panel.SelectedItem = null;
    }

    private bool IsPinned(FileSystemItem item) => item.IsDirectory
        ? _settings.PinnedFolders.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase)
        : _settings.PinnedFiles.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase);

    private static void TogglePinPath(List<string> paths, string targetPath, bool pin)
    {
        var existing = paths.FirstOrDefault(p => string.Equals(p, targetPath, StringComparison.OrdinalIgnoreCase));
        if (pin)
        {
            if (existing is null) paths.Add(targetPath);
        }
        else if (existing is not null) paths.Remove(existing);
    }

    private static void ToggleFavoritePath(List<string> paths, string targetPath, bool favorite)
    {
        var existing = paths.FirstOrDefault(p => string.Equals(p, targetPath, StringComparison.OrdinalIgnoreCase));
        if (favorite)
        {
            if (existing is null)
            {
                paths.Add(targetPath);
            }
            return;
        }

        if (existing is not null)
        {
            paths.Remove(existing);
        }
    }

    private void ReplacePathInSettings(string oldPath, string newPath, bool isDirectory)
    {
        ReplacePath(_settings.FavoriteFolders, oldPath, newPath, isDirectory);
        ReplacePath(_settings.FavoriteFiles, oldPath, newPath, !isDirectory);
        ReplacePath(_settings.PinnedFolders, oldPath, newPath, isDirectory);
        ReplacePath(_settings.PinnedFiles, oldPath, newPath, !isDirectory);
        ReplaceFavoriteFileCategoryMappings(oldPath, newPath, isDirectory);
        ReplaceMemoPaths(oldPath, newPath, isDirectory);
    }

    private static void ReplacePath(List<string> list, string oldPath, string newPath, bool apply)
    {
        if (!apply) return;
        var idx = list.FindIndex(p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) list[idx] = newPath;
    }

    private void ReplaceMemoPaths(string oldPath, string newPath, bool isDirectory)
    {
        if (_settings.ItemMemos is null || _settings.ItemMemos.Count == 0)
        {
            return;
        }

        var updates = new List<(string OldKey, string NewKey, string Value)>();
        var oldPrefix = oldPath.TrimEnd('\\') + "\\";
        var newPrefix = newPath.TrimEnd('\\') + "\\";

        foreach (var pair in _settings.ItemMemos)
        {
            if (string.Equals(pair.Key, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                updates.Add((pair.Key, newPath, pair.Value));
                continue;
            }

            if (isDirectory && pair.Key.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var suffix = pair.Key[oldPrefix.Length..];
                updates.Add((pair.Key, newPrefix + suffix, pair.Value));
            }
        }

        if (updates.Count == 0)
        {
            return;
        }

        foreach (var update in updates)
        {
            _settings.ItemMemos.Remove(update.OldKey);
        }

        foreach (var update in updates)
        {
            _settings.ItemMemos[update.NewKey] = update.Value;
        }
    }

    private void ReplaceFavoriteFileCategoryMappings(string oldPath, string newPath, bool isDirectory)
    {
        if (_settings.FavoriteFileCategoryMappings is null || _settings.FavoriteFileCategoryMappings.Count == 0)
        {
            return;
        }

        var mappings = ParseFavoriteFileCategoryMappings(_settings.FavoriteFileCategoryMappings);
        var updated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var oldPrefix = oldPath.TrimEnd('\\') + "\\";
        var newPrefix = newPath.TrimEnd('\\') + "\\";

        foreach (var pair in mappings)
        {
            if (string.Equals(pair.Key, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                updated[newPath] = pair.Value;
                continue;
            }

            if (isDirectory && pair.Key.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var suffix = pair.Key[oldPrefix.Length..];
                updated[newPrefix + suffix] = pair.Value;
                continue;
            }

            updated[pair.Key] = pair.Value;
        }

        _settings.FavoriteFileCategoryMappings = SerializeFavoriteFileCategoryMappings(updated);
    }

    private bool RemoveMemoEntriesForPath(string path, bool isDirectory)
    {
        if (_settings.ItemMemos is null || _settings.ItemMemos.Count == 0 || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var removed = false;
        if (_settings.ItemMemos.Remove(path))
        {
            removed = true;
        }

        if (!isDirectory)
        {
            return removed;
        }

        var prefix = path.TrimEnd('\\') + "\\";
        var descendants = _settings.ItemMemos.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var key in descendants)
        {
            if (_settings.ItemMemos.Remove(key))
            {
                removed = true;
            }
        }

        return removed;
    }

    private bool TryResolveTransferDestination(FileSystemItem sourceItem, string targetDirectory, TransferConflictPolicy policy, out string? destinationPath, out bool exists)
    {
        destinationPath = Path.Combine(targetDirectory, sourceItem.Name);
        exists = File.Exists(destinationPath) || Directory.Exists(destinationPath);
        if (!exists) return true;
        switch (policy)
        {
            case TransferConflictPolicy.Skip:
                destinationPath = null;
                return false;
            case TransferConflictPolicy.RenameNew:
                destinationPath = EnsureUniquePath(targetDirectory, sourceItem.Name, sourceItem.IsDirectory);
                exists = false;
                return true;
            case TransferConflictPolicy.Overwrite:
                PrepareOverwriteDestination(sourceItem, destinationPath);
                exists = File.Exists(destinationPath) || Directory.Exists(destinationPath);
                return true;
            default:
                return true;
        }
    }

    private static void PrepareOverwriteDestination(FileSystemItem sourceItem, string destinationPath)
    {
        if (sourceItem.IsDirectory && File.Exists(destinationPath)) File.Delete(destinationPath);
        if (!sourceItem.IsDirectory && Directory.Exists(destinationPath)) Directory.Delete(destinationPath, true);
    }

    private static bool TryTransferWithElevation(
        FileSystemItem item,
        string destinationPath,
        bool move,
        TransferConflictPolicy policy,
        bool destinationExists)
    {
        try
        {
            var src = item.FullPath.Replace("'", "''");
            var dst = destinationPath.Replace("'", "''");
            var overwrite = policy == TransferConflictPolicy.Overwrite;
            var script =
                "$ErrorActionPreference='Stop';" +
                $"$src='{src}';$dst='{dst}';$isDir={(item.IsDirectory ? "$true" : "$false")};$move={(move ? "$true" : "$false")};$overwrite={(overwrite ? "$true" : "$false")};$exists={(destinationExists ? "$true" : "$false")};" +
                "if($move){" +
                    "if($isDir){" +
                        "if($exists -and $overwrite){Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force;Remove-Item -LiteralPath $src -Recurse -Force;}" +
                        "else{Move-Item -LiteralPath $src -Destination $dst -Force;}" +
                    "}else{" +
                        "Move-Item -LiteralPath $src -Destination $dst -Force;" +
                    "}" +
                "}else{" +
                    "if($isDir){Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force;}" +
                    "else{Copy-Item -LiteralPath $src -Destination $dst -Force;}" +
                "}";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private TransferConflictPolicy GetTransferConflictPolicy() => SelectedConflictPolicyDisplay switch
    {
        "Overwrite" => TransferConflictPolicy.Overwrite,
        "Skip" => TransferConflictPolicy.Skip,
        _ => TransferConflictPolicy.RenameNew
    };

    private static string NormalizeExtensionFilter(string extension)
    {
        var value = (extension ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.StartsWith('.') ? value : $".{value}";
    }

    private static bool FilterSize(FileSystemItem item, bool hasMin, long minKb, bool hasMax, long maxKb)
    {
        if (item.IsDirectory) return !hasMin && !hasMax;
        var kb = item.SizeBytes / 1024.0;
        if (hasMin && kb < minKb) return false;
        if (hasMax && kb > maxKb) return false;
        return true;
    }

    private static bool FilterDate(FileSystemItem item, DateTime? dateFrom, DateTime? dateTo)
    {
        if (!dateFrom.HasValue && !dateTo.HasValue)
        {
            return true;
        }

        var target = item.LastModified.Date;
        if (dateFrom.HasValue && target < dateFrom.Value.Date)
        {
            return false;
        }

        if (dateTo.HasValue && target > dateTo.Value.Date)
        {
            return false;
        }

        return true;
    }

    private static IReadOnlyList<string> ParsePatternList(string? source, string fallback = "")
    {
        var raw = string.IsNullOrWhiteSpace(source) ? fallback : source;
        return raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeFileMaskToken)
            .ToArray();
    }

    private static bool MatchesFileMasks(FileSystemItem item, IReadOnlyList<string> masks)
    {
        if (masks.Count == 0)
        {
            return true;
        }

        if (item.IsDirectory)
        {
            return masks.Any(mask =>
                string.Equals(mask, "*", StringComparison.Ordinal) ||
                string.Equals(mask, "*.*", StringComparison.Ordinal));
        }

        return masks.Any(mask => WildcardMatch(item.Name, mask));
    }

    private static bool MatchesAnyPattern(string value, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return false;
        }

        return patterns.Any(pattern => WildcardMatch(value, pattern));
    }

    private static bool WildcardMatch(string input, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
        {
            return true;
        }

        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase);
    }

    private static string NormalizeFileMaskToken(string token)
    {
        var value = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return "*";
        }

        if (value.Contains('*') || value.Contains('?'))
        {
            return value;
        }

        if (value.StartsWith('.'))
        {
            return $"*{value}";
        }

        return value.Contains('.')
            ? value
            : $"*.{value}";
    }

    private static bool FilterTextContent(FileSystemItem item, FindFilesOptions options, CancellationToken cancellationToken)
    {
        var query = (options.TextQuery ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(query) || item.IsDirectory)
        {
            return true;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 10 * 1024 * 1024)
            {
                return false;
            }

            using var reader = new StreamReader(stream, ResolveEncoding(options.EncodingName), detectEncodingFromByteOrderMarks: true);
            var content = reader.ReadToEnd();
            if (options.UseRegex)
            {
                var regexOptions = options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                return Regex.IsMatch(content, query, regexOptions);
            }

            return content.Contains(query, options.CaseSensitive ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static Encoding ResolveEncoding(string? encodingName)
    {
        if (string.IsNullOrWhiteSpace(encodingName) || string.Equals(encodingName, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.Default;
        }

        if (string.Equals(encodingName, "ANSI", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.Default;
        }

        if (string.Equals(encodingName, "OEM", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.GetEncoding(Console.OutputEncoding.CodePage);
        }

        try
        {
            return Encoding.GetEncoding(encodingName);
        }
        catch
        {
            return Encoding.Default;
        }
    }

    private static string ToReadableSize(long size)
    {
        if (size <= 0) return "0 B";
        string[] suffix = ["B", "KB", "MB", "GB", "TB"];
        var idx = 0;
        double value = size;
        while (value >= 1024 && idx < suffix.Length - 1) { value /= 1024; idx++; }
        return $"{value:0.##} {suffix[idx]}";
    }

    private PanelViewModel GetActivePanel() => IsLeftPanelActive ? LeftPanel : RightPanel;
    private PanelViewModel GetPassivePanel() => IsLeftPanelActive ? RightPanel : LeftPanel;

    private PanelViewModel GetCommandTargetPanel()
    {
        if (IsFourPanelMode)
        {
            var activeSlot = _fourPanels.FirstOrDefault(slot => slot.IsActive) ?? _fourPanels.FirstOrDefault();
            if (activeSlot is not null)
            {
                return activeSlot.Panel;
            }
        }

        return GetActivePanel();
    }

    private IReadOnlyList<FileSystemItem> ApplyPinnedPriority(IReadOnlyList<FileSystemItem> items)
    {
        if (items.Count == 0)
        {
            return items;
        }

        return items
            .Select((item, index) => new
            {
                Index = index,
                Item = WithPinnedState(item)
            })
            .OrderByDescending(entry => entry.Item.IsParentDirectory)
            .ThenByDescending(entry => entry.Item.IsDirectory)
            .ThenByDescending(entry => entry.Item.IsPinned)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToArray();
    }

    private FileSystemItem WithPinnedState(FileSystemItem item)
    {
        if (item.IsParentDirectory || string.IsNullOrWhiteSpace(item.FullPath))
        {
            return item;
        }

        var isPinned = item.IsDirectory
            ? _settings.PinnedFolders.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase)
            : _settings.PinnedFiles.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase);
        var isFavorite = item.IsDirectory
            ? _settings.FavoriteFolders.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase)
            : _settings.FavoriteFiles.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase);
        var memo = GetItemMemoText(item.FullPath);
        if (item.IsPinned == isPinned &&
            item.IsFavorite == isFavorite &&
            string.Equals(item.Memo, memo, StringComparison.Ordinal))
        {
            return item;
        }

        return new FileSystemItem
        {
            Name = item.Name,
            Extension = item.Extension,
            FullPath = item.FullPath,
            IsParentDirectory = item.IsParentDirectory,
            IsDirectory = item.IsDirectory,
            IsPinned = isPinned,
            IsFavorite = isFavorite,
            Memo = memo,
            SizeBytes = item.SizeBytes,
            SizeDisplay = item.SizeDisplay,
            LastModified = item.LastModified,
            TypeDisplay = item.TypeDisplay
        };
    }

    private void SetClipboardItems(IReadOnlyList<FileSystemItem> selectedItems, bool cutMode)
    {
        _clipboardItems.Clear();
        foreach (var item in selectedItems.Where(item => !item.IsParentDirectory))
        {
            _clipboardItems.Add(new ClipboardTransferItem(item.FullPath, item.IsDirectory));
        }

        _clipboardCutMode = _clipboardItems.Count > 0 && cutMode;

        try
        {
            if (_clipboardItems.Count == 0)
            {
                return;
            }

            var paths = _clipboardItems
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 0)
            {
                return;
            }

            var joined = string.Join(Environment.NewLine, paths);
            var dataObject = new DataObject();
            dataObject.SetData(DataFormats.UnicodeText, joined);
            dataObject.SetData(DataFormats.Text, joined);

            var fileDropList = new StringCollection();
            foreach (var path in paths)
            {
                fileDropList.Add(path);
            }

            dataObject.SetFileDropList(fileDropList);
            Clipboard.SetDataObject(dataObject, true);
        }
        catch
        {
            // Ignore external clipboard contention and keep internal clipboard working.
        }
    }

    private static FileSystemItem? CreateTransferItem(ClipboardTransferItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return null;
        }

        var name = item.IsDirectory
            ? Path.GetFileName(item.Path.TrimEnd('\\'))
            : Path.GetFileName(item.Path);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new FileSystemItem
        {
            Name = name,
            Extension = item.IsDirectory ? string.Empty : Path.GetExtension(name),
            FullPath = item.Path,
            IsDirectory = item.IsDirectory
        };
    }

    private static bool IsImageFile(string path)
    {
        var extension = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(extension) && ImageExtensions.Contains(extension);
    }

    private static WeakReference<ImageViewerWindow>? _imageViewerWindowRef;

    private static bool TryShowImageViewer(string imagePath)
    {
        try
        {
            var app = Application.Current;
            if (app is null)
            {
                return false;
            }

            app.Dispatcher.Invoke(() =>
            {
                var owner = app.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) ?? app.MainWindow;

                if (_imageViewerWindowRef is not null &&
                    _imageViewerWindowRef.TryGetTarget(out var existingViewer) &&
                    existingViewer.IsLoaded)
                {
                    existingViewer.ShowImage(imagePath);
                    if (existingViewer.WindowState == WindowState.Minimized)
                    {
                        existingViewer.WindowState = WindowState.Normal;
                    }

                    existingViewer.Show();
                    existingViewer.Activate();
                    return;
                }

                var viewer = new ImageViewerWindow(imagePath)
                {
                    Owner = owner
                };
                _imageViewerWindowRef = new WeakReference<ImageViewerWindow>(viewer);
                viewer.Closed += (_, _) =>
                {
                    if (_imageViewerWindowRef is not null &&
                        _imageViewerWindowRef.TryGetTarget(out var trackedViewer) &&
                        ReferenceEquals(trackedViewer, viewer))
                    {
                        _imageViewerWindowRef = null;
                    }
                };

                viewer.Show();
                viewer.Activate();
            });

            return true;
        }
        catch (Exception ex)
        {
            LiveTrace.Write($"TryShowImageViewer failed: {ex}");
            return false;
        }
    }

    public void ReplaceSearchResults(IReadOnlyList<FileSystemItem> items)
    {
        ResetCollection(SearchResults, items);
        StatusText = $"검색 완료: {SearchResults.Count}개";
    }

    private void ResetCollection<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items) collection.Add(item);
    }

    private void RunCommandSafely(Func<Task> action)
    {
        _ = ExecuteCommandAsync(action);
    }

    private async Task ExecuteCommandAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch
        {
            StatusText = "An error occurred while processing";
        }
    }

    private void Log(string message)
    {
        _ = message;
    }

    private sealed record ClipboardTransferItem(string Path, bool IsDirectory);
}



