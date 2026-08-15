using MetaVoiceType.Audio;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.State;
using MetaVoiceType.Diagnostics;
using MetaVoiceType.Models;
using MetaVoiceType.Platform.Windows;
using MetaVoiceType.Sessions;
using MetaVoiceType.Storage;
using MetaVoiceType.UI.ViewModels;
using MetaVoiceType.VoiceCommands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace MetaVoiceType;

public static class AppServices
{
    public static IHost Host { get; private set; } = null!;

    public static void Initialize(string[] args, StartupOptions options)
    {
        var paths = new AppPaths(); paths.EnsureCreated();
        Log.Logger = new LoggerConfiguration().MinimumLevel.Is(options.Diagnostics ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.File(Path.Combine(paths.Logs, "metavoicetype-.log"), formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();
        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args).UseSerilog().ConfigureServices(services =>
        {
            services.AddSingleton(paths);
            services.AddSingleton(options);
            services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromHours(2) });
            services.AddSingleton<ISettingsStore, JsonSettingsStore>();
            services.AddSingleton<IHistoryStore, JsonHistoryStore>();
            services.AddSingleton<IModelDownloadService, ModelDownloadService>();
            services.AddSingleton<IAudioCaptureService, WindowsAudioCaptureService>();
            services.AddSingleton<IAudioCueService, AudioCueService>();
            services.AddSingleton<IClipboardService, WindowsClipboardService>();
            services.AddSingleton<ITextInsertionService, WindowsTextInsertionService>();
            services.AddSingleton<IStartupService, WindowsStartupService>();
            services.AddSingleton<IGlobalHotkeyService, WindowsGlobalHotkeyService>();
            services.AddSingleton<IUpdateService, VelopackUpdateService>();
            services.AddSingleton<MetaVoiceTypeState>();
            services.AddSingleton<PasteCoordinator>();
            services.AddSingleton<DecodeCoordinator>();
            services.AddSingleton<RecoveryWriter>();
            services.AddSingleton<VoskCommandRecognizer>();
            services.AddSingleton<ApplicationOrchestrator>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<DiagnosticRunner>();
        }).Build();
        Host.Start();
        if (options.ResetOnboarding)
        {
            ISettingsStore store = Host.Services.GetRequiredService<ISettingsStore>();
            var settings = store.LoadAsync().GetAwaiter().GetResult() with { OnboardingComplete = false };
            store.SaveAsync(settings).GetAwaiter().GetResult();
        }
    }

    public static T Get<T>() where T : notnull => Host.Services.GetRequiredService<T>();

    public static async Task ShutdownAsync()
    {
        if (Host is null) return;
        await Host.StopAsync().ConfigureAwait(false);
        Host.Dispose();
        Log.CloseAndFlush();
    }
}
