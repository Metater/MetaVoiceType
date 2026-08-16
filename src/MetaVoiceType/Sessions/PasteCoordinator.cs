using MetaVoiceType.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MetaVoiceType.Sessions;

public enum PasteRequestResult { Accepted, AlreadyPending, NoText }
public enum PasteRequestState { Idle, Queued, Preparing, Pasting, Succeeded, Failed, Canceled }

public sealed partial class PasteCoordinator(IClipboardService clipboard, ITextInsertionService insertion, ILogger<PasteCoordinator> logger) : IDisposable
{
    private readonly SemaphoreSlim _clipboardGate = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _pending;
    private PasteRequestState _state;
    public PasteRequestState State { get { lock (_stateGate) return _state; } }
    public bool IsActive => State is PasteRequestState.Queued or PasteRequestState.Preparing or PasteRequestState.Pasting;
    public event EventHandler<PasteRequestState>? StateChanged;

    public PasteRequestResult Queue(string text, Func<Task>? completed = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return PasteRequestResult.NoText;
        PasteRequestResult reservation = Reserve();
        if (reservation != PasteRequestResult.Accepted) return reservation;
        StartReserved(text, completed);
        return PasteRequestResult.Accepted;
    }

    public PasteRequestResult Reserve()
    {
        lock (_stateGate)
        {
            if (_pending is not null) return PasteRequestResult.AlreadyPending;
            _pending = new CancellationTokenSource();
            _state = PasteRequestState.Queued;
        }
        StateChanged?.Invoke(this, PasteRequestState.Queued);
        return PasteRequestResult.Accepted;
    }

    public void StartReserved(string text, Func<Task>? completed = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Paste text cannot be blank.", nameof(text));
        CancellationTokenSource request;
        lock (_stateGate)
        {
            request = _pending ?? throw new InvalidOperationException("No paste request is reserved.");
            if (_state != PasteRequestState.Queued) throw new InvalidOperationException("The paste request already started.");
            _state = PasteRequestState.Preparing;
        }
        StateChanged?.Invoke(this, PasteRequestState.Preparing);
        _ = ExecuteAsync(text, request, completed);
    }

    public void Cancel()
    {
        CancellationTokenSource? reserved = null;
        lock (_stateGate)
        {
            if (_pending is null) return;
            if (_state == PasteRequestState.Queued)
            {
                reserved = _pending;
                _pending = null;
                _state = PasteRequestState.Canceled;
            }
            else _pending.Cancel();
        }
        if (reserved is not null)
        {
            reserved.Cancel();
            reserved.Dispose();
            StateChanged?.Invoke(this, PasteRequestState.Canceled);
            LogCanceled(logger);
        }
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
                SetState(PasteRequestState.Pasting);
                await insertion.PasteAsync(request.Token).ConfigureAwait(false);
            }
            finally { _clipboardGate.Release(); }
            if (completed is not null) await completed().ConfigureAwait(false);
            SetState(PasteRequestState.Succeeded);
            LogPasted(logger, clock.Elapsed.TotalMilliseconds, exactText.Length);
        }
        catch (OperationCanceledException) { SetState(PasteRequestState.Canceled); LogCanceled(logger); }
        catch (Exception ex) { SetState(PasteRequestState.Failed); LogPasteFailed(logger, ex); }
        finally
        {
            lock (_stateGate) { if (ReferenceEquals(_pending, request)) { _pending = null; request.Dispose(); } }
        }
    }

    public void ResetTerminalState()
    {
        if (State is PasteRequestState.Succeeded or PasteRequestState.Failed or PasteRequestState.Canceled) SetState(PasteRequestState.Idle);
    }

    private void SetState(PasteRequestState state)
    {
        lock (_stateGate) _state = state;
        StateChanged?.Invoke(this, state);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Paste completed in {ElapsedMs:F1} ms (chars={Characters}).")]
    private static partial void LogPasted(ILogger logger, double elapsedMs, int characters);
    [LoggerMessage(Level = LogLevel.Information, Message = "Pending paste canceled.")]
    private static partial void LogCanceled(ILogger logger);
    [LoggerMessage(Level = LogLevel.Error, Message = "Paste transaction failed.")]
    private static partial void LogPasteFailed(ILogger logger, Exception exception);

    public void Dispose()
    {
        lock (_stateGate) { _pending?.Cancel(); _pending?.Dispose(); _pending = null; _state = PasteRequestState.Idle; }
        _clipboardGate.Dispose();
    }
}
