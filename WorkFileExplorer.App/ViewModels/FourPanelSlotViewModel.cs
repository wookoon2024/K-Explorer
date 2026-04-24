using System.Collections.ObjectModel;

namespace WorkFileExplorer.App.ViewModels;

public sealed class FourPanelSlotViewModel : ObservableObject
{
    private readonly PanelViewModel _fallbackPanel = new();
    private PanelTabViewModel? _selectedTab;
    private bool _isActive;
    private string _freeSpaceText = "-";
    private bool _isTerminalVisible;
    private string _terminalOutput = string.Empty;

    public FourPanelSlotViewModel(string slotKey, string initialPath)
    {
        SlotKey = slotKey;
        var tab = new PanelTabViewModel($"{slotKey}1", new PanelViewModel
        {
            CurrentPath = initialPath
        });
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    public string SlotKey { get; }

    public ObservableCollection<PanelTabViewModel> Tabs { get; } = new();

    public PanelTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!SetProperty(ref _selectedTab, value))
            {
                return;
            }

            OnPropertyChanged(nameof(Panel));
        }
    }

    public PanelViewModel Panel => SelectedTab?.Panel ?? _fallbackPanel;

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public string FreeSpaceText
    {
        get => _freeSpaceText;
        set => SetProperty(ref _freeSpaceText, value);
    }

    public bool IsTerminalVisible
    {
        get => _isTerminalVisible;
        set => SetProperty(ref _isTerminalVisible, value);
    }

    public string TerminalOutput
    {
        get => _terminalOutput;
        set => SetProperty(ref _terminalOutput, value);
    }

    public PanelTabViewModel AddTab(string path)
    {
        var tab = new PanelTabViewModel($"{SlotKey}{Tabs.Count + 1}", new PanelViewModel
        {
            CurrentPath = path
        });
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    public bool CloseCurrentTab()
    {
        if (Tabs.Count <= 1 || SelectedTab is null)
        {
            return false;
        }

        var index = Tabs.IndexOf(SelectedTab);
        Tabs.Remove(SelectedTab);
        var next = Math.Clamp(index - 1, 0, Tabs.Count - 1);
        SelectedTab = Tabs[next];
        return true;
    }
}
