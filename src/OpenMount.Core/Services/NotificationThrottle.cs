namespace OpenMount.Core.Services;

/// <summary>
/// Desktop-notification throttle (FR-09.6): a warning/error for the same drive and kind is shown at most once per window,
/// while list state keeps updating; success/info notifications always pass. Thread-safe.
/// </summary>
public sealed class NotificationThrottle
{
    private readonly TimeSpan _window;
    private readonly Dictionary<string, DateTimeOffset> _lastShown = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public NotificationThrottle(TimeSpan? window = null) => _window = window ?? TimeSpan.FromMinutes(10);

    /// <summary>True when the notification should be shown now.</summary>
    public bool ShouldShow(string title, NotificationKind kind, DateTimeOffset now)
    {
        if (kind is not (NotificationKind.Warning or NotificationKind.Error)) return true;
        var key = title + "|" + kind;
        lock (_gate)
        {
            if (_lastShown.TryGetValue(key, out var last) && now - last < _window) return false;
            _lastShown[key] = now;
            return true;
        }
    }

    /// <summary>Forget the history for one drive (e.g. after it was deleted or renamed).</summary>
    public void Reset(string title)
    {
        lock (_gate)
        {
            foreach (var key in _lastShown.Keys.Where(k => k.StartsWith(title + "|", StringComparison.Ordinal)).ToList())
                _lastShown.Remove(key);
        }
    }
}
