using System.Net;
using System.Net.Sockets;
using OpenMount.Core.Services;
using OpenMount.Windows;
using Xunit;

namespace OpenMount.Tests;

public class HostKeyScannerTests
{
    [Theory]
    [InlineData("ssh-ed25519", "ssh-ed25519")]
    [InlineData("ecdsa-sha2-nistp256", "ecdsa-sha2-nistp256")]
    [InlineData("rsa-sha2-512", "ssh-rsa")]
    [InlineData("rsa-sha2-256", "ssh-rsa")]
    [InlineData("ssh-rsa", "ssh-rsa")]
    [InlineData("ssh-ed25519-cert-v01@openssh.com", "ssh-ed25519")]
    [InlineData("rsa-sha2-512-cert-v01@openssh.com", "ssh-rsa")]
    public void NegotiatedAlgorithmMapsToKnownHostsKeyType(string negotiated, string expected) =>
        Assert.Equal(expected, SshNetHostKeyScanner.KnownHostsKeyType(negotiated));

    [Fact]
    public void FamiliesCoverEveryKeyTypeRcloneCanNegotiate()
    {
        var all = SshNetHostKeyScanner.Families.SelectMany(f => f).ToList();
        Assert.Contains("ssh-ed25519", all);
        Assert.Contains("ecdsa-sha2-nistp256", all);
        Assert.Contains("rsa-sha2-256", all);
        Assert.Contains("ssh-rsa", all);
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public async Task ClosedPortFailsFastWithATransportError()
    {
        // Reserve a loopback port and close it so nothing listens there.
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        var scanner = new SshNetHostKeyScanner();
        var endpoint = new SshEndpoint("127.0.0.1", port, null, null, null, null, null, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(() => scanner.ScanAsync(endpoint, CancellationToken.None));
    }
}
