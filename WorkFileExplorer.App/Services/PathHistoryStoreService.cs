using Microsoft.Data.Sqlite;
using WorkFileExplorer.App.Helpers;
using WorkFileExplorer.App.Models;
using WorkFileExplorer.App.Services.Interfaces;

namespace WorkFileExplorer.App.Services;

public sealed class PathHistoryStoreService : IPathHistoryStoreService
{
    private const string LeftPanelKey = "left";
    private const string RightPanelKey = "right";
    private const int MaxHistoryCount = 40;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PathHistoryStoreService()
    {
        AppPaths.EnsureCreated();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.HistoryDbFile
        }.ToString();
        EnsureSchema();
    }

    public async Task<PathHistorySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var left = new List<string>();
            var right = new List<string>();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT panel_key, path
                FROM panel_path_history
                ORDER BY panel_key, position
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var panelKey = reader.GetString(0);
                var path = reader.GetString(1);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (string.Equals(panelKey, LeftPanelKey, StringComparison.OrdinalIgnoreCase))
                {
                    left.Add(path);
                    continue;
                }

                if (string.Equals(panelKey, RightPanelKey, StringComparison.OrdinalIgnoreCase))
                {
                    right.Add(path);
                }
            }

            return new PathHistorySnapshot
            {
                LeftPaths = left,
                RightPaths = right
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyList<string> leftPaths, IReadOnlyList<string> rightPaths, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var left = Normalize(leftPaths);
            var right = Normalize(rightPaths);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)(await connection.BeginTransactionAsync(cancellationToken));

            var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM panel_path_history WHERE panel_key IN ($left, $right);";
            delete.Parameters.AddWithValue("$left", LeftPanelKey);
            delete.Parameters.AddWithValue("$right", RightPanelKey);
            await delete.ExecuteNonQueryAsync(cancellationToken);

            await InsertPanelPathsAsync(connection, transaction, LeftPanelKey, left, cancellationToken);
            await InsertPanelPathsAsync(connection, transaction, RightPanelKey, right, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
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
            CREATE TABLE IF NOT EXISTS panel_path_history (
                panel_key TEXT NOT NULL,
                position INTEGER NOT NULL,
                path TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY(panel_key, position)
            );
            """;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> Normalize(IReadOnlyList<string> paths)
    {
        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxHistoryCount)
            .ToArray();
    }

    private static async Task InsertPanelPathsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string panelKey,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < paths.Count; index++)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO panel_path_history (panel_key, position, path, updated_utc)
                VALUES ($panel_key, $position, $path, $updated_utc);
                """;
            command.Parameters.AddWithValue("$panel_key", panelKey);
            command.Parameters.AddWithValue("$position", index);
            command.Parameters.AddWithValue("$path", paths[index]);
            command.Parameters.AddWithValue("$updated_utc", DateTime.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
