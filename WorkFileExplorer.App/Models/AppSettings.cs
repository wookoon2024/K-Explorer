namespace WorkFileExplorer.App.Models;

public sealed class AppSettings
{
    public string LeftStartPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string RightStartPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public int PanelCount { get; set; } = 2;
    public string PanelLayout { get; set; } = "Horizontal";
    public bool RememberSessionTabs { get; set; } = true;
    public bool DefaultTileViewEnabled { get; set; }
    public bool UseExtensionColors { get; set; } = true;
    public bool UsePinnedHighlightColor { get; set; } = true;
    public string ThemeMode { get; set; } = "Black";
    public bool ConfirmBeforeDelete { get; set; } = true;
    public string ConflictPolicyDisplay { get; set; } = "Rename new";
    public string DefaultSearchScope { get; set; } = "Active panel";
    public bool DefaultSearchRecursive { get; set; } = true;
    public List<string> FourPanelPaths { get; set; } = new();
    public List<string> LeftOpenTabPaths { get; set; } = new();
    public List<string> RightOpenTabPaths { get; set; } = new();
    public string FourPanelTabStateJson { get; set; } = string.Empty;
    public int SelectedLeftTabIndex { get; set; }
    public int SelectedRightTabIndex { get; set; }
    public List<string> FavoriteFolders { get; set; } = new();
    public List<string> FavoriteFiles { get; set; } = new();
    public List<string> FavoriteFileCategoryFolders { get; set; } = new();
    public List<string> FavoriteFileCategoryMappings { get; set; } = new();
    public List<string> ExtensionColorOverrides { get; set; } = new();
    public List<string> ThemeColorOverrides { get; set; } = new();
    public List<string> PinnedFolders { get; set; } = new();
    public List<string> PinnedFiles { get; set; } = new();
    public List<string> MessengerDownloadFolders { get; set; } = new();
    public Dictionary<string, string> ItemMemos { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1600;
    public double WindowHeight { get; set; } = 920;
    public bool WindowMaximized { get; set; }
    public string FileListFontFamily { get; set; } = "Malgun Gothic";
    public double FileListFontSize { get; set; } = 13;
    public double FileListRowHeight { get; set; } = 18;
}
