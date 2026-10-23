using OpenMount.Core.Models;

namespace OpenMount.Core.Services;

/// <summary>The proxy that actually applies to one drive, with the password already resolved.</summary>
public sealed record EffectiveProxy(ProxyScheme Scheme, string Host, int Port, string? Username, string? Password, IReadOnlyList<string> Bypass)
{
    public string SchemeName => Scheme == ProxyScheme.Socks5 ? "socks5" : "http";

    /// <summary>URL form used for HTTP(S)_PROXY and the rclone SFTP http_proxy option.</summary>
    public string ToUrl()
    {
        var auth = string.IsNullOrEmpty(Username) ? string.Empty : Uri.EscapeDataString(Username) + (string.IsNullOrEmpty(Password) ? string.Empty : ":" + Uri.EscapeDataString(Password)) + "@";
        return $"{SchemeName}://{auth}{Host}:{Port}";
    }

    /// <summary>"user:pass@host:port" form used by the rclone SFTP socks_proxy option.</summary>
    public string ToHostPortForm()
    {
        var auth = string.IsNullOrEmpty(Username) ? string.Empty : Username + (string.IsNullOrEmpty(Password) ? string.Empty : ":" + Password) + "@";
        return $"{auth}{Host}:{Port}";
    }
}

public sealed record SystemProxyInfo(string Host, int Port, IReadOnlyList<string> Bypass);

/// <summary>Reads the WinINET per-user proxy configuration. Implemented in OpenMount.Windows.</summary>
public interface ISystemProxyReader
{
    SystemProxyInfo? Read();
}

public interface IProxyResolver
{
    EffectiveProxy? ResolveFor(Drive drive, AppSettings settings, string? customProxyPassword);
}

public sealed class ProxyResolver : IProxyResolver
{
    private readonly ISystemProxyReader? _system;

    public ProxyResolver(ISystemProxyReader? system) => _system = system;

    public EffectiveProxy? ResolveFor(Drive drive, AppSettings settings, string? customProxyPassword)
    {
        if (!drive.Mount.UseProxy) return null;
        switch (settings.Proxy.Mode)
        {
            case ProxyMode.None:
                return null;
            case ProxyMode.System:
                var sys = _system?.Read();
                return sys is null ? null : new EffectiveProxy(ProxyScheme.Http, sys.Host, sys.Port, null, null, sys.Bypass);
            case ProxyMode.Custom:
                if (string.IsNullOrWhiteSpace(settings.Proxy.Host) || settings.Proxy.Port is null) return null;
                return new EffectiveProxy(settings.Proxy.Scheme, settings.Proxy.Host!, settings.Proxy.Port.Value, settings.Proxy.Username, customProxyPassword, settings.Proxy.Bypass);
            default:
                return null;
        }
    }
}

/// <summary>Builds the SSH connection context used by both the host-key scan and the rclone SFTP options (review R09).</summary>
public static class SshEndpointResolver
{
    public static SshEndpoint Resolve(Drive drive, AppSettings settings, EffectiveProxy? proxy)
    {
        var c = drive.Sftp ?? throw new InvalidOperationException("not an SFTP drive");
        return new SshEndpoint(
            c.Host.Trim(),
            c.Port,
            proxy?.Scheme,
            proxy?.Host,
            proxy?.Port,
            proxy?.Username,
            proxy?.Password,
            TimeSpan.FromSeconds(Math.Max(5, settings.Advanced.RemoteProbeTimeoutSeconds)));
    }
}
