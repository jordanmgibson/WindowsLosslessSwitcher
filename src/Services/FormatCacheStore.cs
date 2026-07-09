using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Persists catalog format resolutions in SQLite, keyed by <see cref="TrackSnapshot.UniqueKey"/>.
/// </summary>
public sealed class FormatCacheStore
{
    private readonly string _databasePath;
    private readonly DiagnosticsLogger? _logger;
    private readonly object _sync = new();
    private bool _initialized;

    public FormatCacheStore()
        : this(AppDataPaths.FormatCacheDatabasePath, null)
    {
    }

    public FormatCacheStore(DiagnosticsLogger logger)
        : this(AppDataPaths.FormatCacheDatabasePath, logger)
    {
    }

    internal FormatCacheStore(string databasePath, DiagnosticsLogger? logger)
    {
        _databasePath = databasePath;
        _logger = logger;
    }

    public bool TryGet(string uniqueKey, out FormatCacheEntry? entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueKey);
        EnsureInitialized();

        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT unique_key, catalog_song_id, sample_rate_hz, bit_depth, confidence, description, cached_at_utc, last_verified_at_utc
                FROM format_cache
                WHERE unique_key = $uniqueKey
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$uniqueKey", uniqueKey);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                entry = null;
                return false;
            }

            entry = ReadEntry(reader);
            return true;
        }
    }

    public void Store(string uniqueKey, ResolvedAudioFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueKey);
        ArgumentNullException.ThrowIfNull(format);
        if (format.Source != AudioFormatSource.CatalogManifest)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        Upsert(uniqueKey, format, now, now);
        _logger?.Info(
            $"Format cached for '{uniqueKey}': {format.BitDepth}/{format.SampleRateHz} " +
            $"(catalogSongId={format.CatalogSongId ?? "none"}).");
    }

    public void UpdateEntry(string uniqueKey, ResolvedAudioFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueKey);
        ArgumentNullException.ThrowIfNull(format);
        if (format.Source is not (AudioFormatSource.CatalogManifest or AudioFormatSource.CachedCatalog))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        EnsureInitialized();

        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE format_cache
                SET catalog_song_id = $catalogSongId,
                    sample_rate_hz = $sampleRateHz,
                    bit_depth = $bitDepth,
                    confidence = $confidence,
                    description = $description,
                    last_verified_at_utc = $lastVerifiedAtUtc
                WHERE unique_key = $uniqueKey;
                """;
            command.Parameters.AddWithValue("$uniqueKey", uniqueKey);
            command.Parameters.AddWithValue("$catalogSongId", (object?)format.CatalogSongId ?? DBNull.Value);
            command.Parameters.AddWithValue("$sampleRateHz", format.SampleRateHz);
            command.Parameters.AddWithValue("$bitDepth", format.BitDepth);
            command.Parameters.AddWithValue("$confidence", (int)format.Confidence);
            command.Parameters.AddWithValue("$description", format.Description);
            command.Parameters.AddWithValue("$lastVerifiedAtUtc", FormatTimestamp(now));

            if (command.ExecuteNonQuery() == 0)
            {
                Upsert(uniqueKey, format, now, now);
            }
        }
    }

    public void TouchLastVerified(string uniqueKey, DateTimeOffset lastVerifiedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueKey);
        EnsureInitialized();

        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE format_cache
                SET last_verified_at_utc = $lastVerifiedAtUtc
                WHERE unique_key = $uniqueKey;
                """;
            command.Parameters.AddWithValue("$uniqueKey", uniqueKey);
            command.Parameters.AddWithValue("$lastVerifiedAtUtc", FormatTimestamp(lastVerifiedAtUtc));
            command.ExecuteNonQuery();
        }
    }

    private void Upsert(
        string uniqueKey,
        ResolvedAudioFormat format,
        DateTimeOffset cachedAtUtc,
        DateTimeOffset lastVerifiedAtUtc)
    {
        EnsureInitialized();

        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO format_cache (
                    unique_key,
                    catalog_song_id,
                    sample_rate_hz,
                    bit_depth,
                    confidence,
                    description,
                    cached_at_utc,
                    last_verified_at_utc)
                VALUES (
                    $uniqueKey,
                    $catalogSongId,
                    $sampleRateHz,
                    $bitDepth,
                    $confidence,
                    $description,
                    $cachedAtUtc,
                    $lastVerifiedAtUtc)
                ON CONFLICT(unique_key) DO UPDATE SET
                    catalog_song_id = excluded.catalog_song_id,
                    sample_rate_hz = excluded.sample_rate_hz,
                    bit_depth = excluded.bit_depth,
                    confidence = excluded.confidence,
                    description = excluded.description,
                    cached_at_utc = excluded.cached_at_utc,
                    last_verified_at_utc = excluded.last_verified_at_utc;
                """;
            command.Parameters.AddWithValue("$uniqueKey", uniqueKey);
            command.Parameters.AddWithValue("$catalogSongId", (object?)format.CatalogSongId ?? DBNull.Value);
            command.Parameters.AddWithValue("$sampleRateHz", format.SampleRateHz);
            command.Parameters.AddWithValue("$bitDepth", format.BitDepth);
            command.Parameters.AddWithValue("$confidence", (int)format.Confidence);
            command.Parameters.AddWithValue("$description", format.Description);
            command.Parameters.AddWithValue("$cachedAtUtc", FormatTimestamp(cachedAtUtc));
            command.Parameters.AddWithValue("$lastVerifiedAtUtc", FormatTimestamp(lastVerifiedAtUtc));
            command.ExecuteNonQuery();
        }
    }

    private void EnsureInitialized()
    {
        lock (_sync)
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var connection = OpenConnection();
            ExecuteNonQuery(
                connection,
                """
                CREATE TABLE IF NOT EXISTS format_cache (
                    unique_key TEXT PRIMARY KEY NOT NULL,
                    catalog_song_id TEXT,
                    sample_rate_hz INTEGER NOT NULL,
                    bit_depth INTEGER NOT NULL,
                    confidence INTEGER NOT NULL,
                    description TEXT NOT NULL,
                    cached_at_utc TEXT NOT NULL,
                    last_verified_at_utc TEXT NOT NULL
                );
                """);
            ExecuteNonQuery(
                connection,
                """
                CREATE INDEX IF NOT EXISTS idx_format_cache_catalog_song_id
                    ON format_cache(catalog_song_id)
                    WHERE catalog_song_id IS NOT NULL;
                """);
            _initialized = true;
        }
    }

    internal static void ReleaseConnectionsForTesting()
    {
        SqliteConnection.ClearAllPools();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        return connection;
    }

    private static FormatCacheEntry ReadEntry(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            (ResolutionConfidence)reader.GetInt32(4),
            reader.GetString(5),
            ParseTimestamp(reader.GetString(6)),
            ParseTimestamp(reader.GetString(7)));

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}