using OpenMount.Core.State;

namespace OpenMount.Core.Services;

public enum DestructiveOperation
{
    Disconnect,
    Exit,
    Delete,
    ChangeIdentity,
    ChangeCacheModeToUnsafe,
    MigrateCache,
    CleanCache,
    ForceKill,
}

public enum GuardChoice
{
    WaitForUploads,
    StopAndRetain,
    ConnectAndUpload,
    Discard,
    Cancel,
}

public abstract record GuardDecision
{
    private GuardDecision() { }

    public sealed record Allow : GuardDecision;

    /// <summary>The user must pick one of the offered choices; Cancel is always included.</summary>
    public sealed record RequireChoice(IReadOnlyList<GuardChoice> Choices, string Reason) : GuardDecision;

    public sealed record Deny(string Reason) : GuardDecision;
}

/// <summary>
/// The single gate for every destructive operation (FR-22, design §6.3). Pure decision table; no side effects.
/// </summary>
public static class UnsyncedDataGuard
{
    public static GuardDecision Evaluate(DestructiveOperation op, SyncState state)
    {
        if (op == DestructiveOperation.ForceKill) return new GuardDecision.Allow(); // fault handling; never a reason to delete cache
        if (!state.RequiresProtection) return new GuardDecision.Allow();

        var reason = state.Kind == SyncKind.Unknown
            ? "无法确认缓存是否含有未上传的内容"
            : $"仍有 {state.PendingCount} 个文件（约 {SizeSuffix.Format(state.PendingBytes)}）尚未确认上传完成";

        return op switch
        {
            DestructiveOperation.Disconnect or DestructiveOperation.Exit =>
                new GuardDecision.RequireChoice(new[] { GuardChoice.WaitForUploads, GuardChoice.StopAndRetain, GuardChoice.Cancel }, reason),
            DestructiveOperation.Delete or DestructiveOperation.ChangeIdentity or DestructiveOperation.ChangeCacheModeToUnsafe =>
                new GuardDecision.RequireChoice(new[] { GuardChoice.ConnectAndUpload, GuardChoice.Discard, GuardChoice.Cancel }, reason),
            DestructiveOperation.MigrateCache or DestructiveOperation.CleanCache =>
                new GuardDecision.Deny(reason),
            _ => new GuardDecision.Deny(reason),
        };
    }
}
