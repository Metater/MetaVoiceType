using MetaVoiceType.Core.Models;

namespace MetaVoiceType.Core.Interfaces;

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
