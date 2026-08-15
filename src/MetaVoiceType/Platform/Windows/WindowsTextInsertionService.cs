using MetaVoiceType.Core.Interfaces;
using SharpHook;
using SharpHook.Data;

namespace MetaVoiceType.Platform.Windows;

public sealed class WindowsTextInsertionService : ITextInsertionService
{
    private readonly EventSimulator _simulator = new();
    public Task PasteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UioHookResult result = _simulator.SimulateKeyStroke(KeyCode.VcLeftControl, KeyCode.VcV);
        if (result != UioHookResult.Success) throw new InvalidOperationException($"SharpHook paste simulation failed: {result}.");
        return Task.CompletedTask;
    }
}
