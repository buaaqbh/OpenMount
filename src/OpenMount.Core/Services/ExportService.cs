using System.Text.Json;
using System.Text.Json.Serialization;
using OpenMount.Core.Engine;
using OpenMount.Core.Models;
using OpenMount.Core.Security;

namespace OpenMount.Core.Services;

/// <summary>Plain (no credentials) and encrypted (with credentials) configuration export/import (FR-15).</summary>
public sealed class ExportService
{
    public sealed class ExportDocument
    {
        public string Format { get; set; } = ExportCrypto.Format;
        public int Version { get; set; } = ExportCrypto.Version;
        public bool Encrypted { get; set; }
        public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
        public JsonElement Drives { get; set; }
        public AppSettings? Settings { get; set; }
        /// <summary>Only present inside the encrypted payload.</summary>
        public List<ExportedCredential>? Credentials { get; set; }
    }

    public sealed class ExportedCredential
    {
        public Guid DriveId { get; set; }
        public string Field { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private readonly ICredentialStore _store;

    public ExportService(ICredentialStore store) => _store = store;

    public string ExportPlain(IEnumerable<Drive> drives, AppSettings settings)
    {
        var doc = new ExportDocument
        {
            Encrypted = false,
            Drives = JsonSerializer.SerializeToElement(JsonDocument.Parse(DriveRepository.Serialize(StripStatus(drives))).RootElement),
            Settings = StripSecrets(settings),
        };
        return JsonSerializer.Serialize(doc, JsonDefaults.Options);
    }

    public string ExportEncrypted(IEnumerable<Drive> drives, AppSettings settings, string password)
    {
        var list = drives.ToList();
        var creds = new List<ExportedCredential>();
        foreach (var d in list.Where(d => d.CredentialPolicy == CredentialPolicy.Store))
        {
            foreach (var field in new[] { CredentialFields.Password, CredentialFields.KeyPassphrase, CredentialFields.BearerToken })
            {
                var v = _store.Get(CredentialFields.DriveTarget(d.Id, field));
                if (!string.IsNullOrEmpty(v)) creds.Add(new ExportedCredential { DriveId = d.Id, Field = field, Value = v });
            }
        }
        var inner = new ExportDocument
        {
            Encrypted = true,
            Drives = JsonSerializer.SerializeToElement(JsonDocument.Parse(DriveRepository.Serialize(StripStatus(list))).RootElement),
            Settings = StripSecrets(settings),
            Credentials = creds,
        };
        return ExportCrypto.Encrypt(JsonSerializer.Serialize(inner, JsonDefaults.Options), password);
    }

    public sealed record ImportResult(List<Drive> Drives, AppSettings? Settings, List<ExportedCredential> Credentials);

    /// <summary>Parse an export file. Encrypted files require a password. Validation is the caller's job (same validator as the UI).</summary>
    public static ImportResult Import(string json, string? password)
    {
        using var probe = JsonDocument.Parse(json);
        var root = probe.RootElement;
        var encrypted = root.TryGetProperty("encrypted", out var encEl) && encEl.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("format", out var fmt) && fmt.GetString() != ExportCrypto.Format)
            throw new ExportFormatException("不是 OpenMount 导出文件");
        string payload = json;
        if (encrypted)
        {
            if (string.IsNullOrEmpty(password)) throw new ExportFormatException("该文件已加密，需要口令");
            payload = ExportCrypto.Decrypt(json, password);
        }
        var doc = JsonSerializer.Deserialize<ExportDocument>(payload, JsonDefaults.Options) ?? throw new ExportFormatException("文件内容无效");
        if (doc.Version != ExportCrypto.Version) throw new ExportFormatException($"不支持的导出文件版本 {doc.Version}");
        var drives = DriveRepository.Parse(doc.Drives.GetRawText());
        foreach (var d in drives) d.Mount.AutoConnectAtStart = false; // FR-15.3: re-check on this machine first (trust, letters, credentials)
        return new ImportResult(drives, doc.Settings, doc.Credentials ?? new List<ExportedCredential>());
    }

    private static IEnumerable<Drive> StripStatus(IEnumerable<Drive> drives)
    {
        foreach (var d in drives)
        {
            var c = d.Clone();
            c.Last = null;
            c.Recovery = null;
            yield return c;
        }
    }

    private static AppSettings StripSecrets(AppSettings s)
    {
        var json = JsonSerializer.Serialize(s, JsonDefaults.Options);
        var copy = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options)!;
        copy.Proxy.Username = null; // proxy password is never in settings; username is dropped too to keep exports impersonal
        return copy;
    }
}
