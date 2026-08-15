using MetaVoiceType.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MetaVoiceType.Sessions;

public enum PasteRequestResult { Accepted, AlreadyPending, NoText }

public sealed partial class PasteCoordinator(IClipboardService clipboard, ITextInsertionService insertion, ILogger<PasteCoordinator> logger) : IDisposable
{
    private readonly SemaphoreSlim _clipboardGate = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _pending;
    public bool IsPending { get { lock (_stateGate) return _pending is not null; } }

    public PasteRequestResult Queue(string text, Func<Task>? completed = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return PasteRequestResult.NoText;
        CancellationTokenSource request;
        lock (_stateGate)
        {
            if (_pending is not null) return PasteRequestResult.AlreadyPending;
            _pending = request = new CancellationTokenSource();
        }
        _ = ExecuteAsync(text, request, completed);
        return PasteRequestResult.Accepted;
    }

    public void Cancel()
    {
        lock (_stateGate) _pending?.Cancel();
    }

    public async Task CopyAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        await _clipboardGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await clipboard.SetTextAsync(text, cancellationToken).ConfigureAwait(false); }
        finally { _clipboardGate.Release(); }
    }

    private async Task ExecuteAsync(string exactText, CancellationTokenSource request, Func<Task>? completed)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _clipboardGate.WaitAsync(request.Token).ConfigureAwait(false);
            try
            {
                await clipboard.SetTextAsync(exactText, request.Token).ConfigureAwait(false);
                request.Token.ThrowIfCancellationRequested();
                await insertion.PasteAsync(request.Token).ConfigureAwait(false);
            }
            finally { _clipboardGate.Release(); }
            if (completed is not null) await completed().ConfigureAwait(false);
            LogPasted(logger, clock.Elapsed.TotalMilliseconds, exactText.Length);
        }
        catch (OperationCanceledException) { LogCanceled(logger); }
        catch (Exception ex) { LogPasteFailed(logger, ex); }
        finally
        {
            lock (_stateGate) { if (ReferenceEquals(_pending, request)) { _pending = null; request.Dispose(); } }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Paste completed in {ElapsedMs:F1} ms (chars={Characters}).")]
    private static partial void LogPasted(ILogger logger, double elapsedMs, int characters);
    [LoggerMessage(Level = LogLevel.Information, Message = "Pending paste canceled.")]
    private static partial void LogCanceled(ILogger logger);
    [LoggerMessage(Level = LogLevel.Error, Message = "Paste transaction failed.")]
    private static partial void LogPasteFailed(ILogger logger, Exception exception);

    public void Dispose()
    {
        lock (_stateGate) { _pending?.Cancel(); _pending?.Dispose(); _pending = null; }
        _clipboardGate.Dispose();
    }
}
