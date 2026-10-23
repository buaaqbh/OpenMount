using System.Net.NetworkInformation;

namespace OpenMount.Windows;

/// <summary>Debounced network availability events (design §13).</summary>
public sealed class NetworkMonitor : IDisposable
{
    private readonly TimeSpan _debounce;
    private Timer? _timer;
    private bool _lastAvailable;

    public NetworkMonitor(TimeSpan? debounce = null)
    {
        _debounce = debounce ?? TimeSpan.FromSeconds(3);
        _lastAvailable = NetworkInterface.GetIsNetworkAvailable();
        NetworkChange.NetworkAvailabilityChanged += OnChange;
        NetworkChange.NetworkAddressChanged += OnAddressChange;
    }

    public bool IsAvailable => _lastAvailable;
    public event Action<bool>? AvailabilityChanged;

    private void OnChange(object? sender, NetworkAvailabilityEventArgs e) => Schedule();
    private void OnAddressChange(object? sender, EventArgs e) => Schedule();

    private void Schedule()
    {
        _timer?.Dispose();
        _timer = new Timer(_ =>
        {
            var now = NetworkInterface.GetIsNetworkAvailable();
            var changed = now != _lastAvailable;
            _lastAvailable = now;
            if (changed || now) AvailabilityChanged?.Invoke(now);
        }, null, _debounce, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnChange;
        NetworkChange.NetworkAddressChanged -= OnAddressChange;
        _timer?.Dispose();
    }
}
