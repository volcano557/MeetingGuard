using MeetingAgent.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingAgent.Transport;

public sealed class RawFrameDetectionBackendAdapter : IDetectionBackendAdapter
{
    private readonly ITransportClient _transportClient;
    private readonly IFrameCompressionService _compressionService;
    private readonly ILogger<RawFrameDetectionBackendAdapter> _logger;
    private readonly CaptureOptions _captureOptions;
    private readonly AudioOptions _audioOptions;
    private string? _sessionId;

    public RawFrameDetectionBackendAdapter(
        ITransportClient transportClient,
        IFrameCompressionService compressionService,
        IOptions<CaptureOptions> captureOptions,
        IOptions<AudioOptions> audioOptions,
        ILogger<RawFrameDetectionBackendAdapter> logger)
    {
        _transportClient = transportClient;
        _compressionService = compressionService;
        _captureOptions = captureOptions.Value;
        _audioOptions = audioOptions.Value;
        _logger = logger;
    }

    public async Task StartSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        _sessionId = sessionId;
        if (!_transportClient.IsConnected)
        {
            await _transportClient.ConnectAsync(cancellationToken);
        }

        await _transportClient.SendSessionStartAsync(new SessionStartMessage(
            SessionId: sessionId,
            TimestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            AgentVersion: "1.0.0-phase1",
            Hostname: Environment.MachineName,
            CaptureFps: _captureOptions.TargetFps,
            AudioSampleRate: _audioOptions.SampleRate), cancellationToken);

        _logger.LogInformation("Detection session started: {SessionId}", sessionId);
    }

    public async Task StopSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            return;
        }

        await _transportClient.SendSessionStopAsync(new SessionStopMessage(
            SessionId: sessionId,
            TimestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Reason: "Stopped by user"), cancellationToken);

        _logger.LogInformation("Detection session stopped: {SessionId}", sessionId);
        _sessionId = null;
    }

    public async Task PushFrameAsync(FramePacket framePacket, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            return;
        }

        var payload = _compressionService.CompressFrame(framePacket);
        await _transportClient.SendVideoFrameAsync(payload, cancellationToken);
    }

    public async Task PushAudioAsync(AudioPacket audioPacket, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            return;
        }

        await _transportClient.SendAudioChunkAsync(audioPacket, cancellationToken);
    }
}
