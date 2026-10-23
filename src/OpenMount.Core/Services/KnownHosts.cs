using System.Security.Cryptography;
using System.Text;
using OpenMount.Core.Models;

namespace OpenMount.Core.Services;

public sealed record HostKeyEntry(string Algorithm, string Base64Key)
{
    public string Sha256Fingerprint
    {
        get
        {
            var hash = SHA256.HashData(Convert.FromBase64String(Base64Key));
            return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
        }
    }

    public string Md5Fingerprint
    {
        get
        {
            var hash = MD5.HashData(Convert.FromBase64String(Base64Key));
            return "MD5:" + string.Join(':', hash.Select(b => b.ToString("x2")));
        }
    }
}

/// <summary>Complete connection context shared by host-key scanning and by the rclone proxy options (review R09).</summary>
public sealed record SshEndpoint(
    string Host,
    int Port,
    ProxyScheme? ProxyScheme,
    string? ProxyHost,
    int? ProxyPort,
    string? ProxyUser,
    string? ProxyPassword,
    TimeSpan Timeout)
{
    public string Display => ProxyHost is null ? $"{Host}:{Port}" : $"{Host}:{Port}（经由 {ProxyScheme?.ToString().ToLowerInvariant()}://{ProxyHost}:{ProxyPort}）";
}

public interface IHostKeyScanner
{
    /// <summary>Collect every host key the server offers (one handshake per algorithm family). Never sends credentials.</summary>
    Task<IReadOnlyList<HostKeyEntry>> ScanAsync(SshEndpoint endpoint, CancellationToken ct);
}

/// <summary>Placeholder until SSH.NET is integrated. Throws so the caller can show "scan unavailable" instead of silently skipping verification.</summary>
public sealed class UnavailableHostKeyScanner : IHostKeyScanner
{
    public Task<IReadOnlyList<HostKeyEntry>> ScanAsync(SshEndpoint endpoint, CancellationToken ct) =>
        throw new NotSupportedException("主机密钥探测组件尚未集成");
}

/// <summary>OpenSSH known_hosts file owned by OpenMount; rclone only reads it.</summary>
public sealed class KnownHostsFile
{
    private readonly string _path;
    private readonly object _gate = new();

    public KnownHostsFile(string path) => _path = path;

    public string Path => _path;

    public static string HostPattern(string host, int port)
    {
        var h = host.Trim().ToLowerInvariant();
        return port == 22 ? h : $"[{h}]:{port}";
    }

    public IReadOnlyList<HostKeyEntry> GetEntries(string host, int port)
    {
        var pattern = HostPattern(host, port);
        lock (_gate)
        {
            if (!File.Exists(_path)) return Array.Empty<HostKeyEntry>();
            var list = new List<HostKeyEntry>();
            foreach (var line in File.ReadAllLines(_path))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || line.StartsWith('#')) continue;
                var hosts = parts[0].Split(',');
                if (hosts.Any(h => string.Equals(h, pattern, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new HostKeyEntry(parts[1], parts[2]));
            }
            return list;
        }
    }

    public bool HasEntries(string host, int port) => GetEntries(host, port).Count > 0;

    public void Trust(string host, int port, IEnumerable<HostKeyEntry> entries)
    {
        var pattern = HostPattern(host, port);
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var sb = new StringBuilder();
            foreach (var e in entries) sb.Append(pattern).Append(' ').Append(e.Algorithm).Append(' ').Append(e.Base64Key).Append('\n');
            File.AppendAllText(_path, sb.ToString(), new UTF8Encoding(false));
        }
    }

    public void Replace(string host, int port, IEnumerable<HostKeyEntry> entries)
    {
        var pattern = HostPattern(host, port);
        lock (_gate)
        {
            var kept = File.Exists(_path)
                ? File.ReadAllLines(_path).Where(l =>
                {
                    var parts = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length < 3 || !parts[0].Split(',').Any(h => string.Equals(h, pattern, StringComparison.OrdinalIgnoreCase));
                }).ToList()
                : new List<string>();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllLines(_path, kept, new UTF8Encoding(false));
        }
        Trust(host, port, entries);
    }
}
