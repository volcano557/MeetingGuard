using System.Collections.ObjectModel;

namespace MeetingAgent.Core;

public enum PixelFormatKind
{
    BGRA8 = 0,
    RGBA8 = 1,
    RGB24 = 2,
    NV12 = 3
}

public enum AgentConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Faulted = 4
}

public enum CaptureLifecycleState
{
    Idle = 0,
    SelectingWindow = 1,
    Running = 2,
    Paused = 3,
    Stopped = 4,
    WindowClosed = 5,
    Failed = 6
}

public enum RiskLevel
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

public sealed record WindowDescriptor(nint Hwnd, string Title, string ProcessName, bool IsMinimized);

public sealed record FramePacket(
    long FrameId,
    long TimestampUtcMs,
    int Width,
    int Height,
    PixelFormatKind PixelFormat,
    int Stride,
    bool IsLosslessCompressed,
    string Compression,
    byte[] Data);

public sealed record AudioPacket(
    long ChunkId,
    long TimestampUtcMs,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    byte[] Data);

public sealed record DetectionBox(float X, float Y, float Width, float Height, string Label, float Score);

public sealed record DetectionResult(
    long FrameId,
    long TimestampUtcMs,
    RiskLevel RiskLevel,
    string RiskType,
    string Message,
    float Score,
    string Source,
    IReadOnlyList<DetectionBox> Boxes,
    string? RawPayload = null);

public sealed record SessionStartMessage(
    string SessionId,
    long TimestampUtcMs,
    string AgentVersion,
    string Hostname,
    int CaptureFps,
    int AudioSampleRate);

public sealed record SessionStopMessage(
    string SessionId,
    long TimestampUtcMs,
    string Reason);

public sealed record TransportMetrics(
    long LastFrameSentUtcMs,
    long LastAudioSentUtcMs,
    long LastDetectionUtcMs,
    double FrameThroughputMbps,
    long DroppedFrames,
    double LastRttMs);

public sealed record AlertEvent(
    DateTimeOffset Timestamp,
    RiskLevel RiskLevel,
    string RiskType,
    string Message,
    string Source,
    long FrameId);

public sealed record AgentStatusSnapshot
{
    public AgentConnectionState ConnectionState { get; init; } = AgentConnectionState.Disconnected;
    public CaptureLifecycleState CaptureState { get; init; } = CaptureLifecycleState.Idle;
    public string CurrentWindowTitle { get; init; } = "N/A";
    public string CurrentResolution { get; init; } = "N/A";
    public int CurrentFps { get; init; }
    public DateTimeOffset? LastFrameSentAt { get; init; }
    public DateTimeOffset? LastDetectionAt { get; init; }
    public RiskLevel CurrentRiskLevel { get; init; } = RiskLevel.Info;
    public double FrameThroughputMbps { get; init; }
    public long DroppedFrames { get; init; }
    public bool AudioActive { get; init; }
    public double LastRttMs { get; init; }
    public long TotalDetections { get; init; }
    public float LastDetectionScore { get; init; }
    public string LastRiskType { get; init; } = "N/A";
    public string LastRiskMessage { get; init; } = "N/A";
    public ReadOnlyCollection<AlertEvent> RecentAlerts { get; init; } = new(new List<AlertEvent>());
}

public sealed record SystemInfoSnapshot(
    string MachineName,
    string OsVersion,
    string RuntimeVersion,
    int ProcessorCount,
    long WorkingSetMb);

public sealed record HealthCheckResult(
    bool IsHealthy,
    DateTimeOffset CheckedAt,
    string Message);

public sealed class CaptureStateChangedEventArgs : EventArgs
{
    public CaptureStateChangedEventArgs(CaptureLifecycleState state, string message)
    {
        State = state;
        Message = message;
    }

    public CaptureLifecycleState State { get; }
    public string Message { get; }
}

public sealed class TransportStateChangedEventArgs : EventArgs
{
    public TransportStateChangedEventArgs(AgentConnectionState state, string message)
    {
        State = state;
        Message = message;
    }

    public AgentConnectionState State { get; }
    public string Message { get; }
}
