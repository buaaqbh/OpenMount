using System.Collections.Concurrent;
using OpenMount.Core.Engine;
using OpenMount.Core.Models;

namespace OpenMount.Core.Services;

public static class CredentialFields
{
    public const string Password = "password";
    public const string KeyPassphrase = "keyPassphrase";
    public const string BearerToken = "bearerToken";
    public const string ProxyPassword = "proxyPassword";

    public static string DriveTarget(Guid driveId, string field) => $"OpenMount/drive/{driveId:D}/{field}";
    public const string ProxyTarget = "OpenMount/proxy/password";
}

/// <summary>Persistent credential storage (Windows Credential Manager). Only used for <see cref="CredentialPolicy.Store"/>.</summary>
public interface ICredentialStore
{
    string? Get(string target);
    void Set(string target, string? userName, string secret);
    void Delete(string target);
}

/// <summary>In-memory store for Session/Prompt policies; cleared when the process exits.</summary>
public sealed class SessionCredentialCache
{
    private readonly ConcurrentDictionary<Guid, DriveSecrets> _cache = new();

    public bool TryGet(Guid driveId, out DriveSecrets secrets) => _cache.TryGetValue(driveId, out secrets!);
    public void Set(Guid driveId, DriveSecrets secrets) => _cache[driveId] = secrets;
    public void Remove(Guid driveId) => _cache.TryRemove(driveId, out _);
    public void Clear() => _cache.Clear();
}

/// <summary>Resolves the secrets required by a drive according to its credential policy and auth mode.</summary>
public sealed class CredentialResolver
{
    private readonly ICredentialStore _store;
    private readonly SessionCredentialCache _session;

    public CredentialResolver(ICredentialStore store, SessionCredentialCache session)
    {
        _store = store;
        _session = session;
    }

    /// <summary>Which secret fields this drive needs for its current auth mode.</summary>
    public static IReadOnlyList<string> RequiredFields(Drive drive)
    {
        var list = new List<string>();
        switch (drive.StorageType)
        {
            case StorageType.WebDav when drive.WebDav is not null:
                if (drive.WebDav.AuthMode == WebDavAuthMode.Bearer) list.Add(CredentialFields.BearerToken);
                // Basic: password is optional (some servers accept empty passwords); nothing strictly required.
                break;
            case StorageType.Sftp when drive.Sftp is not null:
                if (drive.Sftp.AuthMode is SftpAuthMode.Password or SftpAuthMode.KeyAndPassword) list.Add(CredentialFields.Password);
                break;
        }
        return list;
    }

    /// <summary>Returns the secrets, or null when something required is missing (caller then prompts / sets NeedsCredentials).</summary>
    public DriveSecrets? Resolve(Drive drive, string? proxyPassword)
    {
        DriveSecrets secrets;
        if (drive.CredentialPolicy == CredentialPolicy.Store)
        {
            secrets = new DriveSecrets(
                Password: _store.Get(CredentialFields.DriveTarget(drive.Id, CredentialFields.Password)),
                KeyPassphrase: _store.Get(CredentialFields.DriveTarget(drive.Id, CredentialFields.KeyPassphrase)),
                BearerToken: _store.Get(CredentialFields.DriveTarget(drive.Id, CredentialFields.BearerToken)),
                ProxyPassword: proxyPassword);
        }
        else
        {
            if (!_session.TryGet(drive.Id, out var cached)) return null;
            secrets = cached with { ProxyPassword = proxyPassword };
        }

        foreach (var field in RequiredFields(drive))
        {
            var present = field switch
            {
                CredentialFields.Password => !string.IsNullOrEmpty(secrets.Password),
                CredentialFields.KeyPassphrase => !string.IsNullOrEmpty(secrets.KeyPassphrase),
                CredentialFields.BearerToken => !string.IsNullOrEmpty(secrets.BearerToken),
                _ => true,
            };
            if (!present) return null;
        }
        return secrets;
    }

    /// <summary>Persist or cache secrets according to the policy. Store policy writes to the credential store; others never touch disk.</summary>
    public void Save(Drive drive, DriveSecrets secrets, bool rememberForSession)
    {
        switch (drive.CredentialPolicy)
        {
            case CredentialPolicy.Store:
                var user = drive.StorageType == StorageType.WebDav ? drive.WebDav?.Username : drive.Sftp?.Username;
                SetOrDelete(CredentialFields.DriveTarget(drive.Id, CredentialFields.Password), user, secrets.Password);
                SetOrDelete(CredentialFields.DriveTarget(drive.Id, CredentialFields.KeyPassphrase), user, secrets.KeyPassphrase);
                SetOrDelete(CredentialFields.DriveTarget(drive.Id, CredentialFields.BearerToken), user, secrets.BearerToken);
                _session.Remove(drive.Id);
                break;
            case CredentialPolicy.Session:
                _session.Set(drive.Id, secrets);
                DeleteStored(drive.Id);
                break;
            case CredentialPolicy.Prompt:
                if (rememberForSession) _session.Set(drive.Id, secrets); else _session.Remove(drive.Id);
                DeleteStored(drive.Id);
                break;
        }
    }

    public void DeleteStored(Guid driveId)
    {
        _store.Delete(CredentialFields.DriveTarget(driveId, CredentialFields.Password));
        _store.Delete(CredentialFields.DriveTarget(driveId, CredentialFields.KeyPassphrase));
        _store.Delete(CredentialFields.DriveTarget(driveId, CredentialFields.BearerToken));
    }

    public void DeleteAll(Guid driveId)
    {
        DeleteStored(driveId);
        _session.Remove(driveId);
    }

    private void SetOrDelete(string target, string? user, string? value)
    {
        if (string.IsNullOrEmpty(value)) _store.Delete(target);
        else _store.Set(target, user, value);
    }
}
