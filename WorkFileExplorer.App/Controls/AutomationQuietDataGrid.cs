using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace WorkFileExplorer.App.Controls;

/// <summary>
/// DataGrid that exposes itself to UI Automation as a single opaque element
/// (no per-row/per-cell automation peers).
///
/// Once a global UIA client (Korean IME/ctfmon, TextInputHost, screen readers,
/// PowerToys 등) starts listening — which Windows triggers the first time any
/// menu popup opens in this process — the default DataGridAutomationPeer
/// recreates row/cell peers and raises structure-changed events on every
/// ItemsSource rebind, making tab switching visibly lag. Suppressing child
/// peers removes that cost, at the expense of per-row screen-reader access.
/// </summary>
public class AutomationQuietDataGrid : DataGrid
{
    protected override AutomationPeer OnCreateAutomationPeer() => new QuietPeer(this);

    private sealed class QuietPeer : FrameworkElementAutomationPeer
    {
        public QuietPeer(AutomationQuietDataGrid owner) : base(owner)
        {
        }

        protected override List<AutomationPeer> GetChildrenCore() => new();

        protected override string GetClassNameCore() => "DataGrid";

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataGrid;
    }
}
