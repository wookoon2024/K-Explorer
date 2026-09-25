using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WorkFileExplorer.App.Helpers;
using WorkFileExplorer.App.ViewModels;

namespace WorkFileExplorer.App.Controls;

public sealed class PanelVisualCacheEntry : INotifyPropertyChanged
{
    private bool _isSideActive;
    private bool _isCurrent;
    private long _lastTouched;

    public PanelVisualCacheEntry(PanelItemsCacheHost host, PanelTabViewModel tab, bool isLeft)
    {
        Host = host;
        Tab = tab;
        IsLeft = isLeft;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<PanelVisualCacheEntry>? VisualReady;

    public bool NeedsStateRestore { get; set; }

    public PanelItemsCacheHost Host { get; }

    public PanelTabViewModel Tab { get; }

    public PanelViewModel Panel => Tab.Panel;

    public bool IsLeft { get; }

    public bool IsSideActive
    {
        get => _isSideActive;
        private set
        {
            if (_isSideActive == value)
            {
                return;
            }

            _isSideActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSideActive)));
        }
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        private set
        {
            if (_isCurrent == value)
            {
                return;
            }

            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public FrameworkElement? Root { get; private set; }

    public ContentPresenter? Presenter { get; private set; }

    public AutomationQuietDataGrid? Grid { get; private set; }

    public ListBox? List { get; private set; }

    public bool IsVisualReady { get; private set; }

    public long LastTouched
    {
        get => _lastTouched;
        private set
        {
            _lastTouched = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastTouched)));
        }
    }

    public void Touch() => LastTouched = Environment.TickCount64;

    public void InitializeSideActive(bool active) => SetSideActive(active);

    public void SetSideActive(bool active) => IsSideActive = active;

    public void SetCurrent(bool current) => IsCurrent = current;

    public bool EnsureVisual()
    {
        if (IsVisualReady)
        {
            return true;
        }

        if (Presenter is null)
        {
            return false;
        }

        Presenter.Visibility = Visibility.Visible;
        Presenter.UpdateLayout();
        var ready = TryCompleteVisual();
        if (!ready)
        {
            Presenter.Visibility = IsCurrent ? Visibility.Collapsed : Visibility.Hidden;
            if (Root is not null)
            {
                Root.Visibility = IsCurrent ? Visibility.Collapsed : Visibility.Hidden;
                Root.IsHitTestVisible = false;
            }
        }

        return ready;
    }

    public void BeginPrewarm()
    {
        if (!EnsureVisual())
        {
            return;
        }

        if (!IsCurrent)
        {
            Presenter!.Visibility = Visibility.Hidden;
            Root!.Visibility = Visibility.Hidden;
            Root.IsHitTestVisible = false;
        }
    }

    public void Attach(ContentPresenter presenter)
    {
        Presenter = presenter;
        presenter.Loaded += OnPresenterLoaded;
        presenter.Unloaded += OnPresenterUnloaded;
    }

    public bool TryCompleteVisual()
    {
        if (Presenter is null || IsVisualReady)
        {
            return IsVisualReady;
        }

        Presenter.ApplyTemplate();
        Root = FindDescendant<FrameworkElement>(Presenter);
        Grid = FindDescendant<AutomationQuietDataGrid>(Presenter);
        List = FindDescendant<ListBox>(Presenter);
        if (Root is null || Grid is null || List is null)
        {
            return false;
        }

        PanelUi.SetPanelEntry(Root, this);
        PanelUi.SetPanelEntry(Grid, this);
        PanelUi.SetPanelEntry(List, this);
        IsVisualReady = true;
        Root.Visibility = IsCurrent ? Visibility.Visible : Visibility.Hidden;
        if (Presenter is not null)
        {
            Presenter.Visibility = IsCurrent ? Visibility.Visible : Visibility.Hidden;
        }
        Root.IsHitTestVisible = IsCurrent;
        VisualReady?.Invoke(this, this);
        return true;
    }

    public void Detach()
    {
        if (Presenter is not null)
        {
            Presenter.Loaded -= OnPresenterLoaded;
            Presenter.Unloaded -= OnPresenterUnloaded;
        }

        if (Root is not null)
        {
            PanelUi.SetPanelEntry(Root, null);
        }

        if (Grid is not null)
        {
            PanelUi.SetPanelEntry(Grid, null);
        }

        if (List is not null)
        {
            PanelUi.SetPanelEntry(List, null);
        }

        Presenter = null;
        Root = null;
        Grid = null;
        List = null;
        IsVisualReady = false;
        NeedsStateRestore = false;
    }

    private void OnPresenterLoaded(object sender, RoutedEventArgs e)
    {
        Presenter?.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                TryCompleteVisual();
                Root?.UpdateLayout();
            });
    }

    private void OnPresenterUnloaded(object sender, RoutedEventArgs e)
    {
    }

    private static T? FindDescendant<T>(DependencyObject? current) where T : DependencyObject
    {
        if (current is null)
        {
            return null;
        }

        var count = VisualTreeHelper.GetChildrenCount(current);
        for (var index = 0; index < count; index++)
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
}

