using System.Text.Json;

namespace OpenMount.Rclone;

public sealed record RcloneLogEvent(string Level, string Message, string? Source, string? Object, DateTimeOffset? Time, bool IsJson)
{
    public bool IsError => Level is "error" or "critical" or "fatal";
}

/// <summary>Parses rclone --use-json-log lines ({"level":"error","msg":"...","source":"...","time":"..."}).</summary>
public static class RcloneLogParser
{
    public static RcloneLogEvent Parse(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                var level = root.TryGetProperty("level", out var l) ? l.GetString() ?? "info" : "info";
                var msg = root.TryGetProperty("msg", out var m) ? m.GetString() ?? string.Empty : trimmed;
                var source = root.TryGetProperty("source", out var s) ? s.GetString() : null;
                var obj = root.TryGetProperty("object", out var o) ? o.GetString() : null;
                DateTimeOffset? time = root.TryGetProperty("time", out var t) && DateTimeOffset.TryParse(t.GetString(), out var dt) ? dt : null;
                return new RcloneLogEvent(level.ToLowerInvariant(), msg, source, obj, time, true);
            }
            catch (JsonException)
            {
                // fall through to text
            }
        }
        var lvl = trimmed.Contains("ERROR", StringComparison.Ordinal) || trimmed.Contains("Fatal", StringComparison.OrdinalIgnoreCase) ? "error"
            : trimmed.Contains("NOTICE", StringComparison.Ordinal) ? "notice" : "info";
        return new RcloneLogEvent(lvl, trimmed, null, null, null, false);
    }
}
