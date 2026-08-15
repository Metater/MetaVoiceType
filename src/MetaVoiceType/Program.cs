using Avalonia;
using MetaVoiceType.Diagnostics;
using Velopack;

namespace MetaVoiceType;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        VelopackApp.Build().Run();
        StartupOptions options = StartupOptions.Parse(args);
        AppServices.Initialize(args, options);
        if (options.ExitAfterDiagnostics)
        {
            try { Environment.ExitCode = AppServices.Get<DiagnosticRunner>().RunAsync(options).GetAwaiter().GetResult(); }
            catch (Exception ex) { Serilog.Log.Error(ex, "Diagnostic command failed."); Environment.ExitCode = 1; }
            finally { AppServices.ShutdownAsync().GetAwaiter().GetResult(); }
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