public sealed class PanelItemsCacheHost : Grid
{
    public static readonly DependencyProperty TabsProperty = DependencyProperty.Register(
        nameof(Tabs),
        typeof(IEnumerable),
        typeof(PanelItemsCacheHost),
        new PropertyMetadata(null, OnTabsChanged));

    public static readonly DependencyProperty ActiveTabProperty = DependencyProperty.Register(
        nameof(ActiveTab),
        typeof(PanelTabViewModel),
        typeof(PanelItemsCacheHost),
        new PropertyMetadata(null, OnActiveTabChanged));

    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate),
        typeof(DataTemplate),
        typeof(PanelItemsCacheHost),
        new PropertyMetadata(null, OnItemTemplateChanged));

    public static readonly DependencyProperty IsLeftProperty = DependencyProperty.Register(
        nameof(IsLeft),
        typeof(bool),
        typeof(PanelItemsCacheHost),
        new PropertyMetadata(false, OnSideChanged));

    public static readonly DependencyProperty IsSideActiveProperty = DependencyProperty.Register(
        nameof(IsSideActive),
        typeof(bool),
        typeof(PanelItemsCacheHost),
        new PropertyMetadata(false, OnSideActiveChanged));

    private readonly Dictionary<PanelTabViewModel, PanelVisualCacheEntry> _entries = [];
    private INotifyCollectionChanged? _tabsNotifications;
    private PanelVisualCacheEntry? _activeEntry;
    private bool _attachmentScheduled;

    public IEnumerable? Tabs
    {
        get => (IEnumerable?)GetValue(TabsProperty);
        set => SetValue(TabsProperty, value);
    }

    public PanelTabViewModel? ActiveTab
    {
        get => (PanelTabViewModel?)GetValue(ActiveTabProperty);
        set => SetValue(ActiveTabProperty, value);
    }

    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public bool IsLeft
    {
        get => (bool)GetValue(IsLeftProperty);
        set => SetValue(IsLeftProperty, value);
    }

    public bool IsSideActive
    {
        get => (bool)GetValue(IsSideActiveProperty);
        set => SetValue(IsSideActiveProperty, value);
    }

    public IReadOnlyList<PanelVisualCacheEntry> Entries => _entries.Values.ToArray();

    public PanelVisualCacheEntry? ActiveEntry => _activeEntry;

    public event EventHandler<PanelVisualCacheEntry>? EntryRemoved;

    public event EventHandler<PanelVisualCacheEntry>? VisualReady;

    private void OnEntryVisualReady(object? sender, PanelVisualCacheEntry entry)
    {
        VisualReady?.Invoke(this, entry);
    }

    public PanelVisualCacheEntry? GetEntry(PanelTabViewModel? tab) =>
        tab is not null && _entries.TryGetValue(tab, out var entry) ? entry : null;

    public PanelVisualCacheEntry? GetEntry(PanelViewModel? panel) =>
        _entries.Values.FirstOrDefault(entry => ReferenceEquals(entry.Panel, panel));

    public PanelVisualCacheEntry? EnsureEntry(PanelTabViewModel tab)
    {
        if (_entries.TryGetValue(tab, out var existing))
        {
            existing.Touch();
            return existing;
        }

        var entry = new PanelVisualCacheEntry(this, tab, IsLeft);
        entry.InitializeSideActive(IsSideActive);
        var presenter = new ContentPresenter
        {
            Content = entry,
            ContentTemplate = ItemTemplate,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        entry.Attach(presenter);
        entry.VisualReady += OnEntryVisualReady;
        _entries.Add(tab, entry);
        Children.Add(presenter);
        entry.Touch();
        if (ReferenceEquals(tab, ActiveTab))
        {
            Activate(tab);
        }

        return entry;
    }

    public void Activate(PanelTabViewModel? tab)
    {
        if (tab is null)
        {
            SetActiveEntry(null);
            return;
        }

        var entry = EnsureEntry(tab);
        SetActiveEntry(entry);
        if (entry is not null)
        {
            entry.EnsureVisual();
            SetActiveEntry(entry);
        }
    }

    public void Prewarm(PanelTabViewModel tab)
    {
        if (Tabs is not IEnumerable tabs || !tabs.Cast<PanelTabViewModel>().Contains(tab))
        {
            return;
        }

        var entry = EnsureEntry(tab);
        if (entry is null)
        {
            return;
        }

        if (!entry.IsCurrent && !entry.IsVisualReady)
        {
            entry.NeedsStateRestore = true;
        }

        entry.BeginPrewarm();
        entry.Touch();
        if (!entry.IsCurrent && entry.Root is not null)
        {
            entry.Root.Visibility = Visibility.Hidden;
        }
    }

    public void Remove(PanelTabViewModel tab)
    {
        if (!_entries.TryGetValue(tab, out var entry))
        {
            return;
        }

        if (ReferenceEquals(_activeEntry, entry))
        {
            _activeEntry = null;
        }

        _entries.Remove(tab);
        entry.VisualReady -= OnEntryVisualReady;
        if (entry.Presenter is not null)
        {
            Children.Remove(entry.Presenter);
        }

        EntryRemoved?.Invoke(this, entry);
        entry.Detach();
    }

    public void Clear()
    {
        foreach (var tab in _entries.Keys.ToArray())
        {
            Remove(tab);
        }
    }

    public void SetEntriesSideActive(bool active)
    {
        foreach (var entry in _entries.Values)
        {
            entry.SetSideActive(active);
        }
    }

    private void SetActiveEntry(PanelVisualCacheEntry? entry)
    {
        if (!ReferenceEquals(_activeEntry, entry) && _activeEntry is not null)
        {
            _activeEntry.SetCurrent(false);
            if (_activeEntry.Root is not null)
            {
                _activeEntry.Root.Visibility = _activeEntry.IsVisualReady
                    ? Visibility.Hidden
                    : Visibility.Collapsed;
                _activeEntry.Root.IsHitTestVisible = false;
            }

            if (_activeEntry.Presenter is not null)
            {
                _activeEntry.Presenter.Visibility = _activeEntry.IsVisualReady
                    ? Visibility.Hidden
                    : Visibility.Collapsed;
            }
        }

        _activeEntry = entry;
        if (entry is null)
        {
            return;
        }

        entry.Touch();
        entry.SetSideActive(IsSideActive);
        entry.SetCurrent(true);
        if (entry.Root is not null)
        {
            entry.Root.Visibility = entry.IsVisualReady ? Visibility.Visible : Visibility.Collapsed;
            entry.Root.IsHitTestVisible = entry.IsVisualReady;
        }

        if (entry.Presenter is not null)
        {
            entry.Presenter.Visibility = entry.IsVisualReady ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void OnTabsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var host = (PanelItemsCacheHost)d;
        host.AttachTabsNotifications(e.OldValue as IEnumerable, e.NewValue as IEnumerable);
        host.ScheduleSync();
    }

    private static void OnActiveTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((PanelItemsCacheHost)d).Activate((PanelTabViewModel?)e.NewValue);
    }

    private static void OnItemTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var host = (PanelItemsCacheHost)d;
        foreach (var entry in host._entries.Values)
        {
            if (entry.Presenter is not null)
            {
                entry.Presenter.ContentTemplate = (DataTemplate?)e.NewValue;
                entry.EnsureVisual();
            }
        }
    }

    private static void OnSideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        foreach (var entry in ((PanelItemsCacheHost)d)._entries.Values)
        {
            entry.SetSideActive(((PanelItemsCacheHost)d).IsSideActive);
        }
    }

    private static void OnSideActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((PanelItemsCacheHost)d).SetEntriesSideActive((bool)e.NewValue);
    }

    private void AttachTabsNotifications(IEnumerable? oldTabs, IEnumerable? newTabs)
    {
        if (_tabsNotifications is not null)
        {
            _tabsNotifications.CollectionChanged -= OnTabsCollectionChanged;
            _tabsNotifications = null;
        }

        _tabsNotifications = newTabs as INotifyCollectionChanged;
        if (_tabsNotifications is not null)
        {
            _tabsNotifications.CollectionChanged += OnTabsCollectionChanged;
        }
    }

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScheduleSync();
    }

    private void ScheduleSync()
    {
        if (_attachmentScheduled)
        {
            return;
        }

        _attachmentScheduled = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                _attachmentScheduled = false;
                SyncTabs();
            });
    }

    private void SyncTabs()
    {
        if (Tabs is not IEnumerable tabs)
        {
            return;
        }

        var current = tabs.Cast<PanelTabViewModel>().ToHashSet();
        foreach (var tab in _entries.Keys.Where(tab => !current.Contains(tab)).ToArray())
        {
            Remove(tab);
        }

        if (ActiveTab is not null && current.Contains(ActiveTab))
        {
            Activate(ActiveTab);
        }
    }
}
