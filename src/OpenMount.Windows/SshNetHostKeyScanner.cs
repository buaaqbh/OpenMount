using System.Net.Sockets;
using OpenMount.Core.Models;
using OpenMount.Core.Services;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OpenMount.Windows;

/// <summary>
/// Trust-on-first-use host key scanner built on SSH.NET (design §10.6). One handshake per host-key family, each
/// restricted to that family's algorithms so every key the server owns is collected (rclone may negotiate any of them);
/// the handshake is aborted as soon as the key arrives, before any authentication, so credentials are never sent.
/// Uses the same proxy as the mount will (review R09).
/// </summary>
public sealed class SshNetHostKeyScanner : IHostKeyScanner
{
    /// <summary>Host-key algorithm families in preference order; names are the SSH wire names SSH.NET offers.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> Families = new[]
    {
        new[] { "ssh-ed25519" },
        new[] { "ecdsa-sha2-nistp256" },
        new[] { "ecdsa-sha2-nistp384" },
        new[] { "ecdsa-sha2-nistp521" },
        new[] { "rsa-sha2-512", "rsa-sha2-256", "ssh-rsa" },
    };

    /// <summary>known_hosts key type for a negotiated host-key algorithm: the RSA signature variants all name the same ssh-rsa key.</summary>
    public static string KnownHostsKeyType(string negotiatedAlgorithm)
    {
        var name = negotiatedAlgorithm.Trim();
        const string certSuffix = "-cert-v01@openssh.com";
        if (name.EndsWith(certSuffix, StringComparison.Ordinal)) name = name[..^certSuffix.Length];
        return name.StartsWith("rsa-sha2-", StringComparison.Ordinal) || name == "ssh-rsa" ? "ssh-rsa" : name;
    }

    public async Task<IReadOnlyList<HostKeyEntry>> ScanAsync(SshEndpoint endpoint, CancellationToken ct)
    {
        var found = new List<HostKeyEntry>();
        Exception? lastFailure = null;
        foreach (var family in Families)
        {
            ct.ThrowIfCancellationRequested();
            var (entry, failure) = await ScanFamilyAsync(endpoint, family, ct);
            if (entry is not null)
            {
                if (!found.Any(f => f.Algorithm == entry.Algorithm && f.Base64Key == entry.Base64Key)) found.Add(entry);
                continue;
            }
            if (failure is null) continue; // family not offered by SSH.NET or not supported by the server
            lastFailure = failure;
            if (found.Count == 0 && IsTransportFailure(failure)) throw failure; // host down / proxy down: no point trying other families
        }
        if (found.Count == 0 && lastFailure is not null) throw lastFailure;
        return found;
    }

    private static async Task<(HostKeyEntry? Entry, Exception? Failure)> ScanFamilyAsync(SshEndpoint endpoint, IReadOnlyList<string> family, CancellationToken ct)
    {
        var info = CreateConnectionInfo(endpoint);
        foreach (var name in info.HostKeyAlgorithms.Keys.ToList())
            if (!family.Contains(name, StringComparer.Ordinal)) info.HostKeyAlgorithms.Remove(name);
        if (info.HostKeyAlgorithms.Count == 0) return (null, null);

        HostKeyEntry? captured = null;
        using var client = new SshClient(info);
        client.HostKeyReceived += (_, e) =>
        {
            captured = new HostKeyEntry(KnownHostsKeyType(e.HostKeyName), Convert.ToBase64String(e.HostKey));
            e.CanTrust = false; // abort before authentication: we only wanted the key
        };
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            client.Disconnect(); // only reached if SSH.NET ever ignored CanTrust
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (captured is not null)
        {
            _ = ex; // expected: the connection is rejected by our own CanTrust = false
        }
        catch (SshConnectionException ex)
        {
            // Key exchange negotiation failed: the server has no key of this family.
            return (null, IsNegotiationFailure(ex) ? null : ex);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
        return (captured, null);
    }

    private static ConnectionInfo CreateConnectionInfo(SshEndpoint endpoint)
    {
        // "none" authentication is never reached: the handshake stops at HostKeyReceived. The user name is a label only.
        var methods = new AuthenticationMethod[] { new NoneAuthenticationMethod("openmount-hostkey-scan") };
        ConnectionInfo info;
        if (endpoint.ProxyHost is { Length: > 0 } proxyHost && endpoint.ProxyScheme is { } scheme)
        {
            var type = scheme == ProxyScheme.Socks5 ? ProxyTypes.Socks5 : ProxyTypes.Http;
            info = new ConnectionInfo(endpoint.Host, endpoint.Port, "openmount-hostkey-scan", type, proxyHost, endpoint.ProxyPort ?? (type == ProxyTypes.Socks5 ? 1080 : 8080),
                endpoint.ProxyUser ?? string.Empty, endpoint.ProxyPassword ?? string.Empty, methods);
        }
        else
        {
            info = new ConnectionInfo(endpoint.Host, endpoint.Port, "openmount-hostkey-scan", methods);
        }
        info.Timeout = endpoint.Timeout;
        return info;
    }

    private static bool IsNegotiationFailure(Exception ex) =>
        ex.Message.Contains("negotiation", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Key exchange", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransportFailure(Exception ex) =>
        ex is SocketException || ex is SshOperationTimeoutException || ex is ProxyException || ex.InnerException is SocketException;
}
