using System.Text.Json;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using Microsoft.Extensions.Logging;

namespace MetaVoiceType.Storage;

public sealed partial class JsonSettingsStore(AppPaths paths, ILogger<JsonSettingsStore> logger) : ISettingsStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(paths.SettingsFile))
                return new AppSettings();
            await using var stream = File.OpenRead(paths.SettingsFile);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, AtomicJsonFile.Options, cancellationToken).ConfigureAwait(false)
                ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LogUnreadable(logger, ex);
            return new AppSettings();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await AtomicJsonFile.WriteAsync(paths.SettingsFile, settings, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings were unreadable; defaults will be used.")]
    private static partial void LogUnreadable(ILogger logger, Exception exception);
}
