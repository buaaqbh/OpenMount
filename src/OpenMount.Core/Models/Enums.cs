namespace OpenMount.Core.Models;

public enum StorageType { WebDav, Sftp }

public enum WebDavAuthMode { Basic, Anonymous, Bearer }

public enum SftpAuthMode { Password, Key, KeyAndPassword, Agent }

/// <summary>Where credentials live. Only <see cref="Store"/> persists to the Windows Credential Manager.</summary>
public enum CredentialPolicy { Store, Session, Prompt }

public enum MountKind { DriveLetter, Folder }

public enum TlsMode { Verify, CustomCa, Ignore }

public enum HostKeyCheckMode { Tofu, None }

public enum VfsCacheMode { Off, Minimal, Writes, Full }

public enum FileSystemNameMode { Rclone, Ntfs }

public enum WebDavVendor { Other, Nextcloud, Owncloud, Infinitescale, Sharepoint, SharepointNtlm, Fastmail, Rclone }

public enum ProxyMode { None, System, Custom }

public enum ProxyScheme { Http, Socks5 }

public enum RcloneMode { Bundled, Custom }
