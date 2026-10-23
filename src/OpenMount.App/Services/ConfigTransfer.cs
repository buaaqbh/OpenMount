using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using OpenMount.App.Views;
using OpenMount.Core.Engine;
using OpenMount.Core.Logging;
using OpenMount.Core.Security;
using OpenMount.Core.Services;

namespace OpenMount.App.Services;

/// <summary>Export / import of the drive configuration (FR-15) with the small dialogs around <see cref="ExportService"/>.</summary>
internal static class ConfigTransfer
{
    public static Task ExportAsync(Window? owner, AppServices s)
    {
        var drives = s.Repository.Drives;
        var options = new ExportOptionsDialog(drives.Count) { Owner = owner };
        if (options.ShowDialog() != true) return Task.CompletedTask;
        var dlg = new SaveFileDialog
        {
            Title = "导出配置",
            Filter = "OpenMount 配置|*.openmount.json|所有文件|*.*",
            FileName = $"openmount-{DateTime.Now:yyyyMMdd}.openmount.json",
            DefaultExt = ".json",
        };
        if (dlg.ShowDialog(owner) != true) return Task.CompletedTask;
        try
        {
            var svc = new ExportService(s.CredentialStore);
            var json = options.IncludeCredentials
                ? svc.ExportEncrypted(drives, s.Settings.Current, options.Password!)
                : svc.ExportPlain(drives, s.Settings.Current);
            File.WriteAllText(dlg.FileName, json, new UTF8Encoding(false));
            s.Log.Info("ui", $"exported {drives.Count} drives (credentials: {options.IncludeCredentials})");
            new InfoDialog("导出配置", $"已导出 {drives.Count} 个驱动器", dlg.FileName) { Owner = owner }.ShowDialog();
        }
        catch (Exception ex)
        {
            s.Log.Error("ui", "export failed", ex);
            new InfoDialog("导出配置", "导出失败", ex.Message, error: true) { Owner = owner }.ShowDialog();
        }
        return Task.CompletedTask;
    }

    public static async Task ImportAsync(Window? owner, AppServices s)
    {
        var dlg = new OpenFileDialog { Title = "导入配置", Filter = "OpenMount 配置|*.openmount.json;*.json|所有文件|*.*" };
        if (dlg.ShowDialog(owner) != true) return;
        string json;
        try { json = File.ReadAllText(dlg.FileName); }
        catch (Exception ex)
        {
            new InfoDialog("导入配置", "无法读取文件", ex.Message, error: true) { Owner = owner }.ShowDialog();
            return;
        }

        ExportService.ImportResult result;
        string? password = null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                result = ExportService.Import(json, password);
                break;
            }
            catch (ExportFormatException ex) when (ex.Message.Contains("口令") && attempt < 3)
            {
                var prompt = new PasswordPromptDialog("导入配置", attempt == 0 ? "该文件已加密，请输入口令" : "口令错误或文件已损坏", string.Empty) { Owner = owner };
                if (prompt.ShowDialog() != true) return;
                password = prompt.Password;
            }
            catch (Exception ex)
            {
                new InfoDialog("导入配置", "无法导入", ex.Message, error: true) { Owner = owner }.ShowDialog();
                return;
            }
        }

        var existing = s.Repository.Drives.ToList();
        var added = 0;
        var skipped = new List<string>();
        foreach (var drive in result.Drives)
        {
            var originalId = drive.Id;
            if (existing.Any(e => e.Id == drive.Id)) drive.Id = Guid.NewGuid(); // same file imported twice: keep both entries apart
            var errors = s.Validator.Validate(drive, existing);
            if (errors.Count > 0)
            {
                skipped.Add($"{drive.Name}：{errors[0].Message}");
                continue;
            }
            var creds = result.Credentials.Where(c => c.DriveId == originalId).ToList();
            var secrets = creds.Count == 0 ? null : new DriveSecrets(
                Password: creds.FirstOrDefault(c => c.Field == CredentialFields.Password)?.Value,
                KeyPassphrase: creds.FirstOrDefault(c => c.Field == CredentialFields.KeyPassphrase)?.Value,
                BearerToken: creds.FirstOrDefault(c => c.Field == CredentialFields.BearerToken)?.Value);
            try
            {
                await s.Manager.AddAsync(drive, secrets, rememberForSession: false, CancellationToken.None);
                existing.Add(drive);
                added++;
            }
            catch (Exception ex)
            {
                skipped.Add($"{drive.Name}：{ex.Message}");
            }
        }
        s.Log.Info("ui", $"imported {added} drives, skipped {skipped.Count}");
        var body = new StringBuilder();
        body.Append(added > 0 ? "导入的驱动器不会自动连接，请检查盘符与凭据后手动连接。" : "没有导入任何驱动器。");
        body.Append(" 全局设置未导入。");
        if (skipped.Count > 0) body.Append("\n\n跳过：\n• ").Append(string.Join("\n• ", skipped));
        new InfoDialog("导入配置", $"已导入 {added} 个驱动器" + (skipped.Count > 0 ? $"，跳过 {skipped.Count} 个" : string.Empty), body.ToString()) { Owner = owner }.ShowDialog();
    }
}
