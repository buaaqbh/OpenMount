using CommunityToolkit.Mvvm.ComponentModel;
using OpenMount.Core.Models;
using OpenMount.Core.Services;
using OpenMount.Rclone;

namespace OpenMount.App.ViewModels;

public sealed record EnumItem<T>(T Value, string Label) where T : struct;

/// <summary>Form state for the New/Edit/Duplicate Drive dialog. Secrets are read from the PasswordBoxes by the view, never stored here.</summary>
public partial class NewDriveViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Drive? _editing;
    /// <summary>Where pass-through fields (not shown in the form) come from: the drive being edited or the duplication template.</summary>
    private readonly Drive? _source;

    public NewDriveViewModel(AppServices services, Drive? editing, Drive? template = null)
    {
        _services = services;
        _editing = editing;
        _source = editing ?? template;
        IsDuplicate = editing is null && template is not null;
        AvailableLetters = services.DriveLetters.GetAvailable().Select(c => c + ":").ToList();
        if (editing is not null && editing.Mount.Kind == MountKind.DriveLetter && !string.IsNullOrEmpty(editing.Mount.DriveLetter))
        {
            var l = editing.Mount.DriveLetter.ToUpperInvariant() + ":";
            if (!AvailableLetters.Contains(l)) AvailableLetters.Insert(0, l);
        }
        if (_source is null) LoadDefaults(Presets.Find("webdav")!);
        else LoadFrom(_source);
        if (template is not null)
        {
            // FR-01.8: new id, same connection and options; auto-connect off, letter re-chosen, credentials re-entered.
            Name = Truncate(template.Name + " 副本", 32);
            AutoConnectAtStart = false;
            var templateLetter = template.Mount.DriveLetter is null ? null : template.Mount.DriveLetter.ToUpperInvariant() + ":";
            DriveLetter = AvailableLetters.FirstOrDefault(l => l != templateLetter) ?? AvailableLetters.FirstOrDefault();
            if (!UseDriveLetter) FolderPath = null;
            Refresh();
        }
    }

    public bool IsEditing => _editing is not null;
    public bool IsDuplicate { get; }
    public string Title => IsEditing ? "编辑驱动器" : IsDuplicate ? "复制驱动器" : "新建驱动器";
    public string SecretHint => IsEditing ? "留空表示保持原凭据不变" : IsDuplicate ? "复制出的驱动器需要重新输入凭据" : string.Empty;
    public IReadOnlyList<StoragePreset> StoragePresets { get; } = Presets.All;
    public List<string> AvailableLetters { get; }

    public IReadOnlyList<EnumItem<WebDavAuthMode>> WebDavAuthModes { get; } = new[]
    {
        new EnumItem<WebDavAuthMode>(WebDavAuthMode.Basic, "账号密码"),
        new EnumItem<WebDavAuthMode>(WebDavAuthMode.Anonymous, "匿名"),
        new EnumItem<WebDavAuthMode>(WebDavAuthMode.Bearer, "Bearer Token"),
    };

    public IReadOnlyList<EnumItem<SftpAuthMode>> SftpAuthModes { get; } = new[]
    {
        new EnumItem<SftpAuthMode>(SftpAuthMode.Password, "密码"),
        new EnumItem<SftpAuthMode>(SftpAuthMode.Key, "SSH 私钥文件"),
        new EnumItem<SftpAuthMode>(SftpAuthMode.KeyAndPassword, "私钥 + 密码"),
    };

    public IReadOnlyList<EnumItem<WebDavVendor>> Vendors { get; } = new[]
    {
        new EnumItem<WebDavVendor>(WebDavVendor.Other, "其它 / 自动"),
        new EnumItem<WebDavVendor>(WebDavVendor.Nextcloud, "Nextcloud"),
        new EnumItem<WebDavVendor>(WebDavVendor.Owncloud, "ownCloud"),
        new EnumItem<WebDavVendor>(WebDavVendor.Infinitescale, "ownCloud Infinite Scale"),
        new EnumItem<WebDavVendor>(WebDavVendor.Sharepoint, "SharePoint Online"),
        new EnumItem<WebDavVendor>(WebDavVendor.SharepointNtlm, "SharePoint (NTLM)"),
        new EnumItem<WebDavVendor>(WebDavVendor.Fastmail, "Fastmail"),
        new EnumItem<WebDavVendor>(WebDavVendor.Rclone, "rclone serve webdav"),
    };

    public IReadOnlyList<EnumItem<TlsMode>> TlsModes { get; } = new[]
    {
        new EnumItem<TlsMode>(TlsMode.Verify, "校验"),
        new EnumItem<TlsMode>(TlsMode.CustomCa, "自定义 CA 证书"),
        new EnumItem<TlsMode>(TlsMode.Ignore, "忽略校验（不推荐）"),
    };

    public IReadOnlyList<EnumItem<CredentialPolicy>> CredentialPolicies { get; } = new[]
    {
        new EnumItem<CredentialPolicy>(CredentialPolicy.Store, "保存在凭据管理器"),
        new EnumItem<CredentialPolicy>(CredentialPolicy.Session, "仅本次运行期间记住"),
        new EnumItem<CredentialPolicy>(CredentialPolicy.Prompt, "每次连接时询问"),
    };

    public IReadOnlyList<EnumItem<VfsCacheMode>> CacheModes { get; } = new[]
    {
        new EnumItem<VfsCacheMode>(VfsCacheMode.Full, "full（推荐）"),
        new EnumItem<VfsCacheMode>(VfsCacheMode.Writes, "writes"),
        new EnumItem<VfsCacheMode>(VfsCacheMode.Minimal, "minimal"),
        new EnumItem<VfsCacheMode>(VfsCacheMode.Off, "off"),
    };

    // ---- storage ----
    [ObservableProperty] private StoragePreset? _selectedPreset;
    [ObservableProperty] private bool _isWebDav = true;
    [ObservableProperty] private bool _isSftp;

    // ---- drive ----
    [ObservableProperty] private bool _useDriveLetter = true;
    [ObservableProperty] private string? _driveLetter;
    [ObservableProperty] private string? _folderPath;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private CredentialPolicy _credentialPolicy = CredentialPolicy.Store;
    [ObservableProperty] private bool _autoConnectAtStart = true;
    [ObservableProperty] private bool _autoReconnect = true;
    [ObservableProperty] private bool _readOnly;
    [ObservableProperty] private bool _localDisk;
    [ObservableProperty] private bool _useProxy;

    // ---- webdav ----
    [ObservableProperty] private bool _https = true;
    [ObservableProperty] private string _webDavHost = string.Empty;
    [ObservableProperty] private string _webDavPort = string.Empty;
    [ObservableProperty] private string _webDavPath = "/";
    [ObservableProperty] private WebDavAuthMode _webDavAuthMode = WebDavAuthMode.Basic;
    [ObservableProperty] private string _webDavUsername = string.Empty;
    [ObservableProperty] private WebDavVendor _vendor = WebDavVendor.Other;
    [ObservableProperty] private TlsMode _tlsMode = TlsMode.Verify;
    [ObservableProperty] private string? _caCertPath;
    [ObservableProperty] private string _timeoutSeconds = "60";

    // ---- sftp ----
    [ObservableProperty] private string _sftpHost = string.Empty;
    [ObservableProperty] private string _sftpPort = "22";
    [ObservableProperty] private string _sftpPath = string.Empty;
    [ObservableProperty] private string _sftpUsername = string.Empty;
    [ObservableProperty] private SftpAuthMode _sftpAuthMode = SftpAuthMode.Password;
    [ObservableProperty] private string? _keyFilePath;
    [ObservableProperty] private bool _verifyHostKey = true;
    [ObservableProperty] private bool _setModTime = true;
    [ObservableProperty] private bool _disableHashCheck;

    // ---- advanced ----
    [ObservableProperty] private VfsCacheMode _cacheMode = VfsCacheMode.Full;
    [ObservableProperty] private string? _cacheCleanupTarget;
    [ObservableProperty] private string? _cacheMaxAge;
    [ObservableProperty] private string? _reportedCapacity;
    [ObservableProperty] private bool _reportAsNtfs;
    [ObservableProperty] private bool _dotHidden;
    [ObservableProperty] private string? _bwLimitUp;
    [ObservableProperty] private string? _bwLimitDown;

    // ---- derived ----
    [ObservableProperty] private string _preview = string.Empty;
    [ObservableProperty] private string _sftpPathHint = string.Empty;
    [ObservableProperty] private string _commandLinePreview = string.Empty;
    [ObservableProperty] private string _validationText = string.Empty;
    [ObservableProperty] private bool _showPlainHttpWarning;

    public bool WebDavUsesBasic => WebDavAuthMode == WebDavAuthMode.Basic;
    public bool WebDavUsesBearer => WebDavAuthMode == WebDavAuthMode.Bearer;
    public bool SftpUsesPassword => SftpAuthMode is SftpAuthMode.Password or SftpAuthMode.KeyAndPassword;
    public bool SftpUsesKey => SftpAuthMode is SftpAuthMode.Key or SftpAuthMode.KeyAndPassword;
    public bool TlsUsesCustomCa => TlsMode == TlsMode.CustomCa;

    partial void OnSelectedPresetChanged(StoragePreset? value)
    {
        if (value is null || IsEditing) return;
        LoadDefaults(value);
    }

    partial void OnWebDavAuthModeChanged(WebDavAuthMode value) { OnPropertyChanged(nameof(WebDavUsesBasic)); OnPropertyChanged(nameof(WebDavUsesBearer)); Refresh(); }
    partial void OnSftpAuthModeChanged(SftpAuthMode value) { OnPropertyChanged(nameof(SftpUsesPassword)); OnPropertyChanged(nameof(SftpUsesKey)); Refresh(); }
    partial void OnTlsModeChanged(TlsMode value) { OnPropertyChanged(nameof(TlsUsesCustomCa)); Refresh(); }
    partial void OnLocalDiskChanged(bool value) { if (!value) UseDriveLetter = true; Refresh(); }
    partial void OnHttpsChanged(bool value) => Refresh();
    partial void OnWebDavHostChanged(string value) => Refresh();
    partial void OnWebDavPortChanged(string value) => Refresh();
    partial void OnWebDavPathChanged(string value) => Refresh();
    partial void OnWebDavUsernameChanged(string value)
    {
        if (SelectedPreset is { PathTemplate: not null } p && p.PathTemplate.Contains("<user>") && !IsEditing)
            WebDavPath = StoragePreset.ExpandPath(p.PathTemplate, value);
        Refresh();
    }
    partial void OnSftpHostChanged(string value) => Refresh();
    partial void OnSftpPortChanged(string value) => Refresh();
    partial void OnSftpPathChanged(string value) => Refresh();
    partial void OnSftpUsernameChanged(string value) => Refresh();
    partial void OnNameChanged(string value) => Refresh();
    partial void OnDriveLetterChanged(string? value) => Refresh();
    partial void OnReadOnlyChanged(bool value) => Refresh();
    partial void OnVerifyHostKeyChanged(bool value) => Refresh();

    private void LoadDefaults(StoragePreset preset)
    {
        IsWebDav = preset.Base == StorageType.WebDav;
        IsSftp = !IsWebDav;
        if (string.IsNullOrWhiteSpace(Name) || StoragePresets.Any(p => p.DisplayName == Name)) Name = preset.DisplayName;
        DriveLetter ??= AvailableLetters.FirstOrDefault();
        if (IsWebDav)
        {
            Https = preset.Scheme != "http";
            WebDavPort = preset.Port?.ToString() ?? string.Empty;
            WebDavPath = preset.PathTemplate is null ? "/" : StoragePreset.ExpandPath(preset.PathTemplate, WebDavUsername);
            Vendor = preset.Vendor;
        }
        else
        {
            SftpPort = (preset.Port ?? 22).ToString();
        }
        Refresh();
    }

    private void LoadFrom(Drive d)
    {
        SelectedPreset = Presets.Find(d.Preset) ?? Presets.Find(d.StorageType == StorageType.WebDav ? "webdav" : "sftp");
        IsWebDav = d.StorageType == StorageType.WebDav;
        IsSftp = !IsWebDav;
        UseDriveLetter = d.Mount.Kind == MountKind.DriveLetter;
        DriveLetter = d.Mount.DriveLetter is null ? null : d.Mount.DriveLetter.ToUpperInvariant() + ":";
        FolderPath = d.Mount.FolderPath;
        Name = d.Name;
        CredentialPolicy = d.CredentialPolicy;
        AutoConnectAtStart = d.Mount.AutoConnectAtStart;
        AutoReconnect = d.Mount.AutoReconnect;
        ReadOnly = d.Mount.ReadOnly;
        LocalDisk = !d.Mount.NetworkMode;
        UseProxy = d.Mount.UseProxy;
        if (d.WebDav is { } w)
        {
            Https = w.Scheme != "http";
            WebDavHost = w.Host;
            WebDavPort = w.Port?.ToString() ?? string.Empty;
            WebDavPath = w.Path;
            WebDavAuthMode = w.AuthMode;
            WebDavUsername = w.Username ?? string.Empty;
            Vendor = w.Vendor;
            TlsMode = w.TlsMode;
            CaCertPath = w.CaCertPath;
            TimeoutSeconds = w.TimeoutSeconds.ToString();
        }
        if (d.Sftp is { } s)
        {
            SftpHost = s.Host;
            SftpPort = s.Port.ToString();
            SftpPath = s.Path;
            SftpUsername = s.Username;
            SftpAuthMode = s.AuthMode;
            KeyFilePath = s.KeyFilePath;
            VerifyHostKey = s.HostKeyCheck == HostKeyCheckMode.Tofu;
            SetModTime = s.SetModTime;
            DisableHashCheck = s.DisableHashCheck == true;
        }
        var a = d.Advanced;
        CacheMode = a.VfsCacheMode ?? VfsCacheMode.Full;
        CacheCleanupTarget = a.CacheCleanupTarget;
        CacheMaxAge = a.CacheMaxAge;
        ReportedCapacity = a.ReportedCapacity;
        ReportAsNtfs = a.FileSystemName == FileSystemNameMode.Ntfs;
        DotHidden = a.DotHidden;
        BwLimitUp = a.BwLimitUp;
        BwLimitDown = a.BwLimitDown;
        Refresh();
    }

    /// <summary>Build the drive from the form. Returns a fresh object (editing keeps the original id).</summary>
    public Drive BuildDrive()
    {
        var d = _editing?.Clone() ?? new Drive();
        d.Name = Name.Trim();
        d.Preset = SelectedPreset?.Id;
        d.StorageType = IsWebDav ? StorageType.WebDav : StorageType.Sftp;
        d.CredentialPolicy = CredentialPolicy;
        d.Mount = new MountOptions
        {
            Kind = UseDriveLetter ? MountKind.DriveLetter : MountKind.Folder,
            DriveLetter = UseDriveLetter ? DriveLetter?.TrimEnd(':').ToUpperInvariant() : null,
            FolderPath = UseDriveLetter ? null : FolderPath?.Trim(),
            NetworkMode = !LocalDisk,
            ReadOnly = ReadOnly,
            AutoConnectAtStart = AutoConnectAtStart,
            AutoReconnect = AutoReconnect,
            UseProxy = UseProxy,
            OpenExplorer = _source?.Mount.OpenExplorer,
        };
        if (IsWebDav)
        {
            d.Sftp = null;
            d.WebDav = new WebDavConnection
            {
                Scheme = Https ? "https" : "http",
                Host = WebDavHost.Trim(),
                Port = int.TryParse(WebDavPort, out var p) ? p : null,
                Path = WebDavPath,
                AuthMode = WebDavAuthMode,
                Username = WebDavAuthMode == WebDavAuthMode.Basic ? WebDavUsername.Trim() : null,
                Vendor = Vendor,
                TlsMode = Https ? TlsMode : TlsMode.Verify,
                CaCertPath = TlsMode == TlsMode.CustomCa ? CaCertPath : null,
                TimeoutSeconds = int.TryParse(TimeoutSeconds, out var t) ? t : 60,
            };
        }
        else
        {
            d.WebDav = null;
            d.Sftp = new SftpConnection
            {
                Host = SftpHost.Trim(),
                Port = int.TryParse(SftpPort, out var p) ? p : 0,
                Path = SftpPath,
                Username = SftpUsername.Trim(),
                AuthMode = SftpAuthMode,
                KeyFilePath = SftpUsesKey ? KeyFilePath : null,
                HostKeyCheck = VerifyHostKey ? HostKeyCheckMode.Tofu : HostKeyCheckMode.None,
                SetModTime = SetModTime,
                DisableHashCheck = DisableHashCheck ? true : null,
                IdleTimeoutSeconds = _source?.Sftp?.IdleTimeoutSeconds ?? 60,
                ShellType = _source?.Sftp?.ShellType,
                Connections = _source?.Sftp?.Connections,
                Concurrency = _source?.Sftp?.Concurrency,
            };
        }
        d.Advanced = new AdvancedOptions
        {
            VfsCacheMode = CacheMode == VfsCacheMode.Full ? null : CacheMode,
            CacheCleanupTarget = Clean(CacheCleanupTarget),
            CacheMaxAge = Clean(CacheMaxAge),
            ReportedCapacity = Clean(ReportedCapacity),
            FileSystemName = ReportAsNtfs ? FileSystemNameMode.Ntfs : null,
            DotHidden = DotHidden,
            BwLimitUp = Clean(BwLimitUp),
            BwLimitDown = Clean(BwLimitDown),
            CacheMinFreeSpace = _source?.Advanced.CacheMinFreeSpace,
            DirCacheTime = _source?.Advanced.DirCacheTime,
            WriteBack = _source?.Advanced.WriteBack,
            ReadChunkSize = _source?.Advanced.ReadChunkSize,
            ReadAhead = _source?.Advanced.ReadAhead,
            BufferSize = _source?.Advanced.BufferSize,
            Transfers = _source?.Advanced.Transfers,
            NoModTime = _source?.Advanced.NoModTime ?? false,
        };
        d.RemoteIdentityHash = RemoteIdentity.ComputeHash(d);
        return d;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public IReadOnlyList<ValidationError> Validate(Drive drive) => _services.Validator.Validate(drive, _services.Repository.Drives);

    public void Refresh()
    {
        try
        {
            var d = BuildDrive();
            if (IsWebDav)
            {
                Preview = d.WebDav!.BuildUrl();
                ShowPlainHttpWarning = !Https;
            }
            else
            {
                var r = SftpPathResolver.Resolve(SftpPath);
                SftpPathHint = r.Display;
                Preview = $"sftp://{SftpUsername}@{SftpHost}:{SftpPort}/{(r.Meaning == SftpPathMeaning.Home ? "~" : r.Normalized.TrimStart('/'))}";
                ShowPlainHttpWarning = false;
            }
            if (!string.IsNullOrEmpty(d.Mount.DriveLetter) || !string.IsNullOrEmpty(d.Mount.FolderPath))
                CommandLinePreview = RcloneArgsBuilder.Build(d, _services.Settings.Current, "<cache>", 0).CommandLine;
        }
        catch (Exception ex)
        {
            Preview = ex.Message;
        }
    }
}
