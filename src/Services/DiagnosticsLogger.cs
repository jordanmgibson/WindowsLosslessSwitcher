using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace WindowsLosslessSwitcher.Services;

public sealed class DiagnosticsLogger
{
    private const string LogMutexName = @"Local\WindowsLosslessSwitcher.Log";

    private readonly string _logDirectory;
    private readonly string _logPath;
    private readonly ConcurrentQueue<string> _recentEntries = new();
    private readonly object _fileLock = new();
    private readonly Mutex _writeMutex;

    public DiagnosticsLogger()
        : this(null)
    {
    }

    internal DiagnosticsLogger(string? baseDirectory)
    {
        _logDirectory = Path.Combine(
            baseDirectory is null
                ? AppDataPaths.RootDirectory
                : Path.Combine(baseDirectory, "WindowsLosslessSwitcher"),
            "logs");
        Directory.CreateDirectory(_logDirectory);
        _logPath = Path.Combine(_logDirectory, "switcher.log");
        _writeMutex = new Mutex(initiallyOwned: false, LogMutexName);
    }

    public string LogPath => _logPath;

    public void Info(string message) => Write("INFO", message);

    internal void Info(string message, LogCorrelation correlation) => Write("INFO", message, correlation);

    public void Warn(string message) => Write("WARN", message);

    internal void Warn(string message, LogCorrelation correlation) => Write("WARN", message, correlation);

    public void Error(string message, Exception? exception = null)
    {
        var finalMessage = exception is null ? message : $"{message} | {exception}";
        Write("ERROR", finalMessage);
    }

    internal void Error(string message, Exception? exception, LogCorrelation correlation)
    {
        var finalMessage = exception is null ? message : $"{message} | {exception}";
        Write("ERROR", finalMessage, correlation);
    }

    public void Export(string destinationPath)
    {
        lock (_fileLock)
        {
            WaitForWriteMutex();
            try
            {
                if (!File.Exists(_logPath))
                {
                    using var emptyStream = new FileStream(_logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                }

                using var source = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                source.CopyTo(destination);
            }
            finally
            {
                ReleaseWriteMutex();
            }
        }
    }

    public IReadOnlyList<string> GetRecentEntries() => _recentEntries.ToArray();

    private void Write(string level, string message)
        => Write(level, message, default);

    private void Write(string level, string message, LogCorrelation correlation)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {correlation.FormatPrefix()}{message}";
        _recentEntries.Enqueue(line);

        while (_recentEntries.Count > 200)
        {
            _recentEntries.TryDequeue(out _);
        }

        lock (_fileLock)
        {
            WaitForWriteMutex();
            try
            {
                using var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.WriteLine(line);
            }
            finally
            {
                ReleaseWriteMutex();
            }
        }
    }

    internal readonly record struct LogCorrelation(long? Generation = null, string? TrackKey = null, long? SnapshotGeneration = null)
    {
        public string FormatPrefix()
        {
            var parts = new List<string>(3);

            if (Generation is not null)
            {
                parts.Add($"gen={Generation.Value}");
            }

            if (SnapshotGeneration is not null)
            {
                parts.Add($"snap={SnapshotGeneration.Value}");
            }

            if (!string.IsNullOrWhiteSpace(TrackKey))
            {
                parts.Add($"track={TrackKey}");
            }

            return parts.Count == 0 ? string.Empty : $"{string.Join(" ", parts)} ";
        }
    }

    private void WaitForWriteMutex()
    {
        try
        {
            _writeMutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
        }
    }

    private void ReleaseWriteMutex()
    {
        try
        {
            _writeMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
    }
}
