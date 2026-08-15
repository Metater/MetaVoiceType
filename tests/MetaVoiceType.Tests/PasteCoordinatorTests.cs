using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace MetaVoiceType.Tests;

public sealed class PasteCoordinatorTests
{
    [Fact]
    public async Task DuplicateRequestIsRejectedAndExactTextIsPastedOnce()
    {
        var clipboard = new FakeClipboard();
        var insertion = new BlockingInsertion();
        using var coordinator = new PasteCoordinator(clipboard, insertion, NullLogger<PasteCoordinator>.Instance);
        Assert.Equal(PasteRequestResult.Accepted, coordinator.Queue("target A"));
        await insertion.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PasteRequestResult.AlreadyPending, coordinator.Queue("target B"));
        insertion.Release.TrySetResult();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (coordinator.IsPending) await Task.Delay(10, timeout.Token);
        Assert.Equal(["target A"], clipboard.Values);
        Assert.Equal(1, insertion.Count);
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public List<string> Values { get; } = [];
        public Task SetTextAsync(string text, CancellationToken cancellationToken = default) { Values.Add(text); return Task.CompletedTask; }
    }
    private sealed class BlockingInsertion : ITextInsertionService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count { get; private set; }
        public async Task PasteAsync(CancellationToken cancellationToken = default) { Count++; Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
    }
}
