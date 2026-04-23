using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.ComponentModel;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualBasic;
using WorkFileExplorer.App.Dialogs;
using WorkFileExplorer.App.Helpers;
using WorkFileExplorer.App.Models;
using WorkFileExplorer.App.ViewModels;

namespace WorkFileExplorer.App;

public partial class MainWindow : Window
{
    private Point _tabDragStart;
    private Point _panelItemDragStart;
    private PanelViewModel? _panelDragSourcePanel;
    private bool _isPanelItemDragInProgress;
    private TabItem? _draggedTabItem;
    private TabItem? _dragHoverTabItem;
    private bool _ignorePathHistorySelectionChanged;
    private bool _ignoreDriveSelectionChanged;
    private bool _allowInlineNameEdit;
    private bool _isTileInlineRenameCommitting;
    private string? _inlineRenameSourcePath;
    private string? _inlineRenameOriginalName;
    private DateTime _lastTabInteractionUtc = DateTime.MinValue;
    private bool _windowLoaded;
    private readonly Dictionary<FourPanelSlotViewModel, List<DataGrid>> _fourPanelGrids = new();
    private readonly Dictionary<FourPanelSlotViewModel, List<ListBox>> _fourPanelTileLists = new();
    private readonly Dictionary<ListBox, double> _adaptiveTileSizes = new();
    private Point _favoriteToolbarDragStart;
    private QuickAccessItem? _favoriteToolbarDragItem;
    private readonly ObservableCollection<FavoriteFolderTreeNode> _favoriteFlyoutNodes = [];
    private readonly ObservableCollection<FavoriteEntryTreeNode> _favoriteFileFlyoutNodes = [];
    private readonly Dictionary<string, FavoriteFolderTreeNode> _favoriteFolderRootCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DataGridColumn, string> _panelGridHeaderLabels = new();
    private Button? _favoriteFlyoutSourceButton;
    private Button? _favoriteFilesFlyoutSourceButton;
    private bool _isFavoriteFilesOverlayOpen;
    private bool _isFavoriteFolderOverlayOpen;
    private int _activeFourPanelIndex;
    private PanelItemDragPayload? _activePanelDragPayload;
    private MainWindowViewModel? _subscribedVm;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => _windowLoaded = true;
        Loaded += OnMainWindowLoaded;
        DataContextChanged += OnMainWindowDataContextChanged;
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        AttachVmPropertyEvents();
    }

    private void OnMainWindowDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachVmPropertyEvents();
        AttachVmPropertyEvents();
    }

    private void AttachVmPropertyEvents()
    {
        if (Vm is null || ReferenceEquals(_subscribedVm, Vm))
        {
            return;
        }

        _subscribedVm = Vm;
        _subscribedVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void DetachVmPropertyEvents()
    {
        if (_subscribedVm is null)
        {
            return;
        }

        _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
        _subscribedVm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Vm is null || !IsActive)
        {
            return;
        }

        var recentTabInteraction = DateTime.UtcNow - _lastTabInteractionUtc <= TimeSpan.FromMilliseconds(220);

        if (string.Equals(e.PropertyName, nameof(MainWindowViewModel.LeftCurrentPath), StringComparison.Ordinal))
        {
            if (recentTabInteraction)
            {
                return;
            }

            // When folder changes, return keyboard focus to the file list so Up/Down works immediately.
            FocusPanelAfterPathNavigation(left: true);
            return;
        }

        if (string.Equals(e.PropertyName, nameof(MainWindowViewModel.RightCurrentPath), StringComparison.Ordinal))
        {
            if (recentTabInteraction)
            {
                return;
            }

            // When folder changes, return keyboard focus to the file list so Up/Down works immediately.
            FocusPanelAfterPathNavigation(left: false);
        }
    }

    private async void OnLeftPanelDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var originalSource = e.OriginalSource as DependencyObject;
        if (IsDataGridHeaderInteraction(originalSource) ||
            !IsPanelItemDoubleClickSource(sender, originalSource))
        {
            return;
        }

        if (Vm is not null)
        {
            await Vm.OpenItemFromPanelAsync(leftPanel: true);
        }
    }

    private async void OnRightPanelDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var originalSource = e.OriginalSource as DependencyObject;
        if (IsDataGridHeaderInteraction(originalSource) ||
            !IsPanelItemDoubleClickSource(sender, originalSource))
        {
            return;
        }

        if (Vm is not null)
        {
            await Vm.OpenItemFromPanelAsync(leftPanel: false);
        }
    }

    private void OnLeftPanelFocus(object sender, RoutedEventArgs e)
    {
        Vm?.SetActivePanelCommand.Execute("Left");
    }

    private void OnRightPanelFocus(object sender, RoutedEventArgs e)
    {
        Vm?.SetActivePanelCommand.Execute("Right");
    }

    private void OnLeftPanelHostMouseDown(object sender, MouseButtonEventArgs e)
    {
        Vm?.SetActivePanelCommand.Execute("Left");
        if (IsInteractiveFilterControl(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (Vm?.IsTileViewEnabledForPanel(leftPanel: true) == true)
        {
            LeftPanelTilesList.Focus();
        }
        else
        {
            LeftPanelGrid.Focus();
        }
    }

    private void OnRightPanelHostMouseDown(object sender, MouseButtonEventArgs e)
    {
        Vm?.SetActivePanelCommand.Execute("Right");
        if (IsInteractiveFilterControl(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (Vm?.IsTileViewEnabledForPanel(leftPanel: false) == true)
        {
            RightPanelTilesList.Focus();
        }
        else
        {
            RightPanelGrid.Focus();
        }
    }

    private void OnLeftPathComboBoxGotFocus(object sender, RoutedEventArgs e)
    {
        Vm?.SetActivePanelCommand.Execute("Left");
    }

    private void OnRightPathComboBoxGotFocus(object sender, RoutedEventArgs e)
    {
        Vm?.SetActivePanelCommand.Execute("Right");
    }

    private void OnDetailsViewClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        if (Vm.IsFourPanelMode)
        {
            var slot = GetActiveFourPanelSlot();
            if (slot?.SelectedTab is not null)
            {
                slot.SelectedTab.ViewMode = PanelViewMode.Details;
            }
            return;
        }

        Vm.SetViewModeForActivePanel(PanelViewMode.Details);
    }

    private void OnTilesViewClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        if (Vm.IsFourPanelMode)
        {
            var slot = GetActiveFourPanelSlot();
            if (slot?.SelectedTab is not null)
            {
                slot.SelectedTab.ViewMode = PanelViewMode.Tiles;
            }
            return;
        }

        Vm.SetViewModeForActivePanel(PanelViewMode.Tiles);
        Dispatcher.BeginInvoke(RefreshAdaptiveTileLayouts, DispatcherPriority.Background);
    }

    private void OnCompactListViewClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        if (Vm.IsFourPanelMode)
        {
            var slot = GetActiveFourPanelSlot();
            if (slot?.SelectedTab is not null)
            {
                slot.SelectedTab.ViewMode = PanelViewMode.CompactList;
            }

            return;
        }

        Vm.SetViewModeForActivePanel(PanelViewMode.CompactList);
        Dispatcher.BeginInvoke(RefreshAdaptiveTileLayouts, DispatcherPriority.Background);
    }

    private async void OnTwoPanelModeClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.SetPanelCountAsync(2);
    }

    private async void OnFourPanelModeClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.SetPanelCountAsync(4);
    }

    private async void OnPanelLayoutToggleClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.TogglePanelLayoutAsync();
    }

    private void OnFourPanelGridLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DataGrid grid && grid.DataContext is FourPanelSlotViewModel slot)
        {
            if (!_fourPanelGrids.TryGetValue(slot, out var grids))
            {
                grids = new List<DataGrid>();
                _fourPanelGrids[slot] = grids;
            }

            if (!grids.Contains(grid))
            {
                grids.Add(grid);
            }
        }
    }

    private void OnFourPanelTileListLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBox list && list.DataContext is FourPanelSlotViewModel slot)
        {
            if (!_fourPanelTileLists.TryGetValue(slot, out var lists))
            {
                lists = new List<ListBox>();
                _fourPanelTileLists[slot] = lists;
            }

            if (!lists.Contains(list))
            {
                lists.Add(list);
            }

            UpdateAdaptiveTileSize(list);
        }
    }

    private void OnAdaptiveTileListLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBox list)
        {
            UpdateAdaptiveTileSize(list);
        }
    }

    private void OnAdaptiveTileListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is ListBox list)
        {
            UpdateAdaptiveTileSize(list);
        }
    }

    private void RefreshAdaptiveTileLayouts()
    {
        UpdateAdaptiveTileSize(LeftPanelTilesList);
        UpdateAdaptiveTileSize(RightPanelTilesList);

        foreach (var lists in _fourPanelTileLists.Values)
        {
            foreach (var list in lists)
            {
                UpdateAdaptiveTileSize(list);
            }
        }
    }

    private void UpdateAdaptiveTileSize(ListBox list)
    {
        _adaptiveTileSizes.Remove(list);

        var wrapPanel = FindDescendant<WrapPanel>(list);
        if (wrapPanel is null)
        {
            return;
        }

        if (!IsCompactListMode(list))
        {
            wrapPanel.ClearValue(WrapPanel.ItemWidthProperty);
            return;
        }

        const double minItemWidth = 220;
        const double preferredItemWidth = 300;
        const double maxItemWidth = 420;
        const double columnGap = 12; // matches CompactListItemStyle horizontal margin
        const double chromeWidth = 28; // list padding + scrollbar reserve

        var usable = Math.Max(minItemWidth, list.ActualWidth - chromeWidth);
        var columns = Math.Max(1, (int)Math.Floor((usable + columnGap) / (preferredItemWidth + columnGap)));
        var itemWidth = Math.Floor((usable - columnGap * (columns - 1)) / columns);

        while (columns > 1 && itemWidth < minItemWidth)
        {
            columns--;
            itemWidth = Math.Floor((usable - columnGap * (columns - 1)) / columns);
        }

        itemWidth = Math.Clamp(itemWidth, minItemWidth, maxItemWidth);
        wrapPanel.ItemWidth = itemWidth;
        _adaptiveTileSizes[list] = itemWidth;
    }

    private bool IsCompactListMode(ListBox list)
    {
        if (Vm is null)
        {
            return false;
        }

        if (ReferenceEquals(list, LeftPanelTilesList))
        {
            return Vm.LeftPanelIsCompactListViewEnabled;
        }

        if (ReferenceEquals(list, RightPanelTilesList))
        {
            return Vm.RightPanelIsCompactListViewEnabled;
        }

        if (list.DataContext is FourPanelSlotViewModel slot)
        {
            return slot.SelectedTab?.IsCompactListViewEnabled == true;
        }

        return false;
    }

    private static double ComputeAdaptiveTileSize(double listWidth)
    {
        const double minTile = 108;
        const double preferredTile = 124;
        const double maxTile = 148;
        const double tileGap = 4; // from ListBoxItem margin left+right (2+2)
        const double chromeWidth = 30; // list padding + scrollbar reserve

        var usable = Math.Max(minTile, listWidth - chromeWidth);
        var maxColumns = Math.Max(1, (int)Math.Floor((usable + tileGap) / (minTile + tileGap)));

        var bestSize = minTile;
        var bestCols = 1;
        var bestScore = double.MaxValue;

        for (var cols = 1; cols <= maxColumns; cols++)
        {
            var size = Math.Floor((usable - tileGap * (cols - 1)) / cols);
            if (size < minTile || size > maxTile)
            {
                continue;
            }

            var score = Math.Abs(size - preferredTile);
            if (score < bestScore - 0.1 ||
                (Math.Abs(score - bestScore) <= 0.1 && cols > bestCols))
            {
                bestScore = score;
                bestSize = size;
                bestCols = cols;
            }
        }

        if (bestScore != double.MaxValue)
        {
            return bestSize;
        }

        var fallbackCols = Math.Max(1, (int)Math.Floor((usable + tileGap) / (preferredTile + tileGap)));
        var fallbackSize = Math.Floor((usable - tileGap * (fallbackCols - 1)) / fallbackCols);
        return Math.Clamp(fallbackSize, minTile, maxTile);
    }

    private void OnFourPanelPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _panelItemDragStart = e.GetPosition(null);
        _panelDragSourcePanel = TryResolvePanelFromSource(sender as DependencyObject);

        if (sender is FrameworkElement element && TryGetFourPanel(element) is FourPanelSlotViewModel slot && Vm is not null)
        {
            var index = Vm.FourPanels.IndexOf(slot);
            if (index >= 0)
            {
                _activeFourPanelIndex = index;
                Vm.SetActiveFourPanel(index);
            }
        }
    }

    private void OnFourPanelFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && TryGetFourPanel(element) is FourPanelSlotViewModel slot && Vm is not null)
        {
            var index = Vm.FourPanels.IndexOf(slot);
            if (index >= 0)
            {
                _activeFourPanelIndex = index;
                Vm.SetActiveFourPanel(index);
                Vm.StatusText = $"패널 {index + 1} 활성";
            }
        }
    }

    private void OnFourPanelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is null || sender is not FrameworkElement element || TryGetFourPanel(element) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        if (sender is DataGrid grid)
        {
            var selected = grid.SelectedItems.Cast<FileSystemItem>().ToArray();
            var index = Vm.FourPanels.IndexOf(slot) + 1;
            _activeFourPanelIndex = Math.Max(0, index - 1);
            Vm.SetActiveFourPanel(_activeFourPanelIndex);
            Vm.StatusText = $"패널 {index}: 선택 {selected.Length}";
            return;
        }

        if (sender is ListBox list)
        {
            var selected = list.SelectedItems.Cast<FileSystemItem>().ToArray();
            var index = Vm.FourPanels.IndexOf(slot) + 1;
            _activeFourPanelIndex = Math.Max(0, index - 1);
            Vm.SetActiveFourPanel(_activeFourPanelIndex);
            Vm.StatusText = $"패널 {index}: 선택 {selected.Length}";
        }
    }

    private async void OnFourPanelNavigateClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not FrameworkElement element || TryGetFourPanel(element) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        await Vm.NavigateFourPanelToPathAsync(slot.Panel, slot.Panel.CurrentPath);
    }

    private async void OnFourPanelParentClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not FrameworkElement element || TryGetFourPanel(element) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        await Vm.GoParentInFourPanelAsync(slot.Panel);
    }

    private async void OnFourPanelBackClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not FrameworkElement element || TryGetFourPanel(element) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        await Vm.NavigateFourPanelBackAsync(slot);
    }

    private async void OnFourPanelForwardClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not FrameworkElement element || TryGetFourPanel(element) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        await Vm.NavigateFourPanelForwardAsync(slot);
    }

    private async void OnFourPanelHomeClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not FrameworkElement element || TryGetFourPanel(element) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        await Vm.NavigateFourPanelHomeAsync(slot.Panel);
    }

    private async void OnFourPanelRefreshClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not FrameworkElement element || TryGetFourPanel(element) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        await Vm.RefreshFourPanelAsync(slot.Panel);
    }

    private async void OnFourPanelDriveClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not Button button)
        {
            return;
        }

        var slot = button.Tag as FourPanelSlotViewModel ?? FindFourPanelSlot(button);
        if (slot is null)
        {
            return;
        }

        var drive = button.Content?.ToString();
        await Vm.NavigateFourPanelToDriveAsync(slot.Panel, drive);
    }

    private async void OnFourPanelFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not FrameworkElement element || TryGetFourPanel(element) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        await Vm.ToggleFavoriteFolderPathAsync(slot.Panel.CurrentPath);
    }

    private async void OnFourPanelPinToggleClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not FrameworkElement element || TryGetFourPanel(element) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        var selected = GetFourPanelSelectedItems(slot.Panel);
        await Vm.TogglePinForItemsAsync(selected, slot.Panel);
    }

    private async void OnFourPanelFavoriteToggleClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not FrameworkElement element || TryGetFourPanel(element) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        var selected = GetFourPanelSelectedItems(slot.Panel);
        await Vm.ToggleFavoriteForItemsAsync(selected, slot.Panel);
    }

    private async void OnFourPanelDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var originalSource = e.OriginalSource as DependencyObject;
        if (IsDataGridHeaderInteraction(originalSource) ||
            !IsPanelItemDoubleClickSource(sender, originalSource))
        {
            return;
        }

        if (Vm is null || sender is not FrameworkElement element || TryGetFourPanel(element) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        await Vm.OpenItemFromFourPanelAsync(slot.Panel);
    }

    private async void OnFourPanelAddTabClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not FrameworkElement element || TryGetFourPanel(element) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        await Vm.AddFourPanelTabAsync(slot);
    }

    private async void OnFourPanelCloseTabClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not FrameworkElement element || TryGetFourPanel(element) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        await Vm.CloseFourPanelTabAsync(slot);
    }

    private void OnFourPanelSlotDragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            TryGetFourPanel(element) is not FourPanelSlotViewModel slot ||
            TryGetPanelItemDragPayload(e.Data) is not PanelItemDragPayload payload)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (ReferenceEquals(slot.Panel, payload.SourcePanel))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift
            ? DragDropEffects.Move
            : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnFourPanelSlotDrop(object sender, DragEventArgs e)
    {
        if (Vm is null ||
            sender is not FrameworkElement element ||
            TryGetFourPanel(element) is not FourPanelSlotViewModel slot ||
            TryGetPanelItemDragPayload(e.Data) is not PanelItemDragPayload payload)
        {
            return;
        }

        if (ReferenceEquals(slot.Panel, payload.SourcePanel) || payload.Items.Count == 0)
        {
            e.Handled = true;
            return;
        }

        var move = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var targetDirectory = ResolveDropTargetDirectory(e.OriginalSource as DependencyObject, slot.Panel);
        try
        {
            await Vm.CopyOrMoveBetweenPanelsAsync(payload.SourcePanel, slot.Panel, payload.Items, move, targetDirectory);
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"드래그 {(move ? "이동" : "복사")} 실패: {ex.Message}";
        }

        e.Handled = true;
    }

    private void OnFourPanelSlotPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || TryGetFourPanel(element) is not FourPanelSlotViewModel slot || Vm is null)
        {
            return;
        }

        var index = Vm.FourPanels.IndexOf(slot);
        if (index >= 0)
        {
            _activeFourPanelIndex = index;
            Vm.SetActiveFourPanel(index);
        }
    }

    private void OnFourPanelTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not FrameworkElement element || TryGetFourPanel(element) is not FourPanelSlotViewModel slot || Vm is null)
        {
            return;
        }

        var index = Vm.FourPanels.IndexOf(slot);
        if (index >= 0)
        {
            _activeFourPanelIndex = index;
            Vm.SetActiveFourPanel(index);
            Vm.RefreshFreeSpaceIndicators();
            Dispatcher.BeginInvoke(RefreshAdaptiveTileLayouts, DispatcherPriority.Background);
        }
    }

    private void OnFourPanelTabPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || sender is not TabControl tabControl)
        {
            return;
        }

        if (FindAncestor<TabItem>(e.OriginalSource as DependencyObject)?.DataContext is not PanelTabViewModel clickedTab)
        {
            return;
        }

        tabControl.SelectedItem = clickedTab;
        if (TryGetFourPanel(tabControl) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        slot.SelectedTab = clickedTab;
        var index = Vm.FourPanels.IndexOf(slot);
        if (index >= 0)
        {
            _activeFourPanelIndex = index;
            Vm.SetActiveFourPanel(index);
        }
    }

    private async void OnFourPanelTabDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || TryGetFourPanelSlotFromContextSender(sender) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        await Vm.AddFourPanelTabAsync(slot, explicitSourcePath: slot.Panel.CurrentPath);
    }

    private async void OnFourPanelTabCloseCurrentClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || TryGetFourPanelSlotFromContextSender(sender) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        await Vm.CloseFourPanelTabAsync(slot);
    }

    private void OnFourPanelTabCloseToLeftClick(object sender, RoutedEventArgs e)
    {
        if (TryGetFourPanelSlotFromContextSender(sender) is not FourPanelSlotViewModel slot || slot.SelectedTab is null)
        {
            return;
        }

        var selectedIndex = slot.Tabs.IndexOf(slot.SelectedTab);
        for (var index = selectedIndex - 1; index >= 0; index--)
        {
            slot.Tabs.RemoveAt(index);
        }
    }

    private void OnFourPanelTabCloseToRightClick(object sender, RoutedEventArgs e)
    {
        if (TryGetFourPanelSlotFromContextSender(sender) is not FourPanelSlotViewModel slot || slot.SelectedTab is null)
        {
            return;
        }

        var selectedIndex = slot.Tabs.IndexOf(slot.SelectedTab);
        for (var index = slot.Tabs.Count - 1; index > selectedIndex; index--)
        {
            slot.Tabs.RemoveAt(index);
        }
    }

    private void OnFourPanelTabCloseOthersClick(object sender, RoutedEventArgs e)
    {
        if (TryGetFourPanelSlotFromContextSender(sender) is not FourPanelSlotViewModel slot || slot.SelectedTab is null)
        {
            return;
        }

        for (var index = slot.Tabs.Count - 1; index >= 0; index--)
        {
            if (!ReferenceEquals(slot.Tabs[index], slot.SelectedTab))
            {
                slot.Tabs.RemoveAt(index);
            }
        }
    }

    private async void OnFourPanelTabsMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || sender is not FrameworkElement element || TryGetFourPanel(element) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        // Double-click on tab closes current tab in this slot.
        if (FindAncestor<TabItem>(e.OriginalSource as DependencyObject) is not null)
        {
            await Vm.CloseFourPanelTabAsync(slot);
            e.Handled = true;
            return;
        }

        // Double-click on empty strip area creates a new tab.
        await Vm.AddFourPanelTabAsync(slot);
        e.Handled = true;
    }

    private FourPanelSlotViewModel? TryGetFourPanelSlotFromContextSender(object sender)
    {
        if (sender is FrameworkElement element)
        {
            return TryGetFourPanel(element);
        }

        if (sender is MenuItem menuItem &&
            menuItem.Parent is ContextMenu contextMenu &&
            contextMenu.PlacementTarget is FrameworkElement placementTarget)
        {
            return TryGetFourPanel(placementTarget);
        }

        return null;
    }

    private async Task<bool> HandleFourPanelHotkeysAsync(KeyEventArgs e)
    {
        if (Vm is null)
        {
            return false;
        }

        var panel = GetActiveFourPanel();
        if (panel is null)
        {
            return false;
        }

        var selected = GetFourPanelSelectedItems(panel);

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            Vm.CopySelectionToClipboard(selected);
            return true;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.X)
        {
            Vm.CutSelectionToClipboard(selected);
            return true;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            await Vm.PasteClipboardToPanelAsync(panel);
            return true;
        }

        if (e.Key == Key.Enter)
        {
            await Vm.OpenItemFromFourPanelAsync(panel);
            return true;
        }

        if (e.Key == Key.Back)
        {
            await Vm.GoParentInFourPanelAsync(panel);
            return true;
        }

        if (e.Key == Key.Delete)
        {
            if (selected.Count == 0)
            {
                return true;
            }

            var requireConfirm = Vm.ConfirmBeforeDelete;
            var message = $"선택한 {selected.Count}개 항목을 삭제하시겠습니까?";
            if (!requireConfirm || StyledDialogWindow.ShowConfirm(this, "삭제 확인", message))
            {
                await Vm.DeleteItemsFromPanelAsync(panel, selected);
                RestoreKeyboardFocusAfterDelete();
            }

            return true;
        }

        if (e.Key == Key.F2)
        {
            if (selected.Count != 1 || selected[0].IsParentDirectory)
            {
                return true;
            }

            var item = selected[0];
            var newName = Interaction.InputBox("새 이름을 입력하세요.", "이름 바꾸기", item.Name);
            if (!string.IsNullOrWhiteSpace(newName))
            {
                await Vm.RenameItemInPanelAsync(panel, item, newName);
            }

            return true;
        }

        if (e.Key == Key.F5 && GetFourPanelTransferTarget(panel) is PanelViewModel copyTarget)
        {
            await Vm.CopyOrMoveBetweenPanelsAsync(panel, copyTarget, selected, move: false);
            Vm.StatusText = $"복사: 패널 {_activeFourPanelIndex + 1} -> 패널 {GetFourPanelIndex(copyTarget) + 1}";
            return true;
        }

        if (e.Key == Key.F6 && GetFourPanelTransferTarget(panel) is PanelViewModel moveTarget)
        {
            await Vm.CopyOrMoveBetweenPanelsAsync(panel, moveTarget, selected, move: true);
            Vm.StatusText = $"이동: 패널 {_activeFourPanelIndex + 1} -> 패널 {GetFourPanelIndex(moveTarget) + 1}";
            return true;
        }

        if (e.Key == Key.F7)
        {
            var folderName = NewFolderDialog.ShowDialog(this, "New Folder");
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                await Vm.CreateNewFolderInPanelAsync(panel, folderName);
            }

            return true;
        }

        return false;
    }

    private void OnLeftPanelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionSummary(left: true);
        ShowMemoListSelectionMemo(left: true, e);
    }

    private void OnRightPanelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionSummary(left: false);
        ShowMemoListSelectionMemo(left: false, e);
    }

    private void UpdateSelectionSummary(bool left)
    {
        if (Vm is null)
        {
            return;
        }

        var selectedItems = GetPanelSelectedItems(left);
        Vm.UpdateSelectionSummary(left, selectedItems);
    }

    private void ShowMemoListSelectionMemo(bool left, SelectionChangedEventArgs e)
    {
        if (Vm is null || !Vm.IsMemoListPanel(left))
        {
            return;
        }

        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not FileSystemItem item)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Memo))
        {
            Vm.StatusText = $"메모 없음: {item.DisplayName}";
            return;
        }

        Vm.StatusText = $"메모: {item.Memo}";
    }

    private async void OnLeftPathHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignorePathHistorySelectionChanged || Vm is null || sender is not ComboBox combo || combo.SelectedItem is not string selectedPath)
        {
            return;
        }

        // Ignore programmatic selection churn from history refresh; only react to user intent.
        if (!combo.IsKeyboardFocusWithin && !combo.IsDropDownOpen)
        {
            return;
        }

        if (string.Equals(selectedPath, Vm.LeftPanel.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ignorePathHistorySelectionChanged = true;
        try
        {
            var previousPath = Vm.LeftPanel.CurrentPath;
            await Vm.NavigatePanelToPathAsync(leftPanel: true, selectedPath);
            if (!string.Equals(previousPath, Vm.LeftPanel.CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                FocusPanelAfterPathNavigation(left: true);
            }
        }
        finally
        {
            _ignorePathHistorySelectionChanged = false;
        }
    }

    private async void OnLeftPathHistoryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is null || sender is not ComboBox combo)
        {
            return;
        }

        var enteredPath = (combo.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(enteredPath) ||
            string.Equals(enteredPath, Vm.LeftPanel.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            e.Handled = true;
            return;
        }

        _ignorePathHistorySelectionChanged = true;
        try
        {
            var previousPath = Vm.LeftPanel.CurrentPath;
            await Vm.NavigatePanelToPathAsync(leftPanel: true, enteredPath);
            if (!string.Equals(previousPath, Vm.LeftPanel.CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                FocusPanelAfterPathNavigation(left: true);
            }
            else
            {
                combo.Text = Vm.LeftPanel.CurrentPath;
            }
        }
        finally
        {
            _ignorePathHistorySelectionChanged = false;
        }

        e.Handled = true;
    }

    private async void OnRightPathHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignorePathHistorySelectionChanged || Vm is null || sender is not ComboBox combo || combo.SelectedItem is not string selectedPath)
        {
            return;
        }

        // Ignore programmatic selection churn from history refresh; only react to user intent.
        if (!combo.IsKeyboardFocusWithin && !combo.IsDropDownOpen)
        {
            return;
        }

        if (string.Equals(selectedPath, Vm.RightPanel.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ignorePathHistorySelectionChanged = true;
        try
        {
            var previousPath = Vm.RightPanel.CurrentPath;
            await Vm.NavigatePanelToPathAsync(leftPanel: false, selectedPath);
            if (!string.Equals(previousPath, Vm.RightPanel.CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                FocusPanelAfterPathNavigation(left: false);
            }
        }
        finally
        {
            _ignorePathHistorySelectionChanged = false;
        }
    }

    private async void OnRightPathHistoryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is null || sender is not ComboBox combo)
        {
            return;
        }

        var enteredPath = (combo.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(enteredPath) ||
            string.Equals(enteredPath, Vm.RightPanel.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            e.Handled = true;
            return;
        }

        _ignorePathHistorySelectionChanged = true;
        try
        {
            var previousPath = Vm.RightPanel.CurrentPath;
            await Vm.NavigatePanelToPathAsync(leftPanel: false, enteredPath);
            if (!string.Equals(previousPath, Vm.RightPanel.CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                FocusPanelAfterPathNavigation(left: false);
            }
            else
            {
                combo.Text = Vm.RightPanel.CurrentPath;
            }
        }
        finally
        {
            _ignorePathHistorySelectionChanged = false;
        }

        e.Handled = true;
    }

    private async void OnFourPanelPathHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignorePathHistorySelectionChanged || Vm is null || sender is not ComboBox combo || combo.SelectedItem is not string selectedPath)
        {
            return;
        }

        if (TryGetFourPanel(combo) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        // Ignore programmatic selection churn from history refresh; only react to user intent.
        if (!combo.IsKeyboardFocusWithin && !combo.IsDropDownOpen)
        {
            return;
        }

        if (string.Equals(selectedPath, slot.Panel.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ignorePathHistorySelectionChanged = true;
        try
        {
            var previousPath = slot.Panel.CurrentPath;
            await Vm.NavigateFourPanelToPathAsync(slot.Panel, selectedPath);
            if (!string.Equals(previousPath, slot.Panel.CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                FocusFourPanelAfterPathNavigation(slot);
            }
        }
        finally
        {
            _ignorePathHistorySelectionChanged = false;
        }
    }

    private async void OnFourPanelPathHistoryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is null || sender is not ComboBox combo)
        {
            return;
        }

        if (TryGetFourPanel(combo) is not FourPanelSlotViewModel slot)
        {
            return;
        }

        var enteredPath = (combo.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(enteredPath) ||
            string.Equals(enteredPath, slot.Panel.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            e.Handled = true;
            return;
        }

        _ignorePathHistorySelectionChanged = true;
        try
        {
            var previousPath = slot.Panel.CurrentPath;
            await Vm.NavigateFourPanelToPathAsync(slot.Panel, enteredPath);
            if (!string.Equals(previousPath, slot.Panel.CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                FocusFourPanelAfterPathNavigation(slot);
            }
            else
            {
                combo.Text = slot.Panel.CurrentPath;
            }
        }
        finally
        {
            _ignorePathHistorySelectionChanged = false;
        }

        e.Handled = true;
    }

    private async void OnLeftDriveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_windowLoaded || _ignoreDriveSelectionChanged || Vm is null || sender is not Selector selector || selector.SelectedItem is not string drive)
        {
            return;
        }

        _ignoreDriveSelectionChanged = true;
        try
        {
            await Vm.NavigateToDriveAsync(leftPanel: true, drive);
        }
        finally
        {
            _ignoreDriveSelectionChanged = false;
        }
    }

    private async void OnRightDriveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_windowLoaded || _ignoreDriveSelectionChanged || Vm is null || sender is not Selector selector || selector.SelectedItem is not string drive)
        {
            return;
        }

        _ignoreDriveSelectionChanged = true;
        try
        {
            await Vm.NavigateToDriveAsync(leftPanel: false, drive);
        }
        finally
        {
            _ignoreDriveSelectionChanged = false;
        }
    }

    private async void OnLeftHomeClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        try
        {
            await Vm.NavigatePanelToPathAsync(leftPanel: true, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch
        {
        }
    }

    private async void OnRightHomeClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        try
        {
            await Vm.NavigatePanelToPathAsync(leftPanel: false, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch
        {
        }
    }

    private async void OnLeftParentClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        try
        {
            await Vm.GoUpFromPanelAsync(leftPanel: true);
        }
        catch
        {
        }
    }

    private async void OnRightParentClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        try
        {
            await Vm.GoUpFromPanelAsync(leftPanel: false);
        }
        catch
        {
        }
    }

    private async void OnAddLeftFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.ToggleCurrentPanelPathFavoriteAsync(leftPanel: true);
    }

    private async void OnAddRightFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.ToggleCurrentPanelPathFavoriteAsync(leftPanel: false);
    }

    private async void OnLeftPinToggleClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        Vm.SetActivePanelCommand.Execute("Left");
        await Vm.TogglePinForActiveSelectionAsync(GetActiveSelectedItems());
    }

    private async void OnRightPinToggleClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        Vm.SetActivePanelCommand.Execute("Right");
        await Vm.TogglePinForActiveSelectionAsync(GetActiveSelectedItems());
    }

    private async void OnLeftFavoriteToggleClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        Vm.SetActivePanelCommand.Execute("Left");
        await Vm.ToggleFavoriteForActiveSelectionAsync(GetActiveSelectedItems());
    }

    private async void OnRightFavoriteToggleClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        Vm.SetActivePanelCommand.Execute("Right");
        await Vm.ToggleFavoriteForActiveSelectionAsync(GetActiveSelectedItems());
    }

    private async void OnQuickAccessDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || sender is not ListView listView || listView.SelectedItem is not QuickAccessItem item)
        {
            return;
        }

        await Vm.NavigateQuickAccessAsync(item);
    }

    private void OnFavoriteToolbarFilesClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not Button button)
        {
            return;
        }

        if (_isFavoriteFilesOverlayOpen)
        {
            HideFavoriteFilesFlyout();
            return;
        }

        ShowFavoriteFilesFlyout(button);
        e.Handled = true;
    }

    private void ShowFavoriteFilesFlyout(Button sourceButton)
    {
        if (Vm is null)
        {
            return;
        }

        try
        {
            var sw = Stopwatch.StartNew();
            // Keep favorite flyouts mutually exclusive.
            HideFavoriteFolderFlyout();
            var categoryGroups = Vm.GetFavoriteFileCategoriesWithFiles();
            var categories = categoryGroups.Count;
            var files = categoryGroups.Values.Sum(list => list.Count);
            LiveTrace.Write($"FavoriteFilesFlyout open requested categories={categories} files={files}");
            LiveTrace.WriteProcessSnapshot("FavoriteFilesFlyout open requested");
            _favoriteFileFlyoutNodes.Clear();
            BuildFavoriteFilesTreeNodes(_favoriteFileFlyoutNodes, categoryGroups);
            if (_favoriteFileFlyoutNodes.Count == 0)
            {
                LiveTrace.Write($"FavoriteFilesFlyout open aborted (empty) in {sw.ElapsedMilliseconds}ms");
                return;
            }

            _favoriteFilesFlyoutSourceButton = sourceButton;
            FavoriteFilesFlyoutTree.ItemsSource = _favoriteFileFlyoutNodes;
            PositionFavoriteFilesOverlay(sourceButton);
            FavoriteFilesOverlayPanel.Visibility = Visibility.Visible;
            _isFavoriteFilesOverlayOpen = true;
            LiveTrace.Write($"FavoriteFilesFlyout opened roots={_favoriteFileFlyoutNodes.Count} nodes={CountFavoriteEntryNodes(_favoriteFileFlyoutNodes)} in {sw.ElapsedMilliseconds}ms");
            LiveTrace.WriteProcessSnapshot("FavoriteFilesFlyout opened");
        }
        catch (Exception ex)
        {
            LiveTrace.Write($"ShowFavoriteFilesFlyout failed: {ex}");
            Vm.StatusText = "파일 즐겨찾기 목록을 여는 중 오류가 발생했습니다.";
            HideFavoriteFilesFlyout();
        }
    }

    private void HideFavoriteFilesFlyout()
    {
        if (!_isFavoriteFilesOverlayOpen &&
            _favoriteFileFlyoutNodes.Count == 0 &&
            _favoriteFilesFlyoutSourceButton is null)
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        var wasOpen = _isFavoriteFilesOverlayOpen;
        var roots = _favoriteFileFlyoutNodes.Count;
        var nodes = CountFavoriteEntryNodes(_favoriteFileFlyoutNodes);
        FavoriteFilesOverlayPanel.Visibility = Visibility.Collapsed;
        _isFavoriteFilesOverlayOpen = false;
        FavoriteFilesFlyoutTree.ItemsSource = null;
        _favoriteFileFlyoutNodes.Clear();
        _favoriteFilesFlyoutSourceButton = null;
        LiveTrace.Write($"FavoriteFilesFlyout hidden wasOpen={wasOpen} roots={roots} nodes={nodes} in {sw.ElapsedMilliseconds}ms");
        LiveTrace.WriteProcessSnapshot("FavoriteFilesFlyout hidden");
    }

    private void PositionFavoriteFilesOverlay(Button sourceButton)
    {
        if (FavoriteToolbarGrid is null || FavoriteFilesOverlayPanel is null)
        {
            return;
        }

        UpdateLayout();
        FavoriteFilesOverlayPanel.UpdateLayout();
        var origin = sourceButton.TranslatePoint(new Point(0, sourceButton.ActualHeight + 4), this);
        var overlayWidth = FavoriteFilesOverlayPanel.Width > 0
            ? FavoriteFilesOverlayPanel.Width
            : FavoriteFilesOverlayPanel.ActualWidth;
        if (overlayWidth <= 0)
        {
            overlayWidth = 360;
        }

        var maxX = Math.Max(0, ActualWidth - overlayWidth - 12);
        var x = Math.Clamp(origin.X, 0, maxX);
        var y = Math.Max(0d, origin.Y - 4);
        FavoriteFilesOverlayPanel.Margin = new Thickness(x, y, 0, 0);
    }

    private void PositionFavoriteFolderOverlay(Button sourceButton)
    {
        if (FavoriteToolbarGrid is null || FavoriteFolderOverlayPanel is null)
        {
            return;
        }

        UpdateLayout();
        FavoriteFolderOverlayPanel.UpdateLayout();
        var origin = sourceButton.TranslatePoint(new Point(0, sourceButton.ActualHeight + 4), this);
        var overlayWidth = FavoriteFolderOverlayPanel.Width > 0
            ? FavoriteFolderOverlayPanel.Width
            : FavoriteFolderOverlayPanel.ActualWidth;
        if (overlayWidth <= 0)
        {
            overlayWidth = 320;
        }

        var maxX = Math.Max(0, ActualWidth - overlayWidth - 12);
        var x = Math.Clamp(origin.X, 0, maxX);
        var y = Math.Max(0d, origin.Y - 4);
        FavoriteFolderOverlayPanel.Margin = new Thickness(x, y, 0, 0);
    }

    private static void BuildFavoriteFilesTreeNodes(
        ObservableCollection<FavoriteEntryTreeNode> target,
        IReadOnlyDictionary<string, IReadOnlyList<string>> categoryGroups)
    {
        foreach (var group in categoryGroups
                     .OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var validFiles = group.Value
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            if (validFiles.Length == 0)
            {
                continue;
            }

            var categoryPath = group.Key ?? string.Empty;
            var segments = categoryPath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                segments = ["기본"];
            }

            var currentNodes = target;
            var logicalPath = string.Empty;
            FavoriteEntryTreeNode? leafCategoryNode = null;
            foreach (var segment in segments)
            {
                logicalPath = string.IsNullOrWhiteSpace(logicalPath) ? segment : $"{logicalPath}/{segment}";
                var existing = currentNodes.FirstOrDefault(node =>
                    node.IsDirectory &&
                    string.Equals(node.FullPath, logicalPath, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    existing = new FavoriteEntryTreeNode(logicalPath, segment, isDirectory: true);
                    currentNodes.Add(existing);
                }

                existing.IsExpanded = true;
                leafCategoryNode = existing;
                currentNodes = existing.Children;
            }

            if (leafCategoryNode is null)
            {
                continue;
            }

            foreach (var filePath in validFiles)
            {
                string fileName;
                try
                {
                    fileName = Path.GetFileName(filePath);
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                leafCategoryNode.Children.Add(new FavoriteEntryTreeNode(filePath, fileName, isDirectory: false));
            }
        }
    }

    private async void OnFavoriteFilesFlyoutTreeMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (FindAncestor<ToggleButton>(source) is not null)
        {
            return;
        }

        var item = FindAncestor<TreeViewItem>(source);
        if (item?.DataContext is not FavoriteEntryTreeNode node || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        if (node.IsDirectory)
        {
            e.Handled = true;
            return;
        }

        if (!File.Exists(node.FullPath))
        {
            return;
        }

        try
        {
            await Vm.RevealFileInActivePanelAsync(node.FullPath);
            HideFavoriteFilesFlyout();
        }
        catch (Exception ex)
        {
            LiveTrace.Write($"OnFavoriteFilesFlyoutTreeMouseLeftButtonUp failed: {ex}");
            Vm.StatusText = "파일 즐겨찾기 이동 중 오류가 발생했습니다.";
        }

        e.Handled = true;
    }

    private async void OnFavoriteFilesFlyoutTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is null || FavoriteFilesFlyoutTree.SelectedItem is not FavoriteEntryTreeNode node)
        {
            return;
        }

        if (node.IsDirectory)
        {
            e.Handled = true;
            return;
        }

        if (!File.Exists(node.FullPath))
        {
            return;
        }

        try
        {
            await Vm.RevealFileInActivePanelAsync(node.FullPath);
            HideFavoriteFilesFlyout();
        }
        catch (Exception ex)
        {
            LiveTrace.Write($"OnFavoriteFilesFlyoutTreeKeyDown failed: {ex}");
            Vm.StatusText = "파일 즐겨찾기 이동 중 오류가 발생했습니다.";
        }

        e.Handled = true;
    }

    private void OnFavoriteToolbarFolderPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not QuickAccessItem item)
        {
            _favoriteToolbarDragItem = null;
            return;
        }

        _favoriteToolbarDragStart = e.GetPosition(this);
        _favoriteToolbarDragItem = item;
    }

    private void OnFavoriteToolbarFolderPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _favoriteToolbarDragItem is null || sender is not FrameworkElement sourceElement)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _favoriteToolbarDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _favoriteToolbarDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var payload = _favoriteToolbarDragItem;
        DragDrop.DoDragDrop(sourceElement, new DataObject(typeof(QuickAccessItem), payload), DragDropEffects.Move);
    }

    private void OnFavoriteToolbarFolderDragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement targetElement ||
            targetElement.DataContext is not QuickAccessItem target ||
            !e.Data.GetDataPresent(typeof(QuickAccessItem)))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var source = e.Data.GetData(typeof(QuickAccessItem)) as QuickAccessItem;
        var valid = source is not null &&
                    !string.IsNullOrWhiteSpace(source.Path) &&
                    !string.IsNullOrWhiteSpace(target.Path) &&
                    !string.Equals(source.Path, target.Path, StringComparison.OrdinalIgnoreCase);
        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFavoriteToolbarFolderDrop(object sender, DragEventArgs e)
    {
        if (Vm is null || sender is not FrameworkElement targetElement || targetElement.DataContext is not QuickAccessItem target)
        {
            return;
        }

        if (!e.Data.GetDataPresent(typeof(QuickAccessItem)))
        {
            return;
        }

        var source = e.Data.GetData(typeof(QuickAccessItem)) as QuickAccessItem;
        if (source is null ||
            string.IsNullOrWhiteSpace(source.Path) ||
            string.IsNullOrWhiteSpace(target.Path) ||
            string.Equals(source.Path, target.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await Vm.MoveFavoriteToolbarFolderAsync(source.Path, target.Path);
        e.Handled = true;
    }

    private void OnFavoriteToolbarFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not QuickAccessItem item)
        {
            return;
        }

        if (_isFavoriteFolderOverlayOpen &&
            ReferenceEquals(_favoriteFlyoutSourceButton, button) &&
            _favoriteFlyoutNodes.Count > 0 &&
            string.Equals(_favoriteFlyoutNodes[0].FullPath, item.Path, StringComparison.OrdinalIgnoreCase))
        {
            HideFavoriteFolderFlyout();
            e.Handled = true;
            return;
        }

        ShowFavoriteFolderFlyout(button, item);
        e.Handled = true;
    }

    private void ShowFavoriteFolderFlyout(Button sourceButton, QuickAccessItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Path))
        {
            HideFavoriteFolderFlyout();
            return;
        }

        if (!Directory.Exists(item.Path))
        {
            _favoriteFolderRootCache.Remove(item.Path);
            HideFavoriteFolderFlyout();
            return;
        }

        if (_isFavoriteFolderOverlayOpen &&
            ReferenceEquals(_favoriteFlyoutSourceButton, sourceButton) &&
            _favoriteFlyoutNodes.Count > 0 &&
            string.Equals(_favoriteFlyoutNodes[0].FullPath, item.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Keep favorite flyouts mutually exclusive.
        HideFavoriteFilesFlyout();
        _favoriteFlyoutSourceButton = sourceButton;

        _favoriteFlyoutNodes.Clear();
        if (!_favoriteFolderRootCache.TryGetValue(item.Path, out var root))
        {
            root = FavoriteFolderTreeNode.Create(item.Path, HasSubdirectoriesSafe(item.Path));
            _favoriteFolderRootCache[item.Path] = root;
        }

        _favoriteFlyoutNodes.Add(root);
        root.IsExpanded = true;

        FavoriteFolderFlyoutTree.ItemsSource = _favoriteFlyoutNodes;
        PositionFavoriteFolderOverlay(sourceButton);
        FavoriteFolderOverlayPanel.Visibility = Visibility.Visible;
        _isFavoriteFolderOverlayOpen = true;
    }

    private void HideFavoriteFolderFlyout()
    {
        if (!_isFavoriteFolderOverlayOpen &&
            _favoriteFlyoutNodes.Count == 0 &&
            _favoriteFlyoutSourceButton is null)
        {
            return;
        }

        FavoriteFolderOverlayPanel.Visibility = Visibility.Collapsed;
        _isFavoriteFolderOverlayOpen = false;
        FavoriteFolderFlyoutTree.ItemsSource = null;
        _favoriteFlyoutNodes.Clear();
        _favoriteFlyoutSourceButton = null;
    }

    private static async Task EnsureFavoriteNodeChildrenLoadedAsync(FavoriteFolderTreeNode node)
    {
        if (node.IsLoaded || node.IsLoading)
        {
            return;
        }

        node.IsLoading = true;

        (string Path, bool CanExpand)[] directories;

        try
        {
            directories = await Task.Run(() =>
                Directory.EnumerateDirectories(node.FullPath)
                    .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                    .Select(path => (Path: path, CanExpand: HasSubdirectoriesSafe(path)))
                    .ToArray());
        }
        catch
        {
            directories = Array.Empty<(string Path, bool CanExpand)>();
        }

        var childNodes = directories.Select(entry => FavoriteFolderTreeNode.Create(entry.Path, entry.CanExpand)).ToArray();

        try
        {
            node.Children.Clear();
            const int batchSize = 32;
            for (var index = 0; index < childNodes.Length; index++)
            {
                node.Children.Add(childNodes[index]);
                if ((index + 1) % batchSize == 0)
                {
                    // Keep UI responsive while large trees are materialized.
                    await Task.Yield();
                }
            }

            node.IsLoaded = true;
        }
        finally
        {
            node.IsLoading = false;
        }
    }

    private async void OnFavoriteFlyoutTreeMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || sender is not TreeView treeView)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        // Do not navigate when user only toggles expander.
        if (FindAncestor<ToggleButton>(source) is not null)
        {
            return;
        }

        var item = FindAncestor<TreeViewItem>(source);
        if (item?.DataContext is not FavoriteFolderTreeNode node || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        if (!Directory.Exists(node.FullPath))
        {
            return;
        }

        await Vm.NavigateQuickAccessAsync(new QuickAccessItem
        {
            Name = node.Name,
            Path = node.FullPath,
            Category = "즐겨찾기"
        });

        HideFavoriteFolderFlyout();
        e.Handled = true;
    }

    private async void OnFavoriteFlyoutTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is null || FavoriteFolderFlyoutTree.SelectedItem is not FavoriteFolderTreeNode node)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(node.FullPath) || !Directory.Exists(node.FullPath))
        {
            return;
        }

        await Vm.NavigateQuickAccessAsync(new QuickAccessItem
        {
            Name = node.Name,
            Path = node.FullPath,
            Category = "즐겨찾기"
        });

        HideFavoriteFolderFlyout();
        e.Handled = true;
    }

    private async void OnFavoriteFlyoutTreeItemExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem treeViewItem || treeViewItem.DataContext is not FavoriteFolderTreeNode node)
        {
            return;
        }

        await EnsureFavoriteNodeChildrenLoadedAsync(node);
    }

    private async void OnRecentFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || sender is not ListView listView || listView.SelectedItem is not TrackedFileRecord record)
        {
            return;
        }

        await Vm.OpenTrackedFileAsync(record);
    }

    private async void OnFrequentFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || sender is not ListView listView || listView.SelectedItem is not TrackedFileRecord record)
        {
            return;
        }

        await Vm.OpenTrackedFileAsync(record);
    }

    private async void OnPinnedFolderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || sender is not ListView listView || listView.SelectedItem is not QuickAccessItem folder)
        {
            return;
        }

        await Vm.NavigateQuickAccessAsync(folder);
    }

    private async void OnPinnedFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || sender is not ListView listView || listView.SelectedItem is not TrackedFileRecord record)
        {
            return;
        }

        await Vm.OpenTrackedFileAsync(record);
    }

    private async void OnSearchResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || sender is not ListView listView || listView.SelectedItem is not FileSystemItem item)
        {
            return;
        }

        await Vm.OpenSearchResultAsync(item);
    }

    private async void OnSearchContextOpenClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || SearchResultsList.SelectedItem is not FileSystemItem item)
        {
            return;
        }

        await Vm.OpenSearchResultAsync(item);
    }

    private async void OnSearchContextOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || SearchResultsList.SelectedItem is not FileSystemItem item)
        {
            return;
        }

        await Vm.OpenContainingFolderAsync(item);
    }

    private async void OnSearchContextCopyClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.CopySelectedToOtherPanelAsync(GetSearchSelectedItems());
    }

    private async void OnSearchContextMoveClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.MoveSelectedToOtherPanelAsync(GetSearchSelectedItems());
    }

    private async void OnSearchContextDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var selected = GetSearchSelectedItems();
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

    private async void OnSearchContextFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.AddFavoriteItemsAsync(GetSearchSelectedItems());
    }

    private async void OnSearchContextPinClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.TogglePinForItemsAsync(GetSearchSelectedItems());
    }

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        if (Vm.IsFourPanelMode && GetActiveFourPanel() is PanelViewModel sourcePanel && GetFourPanelTransferTarget(sourcePanel) is PanelViewModel targetPanel)
        {
            await Vm.CopyOrMoveBetweenPanelsAsync(sourcePanel, targetPanel, GetFourPanelSelectedItems(sourcePanel), move: false);
            Vm.StatusText = $"복사: 패널 {_activeFourPanelIndex + 1} -> 패널 {GetFourPanelIndex(targetPanel) + 1}";
            return;
        }

        await Vm.CopySelectedToOtherPanelAsync(GetActiveSelectedItems());
    }

    private async void OnMoveClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        if (Vm.IsFourPanelMode && GetActiveFourPanel() is PanelViewModel sourcePanel && GetFourPanelTransferTarget(sourcePanel) is PanelViewModel targetPanel)
        {
            await Vm.CopyOrMoveBetweenPanelsAsync(sourcePanel, targetPanel, GetFourPanelSelectedItems(sourcePanel), move: true);
            Vm.StatusText = $"이동: 패널 {_activeFourPanelIndex + 1} -> 패널 {GetFourPanelIndex(targetPanel) + 1}";
            return;
        }

        await Vm.MoveSelectedToOtherPanelAsync(GetActiveSelectedItems());
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Vm?.IsFourPanelMode == true && GetActiveFourPanel() is PanelViewModel panel)
        {
            var selected = GetFourPanelSelectedItems(panel);
            if (selected.Count == 0)
            {
                return;
            }

            var message = $"선택한 {selected.Count}개 항목을 삭제하시겠습니까?";
            if (Vm.ConfirmBeforeDelete && !StyledDialogWindow.ShowConfirm(this, "삭제 확인", message))
            {
                return;
            }

            await Vm.DeleteItemsFromPanelAsync(panel, selected);
            RestoreKeyboardFocusAfterDelete();
            return;
        }

        await DeleteSelectionAsync();
    }

    private async void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (Vm?.IsFourPanelMode == true && GetActiveFourPanel() is PanelViewModel panel)
        {
            var selected = GetFourPanelSelectedItems(panel);
            if (selected.Count != 1)
            {
                StyledDialogWindow.ShowInfo(this, "알림", "이름 바꾸기는 한 번에 1개 항목만 가능합니다.");
                return;
            }

            var item = selected[0];
            if (item.IsParentDirectory)
            {
                return;
            }

            var newName = Interaction.InputBox("새 이름을 입력하세요.", "이름 바꾸기", item.Name);
            if (string.IsNullOrWhiteSpace(newName))
            {
                return;
            }

            await Vm.RenameItemInPanelAsync(panel, item, newName);
            return;
        }

        await RenameSelectionAsync();
    }

    private async void OnNewFolderClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var folderName = NewFolderDialog.ShowDialog(this, "New Folder");
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        if (Vm.IsFourPanelMode && GetActiveFourPanel() is PanelViewModel fourPanel)
        {
            await Vm.CreateNewFolderInPanelAsync(fourPanel, folderName);
            return;
        }

        await Vm.CreateNewFolderAsync(folderName);
    }

    private async void OnTogglePinClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var selectedPaths = GetActiveSelectedItems()
            .Where(item => !item.IsParentDirectory && !string.IsNullOrWhiteSpace(item.FullPath))
            .Select(item => item.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await Vm.TogglePinForActiveSelectionAsync(GetActiveSelectedItems());
        RestoreMultiSelectionAfterToolbarAction(selectedPaths);
    }

    private async void OnToggleFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var selectedPaths = GetActiveSelectedItems()
            .Where(item => !item.IsParentDirectory && !string.IsNullOrWhiteSpace(item.FullPath))
            .Select(item => item.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await Vm.ToggleFavoriteForActiveSelectionAsync(GetActiveSelectedItems());
        RestoreMultiSelectionAfterToolbarAction(selectedPaths);
    }

    private async void OnEditMemoClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        PanelViewModel targetPanel;
        IReadOnlyList<FileSystemItem> selectedItems;
        if (Vm.IsFourPanelMode && GetActiveFourPanel() is PanelViewModel fourPanel)
        {
            targetPanel = fourPanel;
            selectedItems = GetFourPanelSelectedItems(fourPanel);
        }
        else
        {
            targetPanel = Vm.IsLeftPanelActive ? Vm.LeftPanel : Vm.RightPanel;
            selectedItems = GetActiveSelectedItems();
        }

        if (selectedItems.Count != 1)
        {
            StyledDialogWindow.ShowInfo(this, "메모", "메모는 한 번에 1개 항목만 편집할 수 있습니다.");
            return;
        }

        var item = selectedItems[0];
        if (item.IsParentDirectory || string.IsNullOrWhiteSpace(item.FullPath))
        {
            return;
        }

        var currentMemo = Vm.GetItemMemoText(item.FullPath);
        var result = ItemMemoDialog.ShowDialog(this, item.DisplayName, item.FullPath, currentMemo);
        if (result is null)
        {
            return;
        }

        await Vm.SetItemMemoAsync(targetPanel, item, result.MemoText, result.DeleteRequested);
    }

    private async void OnOpenMemoListClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.OpenMemoListTabAsync();
    }

    private async void OnPropertyPinIconClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || !TryResolvePropertyActionTarget(sender, out var targetPanel, out var item))
        {
            return;
        }

        await Vm.TogglePinForItemsAsync([item], targetPanel);
        e.Handled = true;
    }

    private async void OnPropertyFavoriteIconClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || !TryResolvePropertyActionTarget(sender, out var targetPanel, out var item))
        {
            return;
        }

        if (item.IsDirectory)
        {
            await Vm.ToggleFavoriteForItemsAsync([item], targetPanel);
            e.Handled = true;
            return;
        }

        await ShowFavoriteFileCategoryDialogAsync(targetPanel, item);
        e.Handled = true;
    }

    private async Task ShowFavoriteFileCategoryDialogAsync(PanelViewModel panel, FileSystemItem item)
    {
        if (Vm is null || item.IsDirectory || string.IsNullOrWhiteSpace(item.FullPath))
        {
            return;
        }

        var selectedCategory = Vm.GetFavoriteFileCategoryForPath(item.FullPath);
        var categoryPaths = Vm.GetFavoriteFileCategoryFolders();
        var result = FavoriteFileCategoryDialog.ShowDialog(
            this,
            item.DisplayName,
            item.FullPath,
            categoryPaths,
            selectedCategory,
            canUnfavorite: item.IsFavorite);
        if (result is null)
        {
            return;
        }

        await Vm.ApplyFavoriteFileCategoryFoldersAsync(result.CategoryPaths);
        if (result.Action == FavoriteFileCategoryDialogAction.Unfavorite)
        {
            await Vm.RemoveFavoriteFileAsync(item.FullPath, panel);
            return;
        }

        await Vm.AssignFavoriteFileToCategoryAsync(item.FullPath, result.SelectedCategoryPath, panel);
    }

    private async void OnPropertyMemoIconClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || !TryResolvePropertyActionTarget(sender, out var targetPanel, out var item))
        {
            return;
        }

        var currentMemo = Vm.GetItemMemoText(item.FullPath);
        var result = ItemMemoDialog.ShowDialog(this, item.DisplayName, item.FullPath, currentMemo);
        if (result is null)
        {
            e.Handled = true;
            return;
        }

        await Vm.SetItemMemoAsync(targetPanel, item, result.MemoText, result.DeleteRequested);
        e.Handled = true;
    }

    private bool TryResolvePropertyActionTarget(object sender, out PanelViewModel targetPanel, out FileSystemItem item)
    {
        targetPanel = null!;
        item = null!;

        if (Vm is null || sender is not FrameworkElement element || element.DataContext is not FileSystemItem clickedItem)
        {
            return false;
        }

        if (clickedItem.IsParentDirectory || string.IsNullOrWhiteSpace(clickedItem.FullPath))
        {
            return false;
        }

        targetPanel = TryResolvePanelFromSource(element)
            ?? (TryGetFourPanel(element) is FourPanelSlotViewModel slot ? slot.Panel : null)!;
        if (targetPanel is null)
        {
            return false;
        }

        item = clickedItem;
        targetPanel.SelectedItem = clickedItem;
        return true;
    }

    private async void OnOpenFrequentFoldersClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.OpenFrequentFoldersTabAsync();
    }

    private async void OnOpenFrequentFilesClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.OpenFrequentFilesTabAsync();
    }

    private async void OnContextOpenClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        SetActivePanelByContextSender(sender);
        await Vm.OpenSelectedItemAsync();
    }

    private async void OnContextCopyClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        SetActivePanelByContextSender(sender);
        await Vm.CopySelectedToOtherPanelAsync(GetActiveSelectedItems());
    }

    private async void OnContextMoveClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        SetActivePanelByContextSender(sender);
        await Vm.MoveSelectedToOtherPanelAsync(GetActiveSelectedItems());
    }

    private async void OnContextDeleteClick(object sender, RoutedEventArgs e)
    {
        SetActivePanelByContextSender(sender);
        await DeleteSelectionAsync();
    }

    private async void OnContextRenameClick(object sender, RoutedEventArgs e)
    {
        SetActivePanelByContextSender(sender);
        await RenameSelectionAsync();
    }

    private void OnWinContextCopyClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        SetActivePanelByContextSender(sender);
        Vm.CopySelectionToClipboard(GetActiveSelectedItems());
    }

    private void OnWinContextCutClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        SetActivePanelByContextSender(sender);
        Vm.CutSelectionToClipboard(GetActiveSelectedItems());
    }

    private async void OnWinContextPasteClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        SetActivePanelByContextSender(sender);
        await Vm.PasteClipboardToActivePanelAsync();
    }

    private void OnWinContextPropertiesClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        SetActivePanelByContextSender(sender);
        var selected = GetActiveSelectedItems().FirstOrDefault();
        if (selected is null || string.IsNullOrWhiteSpace(selected.FullPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = selected.FullPath,
                Verb = "properties",
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private async void OnContextPinClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        SetActivePanelByContextSender(sender);
        await Vm.TogglePinForActiveSelectionAsync(GetActiveSelectedItems());
    }

    private async void OnFileMenuOpenClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        if (Vm.IsFourPanelMode && GetActiveFourPanel() is PanelViewModel fourPanel)
        {
            await Vm.OpenItemFromFourPanelAsync(fourPanel);
            return;
        }

        await Vm.OpenSelectedItemAsync();
    }

    private void OnFileMenuPropertiesClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        var selected = GetActiveSelectedItems().FirstOrDefault();
        if (selected is null || string.IsNullOrWhiteSpace(selected.FullPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = selected.FullPath,
                Verb = "properties",
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private void OnFileMenuExitClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow
        {
            Owner = this,
            DataContext = Vm
        };

        dialog.ShowDialog();
    }

    private void OnAddCurrentPathFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        _ = Vm.AddCurrentPanelPathToFavoritesAsync(Vm.IsLeftPanelActive);
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        if (Vm.IsFourPanelMode)
        {
            SelectAllInActiveFourPanel();
            return;
        }

        if (Vm.IsTileViewEnabled)
        {
            var list = Vm.IsLeftPanelActive ? LeftPanelTilesList : RightPanelTilesList;
            SelectAllInList(list);
        }
        else
        {
            var grid = Vm.IsLeftPanelActive ? LeftPanelGrid : RightPanelGrid;
            SelectAllInGrid(grid);
        }
    }

    private void OnSelectNoneClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        if (Vm.IsFourPanelMode)
        {
            ClearSelectionInActiveFourPanel();
            return;
        }

        if (Vm.IsTileViewEnabled)
        {
            var list = Vm.IsLeftPanelActive ? LeftPanelTilesList : RightPanelTilesList;
            list.SelectedItems.Clear();
            return;
        }

        var grid = Vm.IsLeftPanelActive ? LeftPanelGrid : RightPanelGrid;
        grid.SelectedItems.Clear();
    }

    private void OnContextAddFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        SetActivePanelByContextSender(sender);
        Vm.AddFavoriteCommand.Execute(null);
    }

    private async void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        // While typing in an editor (inline rename/path box/search box), do not hijack text-editing keys.
        if (IsTextInputFocused() && (e.Key == Key.Back || e.Key == Key.Delete || e.Key == Key.Enter))
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            OnSearchClick(sender, e);
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt && e.Key == Key.Left)
        {
            await Vm.NavigatePanelBackAsync(Vm.IsLeftPanelActive);
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt && e.Key == Key.Right)
        {
            await Vm.NavigatePanelForwardAsync(Vm.IsLeftPanelActive);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D)
        {
            Vm.AddFavoriteCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (Vm.IsFourPanelMode && await HandleFourPanelHotkeysAsync(e))
        {
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.Up || e.Key == Key.Down) &&
            Keyboard.Modifiers == ModifierKeys.None &&
            !Vm.IsFourPanelMode &&
            !IsTextInputFocused())
        {
            MoveActivePanelSelectionByArrow(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (IsPanelInteractionFocused())
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
            {
                Vm.CopySelectionToClipboard(GetActiveSelectedItems());
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.X)
            {
                Vm.CutSelectionToClipboard(GetActiveSelectedItems());
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
            {
                await Vm.PasteClipboardToActivePanelAsync();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Enter)
        {
            await Vm.OpenSelectedItemAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back)
        {
            Vm.GoUpCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            await DeleteSelectionAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2)
        {
            await BeginInlineRenameAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5)
        {
            await Vm.CopySelectedToOtherPanelAsync(GetActiveSelectedItems());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F6)
        {
            await Vm.MoveSelectedToOtherPanelAsync(GetActiveSelectedItems());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F7)
        {
            var folderName = NewFolderDialog.ShowDialog(this, "New Folder");
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                await Vm.CreateNewFolderAsync(folderName);
            }

            e.Handled = true;
        }
    }

    private static bool IsTextInputFocused()
    {
        if (Keyboard.FocusedElement is TextBox or RichTextBox or PasswordBox)
        {
            return true;
        }

        if (Keyboard.FocusedElement is DependencyObject focused)
        {
            var current = focused;
            while (current is not null)
            {
                if (current is TextBox or RichTextBox or PasswordBox)
                {
                    return true;
                }

                current = VisualTreeHelper.GetParent(current);
            }
        }

        return false;
    }

    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        Vm.SearchStartDirectory = Vm.IsLeftPanelActive ? Vm.LeftCurrentPath : Vm.RightCurrentPath;
        var dialog = new FindFilesWindow
        {
            Owner = this,
            DataContext = Vm
        };

        dialog.ShowDialog();
        BottomInfoTabs.SelectedIndex = 0;
    }

    private void OnSearchClearClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        Vm.SearchText = string.Empty;
        Vm.SearchExtension = string.Empty;
        Vm.SearchMinSizeKb = string.Empty;
        Vm.SearchMaxSizeKb = string.Empty;
        Vm.SearchResults.Clear();
        Vm.LeftPanel.ApplyFilter(string.Empty);
        Vm.RightPanel.ApplyFilter(string.Empty);
        Vm.StatusText = "검색 조건 초기화";
        SearchInputBox.Focus();
    }

    private void OnLeftFilterResetClick(object sender, RoutedEventArgs e)
    {
        Vm?.LeftPanel.ResetQuickFilter();
    }

    private void OnRightFilterResetClick(object sender, RoutedEventArgs e)
    {
        Vm?.RightPanel.ResetQuickFilter();
    }

    private void OnFourPanelFilterResetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (element.Tag is PanelViewModel panel)
        {
            panel.ResetQuickFilter();
            return;
        }

        if (TryGetFourPanel(element) is FourPanelSlotViewModel slot)
        {
            slot.Panel.ResetQuickFilter();
        }
    }

    private void OnLeftQuickFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (Vm is null || sender is not TextBox textBox)
        {
            return;
        }

        if (!string.Equals(Vm.LeftPanel.QuickFilterText, textBox.Text, StringComparison.Ordinal))
        {
            Vm.LeftPanel.ApplyFilter(textBox.Text);
        }
    }

    private void OnRightQuickFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (Vm is null || sender is not TextBox textBox)
        {
            return;
        }

        if (!string.Equals(Vm.RightPanel.QuickFilterText, textBox.Text, StringComparison.Ordinal))
        {
            Vm.RightPanel.ApplyFilter(textBox.Text);
        }
    }

    private void OnFourPanelQuickFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.Tag is not PanelViewModel panel)
        {
            return;
        }

        if (!string.Equals(panel.QuickFilterText, textBox.Text, StringComparison.Ordinal))
        {
            panel.ApplyFilter(textBox.Text);
        }
    }

    private async void OnAddLeftTabClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.AddPanelTabAsync(left: true, explicitSourcePath: Vm.LeftPanel.CurrentPath);
        Vm.SetActivePanelCommand.Execute("Left");
    }

    private void OnCloseLeftTabClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        Vm.CloseCurrentPanelTab(left: true);
        Vm.SetActivePanelCommand.Execute("Left");
    }

    private async void OnAddRightTabClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.AddPanelTabAsync(left: false, explicitSourcePath: Vm.RightPanel.CurrentPath);
        Vm.SetActivePanelCommand.Execute("Right");
    }

    private void OnPanelSplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PanelsGrid.ColumnDefinitions.Count < 3)
        {
            return;
        }

        PanelsGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        PanelsGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        e.Handled = true;
    }

    private void OnFourPanelGridSplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DependencyObject source)
        {
            return;
        }

        var grid = VisualTreeHelper.GetParent(source) as Grid;
        if (grid is null)
        {
            return;
        }

        if (grid.ColumnDefinitions.Count >= 3)
        {
            grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        }

        if (grid.RowDefinitions.Count >= 3)
        {
            grid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            grid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
        }

        e.Handled = true;
    }

    private void OnFourPanelHorizontalSplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DependencyObject source)
        {
            return;
        }

        var grid = VisualTreeHelper.GetParent(source) as Grid;
        if (grid is null || grid.ColumnDefinitions.Count == 0)
        {
            return;
        }

        for (var index = 0; index < grid.ColumnDefinitions.Count; index += 2)
        {
            grid.ColumnDefinitions[index].Width = new GridLength(1, GridUnitType.Star);
        }

        e.Handled = true;
    }

    private void OnCloseRightTabClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        Vm.CloseCurrentPanelTab(left: false);
        Vm.SetActivePanelCommand.Execute("Right");
    }

    private async void OnLeftBackClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.NavigatePanelBackAsync(left: true);
        Vm.SetActivePanelCommand.Execute("Left");
    }

    private async void OnLeftForwardClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.NavigatePanelForwardAsync(left: true);
        Vm.SetActivePanelCommand.Execute("Left");
    }

    private async void OnRightBackClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.NavigatePanelBackAsync(left: false);
        Vm.SetActivePanelCommand.Execute("Right");
    }

    private async void OnRightForwardClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.NavigatePanelForwardAsync(left: false);
        Vm.SetActivePanelCommand.Execute("Right");
    }

    private async void OnLeftTabDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.DuplicateCurrentPanelTabAsync(left: true);
    }

    private async void OnLeftTabsMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        if (FindAncestor<TabItem>(e.OriginalSource as DependencyObject) is not null)
        {
            Vm.CloseCurrentPanelTab(left: true);
            Vm.SetActivePanelCommand.Execute("Left");
            e.Handled = true;
            return;
        }

        // Create a new tab only when double-clicking empty tab strip area.
        await Vm.AddPanelTabAsync(left: true, explicitSourcePath: Vm.LeftPanel.CurrentPath);
        Vm.SetActivePanelCommand.Execute("Left");
        e.Handled = true;
    }

    private void OnLeftTabCloseToLeftClick(object sender, RoutedEventArgs e)
    {
        Vm?.CloseTabsToLeft(left: true);
    }

    private void OnLeftTabCloseToRightClick(object sender, RoutedEventArgs e)
    {
        Vm?.CloseTabsToRight(left: true);
    }

    private void OnLeftTabCloseOthersClick(object sender, RoutedEventArgs e)
    {
        Vm?.CloseOtherTabs(left: true);
    }

    private async void OnRightTabDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        await Vm.DuplicateCurrentPanelTabAsync(left: false);
    }

    private async void OnRightTabsMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        if (FindAncestor<TabItem>(e.OriginalSource as DependencyObject) is not null)
        {
            Vm.CloseCurrentPanelTab(left: false);
            Vm.SetActivePanelCommand.Execute("Right");
            e.Handled = true;
            return;
        }

        // Create a new tab only when double-clicking empty tab strip area.
        await Vm.AddPanelTabAsync(left: false, explicitSourcePath: Vm.RightPanel.CurrentPath);
        Vm.SetActivePanelCommand.Execute("Right");
        e.Handled = true;
    }

    private void OnPanelTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender))
        {
            return;
        }

        DismissFavoriteFlyoutsForNavigation();
        _lastTabInteractionUtc = DateTime.UtcNow;
    }

    private void OnRightTabCloseToLeftClick(object sender, RoutedEventArgs e)
    {
        Vm?.CloseTabsToLeft(left: false);
    }

    private void OnRightTabCloseToRightClick(object sender, RoutedEventArgs e)
    {
        Vm?.CloseTabsToRight(left: false);
    }

    private void OnRightTabCloseOthersClick(object sender, RoutedEventArgs e)
    {
        Vm?.CloseOtherTabs(left: false);
    }

    private void OnTabPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DismissFavoriteFlyoutsForNavigation();
        _lastTabInteractionUtc = DateTime.UtcNow;
        _tabDragStart = e.GetPosition(null);
        _draggedTabItem = FindAncestor<TabItem>(e.OriginalSource as DependencyObject);
        ClearTabDragHighlight();
    }

    private void OnTabPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DismissFavoriteFlyoutsForNavigation();
        _lastTabInteractionUtc = DateTime.UtcNow;
        if (Vm is null || sender is not TabControl tabControl)
        {
            return;
        }

        if (FindAncestor<TabItem>(e.OriginalSource as DependencyObject)?.DataContext is not PanelTabViewModel clickedTab)
        {
            return;
        }

        tabControl.SelectedItem = clickedTab;
        if (ReferenceEquals(tabControl, LeftTabsControl))
        {
            Vm.SelectedLeftTab = clickedTab;
            Vm.SetActivePanelCommand.Execute("Left");
        }
        else if (ReferenceEquals(tabControl, RightTabsControl))
        {
            Vm.SelectedRightTab = clickedTab;
            Vm.SetActivePanelCommand.Execute("Right");
        }
    }

    private void OnTabPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedTabItem is null)
        {
            return;
        }

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(_draggedTabItem, _draggedTabItem.DataContext!, DragDropEffects.Copy | DragDropEffects.Move);
        _draggedTabItem = null;
    }

    private void OnPanelItemsPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // While inline-renaming (TextBox), allow normal text selection drag.
        if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null)
        {
            _panelDragSourcePanel = null;
            return;
        }

        if (sender is ItemsControl itemsControl &&
            Keyboard.Modifiers == ModifierKeys.None &&
            TryGetItemUnderPointer(itemsControl, e.GetPosition(itemsControl), out var clickedItem) &&
            clickedItem is not null &&
            IsItemSelected(itemsControl, clickedItem) &&
            GetSelectedItemCount(itemsControl) > 1)
        {
            // Keep multi-selection intact when dragging from an already selected row/tile.
            itemsControl.Focus();
            e.Handled = true;
        }

        _panelItemDragStart = e.GetPosition(null);
        _panelDragSourcePanel = TryResolvePanelFromSource(sender as DependencyObject);
    }

    private void OnPanelItemsPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null || sender is not ItemsControl itemsControl)
        {
            return;
        }

        var left = TryResolvePanelSide(itemsControl);
        if (left is null)
        {
            return;
        }

        Vm.SetActivePanelCommand.Execute(left.Value ? "Left" : "Right");
        var clickedItem = TrySelectItemUnderPointer(itemsControl, e.GetPosition(itemsControl));
        if (!clickedItem)
        {
            ClearPanelSelection(left.Value);
        }

        var selectedPaths = GetPanelSelectedItems(left.Value)
            .Where(item => !item.IsParentDirectory)
            .Select(item => item.FullPath)
            .ToArray();

        var currentPath = left.Value ? Vm.LeftPanel.CurrentPath : Vm.RightPanel.CurrentPath;
        var invoked = ShellContextMenuHelper.ShowForPaths(this, selectedPaths, currentPath);
        if (invoked)
        {
            Vm.RefreshCommand.Execute(null);
            TriggerPostShellRefresh();
        }

        e.Handled = true;
    }

    private async void TriggerPostShellRefresh()
    {
        if (Vm is null)
        {
            return;
        }

        // Some shell extensions (zip/unzip, extractor tools) complete after InvokeCommand returns.
        // Re-issue refresh a few times so the list updates without manual folder re-entry.
        var delays = new[] { 350, 900, 1800 };
        foreach (var delayMs in delays)
        {
            try
            {
                await Task.Delay(delayMs);
                Vm.RefreshCommand.Execute(null);
            }
            catch
            {
                return;
            }
        }
    }

    private void OnPanelItemsPreviewMouseMove(object sender, MouseEventArgs e)
    {
        // Do not start file drag while the pointer is interacting with a text editor.
        if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (_isPanelItemDragInProgress || e.LeftButton != MouseButtonState.Pressed || Vm is null)
        {
            return;
        }

        var sourcePanel = _panelDragSourcePanel ?? TryResolvePanelFromSource(sender as DependencyObject);
        if (sourcePanel is null && sender is FrameworkElement element && TryGetFourPanel(element) is FourPanelSlotViewModel fourPanelSlot)
        {
            sourcePanel = fourPanelSlot.Panel;
        }

        if (sourcePanel is null)
        {
            return;
        }

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _panelItemDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _panelItemDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var items = GetSelectedItemsForPanel(sourcePanel)
            .Where(item => !item.IsParentDirectory)
            .ToArray();
        if (items.Length == 0 && sourcePanel.SelectedItem is FileSystemItem selected && !selected.IsParentDirectory)
        {
            items = [selected];
        }

        if (items.Length == 0)
        {
            return;
        }

        _isPanelItemDragInProgress = true;
        try
        {
            var payload = new PanelItemDragPayload(sourcePanel, items);
            _activePanelDragPayload = payload;
            var externalPaths = GetExternalDragPaths(items);
            var data = BuildPanelItemDragDataObject(payload, externalPaths);

            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            _activePanelDragPayload = null;
            _isPanelItemDragInProgress = false;
        }
    }

    private void OnPanelItemsDragOver(object sender, DragEventArgs e)
    {
        if (TryGetPanelItemDragPayload(e.Data) is not PanelItemDragPayload payload)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var targetPanel = TryResolvePanelFromSource(sender as DependencyObject);
        if (targetPanel is null || ReferenceEquals(targetPanel, payload.SourcePanel))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift
            ? DragDropEffects.Move
            : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnPanelItemsDrop(object sender, DragEventArgs e)
    {
        if (Vm is null || TryGetPanelItemDragPayload(e.Data) is not PanelItemDragPayload payload)
        {
            return;
        }

        var targetPanel = TryResolvePanelFromSource(sender as DependencyObject);
        if (targetPanel is null || ReferenceEquals(targetPanel, payload.SourcePanel) || payload.Items.Count == 0)
        {
            e.Handled = true;
            return;
        }

        var move = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var targetDirectory = ResolveDropTargetDirectory(e.OriginalSource as DependencyObject, targetPanel);
        try
        {
            await Vm.CopyOrMoveBetweenPanelsAsync(payload.SourcePanel, targetPanel, payload.Items, move, targetDirectory);
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"드래그 {(move ? "이동" : "복사")} 실패: {ex.Message}";
        }
        e.Handled = true;
    }

    private PanelItemDragPayload? TryGetPanelItemDragPayload(IDataObject data)
    {
        if (data.GetDataPresent(typeof(PanelItemDragPayload)))
        {
            return data.GetData(typeof(PanelItemDragPayload)) as PanelItemDragPayload;
        }

        return data.GetData(typeof(PanelItemDragPayload)) as PanelItemDragPayload ?? _activePanelDragPayload;
    }

    private static string ResolveDropTargetDirectory(DependencyObject? originalSource, PanelViewModel fallbackPanel)
    {
        if (FindAncestor<DataGridRow>(originalSource) is DataGridRow row &&
            row.Item is FileSystemItem item &&
            item.IsDirectory &&
            !item.IsParentDirectory &&
            !string.IsNullOrWhiteSpace(item.FullPath))
        {
            return item.FullPath;
        }

        if (FindAncestor<ListBoxItem>(originalSource) is ListBoxItem listBoxItem &&
            listBoxItem.DataContext is FileSystemItem tileItem &&
            tileItem.IsDirectory &&
            !tileItem.IsParentDirectory &&
            !string.IsNullOrWhiteSpace(tileItem.FullPath))
        {
            return tileItem.FullPath;
        }

        return fallbackPanel.CurrentPath;
    }

    private void OnTabsDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(PanelTabViewModel)))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (Vm is not null &&
            sender is TabControl targetControl &&
            e.Data.GetData(typeof(PanelTabViewModel)) is PanelTabViewModel draggedTab)
        {
            var sourceIsLeft = Vm.LeftTabs.Contains(draggedTab);
            var sourceIsRight = Vm.RightTabs.Contains(draggedTab);
            var targetIsLeft = ReferenceEquals(targetControl, LeftTabsControl);
            var isCrossPanel = (sourceIsLeft && !targetIsLeft) || (sourceIsRight && targetIsLeft);
            e.Effects = isCrossPanel ? DragDropEffects.Copy : DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.Move;
        }

        HighlightDragTargetTab(e.OriginalSource as DependencyObject);
        e.Handled = true;
    }

    private async void OnLeftTabsDrop(object sender, DragEventArgs e)
    {
        await HandleTabDropAsync(left: true, sender, e);
        ClearTabDragHighlight();
    }

    private async void OnRightTabsDrop(object sender, DragEventArgs e)
    {
        await HandleTabDropAsync(left: false, sender, e);
        ClearTabDragHighlight();
    }

    private async Task HandleTabDropAsync(bool left, object sender, DragEventArgs e)
    {
        if (Vm is null || !e.Data.GetDataPresent(typeof(PanelTabViewModel)) || sender is not TabControl)
        {
            return;
        }

        if (e.Data.GetData(typeof(PanelTabViewModel)) is not PanelTabViewModel draggedTab)
        {
            return;
        }

        var targetTabs = left ? Vm.LeftTabs : Vm.RightTabs;
        var sourceIsTargetPanel = targetTabs.Contains(draggedTab);

        var targetTabItem = FindAncestor<TabItem>(e.OriginalSource as DependencyObject);
        var toIndex = targetTabItem is null ? targetTabs.Count - 1 : targetTabs.IndexOf((PanelTabViewModel)targetTabItem.DataContext);
        if (toIndex < 0)
        {
            toIndex = targetTabs.Count - 1;
        }

        if (sourceIsTargetPanel)
        {
            var fromIndex = targetTabs.IndexOf(draggedTab);
            if (fromIndex < 0)
            {
                return;
            }

            Vm.MoveTab(left, fromIndex, toIndex);
            if (left)
            {
                Vm.SelectedLeftTab = targetTabs[toIndex];
                Vm.SetActivePanelCommand.Execute("Left");
            }
            else
            {
                Vm.SelectedRightTab = targetTabs[toIndex];
                Vm.SetActivePanelCommand.Execute("Right");
            }

            ClearTabDragHighlight();
            return;
        }

        // Cross-panel drag: create a new tab in target panel using dragged tab's path.
        var sourcePath = draggedTab.Panel.CurrentPath;
        await Vm.AddPanelTabAsync(left, explicitSourcePath: sourcePath);
        var createdTab = left ? Vm.SelectedLeftTab : Vm.SelectedRightTab;
        if (createdTab is not null)
        {
            createdTab.ViewMode = draggedTab.ViewMode;

            if (left)
            {
                Vm.SelectedLeftTab = createdTab;
                Vm.SetActivePanelCommand.Execute("Left");
            }
            else
            {
                Vm.SelectedRightTab = createdTab;
                Vm.SetActivePanelCommand.Execute("Right");
            }
        }

        ClearTabDragHighlight();
    }

    private void HighlightDragTargetTab(DependencyObject? originalSource)
    {
        var target = FindAncestor<TabItem>(originalSource);
        if (ReferenceEquals(_dragHoverTabItem, target))
        {
            return;
        }

        ClearTabDragHighlight();
        _dragHoverTabItem = target;

        if (_dragHoverTabItem is not null)
        {
            _dragHoverTabItem.Background = new SolidColorBrush(Color.FromRgb(173, 216, 230));
            _dragHoverTabItem.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 120, 215));
        }
    }

    private void ClearTabDragHighlight()
    {
        if (_dragHoverTabItem is null)
        {
            return;
        }

        _dragHoverTabItem.ClearValue(Control.BackgroundProperty);
        _dragHoverTabItem.ClearValue(Control.BorderBrushProperty);
        _dragHoverTabItem = null;
    }

    private void DismissFavoriteFlyoutsForNavigation()
    {
        var shouldDismiss =
            _isFavoriteFilesOverlayOpen ||
            _isFavoriteFolderOverlayOpen ||
            _favoriteFlyoutSourceButton is not null ||
            _favoriteFilesFlyoutSourceButton is not null ||
            _favoriteFlyoutNodes.Count > 0 ||
            _favoriteFileFlyoutNodes.Count > 0;

        if (!shouldDismiss)
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        var filesOpen = _isFavoriteFilesOverlayOpen;
        var foldersOpen = _isFavoriteFolderOverlayOpen;
        var fileNodes = CountFavoriteEntryNodes(_favoriteFileFlyoutNodes);
        HideFavoriteFolderFlyout();
        HideFavoriteFilesFlyout();
        LiveTrace.Write($"DismissFavoriteFlyoutsForNavigation done filesOpen={filesOpen} foldersOpen={foldersOpen} fileNodes={fileNodes} in {sw.ElapsedMilliseconds}ms");
        LiveTrace.WriteProcessSnapshot("DismissFavoriteFlyoutsForNavigation done");
    }

    private static int CountFavoriteEntryNodes(IEnumerable<FavoriteEntryTreeNode> nodes)
    {
        var total = 0;
        foreach (var node in nodes)
        {
            total++;
            if (node.Children.Count > 0)
            {
                total += CountFavoriteEntryNodes(node.Children);
            }
        }

        return total;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T target)
            {
                return target;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? current) where T : DependencyObject
    {
        if (current is null)
        {
            return null;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(current);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(current, index);
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

    private static bool IsInteractiveFilterControl(DependencyObject? source)
    {
        return FindAncestor<TextBox>(source) is not null ||
               FindAncestor<ToggleButton>(source) is not null;
    }

    private static bool IsDataGridHeaderInteraction(DependencyObject? source)
    {
        return FindAncestor<DataGridColumnHeader>(source) is not null;
    }

    private static bool IsPanelItemDoubleClickSource(object sender, DependencyObject? source)
    {
        return sender switch
        {
            DataGrid => FindAncestor<DataGridRow>(source) is not null,
            ListBox => FindAncestor<ListBoxItem>(source) is not null,
            _ => false
        };
    }

    private static bool TrySelectItemUnderPointer(ItemsControl control, Point position)
    {
        var hit = control.InputHitTest(position) as DependencyObject;
        if (hit is null)
        {
            return false;
        }

        if (control is DataGrid grid)
        {
            var row = FindAncestor<DataGridRow>(hit);
            if (row?.Item is not FileSystemItem item)
            {
                return false;
            }

            if (!grid.SelectedItems.Contains(item))
            {
                grid.SelectedItems.Clear();
                grid.SelectedItem = item;
            }

            grid.Focus();
            return true;
        }

        if (control is ListBox listBox)
        {
            var listItem = FindAncestor<ListBoxItem>(hit);
            if (listItem?.DataContext is not FileSystemItem item)
            {
                return false;
            }

            if (!listBox.SelectedItems.Contains(item))
            {
                listBox.SelectedItems.Clear();
                listBox.SelectedItem = item;
            }

            listBox.Focus();
            return true;
        }

        return false;
    }

    private static bool TryGetItemUnderPointer(ItemsControl control, Point position, out FileSystemItem? item)
    {
        item = null;
        var hit = control.InputHitTest(position) as DependencyObject;
        if (hit is null)
        {
            return false;
        }

        if (control is DataGrid)
        {
            if (FindAncestor<DataGridRow>(hit)?.Item is FileSystemItem rowItem)
            {
                item = rowItem;
                return true;
            }

            return false;
        }

        if (control is ListBox)
        {
            if (FindAncestor<ListBoxItem>(hit)?.DataContext is FileSystemItem listItem)
            {
                item = listItem;
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool IsItemSelected(ItemsControl control, FileSystemItem item)
    {
        if (control is DataGrid grid)
        {
            return grid.SelectedItems.Contains(item);
        }

        if (control is ListBox listBox)
        {
            return listBox.SelectedItems.Contains(item);
        }

        return false;
    }

    private static int GetSelectedItemCount(ItemsControl control)
    {
        if (control is DataGrid grid)
        {
            return grid.SelectedItems.Count;
        }

        if (control is ListBox listBox)
        {
            return listBox.SelectedItems.Count;
        }

        return 0;
    }

    private void RestoreMultiSelectionAfterToolbarAction(IReadOnlyList<string> selectedPaths)
    {
        if (Vm is null || selectedPaths.Count == 0)
        {
            return;
        }

        var pathSet = new HashSet<string>(selectedPaths, StringComparer.OrdinalIgnoreCase);
        if (pathSet.Count == 0)
        {
            return;
        }

        if (Vm.IsFourPanelMode)
        {
            var panel = GetActiveFourPanel();
            if (panel is null)
            {
                return;
            }

            RestorePanelSelectionByPaths(panel, pathSet);
            return;
        }

        var activePanel = Vm.IsLeftPanelActive ? Vm.LeftPanel : Vm.RightPanel;
        RestorePanelSelectionByPaths(activePanel, pathSet);
    }

    private void RestorePanelSelectionByPaths(PanelViewModel panel, HashSet<string> selectedPaths)
    {
        if (Vm is null)
        {
            return;
        }

        if (ReferenceEquals(panel, Vm.LeftPanel))
        {
            if (Vm.LeftPanelIsTileViewEnabled)
            {
                RestoreListSelectionByPaths(LeftPanelTilesList, selectedPaths);
            }
            else
            {
                RestoreGridSelectionByPaths(LeftPanelGrid, selectedPaths);
            }

            return;
        }

        if (ReferenceEquals(panel, Vm.RightPanel))
        {
            if (Vm.RightPanelIsTileViewEnabled)
            {
                RestoreListSelectionByPaths(RightPanelTilesList, selectedPaths);
            }
            else
            {
                RestoreGridSelectionByPaths(RightPanelGrid, selectedPaths);
            }

            return;
        }

        var slot = Vm.FourPanels.FirstOrDefault(s => ReferenceEquals(s.Panel, panel));
        if (slot is null)
        {
            return;
        }

        if (slot.SelectedTab?.IsTileViewEnabled == true && _fourPanelTileLists.TryGetValue(slot, out var tileLists))
        {
            var targetList = OrderControlsForSelection(tileLists).FirstOrDefault();
            if (targetList is not null)
            {
                RestoreListSelectionByPaths(targetList, selectedPaths);
            }

            return;
        }

        if (_fourPanelGrids.TryGetValue(slot, out var grids))
        {
            var targetGrid = OrderControlsForSelection(grids).FirstOrDefault();
            if (targetGrid is not null)
            {
                RestoreGridSelectionByPaths(targetGrid, selectedPaths);
            }
        }
    }

    private static void RestoreGridSelectionByPaths(DataGrid grid, HashSet<string> selectedPaths)
    {
        var matches = grid.Items
            .Cast<object>()
            .OfType<FileSystemItem>()
            .Where(item => !item.IsParentDirectory &&
                           !string.IsNullOrWhiteSpace(item.FullPath) &&
                           selectedPaths.Contains(item.FullPath))
            .ToArray();

        if (matches.Length == 0)
        {
            return;
        }

        grid.SelectedItems.Clear();
        foreach (var match in matches)
        {
            grid.SelectedItems.Add(match);
        }

        grid.SelectedItem = matches[0];
        if (grid.Columns.Count > 0)
        {
            grid.CurrentCell = new DataGridCellInfo(matches[0], grid.Columns[0]);
        }

        grid.ScrollIntoView(matches[0]);
        grid.Focus();
    }

    private static void RestoreListSelectionByPaths(ListBox list, HashSet<string> selectedPaths)
    {
        var matches = list.Items
            .Cast<object>()
            .OfType<FileSystemItem>()
            .Where(item => !item.IsParentDirectory &&
                           !string.IsNullOrWhiteSpace(item.FullPath) &&
                           selectedPaths.Contains(item.FullPath))
            .ToArray();

        if (matches.Length == 0)
        {
            return;
        }

        list.SelectedItems.Clear();
        foreach (var match in matches)
        {
            list.SelectedItems.Add(match);
        }

        list.SelectedItem = matches[0];
        list.ScrollIntoView(matches[0]);
        list.Focus();
    }

    private void ClearPanelSelection(bool left)
    {
        if (Vm is null)
        {
            return;
        }

        if (Vm.IsTileViewEnabledForPanel(left))
        {
            var list = left ? LeftPanelTilesList : RightPanelTilesList;
            list.SelectedItems.Clear();
            return;
        }

        var grid = left ? LeftPanelGrid : RightPanelGrid;
        grid.SelectedItems.Clear();
        grid.SelectedItem = null;
    }

    private bool? TryResolvePanelSide(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, LeftPanelGrid) || ReferenceEquals(current, LeftPanelTilesList))
            {
                return true;
            }

            if (ReferenceEquals(current, RightPanelGrid) || ReferenceEquals(current, RightPanelTilesList))
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private PanelViewModel? TryResolvePanelFromSource(DependencyObject? source)
    {
        if (source is FrameworkElement fe)
        {
            if (fe.Tag is FourPanelSlotViewModel tagSlot)
            {
                return tagSlot.Panel;
            }

            if (fe.DataContext is FourPanelSlotViewModel dcSlot)
            {
                return dcSlot.Panel;
            }
        }

        var side = TryResolvePanelSide(source);
        if (side.HasValue && Vm is not null)
        {
            return side.Value ? Vm.LeftPanel : Vm.RightPanel;
        }

        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement element && TryGetFourPanel(element) is FourPanelSlotViewModel slot)
            {
                return slot.Panel;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private IReadOnlyList<FileSystemItem> GetSelectedItemsForPanel(PanelViewModel panel)
    {
        if (Vm is null)
        {
            return Array.Empty<FileSystemItem>();
        }

        if (ReferenceEquals(panel, Vm.LeftPanel))
        {
            return GetPanelSelectedItems(left: true);
        }

        if (ReferenceEquals(panel, Vm.RightPanel))
        {
            return GetPanelSelectedItems(left: false);
        }

        return GetFourPanelSelectedItems(panel);
    }

    private static string[] GetExternalDragPaths(IEnumerable<FileSystemItem> items)
    {
        var candidates = items
            .Where(item => !item.IsParentDirectory && !string.IsNullOrWhiteSpace(item.FullPath))
            .Select(item => item.FullPath)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var filesOnly = candidates
            .Where(File.Exists)
            .ToArray();

        // Prefer file-only payload when possible (Discord/Electron apps are stricter than Explorer).
        return filesOnly.Length > 0 ? filesOnly : candidates;
    }

    private static DataObject BuildPanelItemDragDataObject(PanelItemDragPayload payload, IReadOnlyList<string> externalPaths)
    {
        var data = new DataObject();
        // Keep the OLE payload external-process friendly.
        // Internal move/copy uses _activePanelDragPayload fallback in TryGetPanelItemDragPayload.

        if (externalPaths.Count == 0)
        {
            return data;
        }

        var fileDropList = new StringCollection();
        foreach (var path in externalPaths)
        {
            fileDropList.Add(path);
        }

        // Prefer native file-drop list API for best compatibility with external apps (Discord/Electron).
        data.SetFileDropList(fileDropList);
        data.SetData(DataFormats.FileDrop, externalPaths.ToArray());
        data.SetData("FileNameW", externalPaths.ToArray());
        data.SetData("FileName", externalPaths.ToArray());

        // Keep payload minimal for Discord/Electron compatibility.
        // Hint external targets that this is a copy drag.
        data.SetData("Preferred DropEffect", CreatePreferredDropEffectData(DragDropEffects.Copy));
        return data;
    }

    private static MemoryStream CreatePreferredDropEffectData(DragDropEffects effect)
    {
        var bytes = BitConverter.GetBytes((int)effect);
        return new MemoryStream(bytes);
    }

    private sealed class PanelItemDragPayload
    {
        public PanelItemDragPayload(PanelViewModel sourcePanel, IReadOnlyList<FileSystemItem> items)
        {
            SourcePanel = sourcePanel;
            Items = items;
        }

        public PanelViewModel SourcePanel { get; }

        public IReadOnlyList<FileSystemItem> Items { get; }
    }

    private async Task DeleteSelectionAsync()
    {
        if (Vm is null)
        {
            return;
        }

        var activeLeft = Vm.IsLeftPanelActive;
        var selected = GetActiveSelectedItems();
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
        RestoreKeyboardFocusAfterDelete(activeLeft);
    }

    private async Task RenameSelectionAsync()
    {
        if (Vm is null)
        {
            return;
        }

        var selected = GetActiveSelectedItems();
        if (selected.Count != 1)
        {
            StyledDialogWindow.ShowInfo(this, "알림", "이름 바꾸기는 한 번에 1개 항목만 가능합니다.");
            return;
        }

        var currentName = selected[0].Name;
        var newName = Interaction.InputBox("새 이름을 입력하세요.", "이름 바꾸기", currentName);
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        await Vm.RenameSelectedAsync(newName);
    }

    private async Task BeginInlineRenameAsync()
    {
        if (Vm is null)
        {
            return;
        }

        var selected = GetActiveSelectedItems();
        if (selected.Count != 1)
        {
            StyledDialogWindow.ShowInfo(this, "알림", "이름 바꾸기는 한 번에 1개 항목만 가능합니다.");
            return;
        }

        var item = selected[0];
        if (item.IsParentDirectory)
        {
            return;
        }

        if (Vm.IsTileViewEnabledForPanel(Vm.IsLeftPanelActive))
        {
            item.RenameCandidate = item.Name;
            item.IsInlineRenaming = true;
            _inlineRenameSourcePath = item.FullPath;
            _inlineRenameOriginalName = item.Name;
            _allowInlineNameEdit = true;
            FocusActiveTileListForInlineRename();
            return;
        }

        var grid = Vm.IsLeftPanelActive ? LeftPanelGrid : RightPanelGrid;
        var nameColumn = grid.Columns.FirstOrDefault(column =>
            string.Equals(column.SortMemberPath, nameof(FileSystemItem.Name), StringComparison.Ordinal)) ?? grid.Columns.FirstOrDefault();
        if (nameColumn is null)
        {
            return;
        }

        item.RenameCandidate = item.Name;
        _inlineRenameSourcePath = item.FullPath;
        _inlineRenameOriginalName = item.Name;
        grid.CurrentCell = new DataGridCellInfo(item, nameColumn);
        _allowInlineNameEdit = true;
        grid.BeginEdit();
    }

    private IReadOnlyList<FileSystemItem> GetActiveSelectedItems()
    {
        if (Vm is null)
        {
            return Array.Empty<FileSystemItem>();
        }

        return GetPanelSelectedItems(Vm.IsLeftPanelActive);
    }

    private IReadOnlyList<FileSystemItem> GetPanelSelectedItems(bool left)
    {
        if (Vm is null)
        {
            return Array.Empty<FileSystemItem>();
        }

        if (Vm.IsTileViewEnabledForPanel(left))
        {
            var tileList = left ? LeftPanelTilesList : RightPanelTilesList;
            return tileList.SelectedItems.Cast<FileSystemItem>().ToArray();
        }

        var grid = left ? LeftPanelGrid : RightPanelGrid;
        return grid.SelectedItems.Cast<FileSystemItem>().ToArray();
    }

    private void MoveActivePanelSelectionByArrow(int delta)
    {
        if (Vm is null || delta == 0 || Vm.IsFourPanelMode)
        {
            return;
        }

        var left = Vm.IsLeftPanelActive;
        var panel = left ? Vm.LeftPanel : Vm.RightPanel;
        if (panel.Items.Count == 0)
        {
            return;
        }

        var currentIndex = panel.SelectedItem is null
            ? -1
            : panel.Items.IndexOf(panel.SelectedItem);

        var nextIndex = currentIndex < 0
            ? (delta > 0 ? 0 : panel.Items.Count - 1)
            : Math.Clamp(currentIndex + delta, 0, panel.Items.Count - 1);

        var nextItem = panel.Items[nextIndex];
        panel.SelectedItem = nextItem;

        if (Vm.IsTileViewEnabledForPanel(left))
        {
            var list = left ? LeftPanelTilesList : RightPanelTilesList;
            list.Focus();
            Keyboard.Focus(list);
            list.SelectedItem = nextItem;
            list.ScrollIntoView(nextItem);
            return;
        }

        var grid = left ? LeftPanelGrid : RightPanelGrid;
        var nameColumn = grid.Columns.FirstOrDefault(column =>
            string.Equals(column.SortMemberPath, nameof(FileSystemItem.Name), StringComparison.Ordinal))
            ?? grid.Columns.FirstOrDefault();

        grid.Focus();
        Keyboard.Focus(grid);
        grid.SelectedItem = nextItem;
        if (nameColumn is not null)
        {
            grid.CurrentCell = new DataGridCellInfo(nextItem, nameColumn);
            grid.ScrollIntoView(nextItem, nameColumn);
        }
        else
        {
            grid.ScrollIntoView(nextItem);
        }
    }

    private void SelectAllInActiveFourPanel()
    {
        var slot = GetActiveFourPanelSlot();
        if (slot is null)
        {
            return;
        }

        if (slot.SelectedTab?.IsTileViewEnabled == true)
        {
            if (_fourPanelTileLists.TryGetValue(slot, out var lists))
            {
                var target = lists.FirstOrDefault();
                if (target is not null)
                {
                    SelectAllInList(target);
                }
            }
            return;
        }

        if (_fourPanelGrids.TryGetValue(slot, out var grids))
        {
            var target = grids.FirstOrDefault();
            if (target is not null)
            {
                SelectAllInGrid(target);
            }
        }
    }

    private void ClearSelectionInActiveFourPanel()
    {
        var slot = GetActiveFourPanelSlot();
        if (slot is null)
        {
            return;
        }

        if (slot.SelectedTab?.IsTileViewEnabled == true)
        {
            if (_fourPanelTileLists.TryGetValue(slot, out var lists))
            {
                var target = lists.FirstOrDefault();
                target?.SelectedItems.Clear();
            }
            return;
        }

        if (_fourPanelGrids.TryGetValue(slot, out var grids))
        {
            var target = grids.FirstOrDefault();
            target?.SelectedItems.Clear();
        }
    }

    private static void SelectAllInGrid(DataGrid grid)
    {
        grid.SelectedItems.Clear();
        foreach (var item in grid.Items.Cast<object>().OfType<FileSystemItem>())
        {
            if (!item.IsParentDirectory)
            {
                grid.SelectedItems.Add(item);
            }
        }
    }

    private static void SelectAllInList(ListBox list)
    {
        list.SelectedItems.Clear();
        foreach (var item in list.Items.Cast<object>().OfType<FileSystemItem>())
        {
            if (!item.IsParentDirectory)
            {
                list.SelectedItems.Add(item);
            }
        }
    }

    private IReadOnlyList<FileSystemItem> GetSearchSelectedItems()
    {
        return SearchResultsList.SelectedItems.Cast<FileSystemItem>().ToArray();
    }

    private bool IsPanelInteractionFocused()
    {
        if (IsKeyboardFocusWithin && (LeftPanelGrid.IsKeyboardFocusWithin || RightPanelGrid.IsKeyboardFocusWithin || LeftPanelTilesList.IsKeyboardFocusWithin || RightPanelTilesList.IsKeyboardFocusWithin))
        {
            return true;
        }

        var focused = Keyboard.FocusedElement as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, LeftPanelGrid) || ReferenceEquals(focused, RightPanelGrid) || ReferenceEquals(focused, LeftPanelTilesList) || ReferenceEquals(focused, RightPanelTilesList))
            {
                return true;
            }

            if (focused is DataGrid grid && TryGetFourPanel(grid) is not null)
            {
                return true;
            }

            focused = VisualTreeHelper.GetParent(focused);
        }

        return false;
    }

    private static FourPanelSlotViewModel? TryGetFourPanel(FrameworkElement element)
    {
        return element.DataContext as FourPanelSlotViewModel;
    }

    private static FourPanelSlotViewModel? FindFourPanelSlot(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement element && element.DataContext is FourPanelSlotViewModel slot)
            {
                return slot;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private PanelViewModel? GetActiveFourPanel()
    {
        if (Vm is null || Vm.FourPanels.Count == 0)
        {
            return null;
        }

        var index = Math.Clamp(_activeFourPanelIndex, 0, Vm.FourPanels.Count - 1);
        return Vm.FourPanels[index].Panel;
    }

    private IReadOnlyList<FileSystemItem> GetFourPanelSelectedItems(PanelViewModel panel)
    {
        var slot = Vm?.FourPanels.FirstOrDefault(s => ReferenceEquals(s.Panel, panel));
        if (slot is not null && _fourPanelTileLists.TryGetValue(slot, out var tileLists))
        {
            foreach (var tileList in OrderControlsForSelection(tileLists))
            {
                var selectedTiles = tileList.SelectedItems.Cast<FileSystemItem>().ToArray();
                if (selectedTiles.Length > 0)
                {
                    return selectedTiles;
                }
            }
        }

        if (slot is not null && _fourPanelGrids.TryGetValue(slot, out var grids))
        {
            foreach (var grid in OrderControlsForSelection(grids))
            {
                var selected = grid.SelectedItems.Cast<FileSystemItem>().ToArray();
                if (selected.Length > 0)
                {
                    return selected;
                }
            }
        }

        return panel.SelectedItem is null ? Array.Empty<FileSystemItem>() : [panel.SelectedItem];
    }

    private static IEnumerable<T> OrderControlsForSelection<T>(IEnumerable<T> controls) where T : UIElement
    {
        return controls
            .OrderByDescending(control => control.IsKeyboardFocusWithin)
            .ThenByDescending(control => control.IsVisible);
    }

    private PanelViewModel? GetFourPanelTransferTarget(PanelViewModel sourcePanel)
    {
        if (Vm is null || Vm.FourPanels.Count < 2)
        {
            return null;
        }

        var sourceIndex = Vm.FourPanels.IndexOf(Vm.FourPanels.FirstOrDefault(slot => ReferenceEquals(slot.Panel, sourcePanel))!);
        if (sourceIndex < 0)
        {
            sourceIndex = Math.Clamp(_activeFourPanelIndex, 0, Vm.FourPanels.Count - 1);
        }

        var targetIndex = (sourceIndex + 1) % Vm.FourPanels.Count;
        return Vm.FourPanels[targetIndex].Panel;
    }

    private int GetFourPanelIndex(PanelViewModel panel)
    {
        if (Vm is null)
        {
            return -1;
        }

        for (var index = 0; index < Vm.FourPanels.Count; index++)
        {
            if (ReferenceEquals(Vm.FourPanels[index].Panel, panel))
            {
                return index;
            }
        }

        return -1;
    }

    private FourPanelSlotViewModel? GetActiveFourPanelSlot()
    {
        if (Vm is null || Vm.FourPanels.Count == 0)
        {
            return null;
        }

        var index = Math.Clamp(_activeFourPanelIndex, 0, Vm.FourPanels.Count - 1);
        return Vm.FourPanels[index];
    }

    private void SetActivePanelByContextSender(object sender)
    {
        if (Vm is null)
        {
            return;
        }

        if (sender is not MenuItem menuItem || menuItem.Parent is not ContextMenu contextMenu || contextMenu.PlacementTarget is not Control sourceControl)
        {
            return;
        }

        if (ReferenceEquals(sourceControl, LeftPanelGrid) || ReferenceEquals(sourceControl, LeftPanelTilesList))
        {
            Vm.SetActivePanelCommand.Execute("Left");
        }
        else if (ReferenceEquals(sourceControl, RightPanelGrid) || ReferenceEquals(sourceControl, RightPanelTilesList))
        {
            Vm.SetActivePanelCommand.Execute("Right");
        }
    }

    private void OnPanelGridSorting(object sender, DataGridSortingEventArgs e)
    {
        if (sender is not DataGrid grid || string.IsNullOrWhiteSpace(e.Column.SortMemberPath))
        {
            return;
        }

        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        foreach (var column in grid.Columns)
        {
            if (!ReferenceEquals(column, e.Column))
            {
                column.SortDirection = null;
            }
        }

        e.Column.SortDirection = direction;

        if (CollectionViewSource.GetDefaultView(grid.ItemsSource) is ListCollectionView view)
        {
            view.CustomSort = new PanelItemComparer(e.Column.SortMemberPath, direction);
        }

        UpdatePanelGridSortHeaderIndicators(grid, e.Column);
    }

    private void OnPanelGridHeaderMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridColumnHeader header || header.Column is null)
        {
            return;
        }

        var grid = FindAncestor<DataGrid>(header);
        if (grid is null)
        {
            return;
        }

        ResetPanelGridSorting(grid);
        e.Handled = true;
    }

    private void ResetPanelGridSorting(DataGrid grid)
    {
        foreach (var column in grid.Columns)
        {
            column.SortDirection = null;
        }

        if (CollectionViewSource.GetDefaultView(grid.ItemsSource) is ListCollectionView view)
        {
            view.CustomSort = null;
            view.Refresh();
        }

        UpdatePanelGridSortHeaderIndicators(grid, null);
    }

    private void UpdatePanelGridSortHeaderIndicators(DataGrid grid, DataGridColumn? sortedColumn)
    {
        foreach (var column in grid.Columns)
        {
            if (!_panelGridHeaderLabels.TryGetValue(column, out var baseLabel))
            {
                baseLabel = NormalizeHeaderLabel(column.Header?.ToString());
                _panelGridHeaderLabels[column] = baseLabel;
            }

            if (ReferenceEquals(column, sortedColumn) && column.SortDirection.HasValue)
            {
                var arrow = column.SortDirection == ListSortDirection.Ascending ? "↑" : "↓";
                column.Header = $"{baseLabel} {arrow}";
            }
            else
            {
                column.Header = baseLabel;
            }
        }
    }

    private static string NormalizeHeaderLabel(string? header)
    {
        var label = (header ?? string.Empty).TrimEnd();
        if (label.EndsWith(" ↑", StringComparison.Ordinal) || label.EndsWith(" ↓", StringComparison.Ordinal))
        {
            return label[..^2].TrimEnd();
        }

        return label;
    }

    private sealed class PanelItemComparer : IComparer
    {
        private readonly string _sortMemberPath;
        private readonly ListSortDirection _direction;

        public PanelItemComparer(string sortMemberPath, ListSortDirection direction)
        {
            _sortMemberPath = sortMemberPath;
            _direction = direction;
        }

        public int Compare(object? x, object? y)
        {
            if (x is not FileSystemItem left || y is not FileSystemItem right)
            {
                return 0;
            }

            // Keep [..] pinned at top regardless of sort column/direction.
            if (left.IsParentDirectory != right.IsParentDirectory)
            {
                return left.IsParentDirectory ? -1 : 1;
            }

            // Keep folder group above file group regardless of sort column/direction.
            if (left.IsDirectory != right.IsDirectory)
            {
                return left.IsDirectory ? -1 : 1;
            }

            // Keep pinned items at top within each group (folders/files).
            if (left.IsPinned != right.IsPinned)
            {
                return left.IsPinned ? -1 : 1;
            }

            var result = _sortMemberPath switch
            {
                nameof(FileSystemItem.Name) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name),
                nameof(FileSystemItem.Extension) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Extension, right.Extension),
                nameof(FileSystemItem.SizeDisplay) => left.SizeBytes.CompareTo(right.SizeBytes),
                nameof(FileSystemItem.LastModified) => left.LastModified.CompareTo(right.LastModified),
                nameof(FileSystemItem.TypeDisplay) => StringComparer.CurrentCultureIgnoreCase.Compare(left.TypeDisplay, right.TypeDisplay),
                _ => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name)
            };

            if (result == 0)
            {
                result = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
            }

            return _direction == ListSortDirection.Descending ? -result : result;
        }
    }

    private void OnPanelGridBeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (!_allowInlineNameEdit)
        {
            e.Cancel = true;
            return;
        }

        if (e.Row.Item is FileSystemItem item && item.IsParentDirectory)
        {
            e.Cancel = true;
        }
    }

    private async void OnPanelGridCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        _allowInlineNameEdit = false;

        try
        {
            if (Vm is null || sender is not DataGrid grid || e.EditAction != DataGridEditAction.Commit || e.Row.Item is not FileSystemItem item)
            {
                _inlineRenameSourcePath = null;
                _inlineRenameOriginalName = null;
                return;
            }

            if (!string.Equals(item.FullPath, _inlineRenameSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                _inlineRenameSourcePath = null;
                _inlineRenameOriginalName = null;
                return;
            }

            var newName = (item.RenameCandidate ?? string.Empty).Trim();
            var originalName = _inlineRenameOriginalName ?? item.Name;
            _inlineRenameSourcePath = null;
            _inlineRenameOriginalName = null;

            if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, originalName, StringComparison.Ordinal))
            {
                return;
            }

            await Vm.RenameSelectedAsync(newName);

            // After inline rename, restore keyboard focus/current cell so Up/Down keeps working immediately.
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (grid.SelectedItem is not FileSystemItem selected)
                {
                    return;
                }

                var nameColumn = grid.Columns.FirstOrDefault(column =>
                    string.Equals(column.SortMemberPath, nameof(FileSystemItem.Name), StringComparison.Ordinal)) ?? grid.Columns.FirstOrDefault();
                if (nameColumn is null)
                {
                    return;
                }

                grid.Focus();
                Keyboard.Focus(grid);
                grid.CurrentCell = new DataGridCellInfo(selected, nameColumn);
                grid.ScrollIntoView(selected);
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
        catch (Exception ex)
        {
            if (Vm is not null)
            {
                Vm.StatusText = $"이름 바꾸기 실패: {ex.Message}";
            }
        }
    }

    private void OnInlineRenameTextBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.Focus();
            var renameText = textBox.Text ?? string.Empty;
            var selectLength = renameText.Length;

            if (textBox.DataContext is FileSystemItem item && !item.IsDirectory && !item.IsParentDirectory)
            {
                var extension = item.Extension ?? string.Empty;
                if (!string.IsNullOrEmpty(extension) &&
                    renameText.Length > extension.Length &&
                    renameText.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    selectLength = renameText.Length - extension.Length;
                }
            }

            if (selectLength <= 0 || selectLength > renameText.Length)
            {
                textBox.SelectAll();
                return;
            }

            textBox.Select(0, selectLength);
        }
    }

    private async void OnInlineRenameTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            try
            {
                await CompleteTileInlineRenameAsync(textBox, commit: true);
            }
            catch (Exception ex)
            {
                if (Vm is not null)
                {
                    Vm.StatusText = $"이름 바꾸기 실패: {ex.Message}";
                }
            }
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            try
            {
                await CompleteTileInlineRenameAsync(textBox, commit: false);
            }
            catch (Exception ex)
            {
                if (Vm is not null)
                {
                    Vm.StatusText = $"이름 바꾸기 취소 처리 실패: {ex.Message}";
                }
            }
        }
    }

    private async void OnInlineRenameTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        try
        {
            await CompleteTileInlineRenameAsync(textBox, commit: true);
        }
        catch (Exception ex)
        {
            if (Vm is not null)
            {
                Vm.StatusText = $"이름 바꾸기 실패: {ex.Message}";
            }
        }
    }

    private async Task CompleteTileInlineRenameAsync(TextBox textBox, bool commit)
    {
        if (_isTileInlineRenameCommitting)
        {
            return;
        }

        if (Vm is null || textBox.DataContext is not FileSystemItem item || !item.IsInlineRenaming)
        {
            return;
        }

        _isTileInlineRenameCommitting = true;
        try
        {
        var newName = (item.RenameCandidate ?? string.Empty).Trim();
        var originalName = _inlineRenameOriginalName ?? item.Name;
        var sourcePath = _inlineRenameSourcePath;

        item.IsInlineRenaming = false;
        _allowInlineNameEdit = false;
        _inlineRenameSourcePath = null;
        _inlineRenameOriginalName = null;
        FocusActiveTileListForInlineRename();

        if (!commit ||
            string.IsNullOrWhiteSpace(newName) ||
            string.IsNullOrWhiteSpace(sourcePath) ||
            !string.Equals(item.FullPath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(newName, originalName, StringComparison.Ordinal))
        {
            item.RenameCandidate = item.Name;
            return;
        }

        await Vm.RenameSelectedAsync(newName);
        }
        finally
        {
            _isTileInlineRenameCommitting = false;
        }
    }

    private void FocusActiveTileListForInlineRename()
    {
        if (Vm is null)
        {
            return;
        }

        if (Vm.IsLeftPanelActive)
        {
            LeftPanelTilesList.Focus();
            if (Vm.LeftPanel.SelectedItem is not null)
            {
                LeftPanelTilesList.ScrollIntoView(Vm.LeftPanel.SelectedItem);
            }
            return;
        }

        RightPanelTilesList.Focus();
        if (Vm.RightPanel.SelectedItem is not null)
        {
            RightPanelTilesList.ScrollIntoView(Vm.RightPanel.SelectedItem);
        }
    }

    private void RestoreKeyboardFocusAfterDelete(bool? targetLeftPanel = null, int retryCount = 2)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (Vm is null)
            {
                return;
            }

            if (Vm.IsFourPanelMode)
            {
                var slot = GetActiveFourPanelSlot();
                if (slot is null)
                {
                    return;
                }

                var selected = slot.Panel.SelectedItem;
                if (slot.SelectedTab?.IsTileViewEnabled == true)
                {
                    if (_fourPanelTileLists.TryGetValue(slot, out var lists))
                    {
                        var list = lists.FirstOrDefault(control => control.IsVisible) ?? lists.FirstOrDefault();
                        if (list is null)
                        {
                            return;
                        }

                        list.Focus();
                        Keyboard.Focus(list);
                        var focusItem = selected ?? GetFirstNavigableItem(list);
                        if (focusItem is not null)
                        {
                            list.SelectedItem = focusItem;
                            list.ScrollIntoView(focusItem);
                        }
                    }

                    return;
                }

                if (_fourPanelGrids.TryGetValue(slot, out var grids))
                {
                    var grid = grids.FirstOrDefault(control => control.IsVisible) ?? grids.FirstOrDefault();
                    if (grid is null)
                    {
                        return;
                    }

                    grid.Focus();
                    Keyboard.Focus(grid);
                    var focusItem = selected ?? GetFirstNavigableItem(grid);
                    if (focusItem is not null)
                    {
                        var nameColumn = grid.Columns.FirstOrDefault(column =>
                            string.Equals(column.SortMemberPath, nameof(FileSystemItem.Name), StringComparison.Ordinal))
                            ?? grid.Columns.FirstOrDefault();
                        if (nameColumn is not null)
                        {
                            grid.SelectedItem = focusItem;
                            grid.CurrentCell = new DataGridCellInfo(focusItem, nameColumn);
                        }

                        grid.ScrollIntoView(focusItem);
                    }
                }

                return;
            }

            var left = targetLeftPanel ?? Vm.IsLeftPanelActive;
            Vm.SetActivePanelCommand.Execute(left ? "Left" : "Right");
            var selectedItem = left ? Vm.LeftPanel.SelectedItem : Vm.RightPanel.SelectedItem;
            if (Vm.IsTileViewEnabledForPanel(left))
            {
                var list = left ? LeftPanelTilesList : RightPanelTilesList;
                list.UpdateLayout();
                list.Focus();
                Keyboard.Focus(list);
                var focusItem = selectedItem ?? GetFirstNavigableItem(list);
                if (focusItem is not null)
                {
                    list.SelectedItem = focusItem;
                    list.ScrollIntoView(focusItem);
                }
                else if (retryCount > 0)
                {
                    _ = Dispatcher.BeginInvoke(
                        () => RestoreKeyboardFocusAfterDelete(targetLeftPanel, retryCount - 1),
                        DispatcherPriority.Background);
                }

                return;
            }

            var panelGrid = left ? LeftPanelGrid : RightPanelGrid;
            panelGrid.UpdateLayout();
            panelGrid.Focus();
            Keyboard.Focus(panelGrid);
            var focusItemForGrid = selectedItem ?? GetFirstNavigableItem(panelGrid);
            if (focusItemForGrid is not null)
            {
                var nameColumn = panelGrid.Columns.FirstOrDefault(column =>
                    string.Equals(column.SortMemberPath, nameof(FileSystemItem.Name), StringComparison.Ordinal))
                    ?? panelGrid.Columns.FirstOrDefault();
                if (nameColumn is not null)
                {
                    panelGrid.SelectedItem = focusItemForGrid;
                    panelGrid.CurrentCell = new DataGridCellInfo(focusItemForGrid, nameColumn);
                    panelGrid.ScrollIntoView(focusItemForGrid, nameColumn);
                }
                else
                {
                    panelGrid.ScrollIntoView(focusItemForGrid);
                }
            }
            else if (retryCount > 0)
            {
                _ = Dispatcher.BeginInvoke(
                    () => RestoreKeyboardFocusAfterDelete(targetLeftPanel, retryCount - 1),
                    DispatcherPriority.Background);
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private static FileSystemItem? GetFirstNavigableItem(ItemsControl itemsControl)
    {
        return itemsControl.Items
            .Cast<object>()
            .OfType<FileSystemItem>()
            .FirstOrDefault(item => !item.IsParentDirectory)
            ?? itemsControl.Items.Cast<object>().OfType<FileSystemItem>().FirstOrDefault();
    }

    private void FocusPanelAfterPathNavigation(bool left)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (Vm is null)
            {
                return;
            }

            var selectedItem = left ? Vm.LeftPanel.SelectedItem : Vm.RightPanel.SelectedItem;
            if (Vm.IsTileViewEnabledForPanel(left))
            {
                var list = left ? LeftPanelTilesList : RightPanelTilesList;
                list.Focus();
                Keyboard.Focus(list);
                if (selectedItem is not null)
                {
                    list.ScrollIntoView(selectedItem);
                }

                return;
            }

            var grid = left ? LeftPanelGrid : RightPanelGrid;
            grid.Focus();
            Keyboard.Focus(grid);
            if (selectedItem is not null)
            {
                var nameColumn = grid.Columns.FirstOrDefault(column =>
                    string.Equals(column.SortMemberPath, nameof(FileSystemItem.Name), StringComparison.Ordinal))
                    ?? grid.Columns.FirstOrDefault();
                if (nameColumn is not null)
                {
                    grid.CurrentCell = new DataGridCellInfo(selectedItem, nameColumn);
                }

                grid.ScrollIntoView(selectedItem);
            }
        }, DispatcherPriority.Input);
    }

    private void FocusFourPanelAfterPathNavigation(FourPanelSlotViewModel slot)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            var selectedItem = slot.Panel.SelectedItem;
            if (slot.SelectedTab?.IsTileViewEnabled == true)
            {
                if (_fourPanelTileLists.TryGetValue(slot, out var tileLists))
                {
                    var list = tileLists.FirstOrDefault(control => control.IsVisible) ?? tileLists.FirstOrDefault();
                    if (list is null)
                    {
                        return;
                    }

                    list.Focus();
                    Keyboard.Focus(list);
                    if (selectedItem is not null)
                    {
                        list.ScrollIntoView(selectedItem);
                    }
                }

                return;
            }

            if (_fourPanelGrids.TryGetValue(slot, out var grids))
            {
                var grid = grids.FirstOrDefault(control => control.IsVisible) ?? grids.FirstOrDefault();
                if (grid is null)
                {
                    return;
                }

                grid.Focus();
                Keyboard.Focus(grid);
                if (selectedItem is not null)
                {
                    var nameColumn = grid.Columns.FirstOrDefault(column =>
                        string.Equals(column.SortMemberPath, nameof(FileSystemItem.Name), StringComparison.Ordinal))
                        ?? grid.Columns.FirstOrDefault();
                    if (nameColumn is not null)
                    {
                        grid.CurrentCell = new DataGridCellInfo(selectedItem, nameColumn);
                    }

                    grid.ScrollIntoView(selectedItem);
                }
            }
        }, DispatcherPriority.Input);
    }

    private sealed class FavoriteFolderTreeNode
    {
        private FavoriteFolderTreeNode(string fullPath, string name, bool canExpand)
        {
            FullPath = fullPath;
            Name = name;
            // Add dummy only when actual subfolders exist, so expander icon is accurate.
            if (canExpand)
            {
                Children.Add(Dummy);
            }
        }

        public string Name { get; }
        public string FullPath { get; }
        public ObservableCollection<FavoriteFolderTreeNode> Children { get; } = [];
        public bool IsLoaded { get; set; }
        public bool IsLoading { get; set; }
        public bool IsExpanded { get; set; }

        private static FavoriteFolderTreeNode Dummy { get; } = new(string.Empty, string.Empty, canExpand: false) { IsLoaded = true };

        public static FavoriteFolderTreeNode Create(string path, bool canExpand)
        {
            var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(normalized);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = normalized;
            }

            return new FavoriteFolderTreeNode(path, name, canExpand);
        }
    }

    private static bool HasSubdirectoriesSafe(string path)
    {
        try
        {
            using var e = Directory.EnumerateDirectories(path).GetEnumerator();
            return e.MoveNext();
        }
        catch
        {
            return false;
        }
    }

    private sealed class FavoriteEntryTreeNode
    {
        public FavoriteEntryTreeNode(string fullPath, string name, bool isDirectory)
        {
            FullPath = fullPath;
            Name = name;
            IsDirectory = isDirectory;
        }

        public string Name { get; }
        public string FullPath { get; }
        public bool IsDirectory { get; }
        public string DisplayName => IsDirectory ? $"▸ {Name}" : Name;
        public ObservableCollection<FavoriteEntryTreeNode> Children { get; } = [];
        public bool IsExpanded { get; set; }
    }

}
