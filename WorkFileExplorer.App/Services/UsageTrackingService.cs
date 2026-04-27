using Microsoft.Data.Sqlite;
using WorkFileExplorer.App.Helpers;
using WorkFileExplorer.App.Models;
using WorkFileExplorer.App.Services.Interfaces;

namespace WorkFileExplorer.App.Services;

public sealed class UsageTrackingService : IUsageTrackingService
{
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly string _connectionString;
    private readonly Dictionary<string, TrackedFolderRecord> _folderRecordMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrackedFileRecord> _fileRecordMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirtyFolderPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirtyFilePaths = new(StringComparer.OrdinalIgnoreCase);
    private UsageData _data;

    public UsageTrackingService()
    {
        AppPaths.EnsureCreated();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.HistoryDbFile
        }.ToString();
        EnsureSchema();
        _data = LoadUsageFromDb();
        RebuildLookupMaps();
    }

    public void RecordFolderAccess(string path, bool pinned)
    {
        var normalized = Normalize(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        lock (_syncRoot)
        {
            if (!_folderRecordMap.TryGetValue(normalized, out var record))
            {
                record = new TrackedFolderRecord
                {
                    Path = normalized,
                    Name = Path.GetFileName(normalized),
                    AccessCount = 1,
                    LastAccessUtc = DateTime.UtcNow,
                    IsPinned = pinned
                };
                _data.FolderRecords.Add(record);
                _folderRecordMap[normalized] = record;
                _dirtyFolderPaths.Add(normalized);
                return;
            }

            record.AccessCount++;
            record.LastAccessUtc = DateTime.UtcNow;
            record.IsPinned = pinned || record.IsPinned;
            if (string.IsNullOrWhiteSpace(record.Name))
            {
                record.Name = Path.GetFileName(normalized);
            }

            _dirtyFolderPaths.Add(normalized);
        }
    }

    public void RecordFileOpen(string path, bool pinned)
    {
        var normalized = Normalize(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        lock (_syncRoot)
        {
            if (!_fileRecordMap.TryGetValue(normalized, out var record))
            {
                record = new TrackedFileRecord
                {
                    Path = normalized,
                    Name = Path.GetFileName(normalized),
                    AccessCount = 1,
                    LastAccessUtc = DateTime.UtcNow,
                    IsPinned = pinned
                };
                _data.FileRecords.Add(record);
                _fileRecordMap[normalized] = record;
                _dirtyFilePaths.Add(normalized);
                return;
            }

            record.AccessCount++;
            record.LastAccessUtc = DateTime.UtcNow;
            record.IsPinned = pinned || record.IsPinned;
            if (string.IsNullOrWhiteSpace(record.Name))
            {
                record.Name = Path.GetFileName(normalized);
            }

            _dirtyFilePaths.Add(normalized);
        }
    }

    public Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            var recentFolders = _data.FolderRecords
                .OrderByDescending(static item => item.LastAccessUtc)
                .Take(20)
                .Select(Clone)
                .ToArray();

            var frequentFolders = _data.FolderRecords
                .OrderByDescending(ScoreFolder)
                .Take(20)
                .Select(Clone)
                .ToArray();

            var recentFiles = _data.FileRecords
                .OrderByDescending(static item => item.LastAccessUtc)
                .Take(30)
                .Select(Clone)
                .ToArray();

            var frequentFiles = _data.FileRecords
                .OrderByDescending(ScoreFile)
                .Take(30)
                .Select(Clone)
                .ToArray();

            return Task.FromResult(new UsageSnapshot
            {
                RecentFolders = recentFolders,
                FrequentFolders = frequentFolders,
                RecentFiles = recentFiles,
                FrequentFiles = frequentFiles
            });
        }
    }

    public async Task PersistAsync(CancellationToken cancellationToken = default)
    {
        await _persistGate.WaitAsync(cancellationToken);
        var dirtyFolderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirtyFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            TrackedFolderRecord[] dirtyFolders;
            TrackedFileRecord[] dirtyFiles;
            lock (_syncRoot)
            {
                if (_dirtyFolderPaths.Count == 0 && _dirtyFilePaths.Count == 0)
                {
                    return;
                }

                dirtyFolderPaths = new HashSet<string>(_dirtyFolderPaths, StringComparer.OrdinalIgnoreCase);
                dirtyFilePaths = new HashSet<string>(_dirtyFilePaths, StringComparer.OrdinalIgnoreCase);

                dirtyFolders = dirtyFolderPaths
                    .Where(path => _folderRecordMap.ContainsKey(path))
                    .Select(path => Clone(_folderRecordMap[path]))
                    .ToArray();

                dirtyFiles = dirtyFilePaths
                    .Where(path => _fileRecordMap.ContainsKey(path))
                    .Select(path => Clone(_fileRecordMap[path]))
                    .ToArray();

                _dirtyFolderPaths.Clear();
                _dirtyFilePaths.Clear();
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var tx = (SqliteTransaction)(await connection.BeginTransactionAsync(cancellationToken));

            foreach (var record in dirtyFolders)
            {
                await UpsertFolderRecordAsync(connection, tx, record, cancellationToken);
            }

            foreach (var record in dirtyFiles)
            {
                await UpsertFileRecordAsync(connection, tx, record, cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            lock (_syncRoot)
            {
                foreach (var path in dirtyFolderPaths)
                {
                    _dirtyFolderPaths.Add(path);
                }

                foreach (var path in dirtyFilePaths)
                {
                    _dirtyFilePaths.Add(path);
                }
            }
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS usage_folder_records (
                path TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                last_access_utc TEXT NOT NULL,
                access_count INTEGER NOT NULL,
                is_pinned INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS usage_file_records (
                path TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                last_access_utc TEXT NOT NULL,
                access_count INTEGER NOT NULL,
                is_pinned INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private UsageData LoadUsageFromDb()
    {
        var data = new UsageData();
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var folderCommand = connection.CreateCommand();
            folderCommand.CommandText = """
                SELECT path, name, last_access_utc, access_count, is_pinned
                FROM usage_folder_records;
                """;
            using (var reader = folderCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    data.FolderRecords.Add(new TrackedFolderRecord
                    {
                        Path = reader.GetString(0),
                        Name = reader.GetString(1),
                        LastAccessUtc = ParseUtc(reader.GetString(2)),
                        AccessCount = reader.GetInt32(3),
                        IsPinned = reader.GetInt64(4) != 0
                    });
                }
            }

            using var fileCommand = connection.CreateCommand();
            fileCommand.CommandText = """
                SELECT path, name, last_access_utc, access_count, is_pinned
                FROM usage_file_records;
                """;
            using var fileReader = fileCommand.ExecuteReader();
            while (fileReader.Read())
            {
                data.FileRecords.Add(new TrackedFileRecord
                {
                    Path = fileReader.GetString(0),
                    Name = fileReader.GetString(1),
                    LastAccessUtc = ParseUtc(fileReader.GetString(2)),
                    AccessCount = fileReader.GetInt32(3),
                    IsPinned = fileReader.GetInt64(4) != 0
                });
            }
        }
        catch
        {
            return new UsageData();
        }

        return data;
    }

    private static async Task UpsertFolderRecordAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        TrackedFolderRecord record,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO usage_folder_records (path, name, last_access_utc, access_count, is_pinned)
            VALUES ($path, $name, $last_access_utc, $access_count, $is_pinned)
            ON CONFLICT(path) DO UPDATE SET
                name = excluded.name,
                last_access_utc = excluded.last_access_utc,
                access_count = excluded.access_count,
                is_pinned = excluded.is_pinned;
            """;
        command.Parameters.AddWithValue("$path", record.Path);
        command.Parameters.AddWithValue("$name", record.Name ?? string.Empty);
        command.Parameters.AddWithValue("$last_access_utc", record.LastAccessUtc.ToString("O"));
        command.Parameters.AddWithValue("$access_count", record.AccessCount);
        command.Parameters.AddWithValue("$is_pinned", record.IsPinned ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertFileRecordAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        TrackedFileRecord record,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO usage_file_records (path, name, last_access_utc, access_count, is_pinned)
            VALUES ($path, $name, $last_access_utc, $access_count, $is_pinned)
            ON CONFLICT(path) DO UPDATE SET
                name = excluded.name,
                last_access_utc = excluded.last_access_utc,
                access_count = excluded.access_count,
                is_pinned = excluded.is_pinned;
            """;
        command.Parameters.AddWithValue("$path", record.Path);
        command.Parameters.AddWithValue("$name", record.Name ?? string.Empty);
        command.Parameters.AddWithValue("$last_access_utc", record.LastAccessUtc.ToString("O"));
        command.Parameters.AddWithValue("$access_count", record.AccessCount);
        command.Parameters.AddWithValue("$is_pinned", record.IsPinned ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTime ParseUtc(string value)
    {
        return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.UtcNow;
    }

    private static double ScoreFolder(TrackedFolderRecord record)
    {
        var ageDays = (DateTime.UtcNow - record.LastAccessUtc).TotalDays;
        var recencyWeight = Math.Max(0, 30 - ageDays) / 30.0;
        return record.AccessCount * 1.5 + recencyWeight * 10 + (record.IsPinned ? 1000 : 0);
    }

    private static double ScoreFile(TrackedFileRecord record)
    {
        var ageDays = (DateTime.UtcNow - record.LastAccessUtc).TotalDays;
        var recencyWeight = Math.Max(0, 30 - ageDays) / 30.0;
        return record.AccessCount * 1.2 + recencyWeight * 12 + (record.IsPinned ? 1000 : 0);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static TrackedFolderRecord Clone(TrackedFolderRecord source)
    {
        return new TrackedFolderRecord
        {
            Path = source.Path,
            Name = source.Name,
            LastAccessUtc = source.LastAccessUtc,
            AccessCount = source.AccessCount,
            IsPinned = source.IsPinned
        };
    }

    private static TrackedFileRecord Clone(TrackedFileRecord source)
    {
        return new TrackedFileRecord
        {
            Path = source.Path,
            Name = source.Name,
            LastAccessUtc = source.LastAccessUtc,
            AccessCount = source.AccessCount,
            IsPinned = source.IsPinned
        };
    }

    private void RebuildLookupMaps()
    {
        lock (_syncRoot)
        {
            _folderRecordMap.Clear();
            _fileRecordMap.Clear();

            foreach (var record in _data.FolderRecords)
            {
                if (string.IsNullOrWhiteSpace(record.Path))
                {
                    continue;
                }

                _folderRecordMap[record.Path] = record;
            }

            foreach (var record in _data.FileRecords)
            {
                if (string.IsNullOrWhiteSpace(record.Path))
                {
                    continue;
                }

                _fileRecordMap[record.Path] = record;
            }
        }
    }
}
