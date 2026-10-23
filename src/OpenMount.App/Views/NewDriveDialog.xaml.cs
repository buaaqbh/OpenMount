using System.Windows;
using Microsoft.Win32;
using OpenMount.App.ViewModels;
using OpenMount.Core.Engine;
using OpenMount.Core.Models;

namespace OpenMount.App.Views;

public partial class NewDriveDialog : Window
{
    private readonly NewDriveViewModel _vm;
    private readonly Drive? _editing;

    /// <param name="editing">Drive to edit (keeps its id), or null.</param>
    /// <param name="template">Drive to duplicate (FR-01.8); ignored when <paramref name="editing"/> is set.</param>
    public NewDriveDialog(AppServices services, Drive? editing, Drive? template = null)
    {
        _editing = editing;
        _vm = new NewDriveViewModel(services, editing, editing is null ? template : null);
        DataContext = _vm;
        InitializeComponent();
        _vm.Refresh();
    }

    public Drive? ResultDrive { get; private set; }
    public DriveSecrets? ResultSecrets { get; private set; }
    public bool RememberForSession { get; private set; }
    public bool ConnectRequested { get; private set; }

    private void Connect_Click(object sender, RoutedEventArgs e) => Submit(connect: true);
    private void Save_Click(object sender, RoutedEventArgs e) => Submit(connect: false);
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void Submit(bool connect)
    {
        var drive = _vm.BuildDrive();
        var errors = _vm.Validate(drive);
        if (errors.Count > 0)
        {
            _vm.ValidationText = string.Join("\n", errors.Select(er => "• " + er.Message));
            return;
        }
        var secrets = CollectSecrets(drive);
        if (secrets is null && _editing is null && drive.CredentialPolicy == CredentialPolicy.Store && RequiresSecret(drive))
        {
            _vm.ValidationText = "• 请输入密码 / 令牌，或把凭据策略改为“每次连接时询问”";
            return;
        }
        _vm.ValidationText = string.Empty;
        ResultDrive = drive;
        ResultSecrets = secrets;
        RememberForSession = drive.CredentialPolicy == CredentialPolicy.Session;
        ConnectRequested = connect;
        DialogResult = true;
        Close();
    }

    private static bool RequiresSecret(Drive d) =>
        d.StorageType switch
        {
            StorageType.WebDav => d.WebDav!.AuthMode == WebDavAuthMode.Bearer,
            StorageType.Sftp => d.Sftp!.AuthMode is SftpAuthMode.Password or SftpAuthMode.KeyAndPassword,
            _ => false,
        };

    /// <summary>Reads the PasswordBoxes. Returns null when nothing was entered (edit: keep existing secrets).</summary>
    private DriveSecrets? CollectSecrets(Drive drive)
    {
        string? password = null, passphrase = null, token = null;
        if (drive.StorageType == StorageType.WebDav)
        {
            if (drive.WebDav!.AuthMode == WebDavAuthMode.Basic) password = NullIfEmpty(WebDavPassword.Password);
            if (drive.WebDav.AuthMode == WebDavAuthMode.Bearer) token = NullIfEmpty(BearerToken.Password);
        }
        else
        {
            if (drive.Sftp!.AuthMode is SftpAuthMode.Password or SftpAuthMode.KeyAndPassword) password = NullIfEmpty(SftpPassword.Password);
            if (drive.Sftp.AuthMode is SftpAuthMode.Key or SftpAuthMode.KeyAndPassword) passphrase = NullIfEmpty(KeyPassphrase.Password);
        }
        if (password is null && passphrase is null && token is null) return null;
        return new DriveSecrets(password, passphrase, token);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择父文件夹（挂载点将在其中创建）" };
        if (dlg.ShowDialog(this) == true)
            _vm.FolderPath = System.IO.Path.Combine(dlg.FolderName, string.IsNullOrWhiteSpace(_vm.Name) ? "OpenMount" : _vm.Name.Trim());
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "选择 OpenSSH 格式的私钥文件", Filter = "所有文件|*.*" };
        if (dlg.ShowDialog(this) == true) _vm.KeyFilePath = dlg.FileName;
    }

    private void BrowseCa_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "选择 CA 证书（PEM）", Filter = "证书|*.pem;*.crt;*.cer|所有文件|*.*" };
        if (dlg.ShowDialog(this) == true) _vm.CaCertPath = dlg.FileName;
    }
}
