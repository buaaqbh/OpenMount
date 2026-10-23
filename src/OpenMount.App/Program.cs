using OpenMount.Windows;

namespace OpenMount.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var instance = new SingleInstance();
        if (!instance.IsFirstInstance)
        {
            SingleInstance.SendToExisting(args.Length == 0 ? new[] { "--show" } : args);
            return 0;
        }
        var app = new App(args, instance);
        return app.Run();
    }
}
