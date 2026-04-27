using Microsoft.Data.Sqlite;
using WorkFileExplorer.App.Helpers;
using WorkFileExplorer.App.Models;
using WorkFileExplorer.App.Services.Interfaces;

namespace WorkFileExplorer.App.Services;

public sealed class SettingsStorageService : ISettingsStorageService
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _cachedValues;
    private Dictionary<string, List<string>>? _cachedLists;
    private Dictionary<string, string>? _cachedMemos;
    private bool _cacheInitialized;

    public SettingsStorageService()
    {
        AppPaths.EnsureCreated();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.HistoryDbFile
        }.ToString();
        EnsureSchema();
    }

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = new AppSettings();
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var values = await LoadKeyValuesAsync(connection, cancellationToken);
            settings.LeftStartPath = GetOrDefault(values, "left_start_path", settings.LeftStartPath);
            settings.RightStartPath = GetOrDefault(values, "right_start_path", settings.RightStartPath);
            settings.PanelCount = Math.Clamp(ParseInt(GetOrDefault(values, "panel_count", "2")), 2, 4);
            settings.PanelLayout = GetOrDefault(values, "panel_layout", "Horizontal");
            settings.RememberSessionTabs = ParseBool(GetOrDefault(values, "remember_session_tabs", "1"), defaultValue: true);
            settings.DefaultTileViewEnabled = ParseBool(GetOrDefault(values, "default_tile_view_enabled", "0"), defaultValue: false);
            settings.UseExtensionColors = ParseBool(GetOrDefault(values, "use_extension_colors", "1"), defaultValue: true);
            settings.UsePinnedHighlightColor = ParseBool(GetOrDefault(values, "use_pinned_highlight_color", "1"), defaultValue: true);
            settings.ThemeMode = GetOrDefault(values, "theme_mode", settings.ThemeMode);
            settings.ConfirmBeforeDelete = ParseBool(GetOrDefault(values, "confirm_before_delete", "1"), defaultValue: true);
            settings.ConflictPolicyDisplay = GetOrDefault(values, "conflict_policy_display", settings.ConflictPolicyDisplay);
            settings.DefaultSearchScope = GetOrDefault(values, "default_search_scope", settings.DefaultSearchScope);
            settings.DefaultSearchRecursive = ParseBool(GetOrDefault(values, "default_search_recursive", "1"), defaultValue: true);
            settings.FourPanelTabStateJson = GetOrDefault(values, "four_panel_tab_state_json", string.Empty);
            settings.SelectedLeftTabIndex = ParseInt(GetOrDefault(values, "selected_left_tab_index", "0"));
            settings.SelectedRightTabIndex = ParseInt(GetOrDefault(values, "selected_right_tab_index", "0"));
            settings.WindowLeft = ParseDouble(GetOrDefault(values, "window_left", "NaN"), settings.WindowLeft);
            settings.WindowTop = ParseDouble(GetOrDefault(values, "window_top", "NaN"), settings.WindowTop);
            settings.WindowWidth = ParseDouble(GetOrDefault(values, "window_width", settings.WindowWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)), settings.WindowWidth);
            settings.WindowHeight = ParseDouble(GetOrDefault(values, "window_height", settings.WindowHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)), settings.WindowHeight);
            settings.WindowMaximized = ParseBool(GetOrDefault(values, "window_maximized", "0"), defaultValue: false);
            settings.FileListFontFamily = GetOrDefault(values, "file_list_font_family", settings.FileListFontFamily);
            settings.FileListFontSize = ParseDouble(GetOrDefault(values, "file_list_font_size", settings.FileListFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)), settings.FileListFontSize);
            settings.FileListRowHeight = ParseDouble(GetOrDefault(values, "file_list_row_height", settings.FileListRowHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)), settings.FileListRowHeight);

            var lists = await LoadListsAsync(connection, cancellationToken);
            settings.LeftOpenTabPaths = lists.TryGetValue("left_open_tab_paths", out var leftTabs) ? leftTabs : new List<string>();
            settings.RightOpenTabPaths = lists.TryGetValue("right_open_tab_paths", out var rightTabs) ? rightTabs : new List<string>();
            settings.FourPanelPaths = lists.TryGetValue("four_panel_paths", out var fourPanelPaths) ? fourPanelPaths : new List<string>();
            settings.FavoriteFolders = lists.TryGetValue("favorite_folders", out var favoriteFolders) ? favoriteFolders : new List<string>();
            settings.FavoriteFiles = lists.TryGetValue("favorite_files", out var favoriteFiles) ? favoriteFiles : new List<string>();
            settings.FavoriteFileCategoryFolders = lists.TryGetValue("favorite_file_category_folders", out var favoriteFileCategoryFolders) ? favoriteFileCategoryFolders : new List<string>();
            settings.FavoriteFileCategoryMappings = lists.TryGetValue("favorite_file_category_mappings", out var favoriteFileCategoryMappings) ? favoriteFileCategoryMappings : new List<string>();
            settings.ExtensionColorOverrides = lists.TryGetValue("extension_color_overrides", out var extensionColorOverrides) ? extensionColorOverrides : new List<string>();
            settings.ThemeColorOverrides = lists.TryGetValue("theme_color_overrides", out var themeColorOverrides) ? themeColorOverrides : new List<string>();
            settings.PinnedFolders = lists.TryGetValue("pinned_folders", out var pinnedFolders) ? pinnedFolders : new List<string>();
            settings.PinnedFiles = lists.TryGetValue("pinned_files", out var pinnedFiles) ? pinnedFiles : new List<string>();
            settings.MessengerDownloadFolders = lists.TryGetValue("messenger_download_folders", out var messengerFolders) ? messengerFolders : new List<string>();
            settings.ItemMemos = await LoadItemMemosAsync(connection, cancellationToken);

            return settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var tx = (SqliteTransaction)(await connection.BeginTransactionAsync(cancellationToken));

            if (!_cacheInitialized || _cachedValues is null || _cachedLists is null || _cachedMemos is null)
            {
                _cachedValues = await LoadKeyValuesAsync(connection, cancellationToken);
                _cachedLists = await LoadListsAsync(connection, cancellationToken);
                _cachedMemos = await LoadItemMemosAsync(connection, cancellationToken);
                _cacheInitialized = true;
            }

            var currentValues = _cachedValues;
            var currentLists = _cachedLists;
            var currentMemos = _cachedMemos;

            var targetValues = BuildSettingValues(settings);
            foreach (var pair in targetValues)
            {
                if (currentValues.TryGetValue(pair.Key, out var existing) &&
                    string.Equals(existing, pair.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                await UpsertSettingAsync(connection, tx, pair.Key, pair.Value, cancellationToken);
            }

            foreach (var staleKey in currentValues.Keys.Where(key => !targetValues.ContainsKey(key)).ToArray())
            {
                await DeleteSettingAsync(connection, tx, staleKey, cancellationToken);
            }

            var targetLists = BuildSettingLists(settings);
            foreach (var pair in targetLists)
            {
                if (currentLists.TryGetValue(pair.Key, out var existingList) &&
                    existingList.SequenceEqual(pair.Value, StringComparer.Ordinal))
                {
                    continue;
                }

                await DeleteListByKeyAsync(connection, tx, pair.Key, cancellationToken);
                await InsertListAsync(connection, tx, pair.Key, pair.Value, cancellationToken, keepDuplicates: true);
            }

            foreach (var staleListKey in currentLists.Keys.Where(key => !targetLists.ContainsKey(key)).ToArray())
            {
                await DeleteListByKeyAsync(connection, tx, staleListKey, cancellationToken);
            }

            var targetMemos = NormalizeItemMemos(settings.ItemMemos);
            foreach (var pair in targetMemos)
            {
                if (currentMemos.TryGetValue(pair.Key, out var existingMemo) &&
                    string.Equals(existingMemo, pair.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                await UpsertItemMemoAsync(connection, tx, pair.Key, pair.Value, cancellationToken);
            }

            foreach (var staleMemoPath in currentMemos.Keys.Where(path => !targetMemos.ContainsKey(path)).ToArray())
            {
                await DeleteItemMemoAsync(connection, tx, staleMemoPath, cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            _cachedValues = new Dictionary<string, string>(targetValues, StringComparer.OrdinalIgnoreCase);
            _cachedLists = CloneLists(targetLists);
            _cachedMemos = new Dictionary<string, string>(targetMemos, StringComparer.OrdinalIgnoreCase);
            _cacheInitialized = true;
        }
        catch
        {
            _cacheInitialized = false;
            _cachedValues = null;
            _cachedLists = null;
            _cachedMemos = null;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS app_settings (
                setting_key TEXT NOT NULL PRIMARY KEY,
                setting_value TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS app_settings_list (
                list_key TEXT NOT NULL,
                position INTEGER NOT NULL,
                list_value TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (list_key, position)
            );

            CREATE TABLE IF NOT EXISTS app_item_memos (
                item_path TEXT NOT NULL PRIMARY KEY,
                memo_text TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static async Task<Dictionary<string, string>> LoadKeyValuesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_key, setting_value FROM app_settings;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var value = reader.GetString(1);
            result[key] = value;
        }

        return result;
    }

    private static async Task<Dictionary<string, List<string>>> LoadListsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT list_key, list_value
            FROM app_settings_list
            ORDER BY list_key, position;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var value = reader.GetString(1);
            if (!result.TryGetValue(key, out var list))
            {
                list = new List<string>();
                result[key] = list;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                list.Add(value);
            }
        }

        return result;
    }

    private static async Task UpsertSettingAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string key,
        string? value,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO app_settings (setting_key, setting_value, updated_utc)
            VALUES ($key, $value, $updated_utc)
            ON CONFLICT(setting_key) DO UPDATE SET
                setting_value = excluded.setting_value,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value ?? string.Empty);
        command.Parameters.AddWithValue("$updated_utc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertListAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string listKey,
        IEnumerable<string>? values,
        CancellationToken cancellationToken,
        bool keepDuplicates = false)
    {
        var source = (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value));

        var normalized = keepDuplicates
            ? source.ToArray()
            : source.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        for (var index = 0; index < normalized.Length; index++)
        {
            var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO app_settings_list (list_key, position, list_value, updated_utc)
                VALUES ($list_key, $position, $list_value, $updated_utc);
                """;
            command.Parameters.AddWithValue("$list_key", listKey);
            command.Parameters.AddWithValue("$position", index);
            command.Parameters.AddWithValue("$list_value", normalized[index]);
            command.Parameters.AddWithValue("$updated_utc", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<Dictionary<string, string>> LoadItemMemosAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT item_path, memo_text FROM app_item_memos;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var path = reader.GetString(0);
            var memo = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(memo))
            {
                continue;
            }

            result[path] = memo;
        }

        return result;
    }

    private static async Task UpsertItemMemoAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string path,
        string memo,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO app_item_memos (item_path, memo_text, updated_utc)
            VALUES ($item_path, $memo_text, $updated_utc)
            ON CONFLICT(item_path) DO UPDATE SET
                memo_text = excluded.memo_text,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$item_path", path);
        command.Parameters.AddWithValue("$memo_text", memo);
        command.Parameters.AddWithValue("$updated_utc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteSettingAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string key,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "DELETE FROM app_settings WHERE setting_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteListByKeyAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string listKey,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "DELETE FROM app_settings_list WHERE list_key = $list_key;";
        command.Parameters.AddWithValue("$list_key", listKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteItemMemoAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string itemPath,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "DELETE FROM app_item_memos WHERE item_path = $item_path;";
        command.Parameters.AddWithValue("$item_path", itemPath);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Dictionary<string, string> BuildSettingValues(AppSettings settings)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["left_start_path"] = settings.LeftStartPath ?? string.Empty,
            ["right_start_path"] = settings.RightStartPath ?? string.Empty,
            ["panel_count"] = settings.PanelCount.ToString(),
            ["panel_layout"] = settings.PanelLayout ?? string.Empty,
            ["remember_session_tabs"] = settings.RememberSessionTabs ? "1" : "0",
            ["default_tile_view_enabled"] = settings.DefaultTileViewEnabled ? "1" : "0",
            ["use_extension_colors"] = settings.UseExtensionColors ? "1" : "0",
            ["use_pinned_highlight_color"] = settings.UsePinnedHighlightColor ? "1" : "0",
            ["theme_mode"] = settings.ThemeMode ?? string.Empty,
            ["confirm_before_delete"] = settings.ConfirmBeforeDelete ? "1" : "0",
            ["conflict_policy_display"] = settings.ConflictPolicyDisplay ?? string.Empty,
            ["default_search_scope"] = settings.DefaultSearchScope ?? string.Empty,
            ["default_search_recursive"] = settings.DefaultSearchRecursive ? "1" : "0",
            ["four_panel_tab_state_json"] = settings.FourPanelTabStateJson ?? string.Empty,
            ["selected_left_tab_index"] = settings.SelectedLeftTabIndex.ToString(),
            ["selected_right_tab_index"] = settings.SelectedRightTabIndex.ToString(),
            ["window_left"] = settings.WindowLeft.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["window_top"] = settings.WindowTop.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["window_width"] = settings.WindowWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["window_height"] = settings.WindowHeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["window_maximized"] = settings.WindowMaximized ? "1" : "0",
            ["file_list_font_family"] = settings.FileListFontFamily ?? string.Empty,
            ["file_list_font_size"] = settings.FileListFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["file_list_row_height"] = settings.FileListRowHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static Dictionary<string, List<string>> BuildSettingLists(AppSettings settings)
    {
        return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["left_open_tab_paths"] = NormalizeListValues(settings.LeftOpenTabPaths, keepDuplicates: true),
            ["right_open_tab_paths"] = NormalizeListValues(settings.RightOpenTabPaths, keepDuplicates: true),
            ["four_panel_paths"] = NormalizeListValues(settings.FourPanelPaths, keepDuplicates: true),
            ["favorite_folders"] = NormalizeListValues(settings.FavoriteFolders, keepDuplicates: false),
            ["favorite_files"] = NormalizeListValues(settings.FavoriteFiles, keepDuplicates: false),
            ["favorite_file_category_folders"] = NormalizeListValues(settings.FavoriteFileCategoryFolders, keepDuplicates: false),
            ["favorite_file_category_mappings"] = NormalizeListValues(settings.FavoriteFileCategoryMappings, keepDuplicates: true),
            ["extension_color_overrides"] = NormalizeListValues(settings.ExtensionColorOverrides, keepDuplicates: false),
            ["theme_color_overrides"] = NormalizeListValues(settings.ThemeColorOverrides, keepDuplicates: false),
            ["pinned_folders"] = NormalizeListValues(settings.PinnedFolders, keepDuplicates: false),
            ["pinned_files"] = NormalizeListValues(settings.PinnedFiles, keepDuplicates: false),
            ["messenger_download_folders"] = NormalizeListValues(settings.MessengerDownloadFolders, keepDuplicates: false)
        };
    }

    private static List<string> NormalizeListValues(IEnumerable<string>? values, bool keepDuplicates)
    {
        var source = (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value));

        return keepDuplicates
            ? source.ToList()
            : source.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, string> NormalizeItemMemos(IReadOnlyDictionary<string, string>? itemMemos)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (itemMemos is null || itemMemos.Count == 0)
        {
            return result;
        }

        foreach (var pair in itemMemos)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static Dictionary<string, List<string>> CloneLists(IReadOnlyDictionary<string, List<string>> source)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            result[pair.Key] = pair.Value.ToList();
        }

        return result;
    }

    private static int ParseInt(string value)
    {
        return int.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static double ParseDouble(string value, double defaultValue)
    {
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static bool ParseBool(string value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return defaultValue;
    }

    private static string GetOrDefault(IReadOnlyDictionary<string, string> values, string key, string fallback)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    }
}
