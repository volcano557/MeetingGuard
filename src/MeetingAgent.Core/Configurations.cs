namespace MeetingAgent.Core;

public sealed class AgentOptions
{
    public LinuxBackendOptions LinuxBackend { get; set; } = new();
    public CaptureOptions Capture { get; set; } = new();
    public AudioOptions Audio { get; set; } = new();
    public AlertingOptions Alerting { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
}

public sealed class LinuxBackendOptions
{
    public string Protocol { get; set; } = "http";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 50053;
    /// <summary>为 true 时走 Gateway :8000，API 路径前缀 /api/aigc/。</summary>
    public bool UseGateway { get; set; }
    /// <summary>可选：完整网关/直连 URL，如 http://172.20.0.1:50053 或 http://172.20.0.1:8000</summary>
    public string GatewayUrl { get; set; } = string.Empty;
    public string AuthToken { get; set; } = string.Empty;
    public bool UseLosslessCompression { get; set; }
    public string Compression { get; set; } = "none";
    public int ReconnectDelaySeconds { get; set; } = 3;
    public int HeartbeatIntervalSeconds { get; set; } = 5;
    public int RequestTimeoutSeconds { get; set; } = 10;
    public HttpBackendOptions Http { get; set; } = new();
}

public sealed class HttpBackendOptions
{
    public string Scheme { get; set; } = "http";
    public string HealthPingMethod { get; set; } = "GET";
    public string SessionStartPath { get; set; } = "/api/agent/session/start";
    public string SessionStopPath { get; set; } = "/api/agent/session/stop";
    public string VideoFramePath { get; set; } = "/api/agent/video/frame";
    public string AudioChunkPath { get; set; } = "/api/agent/audio/chunk";
    public string HealthPingPath { get; set; } = "/health";
    public string DetectionPollPath { get; set; } = "/api/agent/results/poll";
    public bool EnableDetectionPolling { get; set; }
    public int PollingIntervalMs { get; set; } = 1000;
}

public sealed class CaptureOptions
{
    public int TargetFps { get; set; } = 10;
    public bool EnableResize { get; set; }
    public int ResizeWidth { get; set; }
    public int ResizeHeight { get; set; }
    public string PixelFormat { get; set; } = "BGRA8";
}

public sealed class AudioOptions
{
    public bool Enabled { get; set; } = true;
    public int SampleRate { get; set; } = 16000;
    public int Channels { get; set; } = 1;
    public int BitsPerSample { get; set; } = 16;
    /// <summary>
    /// loopback=仅系统播放声（耳机/扬声器）；
    /// microphone=仅麦克风；
    /// mixed=环回+麦克风混合（会议场景推荐）。
    /// </summary>
    public string CaptureMode { get; set; } = "mixed";

    /// <summary>环回设备角色：Communications（默认，会议/通话设备）或 Multimedia。</summary>
    public string LoopbackRole { get; set; } = "Communications";

    public int ChunkMilliseconds { get; set; } = 100;
}

public sealed class AlertingOptions
{
    public int CooldownSeconds { get; set; } = 10;
    public bool EnableToast { get; set; } = true;
    public bool EnableSound { get; set; }
    public int MaxHistory { get; set; } = 100;
}

public sealed class LoggingOptions
{
    public string LogDirectory { get; set; } = "logs";
}

/// <summary>统一 API 网关（MeetingGuard gateway.py，默认 :8000）。</summary>
public sealed class GatewayOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8000;
}

/// <summary>FaceProtect 后端（默认 :5001）。</summary>
public sealed class FaceProtectOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5001;
    /// <summary>完整 URL，优先于 Host+Port，如 http://172.20.0.1:5001</summary>
    public string Url { get; set; } = string.Empty;
}

/// <summary>语义风险 / audio_server（默认 :8003）。</summary>
public sealed class SemanticRiskOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8003;
    /// <summary>完整 URL，优先于 Host+Port，如 http://172.20.0.1:8003</summary>
    public string Url { get; set; } = string.Empty;
}
