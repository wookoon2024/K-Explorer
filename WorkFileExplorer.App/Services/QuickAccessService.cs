using WorkFileExplorer.App.Models;
using WorkFileExplorer.App.Services.Interfaces;

namespace WorkFileExplorer.App.Services;

public sealed class QuickAccessService : IQuickAccessService
{
    public Task<IReadOnlyList<QuickAccessItem>> GetItemsAsync(AppSettings settings, UsageSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var list = new List<QuickAccessItem>();
        AddFixedPath(list, "다운로드", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        AddSpecialFolder(list, "문서", Environment.SpecialFolder.MyDocuments);
        AddSpecialFolder(list, "바탕화면", Environment.SpecialFolder.DesktopDirectory);

        foreach (var path in settings.FavoriteFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(path))
            {
                list.Add(new QuickAccessItem
                {
                    Name = $"★ {Path.GetFileName(path)}",
                    Path = path,
                    Category = "즐겨찾기",
                    IsPinned = settings.PinnedFolders.Contains(path, StringComparer.OrdinalIgnoreCase)
                });
            }
        }

        foreach (var folder in snapshot.RecentFolders.Take(5))
        {
            if (Directory.Exists(folder.Path))
            {
                list.Add(new QuickAccessItem
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
            if (Directory.Exists(folder.Path))
            {
                list.Add(new QuickAccessItem
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
            if (Directory.Exists(messengerPath))
            {
                list.Add(new QuickAccessItem
                {
                    Name = Path.GetFileName(messengerPath),
                    Path = messengerPath,
                    Category = "메신저 다운로드",
                    IsPinned = true
                });
            }
        }

        var deduped = list
            .GroupBy(static item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
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

    private static void AddFixedPath(List<QuickAccessItem> list, string name, string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        list.Add(new QuickAccessItem
        {
            Name = name,
            Path = path,
            Category = name,
            IsPinned = false
        });
    }

    private static void AddSpecialFolder(List<QuickAccessItem> list, string name, Environment.SpecialFolder specialFolder, string? fallback = null)
    {
        var path = Environment.GetFolderPath(specialFolder);
        if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(fallback))
        {
            path = fallback;
        }

        if (Directory.Exists(path))
        {
            list.Add(new QuickAccessItem
            {
                Name = name,
                Path = path,
                Category = name,
                IsPinned = false
            });
        }
    }
}
