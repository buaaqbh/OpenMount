using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenMount.Rclone;

public sealed class RcException : Exception
{
    public RcException(int status, string message) : base(message) => Status = status;
    public int Status { get; }
}

public sealed record VfsStats(long BytesUsed, int UploadsInProgress, int UploadsQueued, int ErroredFiles, bool OutOfSpace, int InUse);

public sealed record VfsQueueItem(long Id, string Name, long Size, double Expiry, int Tries, bool Uploading);

/// <summary>Minimal rclone remote-control client bound to one mount process (loopback + Basic auth).</summary>
public sealed class RcClient : IDisposable
{
    private readonly HttpClient _http;

    public RcClient(int port, string user, string pass)
    {
        Port = port;
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + pass)));
    }

    public int Port { get; }

    public async Task<JsonDocument> PostAsync(string path, object? body, TimeSpan? timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        var json = body is null ? "{}" : JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(path, content, cts.Token);
        var text = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            string message = text;
            try
            {
                using var err = JsonDocument.Parse(text);
                if (err.RootElement.TryGetProperty("error", out var e)) message = e.GetString() ?? text;
            }
            catch (JsonException) { }
            throw new RcException((int)response.StatusCode, message);
        }
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    public async Task<bool> NoopAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var _ = await PostAsync("rc/noop", null, timeout, ct);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Asks rclone to exit. The connection usually drops before a response arrives; that is not an error.</summary>
    public async Task QuitAsync(CancellationToken ct)
    {
        try { using var _ = await PostAsync("core/quit", null, TimeSpan.FromSeconds(5), ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { }
    }

    public async Task<VfsStats> VfsStatsAsync(CancellationToken ct)
    {
        using var doc = await PostAsync("vfs/stats", null, TimeSpan.FromSeconds(10), ct);
        var root = doc.RootElement;
        var inUse = root.TryGetProperty("inUse", out var iu) ? iu.GetInt32() : 0;
        if (!root.TryGetProperty("diskCache", out var dc)) return new VfsStats(0, 0, 0, 0, false, inUse);
        return new VfsStats(
            GetLong(dc, "bytesUsed"),
            (int)GetLong(dc, "uploadsInProgress"),
            (int)GetLong(dc, "uploadsQueued"),
            (int)GetLong(dc, "erroredFiles"),
            dc.TryGetProperty("outOfSpace", out var oos) && oos.ValueKind == JsonValueKind.True,
            inUse);
    }

    public async Task<IReadOnlyList<VfsQueueItem>> VfsQueueAsync(CancellationToken ct)
    {
        using var doc = await PostAsync("vfs/queue", null, TimeSpan.FromSeconds(10), ct);
        var list = new List<VfsQueueItem>();
        if (doc.RootElement.TryGetProperty("queue", out var q) && q.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in q.EnumerateArray())
            {
                list.Add(new VfsQueueItem(
                    GetLong(item, "id"),
                    item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                    GetLong(item, "size"),
                    item.TryGetProperty("expiry", out var ex) && ex.ValueKind == JsonValueKind.Number ? ex.GetDouble() : 0,
                    (int)GetLong(item, "tries"),
                    item.TryGetProperty("uploading", out var up) && up.ValueKind == JsonValueKind.True));
            }
        }
        return list;
    }

    public async Task VfsQueueSetExpiryAsync(long id, double expiry, CancellationToken ct)
    {
        using var _ = await PostAsync("vfs/queue-set-expiry", new { id, expiry }, TimeSpan.FromSeconds(10), ct);
    }

    /// <summary>
    /// Health probe: list the backend root through the rc server, which bypasses the VFS directory cache. (A vfs/refresh of the
    /// root was tried first; when the remote is down it marks the cached root listing stale and every program, Explorer
    /// included, gets an I/O error for the drive root until the remote returns.) Returns null on success, otherwise the error text.
    /// </summary>
    public async Task<string?> ListRootAsync(string fs, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var _ = await PostAsync("operations/list", new { fs, remote = "", opt = new { recurse = false, noModTime = true, noMimeType = true } }, timeout, ct);
            return null;
        }
        catch (RcException ex) { return ex.Message; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return "i/o timeout: operations/list"; }
        catch (HttpRequestException ex) { return "rc unavailable: " + ex.Message; }
    }

    public async Task<(long? Total, long? Free)?> AboutAsync(string fs, CancellationToken ct)
    {
        try
        {
            using var doc = await PostAsync("operations/about", new { fs }, TimeSpan.FromSeconds(15), ct);
            var r = doc.RootElement;
            long? total = r.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : null;
            long? free = r.TryGetProperty("free", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt64() : null;
            return (total, free);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>PID of the process answering on this rc endpoint (readiness identity check, design §4.2), or null.</summary>
    public async Task<int?> CorePidAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await PostAsync("core/pid", null, TimeSpan.FromSeconds(5), ct);
            return doc.RootElement.TryGetProperty("pid", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    public async Task<string?> CoreVersionAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await PostAsync("core/version", null, TimeSpan.FromSeconds(5), ct);
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    public void Dispose() => _http.Dispose();
}
