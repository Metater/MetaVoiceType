using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MetaVoiceType.Integrations;

public interface IDiscordVoiceIntegration : IAsyncDisposable
{
    bool IsAvailable { get; }
    bool IsAuthorized { get; }
    string Status { get; }
    event EventHandler<bool>? MuteStateChanged;
    Task ConfigureAsync(string? clientId, CancellationToken cancellationToken = default);
    Task<bool> GetMuteStateAsync(CancellationToken cancellationToken = default);
    Task SetMuteStateAsync(bool muted, CancellationToken cancellationToken = default);
}

public sealed partial class DiscordRpcVoiceIntegration(ILogger<DiscordRpcVoiceIntegration> logger) : IDiscordVoiceIntegration
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly SemaphoreSlim _rpcGate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    public bool IsAvailable => _pipe?.IsConnected == true;
    public bool IsAuthorized { get; private set; }
    public string Status { get; private set; } = "Not configured";
    public event EventHandler<bool>? MuteStateChanged;

    public async Task ConfigureAsync(string? clientId, CancellationToken cancellationToken = default)
    {
        await DisposePipeAsync().ConfigureAwait(false);
        IsAuthorized = false;
        if (string.IsNullOrWhiteSpace(clientId)) { Status = "Not configured"; return; }
        _pipe = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (_pipe is null) { Status = "Discord is not running"; return; }
        try
        {
            await WriteFrameAsync(_pipe, 0, JsonSerializer.SerializeToUtf8Bytes(new { v = 1, client_id = clientId }, JsonOptions), cancellationToken).ConfigureAwait(false);
            (int opcode, JsonDocument response) = await ReadFrameAsync(_pipe, cancellationToken).ConfigureAwait(false);
            using (response)
            {
                if (opcode != 1) throw new InvalidDataException("Discord rejected the RPC handshake.");
            }
            string? accessToken = Environment.GetEnvironmentVariable("METAVOICETYPE_DISCORD_ACCESS_TOKEN");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                Status = "Not authorized — Discord RPC approval is required";
                return;
            }
            using JsonDocument authenticated = await SendCommandAsync("AUTHENTICATE", new { access_token = accessToken }, cancellationToken).ConfigureAwait(false);
            IsAuthorized = !IsError(authenticated.RootElement);
            Status = IsAuthorized ? "Connected" : "Authorization failed";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            Status = "Discord RPC unavailable: " + ex.Message;
            LogUnavailable(logger, ex);
            await DisposePipeAsync().ConfigureAwait(false);
        }
    }

    public async Task<bool> GetMuteStateAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthorized();
        using JsonDocument response = await SendCommandAsync("GET_VOICE_SETTINGS", null, cancellationToken).ConfigureAwait(false);
        if (IsError(response.RootElement)) throw new InvalidOperationException(ReadError(response.RootElement));
        return response.RootElement.GetProperty("data").GetProperty("mute").GetBoolean();
    }

    public async Task SetMuteStateAsync(bool muted, CancellationToken cancellationToken = default)
    {
        EnsureAuthorized();
        using JsonDocument response = await SendCommandAsync("SET_VOICE_SETTINGS", new { mute = muted }, cancellationToken).ConfigureAwait(false);
        if (IsError(response.RootElement)) throw new InvalidOperationException(ReadError(response.RootElement));
        MuteStateChanged?.Invoke(this, muted);
    }

    private async Task<JsonDocument> SendCommandAsync(string command, object? arguments, CancellationToken cancellationToken)
    {
        NamedPipeClientStream pipe = _pipe is { IsConnected: true } connected ? connected : throw new IOException("Discord RPC is disconnected.");
        await _rpcGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string nonce = Guid.NewGuid().ToString("N");
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { cmd = command, args = arguments, nonce }, JsonOptions);
            await WriteFrameAsync(pipe, 1, payload, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                (int opcode, JsonDocument response) = await ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (opcode == 2) { response.Dispose(); throw new IOException("Discord closed the RPC connection."); }
                JsonElement root = response.RootElement;
                if (root.TryGetProperty("evt", out JsonElement evt) && evt.GetString() == "VOICE_SETTINGS_UPDATE")
                {
                    if (root.TryGetProperty("data", out JsonElement data) && data.TryGetProperty("mute", out JsonElement mute))
                        MuteStateChanged?.Invoke(this, mute.GetBoolean());
                    response.Dispose();
                    continue;
                }
                if (root.TryGetProperty("nonce", out JsonElement responseNonce) && responseNonce.GetString() == nonce) return response;
                response.Dispose();
            }
        }
        finally { _rpcGate.Release(); }
    }

    private static async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken cancellationToken)
    {
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(150, cancellationToken).ConfigureAwait(false); return pipe; }
            catch (TimeoutException) { pipe.Dispose(); }
            catch (IOException) { pipe.Dispose(); }
        }
        return null;
    }

    private static async Task WriteFrameAsync(Stream stream, int opcode, byte[] payload, CancellationToken cancellationToken)
    {
        byte[] header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(int Opcode, JsonDocument Payload)> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[8];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > 16 * 1024 * 1024) throw new InvalidDataException("Discord RPC frame length is invalid.");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return (opcode, JsonDocument.Parse(payload));
    }

    private void EnsureAuthorized()
    {
        if (!IsAvailable || !IsAuthorized) throw new InvalidOperationException("Discord RPC is not authorized.");
    }
    private static bool IsError(JsonElement root) => root.TryGetProperty("evt", out JsonElement evt) && evt.GetString() == "ERROR";
    private static string ReadError(JsonElement root) => root.TryGetProperty("data", out JsonElement data) && data.TryGetProperty("message", out JsonElement message) ? message.GetString() ?? "Discord RPC error" : "Discord RPC error";
    private async Task DisposePipeAsync() { if (_pipe is not null) { await _pipe.DisposeAsync().ConfigureAwait(false); _pipe = null; } }
    public async ValueTask DisposeAsync() { await DisposePipeAsync().ConfigureAwait(false); _rpcGate.Dispose(); }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord local RPC is unavailable.")]
    private static partial void LogUnavailable(ILogger logger, Exception exception);
}

public sealed partial class DiscordAutoMuteCoordinator(IDiscordVoiceIntegration discord, ILogger<DiscordAutoMuteCoordinator> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _activeSessions = new(StringComparer.Ordinal);
    private bool _mutedByApp;

    public async Task RecordingStartedAsync(string sessionId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled || !discord.IsAuthorized) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_activeSessions.Add(sessionId)) return;
            if (_activeSessions.Count > 1) return;
            bool alreadyMuted = await discord.GetMuteStateAsync(cancellationToken).ConfigureAwait(false);
            _mutedByApp = !alreadyMuted;
            if (_mutedByApp) await discord.SetMuteStateAsync(true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            _activeSessions.Remove(sessionId);
            _mutedByApp = false;
            LogFailure(logger, ex, "mute");
        }
        finally { _gate.Release(); }
    }

    public async Task RecordingEndedAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_activeSessions.Remove(sessionId) || _activeSessions.Count != 0 || !_mutedByApp) return;
            _mutedByApp = false;
            if (discord.IsAuthorized) await discord.SetMuteStateAsync(false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { LogFailure(logger, ex, "restore"); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord auto-mute could not {Operation}; recording continues normally.")]
    private static partial void LogFailure(ILogger logger, Exception exception, string operation);
}
