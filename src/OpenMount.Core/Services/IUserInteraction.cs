using OpenMount.Core.Engine;
using OpenMount.Core.Models;
using OpenMount.Core.State;

namespace OpenMount.Core.Services;

public enum NotificationKind { Info, Success, Warning, Error }

public sealed record CredentialPromptResult(DriveSecrets Secrets, bool RememberForSession);

/// <summary>Everything the supervisor needs from the user. Implemented by the WPF layer; a headless implementation is used by tests.</summary>
public interface IUserInteraction
{
    /// <summary>Returns null when the user cancels.</summary>
    Task<CredentialPromptResult?> PromptCredentialsAsync(Drive drive, CancellationToken ct);

    /// <summary>TOFU confirmation (isChange=false) or key-change warning (isChange=true). Returns true to trust.</summary>
    Task<bool> ConfirmHostKeysAsync(Drive drive, SshEndpoint endpoint, IReadOnlyList<HostKeyEntry> keys, bool isChange, CancellationToken ct);

    /// <summary>Unsynced-data dialog. Must return one of <paramref name="choices"/>.</summary>
    Task<GuardChoice> ChooseUnsyncedActionAsync(Drive drive, DestructiveOperation op, SyncState state, IReadOnlyList<GuardChoice> choices, string reason, CancellationToken ct);

    /// <summary>Asked when waiting for uploads makes no progress. Returns WaitForUploads (keep waiting), StopAndRetain or Cancel.</summary>
    Task<GuardChoice> ChooseWhenNoProgressAsync(Drive drive, StopProgressInfo progress, CancellationToken ct);

    /// <summary>Final confirmation for the separately named "discard unsynced data" command.</summary>
    Task<bool> ConfirmDiscardAsync(Drive drive, SyncState state, CancellationToken ct);

    void Notify(string title, string message, NotificationKind kind);
}
