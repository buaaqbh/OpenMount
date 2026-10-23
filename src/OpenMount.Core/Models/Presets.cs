namespace OpenMount.Core.Models;

public sealed record StoragePreset(
    string Id,
    string DisplayName,
    StorageType Base,
    string? Scheme,
    int? Port,
    string? PathTemplate,
    WebDavVendor Vendor,
    string? Help)
{
    /// <summary>Apply preset defaults to a new drive.</summary>
    public void ApplyTo(Drive drive)
    {
        drive.Preset = Id;
        drive.StorageType = Base;
        if (string.IsNullOrWhiteSpace(drive.Name)) drive.Name = DisplayName;
        if (Base == StorageType.WebDav)
        {
            drive.WebDav ??= new WebDavConnection();
            drive.Sftp = null;
            if (Scheme is not null) drive.WebDav.Scheme = Scheme;
            drive.WebDav.Port = Port;
            drive.WebDav.Vendor = Vendor;
            if (PathTemplate is not null) drive.WebDav.Path = ExpandPath(PathTemplate, drive.WebDav.Username);
        }
        else
        {
            drive.Sftp ??= new SftpConnection();
            drive.WebDav = null;
            if (Port is not null) drive.Sftp.Port = Port.Value;
        }
    }

    public static string ExpandPath(string template, string? username) =>
        template.Replace("<user>", username ?? string.Empty, StringComparison.Ordinal);
}

public static class Presets
{
    public static readonly IReadOnlyList<StoragePreset> All = new[]
    {
        new StoragePreset("webdav", "WebDAV", StorageType.WebDav, "https", null, "/", WebDavVendor.Other, "https://rclone.org/webdav/"),
        new StoragePreset("sftp", "SFTP", StorageType.Sftp, null, 22, null, WebDavVendor.Other, "https://rclone.org/sftp/"),
        new StoragePreset("nextcloud", "Nextcloud", StorageType.WebDav, "https", null, "/remote.php/dav/files/<user>/", WebDavVendor.Nextcloud, "https://docs.nextcloud.com/"),
        new StoragePreset("owncloud", "ownCloud", StorageType.WebDav, "https", null, "/remote.php/webdav/", WebDavVendor.Owncloud, "https://doc.owncloud.com/"),
        new StoragePreset("synology", "Synology", StorageType.WebDav, "https", 5006, "/", WebDavVendor.Other, "https://kb.synology.com/DSM/tutorial/How_to_access_files_on_Synology_NAS_with_WebDAV"),
        new StoragePreset("qnap", "QNAP", StorageType.WebDav, "https", 5001, "/", WebDavVendor.Other, "https://www.qnap.com/en/how-to/tutorial/article/accessing-your-qnap-nas-remotely-with-webdav"),
        new StoragePreset("asustor", "ASUSTOR", StorageType.WebDav, "https", 443, "/", WebDavVendor.Other, "https://www.asustor.com/"),
    };

    public static StoragePreset? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
