using System.Diagnostics;
using System.Threading.Channels;
using MetaVoiceType.Core.Interfaces;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MetaVoiceType.Audio;

public sealed partial class WindowsAudioCaptureService(ILogger<WindowsAudioCaptureService> logger) : IAudioCaptureService
{
    private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _lifetime = new();
    private WasapiCapture? _capture;
    private Task? _dispatch;
    private long _captured;
    private int _depth;
    private int _maxDepth;
    private long _lost;
    private long _callbackTicks;

    public event EventHandler<AudioFrame>? FrameReady;
    public event EventHandler<double>? LevelChanged;
    public bool IsRunning => _capture is not null;
    public AudioMetrics Metrics => new(Interlocked.Read(ref _captured), Volatile.Read(ref _depth), Volatile.Read(ref _maxDepth), Interlocked.Read(ref _lost),
        Interlocked.Read(ref _callbackTicks) * 1000d / Stopwatch.Frequency);

    public IReadOnlyList<AudioDevice> EnumerateDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try { defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID; }
        catch (System.Runtime.InteropServices.COMException) { }
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(x => new AudioDevice(x.ID, x.FriendlyName, x.ID == defaultId)).ToArray();
    }

    public Task StartAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        if (_capture is not null) return Task.CompletedTask;
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device = string.IsNullOrWhiteSpace(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            : enumerator.GetDevice(deviceId);
        _capture = new WasapiCapture(device, true, 40) { WaveFormat = new WaveFormat(AudioFrame.SampleRate, 16, 1) };
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _dispatch ??= Task.Run(() => DispatchAsync(_lifetime.Token), CancellationToken.None);
        _capture.StartRecording();
        LogStarted(logger, device.FriendlyName);
        return Task.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        long started = Stopwatch.GetTimestamp();
        if (args.BytesRecorded > 0)
        {
            var copy = new byte[args.BytesRecorded];
            Buffer.BlockCopy(args.Buffer, 0, copy, 0, args.BytesRecorded);
            if (_frames.Writer.TryWrite(copy))
            {
                Interlocked.Increment(ref _captured);
                int depth = Interlocked.Increment(ref _depth);
                int maximum = Volatile.Read(ref _maxDepth);
                while (depth > maximum)
                {
                    int found = Interlocked.CompareExchange(ref _maxDepth, depth, maximum);
                    if (found == maximum) break;
                    maximum = found;
                }
                if (depth == 250) LogBacklog(logger, depth);
            }
            else Interlocked.Increment(ref _lost);
        }
        Interlocked.Exchange(ref _callbackTicks, Stopwatch.GetTimestamp() - started);
    }

    private async Task DispatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (byte[] bytes in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _depth);
                AudioFrame frame = Pcm16Converter.Convert(bytes);
                FrameReady?.Invoke(this, frame);
                LevelChanged?.Invoke(this, frame.Peak);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null) LogCaptureError(logger, args.Exception);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        WasapiCapture? capture = Interlocked.Exchange(ref _capture, null);
        if (capture is null) return;
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        capture.StopRecording();
        capture.Dispose();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifetime.Cancel();
        _frames.Writer.TryComplete();
        if (_dispatch is not null) try { await _dispatch.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audio capture started: {Device} (16 kHz mono PCM16).")]
    private static partial void LogStarted(ILogger logger, string device);
    [LoggerMessage(Level = LogLevel.Error, Message = "Audio capture failed.")]
    private static partial void LogCaptureError(ILogger logger, Exception exception);
    [LoggerMessage(Level = LogLevel.Error, Message = "Audio dispatch backlog reached {Depth} frames; recognition cannot keep up.")]
    private static partial void LogBacklog(ILogger logger, int depth);
}
