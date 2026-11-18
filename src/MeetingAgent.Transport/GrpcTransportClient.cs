using Grpc.Core;
using Grpc.Net.Client;
using MeetingAgent.Core;
using MeetingAgent.Transport.Proto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net.Http;

namespace MeetingAgent.Transport;

public sealed class GrpcTransportClient : ITransportClient, IVideoSender, IAudioSender, IDetectionResultReceiver
{
    private readonly ILogger<GrpcTransportClient> _logger;
    private readonly IOptionsMonitor<LinuxBackendOptions> _optionsMonitor;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<long, long> _pendingPings = new();

    private GrpcChannel? _channel;
    private DetectionAgentService.DetectionAgentServiceClient? _client;
    private AsyncDuplexStreamingCall<ClientEnvelope, ServerEnvelope>? _stream;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoopTask;
    private Task? _heartbeatTask;
    private long _sessionFrameBytes;
    private long _sessionAudioBytes;
    private long _droppedFrames;
    private long _pingId;
    private long _connectedAtTickMs;
    private string _sessionId = string.Empty;

    public GrpcTransportClient(
        ILogger<GrpcTransportClient> logger,
        IOptionsMonitor<LinuxBackendOptions> optionsMonitor)
    {
        _logger = logger;
        _optionsMonitor = optionsMonitor;
    }

