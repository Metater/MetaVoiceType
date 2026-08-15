namespace MetaVoiceType.Core.Interfaces;

public interface IClipboardService { Task SetTextAsync(string text, CancellationToken cancellationToken = default); }
public interface ITextInsertionService { Task PasteAsync(CancellationToken cancellationToken = default); }
public interface IStartupService { bool IsEnabled { get; } void SetEnabled(bool enabled); }
public interface IGlobalHotkeyService : IAsyncDisposable { event EventHandler? ToggleRecording; Task StartAsync(CancellationToken cancellationToken = default); }
