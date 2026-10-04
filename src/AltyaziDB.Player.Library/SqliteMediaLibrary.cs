using System.Globalization;
using System.IO;
using System.Text.Json;
using AltyaziDB.Player.Core.Interfaces;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Core.Parsing;
using AltyaziDB.Player.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AltyaziDB.Player.Library;

public sealed class SqliteMediaLibrary : IMediaLibrary, IResumeStore, ICloudSyncJournal
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".webm", ".mov", ".m4v", ".ts", ".m2ts", ".mts",
        ".mpeg", ".mpg", ".wmv", ".flv", ".ogv", ".vob", ".3gp"
    };

    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;
    private readonly ReleaseParser _parser;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _initialized;

    public SqliteMediaLibrary(AppPaths paths, IAppLogger logger, ReleaseParser parser)
    {
        _paths = paths;
        _logger = logger;
        _parser = parser;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.LibraryDatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS library_folders (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    added_utc TEXT NOT NULL,
                    last_scan_utc TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS media_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    root_path TEXT NOT NULL COLLATE NOCASE,
                    file_name TEXT NOT NULL,
                    title TEXT NOT NULL,
                    normalized_title TEXT NOT NULL,
                    media_type TEXT NOT NULL,
                    year INTEGER NULL,
                    season INTEGER NULL,
                    episode INTEGER NULL,
                    size_bytes INTEGER NOT NULL,
                    modified_utc TEXT NOT NULL,
                    scan_token TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS playback_progress (
                    source TEXT PRIMARY KEY COLLATE NOCASE,
                    position_seconds REAL NOT NULL,
                    duration_seconds REAL NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS cloud_media_sources (
                    media_key TEXT PRIMARY KEY COLLATE NOCASE,
                    source TEXT NOT NULL,
                    title TEXT NOT NULL,
                    media_type TEXT NOT NULL,
                    year INTEGER NULL,
                    season INTEGER NULL,
                    episode INTEGER NULL,
                    imdb_id TEXT NULL,
                    tmdb_id TEXT NULL,
                    source_kind TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS cloud_watch_queue (
                    media_key TEXT PRIMARY KEY COLLATE NOCASE,
                    source TEXT NOT NULL,
                    title TEXT NOT NULL,
                    media_type TEXT NOT NULL,
                    year INTEGER NULL,
                    season INTEGER NULL,
                    episode INTEGER NULL,
                    imdb_id TEXT NULL,
                    tmdb_id TEXT NULL,
                    source_kind TEXT NOT NULL,
                    position_seconds REAL NOT NULL,
                    duration_seconds REAL NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_media_items_title ON media_items(normalized_title);
                CREATE INDEX IF NOT EXISTS idx_media_items_root ON media_items(root_path);
                CREATE INDEX IF NOT EXISTS idx_media_items_series ON media_items(media_type, season, episode);
                CREATE INDEX IF NOT EXISTS idx_progress_updated ON playback_progress(updated_utc DESC);
                CREATE INDEX IF NOT EXISTS idx_cloud_watch_queue_updated ON cloud_watch_queue(updated_utc ASC);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await ImportLegacyResumeAsync(cancellationToken).ConfigureAwait(false);
            _logger.Info($"SQLite medya veritabanı hazır: {_paths.LibraryDatabaseFile}");
        }
        catch
        {
            Interlocked.Exchange(ref _initialized, 0);
            throw;
        }
    }

    public async Task<IReadOnlyList<LibraryFolder>> GetFoldersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<LibraryFolder>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id, f.path, f.added_utc, f.last_scan_utc, COUNT(m.id)
            FROM library_folders f
            LEFT JOIN media_items m ON m.root_path = f.path
            GROUP BY f.id, f.path, f.added_utc, f.last_scan_utc
            ORDER BY f.path COLLATE NOCASE;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new LibraryFolder(
                reader.GetInt64(0),
                reader.GetString(1),
                ParseDate(reader.GetString(2)) ?? DateTimeOffset.UtcNow,
                reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3)),
                reader.GetInt32(4)));
        }
        return result;
    }

    public async Task<LibraryFolder> AddFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException(fullPath);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO library_folders(path, added_utc)
                VALUES($path, $added)
                ON CONFLICT(path) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$path", fullPath);
            command.Parameters.AddWithValue("$added", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        return (await GetFoldersAsync(cancellationToken).ConfigureAwait(false))
            .First(folder => string.Equals(folder.Path, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    public async Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var pathCommand = connection.CreateCommand();
            pathCommand.CommandText = "SELECT path FROM library_folders WHERE id = $id;";
            pathCommand.Parameters.AddWithValue("$id", folderId);
            var path = await pathCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (path is null) return;

            using var transaction = connection.BeginTransaction();
            await using var deleteItems = connection.CreateCommand();
            deleteItems.Transaction = transaction;
            deleteItems.CommandText = "DELETE FROM media_items WHERE root_path = $path;";
            deleteItems.Parameters.AddWithValue("$path", path);
            await deleteItems.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var deleteFolder = connection.CreateCommand();
            deleteFolder.Transaction = transaction;
            deleteFolder.CommandText = "DELETE FROM library_folders WHERE id = $id;";
            deleteFolder.Parameters.AddWithValue("$id", folderId);
            await deleteFolder.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task ScanAllAsync(IProgress<LibraryScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        foreach (var folder in await GetFoldersAsync(cancellationToken).ConfigureAwait(false))
            await ScanFolderAsync(folder.Id, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task ScanFolderAsync(long folderId, IProgress<LibraryScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var folder = (await GetFoldersAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => item.Id == folderId)
                     ?? throw new InvalidOperationException("Kütüphane klasörü bulunamadı.");
        if (!Directory.Exists(folder.Path)) throw new DirectoryNotFoundException(folder.Path);

        var scanToken = Guid.NewGuid().ToString("N");
        var scanned = 0;
        var updated = 0;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO media_items(
                    path, root_path, file_name, title, normalized_title, media_type,
                    year, season, episode, size_bytes, modified_utc, scan_token)
                VALUES(
                    $path, $root, $file, $title, $normalized, $type,
                    $year, $season, $episode, $size, $modified, $token)
                ON CONFLICT(path) DO UPDATE SET
                    root_path = excluded.root_path,
                    file_name = excluded.file_name,
                    title = excluded.title,
                    normalized_title = excluded.normalized_title,
                    media_type = excluded.media_type,
                    year = excluded.year,
                    season = excluded.season,
                    episode = excluded.episode,
                    size_bytes = excluded.size_bytes,
                    modified_utc = excluded.modified_utc,
                    scan_token = excluded.scan_token;
                """;
            foreach (var name in new[] { "$path", "$root", "$file", "$title", "$normalized", "$type", "$year", "$season", "$episode", "$size", "$modified", "$token" })
                upsert.Parameters.Add(new SqliteParameter(name, DBNull.Value));

            foreach (var file in EnumerateVideoFilesSafe(folder.Path, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;
                try
                {
                    var info = new FileInfo(file);
                    var identity = _parser.Parse(info.Name);
                    upsert.Parameters["$path"].Value = info.FullName;
                    upsert.Parameters["$root"].Value = folder.Path;
                    upsert.Parameters["$file"].Value = info.Name;
                    upsert.Parameters["$title"].Value = string.IsNullOrWhiteSpace(identity.Title) ? Path.GetFileNameWithoutExtension(info.Name) : identity.Title;
                    upsert.Parameters["$normalized"].Value = NormalizeSearch(identity.Title + " " + info.Name);
                    upsert.Parameters["$type"].Value = identity.ContentType;
                    upsert.Parameters["$year"].Value = identity.Year is null ? DBNull.Value : identity.Year.Value;
                    upsert.Parameters["$season"].Value = identity.Season is null ? DBNull.Value : identity.Season.Value;
                    upsert.Parameters["$episode"].Value = identity.Episode is null ? DBNull.Value : identity.Episode.Value;
                    upsert.Parameters["$size"].Value = info.Length;
                    upsert.Parameters["$modified"].Value = info.LastWriteTimeUtc.ToString("O");
                    upsert.Parameters["$token"].Value = scanToken;
                    await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    updated++;
                    if (scanned % 25 == 0) progress?.Report(new LibraryScanProgress(folder.Path, scanned, updated, info.Name));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.Warning($"Kütüphane dosyası atlandı: {file} · {exception.Message}");
                }
            }

            await using var removeMissing = connection.CreateCommand();
            removeMissing.Transaction = transaction;
            removeMissing.CommandText = "DELETE FROM media_items WHERE root_path = $root AND scan_token <> $token;";
            removeMissing.Parameters.AddWithValue("$root", folder.Path);
            removeMissing.Parameters.AddWithValue("$token", scanToken);
            await removeMissing.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var updateFolder = connection.CreateCommand();
            updateFolder.Transaction = transaction;
            updateFolder.CommandText = "UPDATE library_folders SET last_scan_utc = $now WHERE id = $id;";
            updateFolder.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            updateFolder.Parameters.AddWithValue("$id", folderId);
            await updateFolder.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
        }
        finally
        {
            _writeGate.Release();
        }

        progress?.Report(new LibraryScanProgress(folder.Path, scanned, updated, null, true));
        _logger.Info($"Kütüphane taraması tamamlandı: {folder.Path} · {updated} video");
    }

    public async Task<IReadOnlyList<LibraryMediaItem>> SearchAsync(string? searchText, LibraryMediaFilter filter, int limit = 1000, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<LibraryMediaItem>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var filterSql = filter switch
        {
            LibraryMediaFilter.Movies => "m.media_type = 'movie'",
            LibraryMediaFilter.Series => "m.media_type = 'series'",
            LibraryMediaFilter.ContinueWatching => "p.position_seconds >= 30 AND p.duration_seconds > 0 AND p.position_seconds < p.duration_seconds * 0.90",
            LibraryMediaFilter.Unwatched => "p.source IS NULL OR p.position_seconds < 30",
            LibraryMediaFilter.Completed => "p.duration_seconds > 0 AND p.position_seconds >= p.duration_seconds * 0.90",
            _ => "1 = 1"
        };
        command.CommandText = $"""
            SELECT m.id, m.path, m.root_path, m.file_name, m.title, m.media_type,
                   m.year, m.season, m.episode, m.size_bytes, m.modified_utc,
                   COALESCE(p.position_seconds, 0), COALESCE(p.duration_seconds, 0), p.updated_utc
            FROM media_items m
            LEFT JOIN playback_progress p ON p.source = m.path
            WHERE ({filterSql})
              AND ($search = '' OR m.normalized_title LIKE $like OR m.path LIKE $like)
            ORDER BY
                CASE WHEN p.updated_utc IS NULL THEN 1 ELSE 0 END,
                p.updated_utc DESC,
                m.title COLLATE NOCASE,
                COALESCE(m.season, 0), COALESCE(m.episode, 0)
            LIMIT $limit;
            """;
        var normalized = NormalizeSearch(searchText ?? string.Empty);
        command.Parameters.AddWithValue("$search", normalized);
        command.Parameters.AddWithValue("$like", $"%{normalized}%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new LibraryMediaItem(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.GetInt64(9),
                ParseDate(reader.GetString(10)) ?? DateTimeOffset.MinValue,
                reader.GetDouble(11), reader.GetDouble(12),
                reader.IsDBNull(13) ? null : ParseDate(reader.GetString(13))));
        }
        return result;
    }

    public async Task<ResumeEntry?> GetAsync(string source, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT position_seconds, duration_seconds, updated_utc FROM playback_progress WHERE source = $source;";
        command.Parameters.AddWithValue("$source", source);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new ResumeEntry(source, reader.GetDouble(0), reader.GetDouble(1), ParseDate(reader.GetString(2)) ?? DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<ResumeEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source, position_seconds, duration_seconds, updated_utc
            FROM playback_progress
            WHERE position_seconds >= 5 AND duration_seconds > 0 AND (duration_seconds - position_seconds) >= 45
            ORDER BY updated_utc DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ResumeEntry>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ResumeEntry(
                reader.GetString(0),
                reader.GetDouble(1),
                reader.GetDouble(2),
                ParseDate(reader.GetString(3)) ?? DateTimeOffset.UtcNow));
        }
        return result;
    }

    public async Task SaveAsync(string source, double positionSeconds, double durationSeconds, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (durationSeconds > 0 && positionSeconds >= durationSeconds * 0.97) positionSeconds = durationSeconds;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO playback_progress(source, position_seconds, duration_seconds, updated_utc)
                VALUES($source, $position, $duration, $updated)
                ON CONFLICT(source) DO UPDATE SET
                    position_seconds = excluded.position_seconds,
                    duration_seconds = excluded.duration_seconds,
                    updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$position", Math.Max(0, positionSeconds));
            command.Parameters.AddWithValue("$duration", Math.Max(0, durationSeconds));
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task RemoveAsync(string source, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM playback_progress WHERE source = $source;";
            command.Parameters.AddWithValue("$source", source);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }


    public async Task QueueWatchProgressAsync(
        CloudWatchProgressMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutation.MediaKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(mutation.Source);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();

            await using (var map = connection.CreateCommand())
            {
                map.Transaction = transaction;
                map.CommandText = """
                    INSERT INTO cloud_media_sources(
                        media_key, source, title, media_type, year, season, episode,
                        imdb_id, tmdb_id, source_kind, updated_utc)
                    VALUES(
                        $key, $source, $title, $type, $year, $season, $episode,
                        $imdb, $tmdb, $kind, $updated)
                    ON CONFLICT(media_key) DO UPDATE SET
                        source = excluded.source,
                        title = excluded.title,
                        media_type = excluded.media_type,
                        year = excluded.year,
                        season = excluded.season,
                        episode = excluded.episode,
                        imdb_id = excluded.imdb_id,
                        tmdb_id = excluded.tmdb_id,
                        source_kind = excluded.source_kind,
                        updated_utc = excluded.updated_utc;
                    """;
                AddCloudMutationParameters(map, mutation);
                await map.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var queue = connection.CreateCommand())
            {
                queue.Transaction = transaction;
                queue.CommandText = """
                    INSERT INTO cloud_watch_queue(
                        media_key, source, title, media_type, year, season, episode,
                        imdb_id, tmdb_id, source_kind, position_seconds, duration_seconds, updated_utc)
                    VALUES(
                        $key, $source, $title, $type, $year, $season, $episode,
                        $imdb, $tmdb, $kind, $position, $duration, $updated)
                    ON CONFLICT(media_key) DO UPDATE SET
                        source = excluded.source,
                        title = excluded.title,
                        media_type = excluded.media_type,
                        year = excluded.year,
                        season = excluded.season,
                        episode = excluded.episode,
                        imdb_id = excluded.imdb_id,
                        tmdb_id = excluded.tmdb_id,
                        source_kind = excluded.source_kind,
                        position_seconds = excluded.position_seconds,
                        duration_seconds = excluded.duration_seconds,
                        updated_utc = excluded.updated_utc
                    WHERE excluded.updated_utc >= cloud_watch_queue.updated_utc;
                    """;
                AddCloudMutationParameters(queue, mutation);
                queue.Parameters.AddWithValue("$position", Math.Max(0, mutation.PositionSeconds));
                queue.Parameters.AddWithValue("$duration", Math.Max(0, mutation.DurationSeconds));
                await queue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<CloudWatchProgressMutation>> GetPendingWatchProgressAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<CloudWatchProgressMutation>();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT media_key, source, title, media_type, year, season, episode,
                   imdb_id, tmdb_id, source_kind, position_seconds, duration_seconds, updated_utc
            FROM cloud_watch_queue
            ORDER BY updated_utc ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CloudWatchProgressMutation(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.GetDouble(10),
                reader.GetDouble(11),
                ParseDate(reader.GetString(12)) ?? DateTimeOffset.UtcNow));
        }

        return result;
    }

    public async Task AcknowledgeWatchProgressAsync(
        IReadOnlyList<CloudWatchProgressMutation> sent,
        CancellationToken cancellationToken = default)
    {
        if (sent.Count == 0) return;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM cloud_watch_queue
                WHERE media_key = $key AND updated_utc <= $updated;
                """;
            command.Parameters.Add(new SqliteParameter("$key", string.Empty));
            command.Parameters.Add(new SqliteParameter("$updated", string.Empty));

            foreach (var mutation in sent)
            {
                command.Parameters["$key"].Value = mutation.MediaKey;
                command.Parameters["$updated"].Value = mutation.UpdatedAtUtc.ToString("O");
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<string?> ResolveLocalSourceAsync(
        CloudWatchProgressEntry entry,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (var mapped = connection.CreateCommand())
        {
            mapped.CommandText = "SELECT source FROM cloud_media_sources WHERE media_key = $key LIMIT 1;";
            mapped.Parameters.AddWithValue("$key", entry.MediaKey);
            var source = await mapped.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (IsUsableResumeSource(source)) return source;
        }

        await using var local = connection.CreateCommand();
        local.CommandText = """
            SELECT path
            FROM media_items
            WHERE lower(title) = lower($title)
              AND ($year IS NULL OR year = $year)
              AND ($season IS NULL OR season = $season)
              AND ($episode IS NULL OR episode = $episode)
            ORDER BY modified_utc DESC
            LIMIT 1;
            """;
        local.Parameters.AddWithValue("$title", entry.Title);
        local.Parameters.AddWithValue("$year", entry.Year is null ? DBNull.Value : entry.Year.Value);
        local.Parameters.AddWithValue("$season", entry.Season is null ? DBNull.Value : entry.Season.Value);
        local.Parameters.AddWithValue("$episode", entry.Episode is null ? DBNull.Value : entry.Episode.Value);

        var resolved = await local.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (!IsUsableResumeSource(resolved)) return null;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var updateConnection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var map = updateConnection.CreateCommand();
            map.CommandText = """
                INSERT INTO cloud_media_sources(
                    media_key, source, title, media_type, year, season, episode,
                    imdb_id, tmdb_id, source_kind, updated_utc)
                VALUES(
                    $key, $source, $title, $type, $year, $season, $episode,
                    $imdb, $tmdb, $kind, $updated)
                ON CONFLICT(media_key) DO UPDATE SET
                    source = excluded.source,
                    title = excluded.title,
                    media_type = excluded.media_type,
                    year = excluded.year,
                    season = excluded.season,
                    episode = excluded.episode,
                    imdb_id = excluded.imdb_id,
                    tmdb_id = excluded.tmdb_id,
                    source_kind = excluded.source_kind,
                    updated_utc = excluded.updated_utc;
                """;
            map.Parameters.AddWithValue("$key", entry.MediaKey);
            map.Parameters.AddWithValue("$source", resolved);
            map.Parameters.AddWithValue("$title", entry.Title);
            map.Parameters.AddWithValue("$type", entry.MediaType);
            map.Parameters.AddWithValue("$year", entry.Year is null ? DBNull.Value : entry.Year.Value);
            map.Parameters.AddWithValue("$season", entry.Season is null ? DBNull.Value : entry.Season.Value);
            map.Parameters.AddWithValue("$episode", entry.Episode is null ? DBNull.Value : entry.Episode.Value);
            map.Parameters.AddWithValue("$imdb", string.IsNullOrWhiteSpace(entry.ImdbId) ? DBNull.Value : entry.ImdbId);
            map.Parameters.AddWithValue("$tmdb", string.IsNullOrWhiteSpace(entry.TmdbId) ? DBNull.Value : entry.TmdbId);
            map.Parameters.AddWithValue("$kind", entry.SourceKind);
            map.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await map.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        return resolved;
    }

    public async Task ApplyRemoteWatchProgressAsync(
        string source,
        CloudWatchProgressEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source) || !entry.HasProgress || entry.ClientUpdatedAtUtc is null) return;
        var remoteUpdatedAt = entry.ClientUpdatedAtUtc.Value;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using (var pending = connection.CreateCommand())
            {
                pending.CommandText = "SELECT 1 FROM cloud_watch_queue WHERE media_key = $key LIMIT 1;";
                pending.Parameters.AddWithValue("$key", entry.MediaKey);
                if (await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    return;
            }

            DateTimeOffset? localUpdated = null;
            await using (var existing = connection.CreateCommand())
            {
                existing.CommandText = "SELECT updated_utc FROM playback_progress WHERE source = $source LIMIT 1;";
                existing.Parameters.AddWithValue("$source", source);
                var value = await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                localUpdated = ParseDate(value);
            }

            if (localUpdated is not null && localUpdated.Value > remoteUpdatedAt)
                return;

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO playback_progress(source, position_seconds, duration_seconds, updated_utc)
                VALUES($source, $position, $duration, $updated)
                ON CONFLICT(source) DO UPDATE SET
                    position_seconds = excluded.position_seconds,
                    duration_seconds = excluded.duration_seconds,
                    updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$position", Math.Max(0, entry.PositionSeconds));
            command.Parameters.AddWithValue("$duration", Math.Max(0, entry.DurationSeconds));
            command.Parameters.AddWithValue("$updated", remoteUpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static void AddCloudMutationParameters(SqliteCommand command, CloudWatchProgressMutation mutation)
    {
        command.Parameters.AddWithValue("$key", mutation.MediaKey);
        command.Parameters.AddWithValue("$source", mutation.Source);
        command.Parameters.AddWithValue("$title", mutation.Title);
        command.Parameters.AddWithValue("$type", mutation.MediaType);
        command.Parameters.AddWithValue("$year", mutation.Year is null ? DBNull.Value : mutation.Year.Value);
        command.Parameters.AddWithValue("$season", mutation.Season is null ? DBNull.Value : mutation.Season.Value);
        command.Parameters.AddWithValue("$episode", mutation.Episode is null ? DBNull.Value : mutation.Episode.Value);
        command.Parameters.AddWithValue("$imdb", string.IsNullOrWhiteSpace(mutation.ImdbId) ? DBNull.Value : mutation.ImdbId);
        command.Parameters.AddWithValue("$tmdb", string.IsNullOrWhiteSpace(mutation.TmdbId) ? DBNull.Value : mutation.TmdbId);
        command.Parameters.AddWithValue("$kind", mutation.SourceKind);
        command.Parameters.AddWithValue("$updated", mutation.UpdatedAtUtc.ToString("O"));
    }

    private static bool IsUsableResumeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (File.Exists(source)) return true;
        return Uri.TryCreate(source, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private async Task ImportLegacyResumeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.ResumeFile)) return;
        try
        {
            await using var countConnection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var countCommand = countConnection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM playback_progress;";
            var count = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (count > 0) return;

            await using var stream = File.OpenRead(_paths.ResumeFile);
            var entries = await JsonSerializer.DeserializeAsync<Dictionary<string, ResumeEntry>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (entries is null || entries.Count == 0) return;
            foreach (var entry in entries.Values)
                await SaveAsync(entry.Source, entry.PositionSeconds, entry.DurationSeconds, cancellationToken).ConfigureAwait(false);
            _logger.Info($"Eski izleme konumları SQLite veritabanına aktarıldı: {entries.Count}");
        }
        catch (Exception exception)
        {
            _logger.Warning($"Eski resume.json içe aktarılamadı: {exception.Message}");
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _initialized) == 0) await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static IEnumerable<string> EnumerateVideoFilesSafe(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            string[] files;
            try { files = Directory.GetFiles(directory); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { continue; }
            foreach (var file in files)
                if (VideoExtensions.Contains(Path.GetExtension(file))) yield return file;

            string[] directories;
            try { directories = Directory.GetDirectories(directory); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { continue; }
            foreach (var child in directories.Reverse()) pending.Push(child);
        }
    }

    private static string NormalizeSearch(string value) => string.Join(' ', value
        .ToLowerInvariant()
        .Replace('.', ' ')
        .Replace('_', ' ')
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result) ? result : null;
}
