using OpenMount.Core.Engine;
using OpenMount.Core.Models;
using OpenMount.Core.Security;
using OpenMount.Core.Services;

namespace OpenMount.Rclone;

/// <summary>
/// Builds the child-process environment from scratch (review R03): a whitelist of system variables plus the
/// remote definition, RC credentials and the per-drive proxy. Nothing from the parent environment leaks through.
/// </summary>
public static class ProcessEnvironmentBuilder
{
    public static readonly string[] SystemWhitelist =
    {
        "SystemRoot", "windir", "SystemDrive", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "ProgramData",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "ComSpec", "HOMEDRIVE", "HOMEPATH", "USERNAME",
    };

    public static Dictionary<string, string> Build(
        Drive drive,
        DriveSecrets secrets,
        AppSettings settings,
        EffectiveProxy? proxy,
        string rcUser,
        string rcPass,
        string knownHostsPath,
        IDictionary<string, string>? parentEnvironment = null)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parent = parentEnvironment ?? ReadParent();
        foreach (var key in SystemWhitelist)
            if (parent.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) env[key] = v;
        var systemRoot = env.TryGetValue("SystemRoot", out var sr) ? sr : @"C:\Windows";
        env["PATH"] = Path.Combine(systemRoot, "System32") + ";" + systemRoot;

        var prefix = "RCLONE_CONFIG_" + drive.RemoteName.ToUpperInvariant() + "_";
        switch (drive.StorageType)
        {
            case StorageType.WebDav:
            {
                var c = drive.WebDav ?? throw new InvalidOperationException("WebDAV connection missing");
                env[prefix + "TYPE"] = "webdav";
                env[prefix + "URL"] = c.BuildUrl();
                env[prefix + "VENDOR"] = VendorName(c.Vendor);
                switch (c.AuthMode)
                {
                    case WebDavAuthMode.Basic:
                        env[prefix + "USER"] = c.Username ?? string.Empty;
                        if (!string.IsNullOrEmpty(secrets.Password)) env[prefix + "PASS"] = Obscure.Encode(secrets.Password);
                        break;
                    case WebDavAuthMode.Bearer:
                        if (!string.IsNullOrEmpty(secrets.BearerToken)) env[prefix + "BEARER_TOKEN"] = secrets.BearerToken;
                        break;
                }
                if (proxy is not null)
                {
                    var url = proxy.ToUrl();
                    env["HTTPS_PROXY"] = url;
                    env["HTTP_PROXY"] = url;
                    if (proxy.Bypass.Count > 0) env["NO_PROXY"] = string.Join(',', proxy.Bypass);
                }
                break;
            }
            case StorageType.Sftp:
            {
                var c = drive.Sftp ?? throw new InvalidOperationException("SFTP connection missing");
                env[prefix + "TYPE"] = "sftp";
                env[prefix + "HOST"] = c.Host.Trim();
                env[prefix + "PORT"] = c.Port.ToString();
                env[prefix + "USER"] = c.Username;
                if (c.AuthMode is SftpAuthMode.Password or SftpAuthMode.KeyAndPassword && !string.IsNullOrEmpty(secrets.Password))
                    env[prefix + "PASS"] = Obscure.Encode(secrets.Password);
                if (c.AuthMode is SftpAuthMode.Key or SftpAuthMode.KeyAndPassword && !string.IsNullOrEmpty(c.KeyFilePath))
                {
                    env[prefix + "KEY_FILE"] = c.KeyFilePath;
                    if (!string.IsNullOrEmpty(secrets.KeyPassphrase)) env[prefix + "KEY_FILE_PASS"] = Obscure.Encode(secrets.KeyPassphrase);
                }
                if (c.AuthMode == SftpAuthMode.Agent) env[prefix + "KEY_USE_AGENT"] = "true";
                if (c.HostKeyCheck == HostKeyCheckMode.Tofu) env[prefix + "KNOWN_HOSTS_FILE"] = knownHostsPath;
                env[prefix + "SET_MODTIME"] = c.SetModTime ? "true" : "false";
                if (c.DisableHashCheck is true) env[prefix + "DISABLE_HASHCHECK"] = "true";
                env[prefix + "IDLE_TIMEOUT"] = c.IdleTimeoutSeconds + "s";
                if (!string.IsNullOrEmpty(c.ShellType)) env[prefix + "SHELL_TYPE"] = c.ShellType;
                if (c.Connections is { } conn) env[prefix + "CONNECTIONS"] = conn.ToString();
                if (c.Concurrency is { } conc) env[prefix + "CONCURRENCY"] = conc.ToString();
                if (proxy is not null)
                {
                    if (proxy.Scheme == ProxyScheme.Socks5) env[prefix + "SOCKS_PROXY"] = proxy.ToHostPortForm();
                    else env[prefix + "HTTP_PROXY"] = proxy.ToUrl();
                }
                break;
            }
        }

        env["RCLONE_RC_USER"] = rcUser;
        env["RCLONE_RC_PASS"] = rcPass;
        return env;
    }

    /// <summary>Environment for helper rclone processes that touch no remote (e.g. the :memory: ghost-repair mount).</summary>
    public static Dictionary<string, string> BuildMinimal(string rcUser, string rcPass, IDictionary<string, string>? parentEnvironment = null)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parent = parentEnvironment ?? ReadParent();
        foreach (var key in SystemWhitelist)
            if (parent.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) env[key] = v;
        var systemRoot = env.TryGetValue("SystemRoot", out var sr) ? sr : @"C:\Windows";
        env["PATH"] = Path.Combine(systemRoot, "System32") + ";" + systemRoot;
        env["RCLONE_RC_USER"] = rcUser;
        env["RCLONE_RC_PASS"] = rcPass;
        return env;
    }

    public static string VendorName(WebDavVendor vendor) => vendor switch
    {
        WebDavVendor.Nextcloud => "nextcloud",
        WebDavVendor.Owncloud => "owncloud",
        WebDavVendor.Infinitescale => "infinitescale",
        WebDavVendor.Sharepoint => "sharepoint",
        WebDavVendor.SharepointNtlm => "sharepoint-ntlm",
        WebDavVendor.Fastmail => "fastmail",
        WebDavVendor.Rclone => "rclone",
        _ => "other",
    };

    private static Dictionary<string, string> ReadParent()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) d[k] = v;
        return d;
    }

    /// <summary>Names of the environment keys that may hold a secret (for redaction and tests).</summary>
    public static bool IsSecretKey(string key) =>
        key.EndsWith("_PASS", StringComparison.OrdinalIgnoreCase) || key.EndsWith("_KEY_FILE_PASS", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("_BEARER_TOKEN", StringComparison.OrdinalIgnoreCase) || key.EndsWith("_SOCKS_PROXY", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("_HTTP_PROXY", StringComparison.OrdinalIgnoreCase) || key.Equals("HTTPS_PROXY", StringComparison.OrdinalIgnoreCase)
        || key.Equals("HTTP_PROXY", StringComparison.OrdinalIgnoreCase) || key.Equals("RCLONE_RC_PASS", StringComparison.OrdinalIgnoreCase);
}
