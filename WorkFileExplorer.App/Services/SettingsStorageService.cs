using Microsoft.Data.Sqlite;
using WorkFileExplorer.App.Helpers;
using WorkFileExplorer.App.Models;
using WorkFileExplorer.App.Services.Interfaces;

namespace WorkFileExplorer.App.Services;

public sealed class SettingsStorageService : ISettingsStorageService
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

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

            var lists = await LoadListsAsync(connection, cancellationToken);
            settings.LeftOpenTabPaths = lists.TryGetValue("left_open_tab_paths", out var leftTabs) ? leftTabs : new List<string>();
            settings.RightOpenTabPaths = lists.TryGetValue("right_open_tab_paths", out var rightTabs) ? rightTabs : new List<string>();
            settings.FourPanelPaths = lists.TryGetValue("four_panel_paths", out var fourPanelPaths) ? fourPanelPaths : new List<string>();
            settings.FavoriteFolders = lists.TryGetValue("favorite_folders", out var favoriteFolders) ? favoriteFolders : new List<string>();
            settings.FavoriteFiles = lists.TryGetValue("favorite_files", out var favoriteFiles) ? favoriteFiles : new List<string>();
            settings.FavoriteFileCategoryFolders = lists.TryGetValue("favorite_file_category_folders", out var favoriteFileCategoryFolders) ? favoriteFileCategoryFolders : new List<string>();
            settings.FavoriteFileCategoryMappings = lists.TryGetValue("favorite_file_category_mappings", out var favoriteFileCategoryMappings) ? favoriteFileCategoryMappings : new List<string>();
            settings.ExtensionColorOverrides = lists.TryGetValue("extension_color_overrides", out var extensionColorOverrides) ? extensionColorOverrides : new List<string>();
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

            var clearValues = connection.CreateCommand();
            clearValues.Transaction = tx;
            clearValues.CommandText = "DELETE FROM app_settings;";
            await clearValues.ExecuteNonQueryAsync(cancellationToken);

            var clearLists = connection.CreateCommand();
            clearLists.Transaction = tx;
            clearLists.CommandText = "DELETE FROM app_settings_list;";
            await clearLists.ExecuteNonQueryAsync(cancellationToken);

            var clearMemos = connection.CreateCommand();
            clearMemos.Transaction = tx;
            clearMemos.CommandText = "DELETE FROM app_item_memos;";
            await clearMemos.ExecuteNonQueryAsync(cancellationToken);

            await InsertSettingAsync(connection, tx, "left_start_path", settings.LeftStartPath, cancellationToken);
            await InsertSettingAsync(connection, tx, "right_start_path", settings.RightStartPath, cancellationToken);
            await InsertSettingAsync(connection, tx, "panel_count", settings.PanelCount.ToString(), cancellationToken);
            await InsertSettingAsync(connection, tx, "panel_layout", settings.PanelLayout, cancellationToken);
            await InsertSettingAsync(connection, tx, "remember_session_tabs", settings.RememberSessionTabs ? "1" : "0", cancellationToken);
            await InsertSettingAsync(connection, tx, "default_tile_view_enabled", settings.DefaultTileViewEnabled ? "1" : "0", cancellationToken);
            await InsertSettingAsync(connection, tx, "use_extension_colors", settings.UseExtensionColors ? "1" : "0", cancellationToken);
            await InsertSettingAsync(connection, tx, "use_pinned_highlight_color", settings.UsePinnedHighlightColor ? "1" : "0", cancellationToken);
            await InsertSettingAsync(connection, tx, "confirm_before_delete", settings.ConfirmBeforeDelete ? "1" : "0", cancellationToken);
            await InsertSettingAsync(connection, tx, "conflict_policy_display", settings.ConflictPolicyDisplay, cancellationToken);
            await InsertSettingAsync(connection, tx, "default_search_scope", settings.DefaultSearchScope, cancellationToken);
            await InsertSettingAsync(connection, tx, "default_search_recursive", settings.DefaultSearchRecursive ? "1" : "0", cancellationToken);
            await InsertSettingAsync(connection, tx, "four_panel_tab_state_json", settings.FourPanelTabStateJson, cancellationToken);
            await InsertSettingAsync(connection, tx, "selected_left_tab_index", settings.SelectedLeftTabIndex.ToString(), cancellationToken);
            await InsertSettingAsync(connection, tx, "selected_right_tab_index", settings.SelectedRightTabIndex.ToString(), cancellationToken);
            await InsertSettingAsync(connection, tx, "window_left", settings.WindowLeft.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            await InsertSettingAsync(connection, tx, "window_top", settings.WindowTop.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            await InsertSettingAsync(connection, tx, "window_width", settings.WindowWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            await InsertSettingAsync(connection, tx, "window_height", settings.WindowHeight.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            await InsertSettingAsync(connection, tx, "window_maximized", settings.WindowMaximized ? "1" : "0", cancellationToken);

            await InsertListAsync(connection, tx, "left_open_tab_paths", settings.LeftOpenTabPaths, cancellationToken, keepDuplicates: true);
            await InsertListAsync(connection, tx, "right_open_tab_paths", settings.RightOpenTabPaths, cancellationToken, keepDuplicates: true);
            await InsertListAsync(connection, tx, "four_panel_paths", settings.FourPanelPaths, cancellationToken, keepDuplicates: true);
            await InsertListAsync(connection, tx, "favorite_folders", settings.FavoriteFolders, cancellationToken);
            await InsertListAsync(connection, tx, "favorite_files", settings.FavoriteFiles, cancellationToken);
            await InsertListAsync(connection, tx, "favorite_file_category_folders", settings.FavoriteFileCategoryFolders, cancellationToken);
            await InsertListAsync(connection, tx, "favorite_file_category_mappings", settings.FavoriteFileCategoryMappings, cancellationToken, keepDuplicates: true);
            await InsertListAsync(connection, tx, "extension_color_overrides", settings.ExtensionColorOverrides, cancellationToken);
            await InsertListAsync(connection, tx, "pinned_folders", settings.PinnedFolders, cancellationToken);
            await InsertListAsync(connection, tx, "pinned_files", settings.PinnedFiles, cancellationToken);
            await InsertListAsync(connection, tx, "messenger_download_folders", settings.MessengerDownloadFolders, cancellationToken);
            await InsertItemMemosAsync(connection, tx, settings.ItemMemos, cancellationToken);

            await tx.CommitAsync(cancellationToken);
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

    private static async Task InsertSettingAsync(
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
            VALUES ($key, $value, $updated_utc);
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

    private static async Task InsertItemMemosAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        IReadOnlyDictionary<string, string>? itemMemos,
        CancellationToken cancellationToken)
    {
        if (itemMemos is null || itemMemos.Count == 0)
        {
            return;
        }

        foreach (var pair in itemMemos)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO app_item_memos (item_path, memo_text, updated_utc)
                VALUES ($item_path, $memo_text, $updated_utc);
                """;
            command.Parameters.AddWithValue("$item_path", pair.Key);
            command.Parameters.AddWithValue("$memo_text", pair.Value);
            command.Parameters.AddWithValue("$updated_utc", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
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
