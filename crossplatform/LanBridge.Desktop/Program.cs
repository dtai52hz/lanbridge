using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using LanBridge.CrossPlatform;

namespace LanBridge.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--smoke"))
        {
            Console.WriteLine($"LanBridge Desktop 0.8.0-preview.1 · {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
            Console.WriteLine("Shared TLS/discovery protocol loaded. Smoke mode does not start hooks, input, network or a window.");
            _ = typeof(PortableInputRouter);
            return 0;
        }
        using var instance = new Mutex(false, OperatingSystem.IsWindows() ? @"Local\LanBridgeDesktop" : "LanBridgeDesktop");
        bool owns;
        try { owns = instance.WaitOne(0); }
        catch (AbandonedMutexException) { owns = true; }
        try
        {
            App.ExistingInstance = !owns;
            return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        }
        finally { if (owns) instance.ReleaseMutex(); }
    }
}

public sealed class App : Application
{
    internal static bool ExistingInstance;
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(ExistingInstance);
        base.OnFrameworkInitializationCompleted();
    }
}
