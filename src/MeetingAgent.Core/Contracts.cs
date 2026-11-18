using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MeetingAgent.Core;

public interface IWindowDiscoveryService
{
    Task<IReadOnlyList<WindowDescriptor>> GetAvailableWindowsAsync(CancellationToken cancellationToken = default);
}

public interface IVideoCaptureService
{
    event EventHandler<FramePacket>? FrameReady;
    event EventHandler<CaptureStateChangedEventArgs>? StateChanged;

    bool IsRunning { get; }
    WindowDescriptor? CurrentWindow { get; }

    Task StartAsync(WindowDescriptor target, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IAudioCaptureService
{
    event EventHandler<AudioPacket>? AudioReady;
    event EventHandler<string>? CaptureError;

    bool IsRunning { get; }

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface ITransportClient : IAsyncDisposable
{
    event EventHandler<TransportStateChangedEventArgs>? StateChanged;
    event EventHandler<DetectionResult>? DetectionReceived;
    event EventHandler<TransportMetrics>? MetricsUpdated;

    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task SendSessionStartAsync(SessionStartMessage message, CancellationToken cancellationToken);
    Task SendSessionStopAsync(SessionStopMessage message, CancellationToken cancellationToken);
    Task SendVideoFrameAsync(FramePacket framePacket, CancellationToken cancellationToken);
    Task SendAudioChunkAsync(AudioPacket audioPacket, CancellationToken cancellationToken);
    Task SendHeartbeatAsync(CancellationToken cancellationToken);
}

public interface IVideoSender
{
    Task SendVideoFrameAsync(FramePacket framePacket, CancellationToken cancellationToken);
}

public interface IAudioSender
{
    Task SendAudioChunkAsync(AudioPacket audioPacket, CancellationToken cancellationToken);
}

public interface IDetectionResultReceiver
{
    event EventHandler<DetectionResult>? DetectionReceived;
}

public interface IDetectionBackendAdapter
{
    Task StartSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task StopSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task PushFrameAsync(FramePacket framePacket, CancellationToken cancellationToken);
    Task PushAudioAsync(AudioPacket audioPacket, CancellationToken cancellationToken);
}

public interface IFrameCompressionService
{
    FramePacket CompressFrame(FramePacket framePacket);
}

public interface IAlertService
{
    event EventHandler<AlertEvent>? AlertRaised;
    IReadOnlyList<AlertEvent> GetRecentAlerts();
    void HandleDetection(DetectionResult detectionResult);
}

public interface ISystemInfoProvider
{
    SystemInfoSnapshot Collect();
}

public interface IAgentHealthService
{
    HealthCheckResult Check();
}
