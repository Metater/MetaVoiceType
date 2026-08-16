using MetaVoiceType.Core.Models;

namespace MetaVoiceType.Core.Interfaces;

public interface IHistoryStore
{
    Task<IReadOnlyList<TranscriptRecord>> LoadAsync(CancellationToken cancellationToken = default);
    Task AddAsync(TranscriptRecord record, CancellationToken cancellationToken = default);
    Task DeleteAsync(string logicalTranscriptId, CancellationToken cancellationToken = default);
}
