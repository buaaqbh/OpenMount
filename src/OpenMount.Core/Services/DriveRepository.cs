using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenMount.Core.Logging;
using OpenMount.Core.Models;

namespace OpenMount.Core.Services;

/// <summary>drives.json persistence: locked, atomic (temp + replace), with a .bak copy. Schema v2.</summary>
public sealed class DriveRepository
{
    public const int CurrentSchemaVersion = 2;

    private readonly AppPaths _paths;
    private readonly IAppLog _log;
    private readonly object _gate = new();
    private List<Drive> _drives = new();

    public DriveRepository(AppPaths paths, IAppLog log)
    {
        _paths = paths;
        _log = log;
    }

    public event Action? Changed;

    /// <summary>Non-null when drives.json belongs to a newer OpenMount: the file is left untouched and saving is refused.</summary>
    public string? ReadOnlyReason { get; private set; }

    public IReadOnlyList<Drive> Drives
    {
        get { lock (_gate) return _drives.OrderBy(d => d.SortIndex).ThenBy(d => d.CreatedAt).ToList(); }
    }

    public Drive? Find(Guid id)
    {
        lock (_gate) return _drives.FirstOrDefault(d => d.Id == id);
    }

    public void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_paths.DrivesFile)) { _drives = new List<Drive>(); return; }
            try
            {
                _drives = Parse(File.ReadAllText(_paths.DrivesFile));
                ReadOnlyReason = null;
            }
            catch (ConfigNewerVersionException newer)
            {
                // Written by a newer OpenMount: show nothing, refuse to save, never replace it with the backup (SRS §8.3).
                _drives = new List<Drive>();
                ReadOnlyReason = newer.Message;
                _log.Error("repo", newer.Message, newer);
                throw;
            }
            catch (Exception ex)
            {
                _log.Error("repo", "drives.json 损坏，尝试从备份恢复", ex);
                var bak = _paths.DrivesFile + ".bak";
                if (File.Exists(bak))
                {
                    try { _drives = Parse(File.ReadAllText(bak)); return; }
                    catch (Exception ex2) { _log.Error("repo", "备份也无法读取", ex2); }
                }
                throw new ConfigCorruptException("drives.json 损坏且无法从备份恢复", ex);
            }
        }
    }

    public static List<Drive> Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<DrivesDocument>(json, JsonDefaults.Options) ?? new DrivesDocument();
        if (doc.SchemaVersion > CurrentSchemaVersion) throw new ConfigNewerVersionException(doc.SchemaVersion, CurrentSchemaVersion);
        var list = new List<Drive>();
        foreach (var d in doc.Drives)
        {
            var drive = DriveDocument.ToDrive(d, doc.SchemaVersion);
            drive.RemoteIdentityHash = RemoteIdentity.ComputeHash(drive);
            list.Add(drive);
        }
        return list;
    }

    public static string Serialize(IEnumerable<Drive> drives)
    {
        var doc = new DrivesDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            Drives = drives.Select(DriveDocument.FromDrive).ToList(),
        };
        return JsonSerializer.Serialize(doc, JsonDefaults.Options);
    }

    public void Upsert(Drive drive)
    {
        lock (_gate)
        {
            drive.UpdatedAt = DateTimeOffset.UtcNow;
            drive.RemoteIdentityHash = RemoteIdentity.ComputeHash(drive);
            var idx = _drives.FindIndex(d => d.Id == drive.Id);
            if (idx >= 0) _drives[idx] = drive;
            else
            {
                drive.SortIndex = _drives.Count == 0 ? 0 : _drives.Max(d => d.SortIndex) + 1;
                _drives.Add(drive);
            }
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            _drives.RemoveAll(d => d.Id == id);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void Save()
    {
        lock (_gate) SaveLocked();
        Changed?.Invoke();
    }

    private void SaveLocked()
    {
        if (ReadOnlyReason is not null) throw new InvalidOperationException(ReadOnlyReason);
        Directory.CreateDirectory(_paths.ConfigDir);
        var json = Serialize(_drives);
        using var _ = AcquireLock(_paths.ConfigLockFile);
        var tmp = _paths.DrivesFile + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        if (File.Exists(_paths.DrivesFile))
            File.Replace(tmp, _paths.DrivesFile, _paths.DrivesFile + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(tmp, _paths.DrivesFile);
    }

    internal static IDisposable AcquireLock(string lockFile)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            try
            {
                return new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    // ---- document shapes (match SRS §8 / SDD §8.1) ----

    private sealed class DrivesDocument
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public List<DriveDocument> Drives { get; set; } = new();
    }

    private sealed class DriveDocument
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public StorageType StorageType { get; set; }
        public string? Preset { get; set; }
        public MountOptions Mount { get; set; } = new();
        public JsonElement? Connection { get; set; }
        public string? RemoteIdentityHash { get; set; }
        public CredentialPolicy CredentialPolicy { get; set; } = CredentialPolicy.Store;
        public AdvancedOptions Advanced { get; set; } = new();
        public int SortIndex { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public LastStatus? Last { get; set; }
        public RecoveryInfo? Recovery { get; set; }

        public static DriveDocument FromDrive(Drive d) => new()
        {
            Id = d.Id,
            Name = d.Name,
            StorageType = d.StorageType,
            Preset = d.Preset,
            Mount = d.Mount,
            Connection = d.StorageType == StorageType.WebDav
                ? JsonSerializer.SerializeToElement(d.WebDav ?? new WebDavConnection(), JsonDefaults.Options)
                : JsonSerializer.SerializeToElement(d.Sftp ?? new SftpConnection(), JsonDefaults.Options),
            RemoteIdentityHash = d.RemoteIdentityHash,
            CredentialPolicy = d.CredentialPolicy,
            Advanced = d.Advanced,
            SortIndex = d.SortIndex,
            CreatedAt = d.CreatedAt,
            UpdatedAt = d.UpdatedAt,
            Last = d.Last,
            Recovery = d.Recovery,
        };

        public static Drive ToDrive(DriveDocument doc, int schemaVersion)
        {
            var drive = new Drive
            {
                Id = doc.Id == Guid.Empty ? Guid.NewGuid() : doc.Id,
                Name = doc.Name,
                StorageType = doc.StorageType,
                Preset = doc.Preset,
                Mount = doc.Mount ?? new MountOptions(),
                CredentialPolicy = doc.CredentialPolicy,
                Advanced = doc.Advanced ?? new AdvancedOptions(),
                SortIndex = doc.SortIndex,
                CreatedAt = doc.CreatedAt == default ? DateTimeOffset.UtcNow : doc.CreatedAt,
                UpdatedAt = doc.UpdatedAt == default ? DateTimeOffset.UtcNow : doc.UpdatedAt,
                Last = doc.Last,
                Recovery = doc.Recovery,
            };
            if (doc.Connection is { } el)
            {
                if (doc.StorageType == StorageType.WebDav)
                {
                    var c = el.Deserialize<WebDavConnection>(JsonDefaults.Options) ?? new WebDavConnection();
                    if (schemaVersion < 2 && !el.TryGetProperty("authMode", out _))
                        c.AuthMode = string.IsNullOrEmpty(c.Username) ? WebDavAuthMode.Anonymous : WebDavAuthMode.Basic; // v1 → v2 migration
                    drive.WebDav = c;
                }
                else
                {
                    drive.Sftp = el.Deserialize<SftpConnection>(JsonDefaults.Options) ?? new SftpConnection();
                }
            }
            return drive;
        }
    }
}

/// <summary>drives.json (or an export) carries a schemaVersion newer than this build understands.</summary>
public sealed class ConfigNewerVersionException : Exception
{
    public ConfigNewerVersionException(int found, int supported)
        : base($"配置文件来自更新版本的 OpenMount（schemaVersion {found}，本版本支持到 {supported}）。为避免丢失设置，本版本不会修改它；请使用新版本打开。") { }
}

public sealed class ConfigCorruptException : Exception
{
    public ConfigCorruptException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>settings.json persistence.</summary>
public sealed class SettingsService
{
    private readonly AppPaths _paths;
    private readonly IAppLog _log;
    private readonly object _gate = new();

    public SettingsService(AppPaths paths, IAppLog log)
    {
        _paths = paths;
        _log = log;
        Current = new AppSettings();
    }

    public AppSettings Current { get; private set; }
    public event Action? Changed;

    public void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_paths.SettingsFile)) { Current = new AppSettings(); return; }
            try
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_paths.SettingsFile), JsonDefaults.Options) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                _log.Error("settings", "settings.json 损坏，使用默认设置", ex);
                Current = new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_paths.ConfigDir);
            using var _ = DriveRepository.AcquireLock(_paths.ConfigLockFile);
            var tmp = _paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonDefaults.Options), new UTF8Encoding(false));
            if (File.Exists(_paths.SettingsFile)) File.Replace(tmp, _paths.SettingsFile, _paths.SettingsFile + ".bak", true);
            else File.Move(tmp, _paths.SettingsFile);
            Current = settings;
        }
        Changed?.Invoke();
    }
}
