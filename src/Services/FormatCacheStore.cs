using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Persists catalog format resolutions in a versioned JSON cache.
/// </summary>
public sealed class FormatCacheStore
{
    private const int SchemaVersion = 1;

    // Bump this whenever catalog matching, metadata normalization, cache-key construction,
    // or manifest format selection changes in a way that can alter a cached result.
    internal const int CatalogResolverVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _cachePath;
    private readonly DiagnosticsLogger? _logger;
    private readonly object _sync = new();
    private Dictionary<string, FormatCacheEntry> _entries = new(StringComparer.Ordinal);
    private bool _initialized;
    private bool _loadFailureLogged;
    private long _clearGeneration;

    public FormatCacheStore()
        : this(AppDataPaths.FormatCachePath, null)
    {
    }

    public FormatCacheStore(DiagnosticsLogger logger)
        : this(AppDataPaths.FormatCachePath, logger)
    {
    }

    internal FormatCacheStore(string cachePath, DiagnosticsLogger? logger)
    {
        _cachePath = cachePath;
        _logger = logger;
    }

    public bool TryGet(string uniqueKey, out FormatCacheEntry? entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueKey);

        lock (_sync)
        {
            if (!EnsureInitialized())
            {
                entry = null;
                return false;
            }

            return _entries.TryGetValue(uniqueKey, out entry);
        }
    }

    public bool Store(string uniqueKey, ResolvedAudioFormat format)
        => Store(uniqueKey, format, DateTimeOffset.UtcNow);

    internal bool Store(string uniqueKey, ResolvedAudioFormat format, DateTimeOffset storedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueKey);
        ArgumentNullException.ThrowIfNull(format);
        if (format.Source != AudioFormatSource.CatalogManifest)
        {
            return false;
        }

        lock (_sync)
        {
            if (!EnsureInitialized())
            {
                return false;
            }

            var candidate = CopyEntries();
            var cachedAtUtc = candidate.TryGetValue(uniqueKey, out var current)
                ? current.CachedAtUtc
                : storedAtUtc;
            candidate[uniqueKey] = CreateEntry(uniqueKey, format, cachedAtUtc, storedAtUtc);

            if (!TryPersist(candidate))
            {
                return false;
            }

            _entries = candidate;
        }

        TryLogInfo(
            $"Format cached for '{uniqueKey}': {format.BitDepth}/{format.SampleRateHz} " +
            $"(catalogSongId={format.CatalogSongId ?? "none"}).");
        return true;
    }

    internal long ClearGeneration
    {
        get
        {
            lock (_sync)
            {
                return _clearGeneration;
            }
        }
    }

    internal bool TryApplyVerification(FormatCacheEntry expectedEntry, ResolvedAudioFormat format) =>
        TryApplyVerification(expectedEntry, format, out _);

    internal bool TryApplyVerification(
        FormatCacheEntry expectedEntry,
        ResolvedAudioFormat format,
        out long clearGeneration)
    {
        ArgumentNullException.ThrowIfNull(expectedEntry);
        ArgumentNullException.ThrowIfNull(format);
        clearGeneration = 0;
        if (format.Source != AudioFormatSource.CatalogManifest)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            if (!EnsureInitialized())
            {
                return false;
            }

            if (!_entries.TryGetValue(expectedEntry.UniqueKey, out var current) || current != expectedEntry)
            {
                return false;
            }

            var candidate = CopyEntries();
            candidate[expectedEntry.UniqueKey] = CreateEntry(
                expectedEntry.UniqueKey,
                format,
                current.CachedAtUtc,
                now);

            if (!TryPersist(candidate))
            {
                return false;
            }

            _entries = candidate;
            clearGeneration = _clearGeneration;
            return true;
        }
    }

    public bool Clear()
    {
        lock (_sync)
        {
            // Clearing is valid even when the on-disk cache cannot be read: the point is to
            // replace whatever is there with an empty cache.
            var candidate = new Dictionary<string, FormatCacheEntry>(StringComparer.Ordinal);
            if (!TryPersist(candidate))
            {
                return false;
            }

            _entries = candidate;
            _initialized = true;
            _clearGeneration++;
        }

        TryLogInfo("Catalog format cache cleared.");
        return true;
    }

    private bool EnsureInitialized()
    {
        if (_initialized)
        {
            return true;
        }

        if (!File.Exists(_cachePath))
        {
            _initialized = true;
            return true;
        }

        try
        {
            using var stream = new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<FormatCacheDocument>(stream, JsonOptions);
            if (document is null ||
                document.SchemaVersion != SchemaVersion ||
                document.CatalogResolverVersion != CatalogResolverVersion)
            {
                TryLogWarning("Catalog format cache has an unsupported version and will be rebuilt.");
            }
            else if (!TryValidateEntries(document.Entries, out var entries))
            {
                TryLogWarning("Catalog format cache contains invalid entries and will be rebuilt.");
            }
            else
            {
                _entries = entries;
            }
        }
        catch (JsonException ex)
        {
            TryLogWarning($"Catalog format cache is corrupt and will be rebuilt: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Likely transient (e.g. the file is held by a sync or backup tool). Serve misses and
            // retry on the next call instead of marking the store initialized, so a later Store
            // cannot persist an empty dictionary over entries that are still intact on disk.
            if (!_loadFailureLogged)
            {
                _loadFailureLogged = true;
                TryLogWarning($"Catalog format cache could not be read; caching is paused until it can be: {ex.Message}");
            }

            return false;
        }

        _initialized = true;
        _loadFailureLogged = false;
        return true;
    }

    private bool TryPersist(Dictionary<string, FormatCacheEntry> entries)
    {
        var temporaryPath = $"{_cachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var document = new FormatCacheDocument(SchemaVersion, CatalogResolverVersion, entries);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _cachePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            TryLogWarning($"Catalog format cache could not be saved: {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // A leftover temporary cache file is harmless and can be removed on a later run.
            }
        }
    }

    private Dictionary<string, FormatCacheEntry> CopyEntries() =>
        new(_entries, StringComparer.Ordinal);

    private static FormatCacheEntry CreateEntry(
        string uniqueKey,
        ResolvedAudioFormat format,
        DateTimeOffset cachedAtUtc,
        DateTimeOffset lastVerifiedAtUtc) =>
        new(
            uniqueKey,
            format.CatalogSongId,
            format.SampleRateHz,
            format.BitDepth,
            format.Confidence,
            format.Description,
            cachedAtUtc,
            lastVerifiedAtUtc);

    private static bool TryValidateEntries(
        Dictionary<string, FormatCacheEntry>? persistedEntries,
        out Dictionary<string, FormatCacheEntry> entries)
    {
        entries = new Dictionary<string, FormatCacheEntry>(StringComparer.Ordinal);
        if (persistedEntries is null)
        {
            return false;
        }

        foreach (var pair in persistedEntries)
        {
            var entry = pair.Value;
            if (string.IsNullOrWhiteSpace(pair.Key) ||
                entry is null ||
                !string.Equals(pair.Key, entry.UniqueKey, StringComparison.Ordinal) ||
                entry.SampleRateHz <= 0 ||
                entry.BitDepth <= 0 ||
                !Enum.IsDefined(entry.Confidence) ||
                string.IsNullOrWhiteSpace(entry.Description) ||
                entry.CachedAtUtc == default ||
                entry.LastVerifiedAtUtc == default)
            {
                return false;
            }

            entries.Add(pair.Key, entry);
        }

        return true;
    }

    private void TryLogInfo(string message)
    {
        try
        {
            _logger?.Info(message);
        }
        catch
        {
        }
    }

    private void TryLogWarning(string message)
    {
        try
        {
            _logger?.Warn(message);
        }
        catch
        {
        }
    }

    private sealed record FormatCacheDocument(
        int SchemaVersion,
        int CatalogResolverVersion,
        Dictionary<string, FormatCacheEntry> Entries);
}
