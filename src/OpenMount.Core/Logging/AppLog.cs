using System.Text;
using OpenMount.Core.Security;

namespace OpenMount.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogLine(DateTimeOffset At, LogLevel Level, string Source, string Message);

public interface IAppLog
{
    void Write(LogLevel level, string source, string message, Exception? ex = null);
    IReadOnlyList<LogLine> Recent();
    event Action<LogLine>? LineWritten;
}

public static class AppLogExtensions
{
    public static void Debug(this IAppLog log, string source, string message) => log.Write(LogLevel.Debug, source, message);
    public static void Info(this IAppLog log, string source, string message) => log.Write(LogLevel.Info, source, message);
    public static void Warn(this IAppLog log, string source, string message) => log.Write(LogLevel.Warn, source, message);
    public static void Error(this IAppLog log, string source, string message, Exception? ex = null) => log.Write(LogLevel.Error, source, message, ex);
}

/// <summary>Daily rolling file log with an in-memory ring buffer. Every line passes through the <see cref="Redactor"/> first.</summary>
public sealed class FileAppLog : IAppLog, IDisposable
{
    private readonly string _dir;
    private readonly string _prefix;
    private readonly Redactor _redactor;
    private readonly int _retainDays;
    private readonly LogLevel _minLevel;
    private readonly object _gate = new();
    private readonly Queue<LogLine> _ring = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;

    public FileAppLog(string dir, string prefix, Redactor redactor, int retainDays = 7, LogLevel minLevel = LogLevel.Debug)
    {
        _dir = dir;
        _prefix = prefix;
        _redactor = redactor;
        _retainDays = retainDays;
        _minLevel = minLevel;
        Directory.CreateDirectory(dir);
        Cleanup();
    }

    public event Action<LogLine>? LineWritten;

    public void Write(LogLevel level, string source, string message, Exception? ex = null)
    {
        if (level < _minLevel) return;
        var text = ex is null ? message : message + " | " + ex.GetType().Name + ": " + ex.Message;
        var line = new LogLine(DateTimeOffset.Now, level, source, _redactor.Redact(text));
        lock (_gate)
        {
            _ring.Enqueue(line);
            while (_ring.Count > 2000) _ring.Dequeue();
            try
            {
                EnsureWriter(DateOnly.FromDateTime(line.At.LocalDateTime));
                _writer!.WriteLine($"{line.At:yyyy-MM-dd HH:mm:ss.fff} [{line.Level,-5}] {line.Source}: {line.Message}");
            }
            catch
            {
                // Logging must never throw.
            }
        }
        LineWritten?.Invoke(line);
    }

    public IReadOnlyList<LogLine> Recent()
    {
        lock (_gate) return _ring.ToArray();
    }

    private void EnsureWriter(DateOnly today)
    {
        if (_writer is not null && _writerDate == today) return;
        _writer?.Dispose();
        var path = Path.Combine(_dir, $"{_prefix}-{today:yyyyMMdd}.log");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        _writerDate = today;
    }

    private void Cleanup()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_retainDays);
            foreach (var f in Directory.EnumerateFiles(_dir, _prefix + "-*.log"))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_gate) { _writer?.Dispose(); _writer = null; }
    }
}

/// <summary>Log that only keeps lines in memory (tests, early startup).</summary>
public sealed class MemoryAppLog : IAppLog
{
    private readonly List<LogLine> _lines = new();
    public event Action<LogLine>? LineWritten;

    public void Write(LogLevel level, string source, string message, Exception? ex = null)
    {
        var line = new LogLine(DateTimeOffset.Now, level, source, ex is null ? message : message + " | " + ex.Message);
        lock (_lines) _lines.Add(line);
        LineWritten?.Invoke(line);
    }

    public IReadOnlyList<LogLine> Recent() { lock (_lines) return _lines.ToArray(); }
}
