using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenMount.Core.Models;
using OpenMount.Core.Services;
using OpenMount.Core.State;
using Wpf.Ui.Controls;

namespace OpenMount.App.ViewModels;

/// <summary>Callbacks a row needs from its owner; keeps the row free of window/service knowledge.</summary>
public sealed class DriveRowActions
{
    public required Func<DriveRowViewModel, Task> Toggle { get; init; }
    public required Action<DriveRowViewModel> Edit { get; init; }
    public required Action<DriveRowViewModel> Duplicate { get; init; }
    public required Action<DriveRowViewModel> Open { get; init; }
    public required Func<DriveRowViewModel, Task> Delete { get; init; }
    public required Action<DriveRowViewModel> Details { get; init; }
}

/// <summary>One card in the drive list. Everything shown is derived from the supervisor's snapshot (never inferred here).</summary>
public partial class DriveRowViewModel : ObservableObject
{
    private readonly DriveRowActions _actions;

    public DriveRowViewModel(DriveSupervisor supervisor, DriveRowActions actions)
    {
        Supervisor = supervisor;
        _actions = actions;
        Apply(supervisor.Snapshot);
    }

    public DriveSupervisor Supervisor { get; }
    public Drive Drive => Supervisor.Drive;
    public Guid Id => Drive.Id;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _mountPoint = string.Empty;
    [ObservableProperty] private string _typeLabel = string.Empty;
    [ObservableProperty] private SymbolRegular _typeSymbol = SymbolRegular.Globe24;
    [ObservableProperty] private string _account = string.Empty;
    [ObservableProperty] private string _spaceText = string.Empty;
    [ObservableProperty] private bool _isMounted;
    [ObservableProperty] private Chip _lifecycle = new("已断开", ChipKind.Neutral, ChipMark.Ring);
    [ObservableProperty] private Chip? _health;
    [ObservableProperty] private Chip? _sync;
    [ObservableProperty] private IReadOnlyList<Chip> _badges = Array.Empty<Chip>();
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _primaryLabel = "连接";
    [ObservableProperty] private SymbolRegular _primarySymbol = SymbolRegular.Play24;
    [ObservableProperty] private ControlAppearance _primaryAppearance = ControlAppearance.Primary;
    [ObservableProperty] private bool _canToggle = true;
    [ObservableProperty] private bool _canEdit = true;
    [ObservableProperty] private bool _canOpen;
    [ObservableProperty] private DriveSnapshot? _snapshot;

    public void Apply(DriveSnapshot s)
    {
        Snapshot = s;
        var d = Drive;
        var now = DateTimeOffset.Now;
        Name = d.Name;
        MountPoint = d.Mount.MountPointDisplay;
        TypeLabel = StatusPresentation.TypeLabel(d);
        TypeSymbol = StatusPresentation.TypeSymbol(d);
        Account = d.AccountDisplay;
        IsMounted = s.Lifecycle == LifecycleState.Mounted;
        Lifecycle = StatusPresentation.Lifecycle(s, now);
        Health = StatusPresentation.Health(s);
        Sync = StatusPresentation.Sync(s);
        Badges = StatusPresentation.Badges(d, s);
        Summary = StatusPresentation.Summary(s, now);
        SpaceText = s.Lifecycle == LifecycleState.Mounted
            ? (s.Space is { Total: { } total, Free: { } free } ? $"可用 {SizeSuffix.Format(free)} / 共 {SizeSuffix.Format(total)}" : "未知空间")
            : "—";
        if (s.CanDisconnect)
        {
            var cancelling = s.Lifecycle is LifecycleState.Connecting or LifecycleState.Reconnecting;
            PrimaryLabel = cancelling ? "取消" : "断开";
            PrimarySymbol = cancelling ? SymbolRegular.Dismiss24 : SymbolRegular.Stop24;
            PrimaryAppearance = ControlAppearance.Secondary;
        }
        else
        {
            PrimaryLabel = "连接";
            PrimarySymbol = SymbolRegular.Play24;
            PrimaryAppearance = ControlAppearance.Primary;
        }
        CanToggle = s.CanDisconnect || s.CanConnect;
        CanEdit = s.CanEdit;
        CanOpen = s.CanOpen;
    }

    /// <summary>Once a second: only the reconnect countdown changes without a new snapshot.</summary>
    public void Tick()
    {
        if (Snapshot is { Lifecycle: LifecycleState.Reconnecting } s)
        {
            Lifecycle = StatusPresentation.Lifecycle(s, DateTimeOffset.Now);
            Summary = StatusPresentation.Summary(s, DateTimeOffset.Now);
        }
    }

    [RelayCommand] private Task Toggle() => _actions.Toggle(this);
    [RelayCommand] private void Edit() => _actions.Edit(this);
    [RelayCommand] private void Duplicate() => _actions.Duplicate(this);
    [RelayCommand] private void Open() => _actions.Open(this);
    [RelayCommand] private Task Delete() => _actions.Delete(this);
    [RelayCommand] private void Details() => _actions.Details(this);
}
