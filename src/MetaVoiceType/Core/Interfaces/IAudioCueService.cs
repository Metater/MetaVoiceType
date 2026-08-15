using MetaVoiceType.Core.Models;

namespace MetaVoiceType.Core.Interfaces;

public interface IAudioCueService
{
    void PlayAccepted(VoiceCommand command, double volume);
    void PlayError(double volume);
    void PlayRecovered(double volume);
}

public interface IUpdateService
{
    bool IsInstalled { get; }
    Task<string?> CheckAsync(CancellationToken cancellationToken = default);
    Task DownloadAndRestartAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default);
}
