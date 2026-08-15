using MetaVoiceType.Core.Interfaces;
using TextCopy;

namespace MetaVoiceType.Platform.Windows;

public sealed class WindowsClipboardService : IClipboardService
{
    public Task SetTextAsync(string text, CancellationToken cancellationToken = default) => ClipboardService.SetTextAsync(text, cancellationToken);
}