    public event EventHandler<TransportStateChangedEventArgs>? StateChanged;
    public event EventHandler<DetectionResult>? DetectionReceived;
    public event EventHandler<TransportMetrics>? MetricsUpdated;

    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsConnected)
            {
                return;
            }

            _receiveCts?.Cancel();
            if (_stream is not null)
            {
                try
                {
                    await _stream.RequestStream.CompleteAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Ignore complete stream exception");
                }
            }

            if (_receiveLoopTask is not null)
            {
                await Task.WhenAny(_receiveLoopTask, Task.Delay(1000, cancellationToken));
            }

            if (_heartbeatTask is not null)
            {
                await Task.WhenAny(_heartbeatTask, Task.Delay(1000, cancellationToken));
            }

            CleanupConnection();
            NotifyState(AgentConnectionState.Disconnected, "Disconnected");
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task SendSessionStartAsync(MeetingAgent.Core.SessionStartMessage message, CancellationToken cancellationToken)
    {
        _sessionId = message.SessionId;
        var envelope = new ClientEnvelope
        {
            SessionStart = new MeetingAgent.Transport.Proto.SessionStartMessage
            {
                SessionId = message.SessionId,
                TimestampUtcMs = message.TimestampUtcMs,
                AgentVersion = message.AgentVersion,
                Hostname = message.Hostname,
                CaptureFps = message.CaptureFps,
                AudioSampleRate = message.AudioSampleRate
            }
        };

        await SendEnvelopeAsync(envelope, cancellationToken);
    }

    public Task SendSessionStopAsync(MeetingAgent.Core.SessionStopMessage message, CancellationToken cancellationToken)
    {
        var envelope = new ClientEnvelope
        {
            SessionStop = new MeetingAgent.Transport.Proto.SessionStopMessage
            {
                SessionId = message.SessionId,
                TimestampUtcMs = message.TimestampUtcMs,
                Reason = message.Reason
            }
        };

        return SendEnvelopeAsync(envelope, cancellationToken);
    }

    public Task SendVideoFrameAsync(FramePacket framePacket, CancellationToken cancellationToken)
    {
        var envelope = new ClientEnvelope
        {
            VideoFrame = new VideoFrameMessage
            {
                SessionId = _sessionId,
                FrameId = framePacket.FrameId,
                TimestampUtcMs = framePacket.TimestampUtcMs,
                Width = framePacket.Width,
                Height = framePacket.Height,
                PixelFormat = framePacket.PixelFormat.ToString(),
                Stride = framePacket.Stride,
                IsLosslessCompressed = framePacket.IsLosslessCompressed,
                Compression = framePacket.Compression,
                FrameBytes = Google.Protobuf.ByteString.CopyFrom(framePacket.Data)
            }
        };

        Interlocked.Add(ref _sessionFrameBytes, framePacket.Data.LongLength);
        EmitMetrics(lastFrameUtcMs: framePacket.TimestampUtcMs);
        return SendEnvelopeAsync(envelope, cancellationToken);
    }

    public Task SendAudioChunkAsync(AudioPacket audioPacket, CancellationToken cancellationToken)
    {
        var envelope = new ClientEnvelope
        {
            AudioChunk = new AudioChunkMessage
            {
                SessionId = _sessionId,
                ChunkId = audioPacket.ChunkId,
                TimestampUtcMs = audioPacket.TimestampUtcMs,
                SampleRate = audioPacket.SampleRate,
                Channels = audioPacket.Channels,
                BitsPerSample = audioPacket.BitsPerSample,
                PcmBytes = Google.Protobuf.ByteString.CopyFrom(audioPacket.Data)
            }
        };

        Interlocked.Add(ref _sessionAudioBytes, audioPacket.Data.LongLength);
        EmitMetrics(lastAudioUtcMs: audioPacket.TimestampUtcMs);
        return SendEnvelopeAsync(envelope, cancellationToken);
    }

    public Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var pingId = Interlocked.Increment(ref _pingId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _pendingPings[pingId] = now;

        var envelope = new ClientEnvelope
        {
            Ping = new HealthPingMessage
            {
                SessionId = _sessionId,
                PingId = pingId,
                ClientSendUtcMs = now
            }
        };
        return SendEnvelopeAsync(envelope, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None);
        _connectionLock.Dispose();
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (IsConnected)
        {
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
            {
                return;
            }

            NotifyState(AgentConnectionState.Connecting, "Connecting");
            var options = _optionsMonitor.CurrentValue;
            var address = $"http://{options.Host}:{options.Port}";
            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                EnableMultipleHttp2Connections = true
            };

            _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = handler
            });
            _client = new DetectionAgentService.DetectionAgentServiceClient(_channel);

            var headers = new Metadata();
            if (!string.IsNullOrWhiteSpace(options.AuthToken))
            {
                headers.Add("authorization", $"Bearer {options.AuthToken}");
            }

            _stream = _client.Stream(headers, cancellationToken: cancellationToken);
            _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), _receiveCts.Token);
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_receiveCts.Token), _receiveCts.Token);
            _connectedAtTickMs = Environment.TickCount64;
            IsConnected = true;
            NotifyState(AgentConnectionState.Connected, $"Connected {options.Host}:{options.Port}");
            _logger.LogInformation("Connected to Linux backend: {Address}", address);
        }
        catch (Exception ex)
        {
            NotifyState(AgentConnectionState.Faulted, ex.Message);
            CleanupConnection();
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task SendEnvelopeAsync(ClientEnvelope envelope, CancellationToken cancellationToken)
    {
        var retries = 0;
        while (retries < 2)
        {
            try
            {
                await EnsureConnectedAsync(cancellationToken);
                if (_stream is null)
                {
                    throw new InvalidOperationException("Stream is unavailable.");
                }

                await _stream.RequestStream.WriteAsync(envelope).WaitAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is RpcException or IOException or InvalidOperationException)
            {
                retries++;
                _logger.LogWarning(ex, "Send failed, retry {Retry}", retries);
                Interlocked.Increment(ref _droppedFrames);
                NotifyState(AgentConnectionState.Reconnecting, "Reconnecting...");
                var reconnectDelaySeconds = Math.Max(1, _optionsMonitor.CurrentValue.ReconnectDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(reconnectDelaySeconds), cancellationToken);
                await ResetConnectionAsync(CancellationToken.None);
                if (retries >= 2)
                {
                    NotifyState(AgentConnectionState.Faulted, ex.Message);
                    throw;
                }
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return;
        }

        try
        {
            await foreach (var message in _stream.ResponseStream.ReadAllAsync(cancellationToken))
            {
                if (message.PayloadCase == ServerEnvelope.PayloadOneofCase.DetectionResult)
                {
                    var result = MapDetectionResult(message.DetectionResult);
                    DetectionReceived?.Invoke(this, result);
                    EmitMetrics(lastDetectionUtcMs: result.TimestampUtcMs);
                }
                else if (message.PayloadCase == ServerEnvelope.PayloadOneofCase.Pong)
                {
                    var pong = message.Pong;
                    if (_pendingPings.TryRemove(pong.PingId, out var clientSendMs))
                    {
                        var rtt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - clientSendMs;
                        EmitMetrics(lastRttMs: rtt);
                    }
                }
                else if (message.PayloadCase == ServerEnvelope.PayloadOneofCase.Error)
                {
                    _logger.LogWarning("Backend error code={Code}, message={Message}",
                        message.Error.Code, message.Error.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Receive loop faulted.");
            NotifyState(AgentConnectionState.Reconnecting, "Receive loop disconnected.");
            await ResetConnectionAsync(CancellationToken.None);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        var interval = Math.Max(1, _optionsMonitor.CurrentValue.HeartbeatIntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (!IsConnected)
            {
                continue;
            }

            try
            {
                await SendHeartbeatAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat failed.");
            }
        }
    }

    private async Task ResetConnectionAsync(CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            CleanupConnection();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private void CleanupConnection()
    {
        lock (_stateLock)
        {
            IsConnected = false;
            _receiveCts?.Cancel();
            _stream?.Dispose();
            _receiveCts?.Dispose();
            _channel?.Dispose();
            _stream = null;
            _client = null;
            _receiveCts = null;
            _receiveLoopTask = null;
            _heartbeatTask = null;
            _channel = null;
            _connectedAtTickMs = 0;
        }
    }

    private DetectionResult MapDetectionResult(DetectionResultMessage message)
    {
        var boxes = message.Boxes.Select(x => new DetectionBox(x.X, x.Y, x.Width, x.Height, x.Label, x.Score)).ToArray();
        var level = message.RiskLevel switch
        {
            RiskLevelProto.RiskLevelLow => RiskLevel.Low,
            RiskLevelProto.RiskLevelMedium => RiskLevel.Medium,
            RiskLevelProto.RiskLevelHigh => RiskLevel.High,
            RiskLevelProto.RiskLevelCritical => RiskLevel.Critical,
            _ => RiskLevel.Info
        };

        return new DetectionResult(
            FrameId: message.FrameId,
            TimestampUtcMs: message.TimestampUtcMs,
            RiskLevel: level,
            RiskType: message.RiskType,
            Message: message.Message,
            Score: message.Score,
            Source: message.Source,
            Boxes: boxes,
            RawPayload: message.RawPayload);
    }

    private void NotifyState(AgentConnectionState state, string message)
    {
        StateChanged?.Invoke(this, new TransportStateChangedEventArgs(state, message));
    }

    private void EmitMetrics(
        long? lastFrameUtcMs = null,
        long? lastAudioUtcMs = null,
        long? lastDetectionUtcMs = null,
        double? lastRttMs = null)
    {
        var frameBytes = Interlocked.Read(ref _sessionFrameBytes);
        var elapsedSeconds = Math.Max(1.0, (Environment.TickCount64 - _connectedAtTickMs) / 1000.0);
        var throughputMbps = (frameBytes * 8.0) / 1024 / 1024 / elapsedSeconds;

        MetricsUpdated?.Invoke(this, new TransportMetrics(
            LastFrameSentUtcMs: lastFrameUtcMs ?? 0,
            LastAudioSentUtcMs: lastAudioUtcMs ?? 0,
            LastDetectionUtcMs: lastDetectionUtcMs ?? 0,
            FrameThroughputMbps: throughputMbps,
            DroppedFrames: Interlocked.Read(ref _droppedFrames),
            LastRttMs: lastRttMs ?? 0));
    }
}
