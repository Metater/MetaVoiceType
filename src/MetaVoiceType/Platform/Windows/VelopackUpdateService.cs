using MetaVoiceType.Core.Interfaces;
using Velopack;
using Velopack.Sources;

namespace MetaVoiceType.Platform.Windows;

public sealed class VelopackUpdateService : IUpdateService
{
    private readonly UpdateManager _manager = new(new GithubSource("https://github.com/Metater/MetaVoiceType", null, false));
    private UpdateInfo? _available;
    public bool IsInstalled => _manager.IsInstalled;

    public async Task<string?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInstalled) return null;
        cancellationToken.ThrowIfCancellationRequested();
        _available = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return _available?.TargetFullRelease.Version?.ToString();
    }

    public async Task DownloadAndRestartAsync(IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new("Checking update package", null, true));
        _available ??= await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (_available is null) return;
        progress?.Report(new("Preparing differential update", null, true));
        await _manager.DownloadUpdatesAsync(_available,
            value => progress?.Report(new("Downloading and preparing update", Math.Clamp(value, 0, 100), true)), cancellationToken).ConfigureAwait(false);
        progress?.Report(new("Applying verified update", 100, true));
        _manager.ApplyUpdatesAndRestart(_available.TargetFullRelease);
    }
}
