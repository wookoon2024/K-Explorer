namespace WorkFileExplorer.App.Helpers;

public static class AppPaths
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WorkFileExplorer");

    public static string RootDirectory => Root;

    public static string HistoryDbFile => Path.Combine(Root, "history.db");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
    }
}
