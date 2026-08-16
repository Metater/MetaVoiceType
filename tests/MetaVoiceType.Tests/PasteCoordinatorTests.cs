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
        while (coordinator.IsActive) await Task.Delay(10, timeout.Token);
        Assert.Equal(["target A"], clipboard.Values);
        Assert.Equal(1, insertion.Count);
        Assert.Equal(PasteRequestState.Succeeded, coordinator.State);
    }

    [Fact]
    public async Task PasteStateAlwaysReachesTerminalStateOnFailureOrCancellation()
    {
        var failed = new PasteCoordinator(new FakeClipboard(), new FailingInsertion(), NullLogger<PasteCoordinator>.Instance);
        Assert.Equal(PasteRequestResult.Accepted, failed.Queue("failure"));
        await WaitForTerminalAsync(failed);
        Assert.Equal(PasteRequestState.Failed, failed.State);
        Assert.False(failed.IsActive);
        failed.Dispose();

        var blocked = new BlockingInsertion();
        var canceled = new PasteCoordinator(new FakeClipboard(), blocked, NullLogger<PasteCoordinator>.Instance);
        Assert.Equal(PasteRequestResult.Accepted, canceled.Queue("cancel"));
        await blocked.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        canceled.Cancel();
        await WaitForTerminalAsync(canceled);
        Assert.Equal(PasteRequestState.Canceled, canceled.State);
        Assert.False(canceled.IsActive);
        canceled.Dispose();
    }

    [Fact]
    public void CancelingAReservedDeferredPasteClearsItImmediately()
    {
        using var coordinator = new PasteCoordinator(new FakeClipboard(), new BlockingInsertion(), NullLogger<PasteCoordinator>.Instance);
        Assert.Equal(PasteRequestResult.Accepted, coordinator.Reserve());
        coordinator.Cancel();
        Assert.Equal(PasteRequestState.Canceled, coordinator.State);
        Assert.False(coordinator.IsActive);
        Assert.Equal(PasteRequestResult.Accepted, coordinator.Reserve());
    }

    private static async Task WaitForTerminalAsync(PasteCoordinator coordinator)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (coordinator.IsActive) await Task.Delay(10, timeout.Token);
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
    private sealed class FailingInsertion : ITextInsertionService
    {
        public Task PasteAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("test failure");
    }
}
