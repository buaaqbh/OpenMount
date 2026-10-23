using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenMount.Core.Logging;
using UiButton = Wpf.Ui.Controls.Button;

namespace OpenMount.App.Views;

/// <summary>Live log viewer (app + drive logs flow through the same ring buffer sinks).</summary>
public sealed class LogWindow : Window
{
    private readonly ListBox _list = new() { FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], FontSize = 11.5, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly IAppLog _log;
    private readonly Action<LogLine> _handler;

    public LogWindow(AppServices services)
    {
        _log = services.Log;
        Style = (Style)Application.Current.Resources["AppWindow"];
        Title = "日志";
        Width = 960;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        foreach (var line in _log.Recent()) _list.Items.Add(Format(line));
        _handler = line => Dispatcher.BeginInvoke(() =>
        {
            _list.Items.Add(Format(line));
            while (_list.Items.Count > 2000) _list.Items.RemoveAt(0);
            _list.ScrollIntoView(_list.Items[^1]);
        });
        _log.LineWritten += _handler;

        var open = new UiButton { Content = "打开日志目录" };
        open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + services.Paths.LogsDir + "\"") { UseShellExecute = true }); } catch { } };
        var copy = new UiButton { Content = "复制全部", Margin = new Thickness(8, 0, 0, 0) };
        copy.Click += (_, _) => { try { Clipboard.SetText(string.Join(Environment.NewLine, _list.Items.Cast<object>().Select(o => o.ToString()))); } catch { } };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 8, 16, 8) };
        toolbar.Children.Add(open);
        toolbar.Children.Add(copy);

        var card = new Border { Style = (Style)Application.Current.Resources["Card"], Margin = new Thickness(16, 0, 16, 16), Padding = new Thickness(8, 6, 8, 6), Child = _list };
        var dock = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        dock.Children.Add(toolbar);
        dock.Children.Add(card);
        Content = dock;
        Closed += (_, _) => _log.LineWritten -= _handler;
        if (_list.Items.Count > 0) _list.ScrollIntoView(_list.Items[^1]);
    }

    private static string Format(LogLine l) => $"{l.At:HH:mm:ss.fff} [{l.Level,-5}] {l.Source}: {l.Message}";
}
