using WorkFileExplorer.App.Models;
using WorkFileExplorer.App.Services.Interfaces;

namespace WorkFileExplorer.App.Services;

public sealed class QuickAccessService : IQuickAccessService
{
    public Task<IReadOnlyList<QuickAccessItem>> GetItemsAsync(AppSettings settings, UsageSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var list = new List<QuickAccessItem>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pinnedFolderSet = settings.PinnedFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directoryExistsCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        bool DirectoryExistsCached(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (directoryExistsCache.TryGetValue(path, out var exists))
            {
                return exists;
            }

            exists = Directory.Exists(path);
            directoryExistsCache[path] = exists;
            return exists;
        }

        void TryAddQuickAccessItem(QuickAccessItem item)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Path))
            {
                return;
            }

            if (!seenPaths.Add(item.Path))
            {
                return;
            }

            list.Add(item);
        }

        AddFixedPath(TryAddQuickAccessItem, "다운로드", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), DirectoryExistsCached);
        AddSpecialFolder(TryAddQuickAccessItem, "문서", Environment.SpecialFolder.MyDocuments, DirectoryExistsCached);
        AddSpecialFolder(TryAddQuickAccessItem, "바탕화면", Environment.SpecialFolder.DesktopDirectory, DirectoryExistsCached);

        foreach (var path in settings.FavoriteFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (DirectoryExistsCached(path))
            {
                TryAddQuickAccessItem(new QuickAccessItem
                {
                    Name = $"★ {Path.GetFileName(path)}",
                    Path = path,
                    Category = "즐겨찾기",
                    IsPinned = pinnedFolderSet.Contains(path)
                });
            }
        }

        foreach (var folder in snapshot.RecentFolders.Take(5))
        {
            if (DirectoryExistsCached(folder.Path))
            {
                TryAddQuickAccessItem(new QuickAccessItem
                {
                    Name = folder.Name,
                    Path = folder.Path,
                    Category = "최근 폴더",
                    IsPinned = folder.IsPinned
                });
            }
        }

        foreach (var folder in snapshot.FrequentFolders.Take(8))
        {
            if (DirectoryExistsCached(folder.Path))
            {
                TryAddQuickAccessItem(new QuickAccessItem
                {
                    Name = folder.Name,
                    Path = folder.Path,
                    Category = "자주 사용하는 폴더",
                    IsPinned = folder.IsPinned
                });
            }
        }

        foreach (var messengerPath in DiscoverMessengerFolders(settings))
        {
            if (DirectoryExistsCached(messengerPath))
            {
                TryAddQuickAccessItem(new QuickAccessItem
                {
                    Name = Path.GetFileName(messengerPath),
                    Path = messengerPath,
                    Category = "메신저 다운로드",
                    IsPinned = true
                });
            }
        }

        var deduped = list
            .OrderBy(static item => CategoryRank(item.Category))
            .ThenByDescending(static item => item.IsPinned)
            .ThenBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyList<QuickAccessItem>>(deduped);
    }

    private static IEnumerable<string> DiscoverMessengerFolders(AppSettings settings)
    {
        foreach (var configured in settings.MessengerDownloadFolders)
        {
            yield return configured;
        }

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(downloads))
        {
            yield break;
        }

        var keywords = new[] { "kakao", "teams", "slack", "discord", "line", "zoom" };
        foreach (var directory in Directory.EnumerateDirectories(downloads))
        {
            var folderName = Path.GetFileName(directory);
            if (keywords.Any(keyword => folderName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                yield return directory;
            }
        }
    }

    private static int CategoryRank(string category) => category switch
    {
        "즐겨찾기" => 0,
        "메신저 다운로드" => 1,
        "다운로드" => 2,
        "문서" => 3,
        "바탕화면" => 4,
        "최근 폴더" => 5,
        "자주 사용하는 폴더" => 6,
        _ => 10
    };

    private static void AddFixedPath(Action<QuickAccessItem> addItem, string name, string path, Func<string, bool>? directoryExists = null)
    {
        directoryExists ??= Directory.Exists;
        if (!directoryExists(path))
        {
            return;
        }

        addItem(new QuickAccessItem
        {
            Name = name,
            Path = path,
            Category = name,
            IsPinned = false
        });
    }

    private static void AddSpecialFolder(
        Action<QuickAccessItem> addItem,
        string name,
        Environment.SpecialFolder specialFolder,
        Func<string, bool>? directoryExists = null,
        string? fallback = null)
    {
        directoryExists ??= Directory.Exists;
        var path = Environment.GetFolderPath(specialFolder);
        if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(fallback))
        {
            path = fallback;
        }

        if (directoryExists(path))
        {
            addItem(new QuickAccessItem
            {
                Name = name,
                Path = path,
                Category = name,
                IsPinned = false
            });
        }
    }
}
