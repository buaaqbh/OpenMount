using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace OpenMount.Core.Security;

/// <summary>
/// Layered redaction shared by logs, UI error details, clipboard, export and diagnostics (design §10.5).
/// Layer 1 (structured fields) is the caller's responsibility; this class implements layers 2 and 3.
/// </summary>
public sealed class Redactor
{
    public const string Mask = "***";

    private readonly ConcurrentDictionary<string, byte> _secrets = new(StringComparer.Ordinal);

    private static readonly Regex AuthHeader = new(
        @"(?i)\b(Authorization|Proxy-Authorization|X-Auth-Token|Cookie|Set-Cookie)\s*[:=]\s*[^\r\n]*",
        RegexOptions.Compiled);

    private static readonly Regex EnvLine = new(
        @"(?i)\b(RCLONE_CONFIG_\w+_(?:PASS|KEY_FILE_PASS|BEARER_TOKEN|SOCKS_PROXY|HTTP_PROXY|TOKEN)|RCLONE_RC_PASS|HTTPS?_PROXY|ALL_PROXY)=[^\r\n]*",
        RegexOptions.Compiled);

    // "key": "value with spaces", key='value', key="value"
    private static readonly Regex QuotedKeyValue = new(
        @"(?i)(?<q>[""']?)\b(?<key>pass|password|passwd|pwd|token|secret|bearer_token|key_file_pass|access_key|secret_key|api_key|passphrase)\b\k<q>\s*[:=]\s*(?<v>[""'])(?:\\.|(?!\k<v>).)*\k<v>",
        RegexOptions.Compiled);

    // key=value to end of line (deliberately greedy)
    private static readonly Regex UnquotedKeyValue = new(
        @"(?i)\b(?<key>pass|password|passwd|pwd|token|secret|bearer_token|key_file_pass|passphrase)\b\s*[:=]\s*[^\r\n]*",
        RegexOptions.Compiled);

    private static readonly Regex UrlUserInfo = new(
        @"://(?<user>[^/\s:@]+):(?<pw>[^/\s@]+)@",
        RegexOptions.Compiled);

    public void RegisterSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 4) return;
        _secrets.TryAdd(secret, 0);
        var escaped = Uri.EscapeDataString(secret);
        if (escaped != secret) _secrets.TryAdd(escaped, 0);
        try { _secrets.TryAdd(Obscure.Encode(secret), 0); } catch { /* obscure is deterministic per IV; registered form only helps when the same encoding was used */ }
    }

    public void RegisterObscuredForm(string? obscured)
    {
        if (!string.IsNullOrEmpty(obscured) && obscured.Length >= 4) _secrets.TryAdd(obscured, 0);
    }

    public void UnregisterSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        _secrets.TryRemove(secret, out _);
        _secrets.TryRemove(Uri.EscapeDataString(secret), out _);
    }

    public string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var s = input;
        // Layer 2: exact replacement of known secrets, longest first.
        foreach (var secret in _secrets.Keys.OrderByDescending(k => k.Length))
            s = s.Replace(secret, Mask, StringComparison.Ordinal);
        // Layer 3: conservative patterns.
        s = AuthHeader.Replace(s, m => m.Groups[1].Value + ": " + Mask);
        s = EnvLine.Replace(s, m => m.Groups[1].Value + "=" + Mask);
        s = QuotedKeyValue.Replace(s, m => m.Groups["q"].Value + m.Groups["key"].Value + m.Groups["q"].Value + "=" + Mask);
        s = UnquotedKeyValue.Replace(s, m => m.Groups["key"].Value + "=" + Mask);
        s = UrlUserInfo.Replace(s, m => "://" + m.Groups["user"].Value + ":" + Mask + "@");
        return s;
    }
}
