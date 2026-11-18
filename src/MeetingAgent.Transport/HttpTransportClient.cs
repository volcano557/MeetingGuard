using MeetingAgent.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MeetingAgent.Transport;

public sealed class HttpTransportClient : ITransportClient, IVideoSender, IAudioSender, IDetectionResultReceiver
{
    private readonly ILogger<HttpTransportClient> _logger;
    private readonly IOptionsMonitor<LinuxBackendOptions> _optionsMonitor;
    private HttpClient? _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;
    private string _sessionId = string.Empty;

    public HttpTransportClient(
        ILogger<HttpTransportClient> logger,
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
        if (IsConnected)
        {
            return;
        }

        var options = _optionsMonitor.CurrentValue;
        var baseUri = BuildBaseUri(options);
        _httpClient?.Dispose();
        _httpClient = CreateHttpClient(options, baseUri);

        await SendHeartbeatAsync(cancellationToken);
        IsConnected = true;
        StateChanged?.Invoke(this, new TransportStateChangedEventArgs(AgentConnectionState.Connected, $"Connected {baseUri}"));
        _logger.LogInformation("Connected to Linux backend via HTTP: {Address}", baseUri);

        StartPollingIfNeeded(options);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return Task.CompletedTask;
        }

        StopPolling();
        _httpClient?.Dispose();
        _httpClient = null;
        IsConnected = false;
        StateChanged?.Invoke(this, new TransportStateChangedEventArgs(AgentConnectionState.Disconnected, "Disconnected"));
        return Task.CompletedTask;
    }

    public Task SendSessionStartAsync(SessionStartMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_optionsMonitor.CurrentValue.Http.SessionStartPath))
        {
            return Task.CompletedTask;
        }

        _sessionId = string.IsNullOrWhiteSpace(message.SessionId)
            ? Guid.NewGuid().ToString("N")
            : message.SessionId;
        var payload = new Dictionary<string, object?>
        {
            ["session_id"] = _sessionId,
            ["timestamp_utc_ms"] = message.TimestampUtcMs,
            ["agent_version"] = message.AgentVersion,
            ["hostname"] = message.Hostname,
            ["capture_fps"] = message.CaptureFps,
            ["audio_sample_rate"] = message.AudioSampleRate
        };
        return TryPostSessionEventAsync(_optionsMonitor.CurrentValue.Http.SessionStartPath, payload, "session/start", cancellationToken);
    }

    public Task SendSessionStopAsync(SessionStopMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_optionsMonitor.CurrentValue.Http.SessionStopPath))
        {
            return Task.CompletedTask;
        }

        var payload = new
        {
            session_id = message.SessionId,
            timestamp_utc_ms = message.TimestampUtcMs,
            reason = message.Reason
        };
        return TryPostSessionEventAsync(_optionsMonitor.CurrentValue.Http.SessionStopPath, payload, "session/stop", cancellationToken);
    }

    public async Task SendVideoFrameAsync(FramePacket framePacket, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_optionsMonitor.CurrentValue.Http.VideoFramePath))
        {
            throw new InvalidOperationException("HTTP VideoFramePath is not configured.");
        }

        var payload = new
        {
            session_id = _sessionId,
            frame_id = framePacket.FrameId,
            timestamp_utc_ms = framePacket.TimestampUtcMs,
            width = framePacket.Width,
            height = framePacket.Height,
            pixel_format = framePacket.PixelFormat.ToString(),
            stride = framePacket.Stride,
            is_lossless_compressed = framePacket.IsLosslessCompressed,
            compression = framePacket.Compression,
            frame_bytes_base64 = Convert.ToBase64String(framePacket.Data)
        };

        var response = await PostAsync(_optionsMonitor.CurrentValue.Http.VideoFramePath, payload, cancellationToken);
        MetricsUpdated?.Invoke(this, new TransportMetrics(
            LastFrameSentUtcMs: framePacket.TimestampUtcMs,
            LastAudioSentUtcMs: 0,
            LastDetectionUtcMs: 0,
            FrameThroughputMbps: framePacket.Data.LongLength * 8.0 / 1024 / 1024,
            DroppedFrames: 0,
            LastRttMs: 0));
        await TryParseDetectionResponseAsync(response, cancellationToken);
    }

    public async Task SendAudioChunkAsync(AudioPacket audioPacket, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_optionsMonitor.CurrentValue.Http.AudioChunkPath))
        {
            return;
        }

        var payload = new
        {
            session_id = _sessionId,
            chunk_id = audioPacket.ChunkId,
            timestamp_utc_ms = audioPacket.TimestampUtcMs,
            sample_rate = audioPacket.SampleRate,
            channels = audioPacket.Channels,
            bits_per_sample = audioPacket.BitsPerSample,
            pcm_bytes_base64 = Convert.ToBase64String(audioPacket.Data)
        };

        var response = await PostAsync(_optionsMonitor.CurrentValue.Http.AudioChunkPath, payload, cancellationToken);
        MetricsUpdated?.Invoke(this, new TransportMetrics(
            LastFrameSentUtcMs: 0,
            LastAudioSentUtcMs: audioPacket.TimestampUtcMs,
            LastDetectionUtcMs: 0,
            FrameThroughputMbps: 0,
            DroppedFrames: 0,
            LastRttMs: 0));
        await TryParseDetectionResponseAsync(response, cancellationToken);
    }

    public async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var healthPath = _optionsMonitor.CurrentValue.Http.HealthPingPath;
        if (string.IsNullOrWhiteSpace(healthPath))
        {
            return;
        }

        var pingId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var method = (_optionsMonitor.CurrentValue.Http.HealthPingMethod ?? "GET").Trim().ToUpperInvariant();

        if (method == "GET")
        {
            var path = $"{healthPath}?session_id={Uri.EscapeDataString(_sessionId)}&ping_id={pingId}&client_send_utc_ms={now}";
            var response = await RequireHttpClient().GetAsync(path, cancellationToken);
            response.EnsureSuccessStatusCode();
            return;
        }

        var payload = new
        {
            session_id = _sessionId,
            ping_id = pingId,
            client_send_utc_ms = now
        };
        await PostAsync(healthPath, payload, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        StopPolling();
        _httpClient?.Dispose();
        _httpClient = null;
        await Task.CompletedTask;
    }

    private Uri BuildBaseUri(LinuxBackendOptions options)
    {
        var scheme = options.Http.Scheme;
        if (string.IsNullOrWhiteSpace(scheme))
        {
            scheme = "http";
        }

        return new Uri($"{scheme}://{options.Host}:{options.Port}");
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object payload, CancellationToken cancellationToken)
    {
        if (!IsConnected && !path.Equals(_optionsMonitor.CurrentValue.Http.HealthPingPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("HTTP transport is not connected.");
        }

        // PostAsJsonAsync uses chunked transfer encoding (no Content-Length).
        // Python BaseHTTPRequestHandler only reads Content-Length bytes, ignoring chunked bodies.
        // Pre-serialize to a byte array so Content-Length is always set explicitly.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json; charset=utf-8");

        var response = await RequireHttpClient().PostAsync(path, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} on '{path}'. Response: {body}",
                null,
                response.StatusCode);
        }
        return response;
    }

    private async Task TryParseDetectionResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength == 0)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        TryPublishDetectionFromJson(body);
    }

    private void StartPollingIfNeeded(LinuxBackendOptions options)
    {
        if (!options.Http.EnableDetectionPolling)
        {
            return;
        }

        StopPolling();
        _pollingCts = new CancellationTokenSource();
        _pollingTask = Task.Run(() => PollDetectionsLoopAsync(_pollingCts.Token), _pollingCts.Token);
    }

    private void StopPolling()
    {
        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
        _pollingCts = null;
        _pollingTask = null;
    }

    private async Task PollDetectionsLoopAsync(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue.Http;
        var interval = Math.Max(200, options.PollingIntervalMs);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                var response = await RequireHttpClient().GetAsync(options.DetectionPollPath, cancellationToken);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                TryPublishDetectionFromJson(body);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Detection polling failed.");
            }
        }
    }

    private void TryPublishDetectionFromJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var result = ParseDetection(item);
                    if (result is not null)
                    {
                        DetectionReceived?.Invoke(this, result);
                    }
                }

                return;
            }

            var node = document.RootElement;
            if (node.TryGetProperty("detection_result", out var wrapped))
            {
                node = wrapped;
            }

            var single = ParseDetection(node);
            if (single is not null)
            {
                DetectionReceived?.Invoke(this, single);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse detection JSON.");
        }
    }

    private static DetectionResult? ParseDetection(JsonElement node)
    {
        if (!node.TryGetProperty("risk_level", out _))
        {
            return null;
        }

        var frameId = node.TryGetProperty("frame_id", out var frameProp) ? frameProp.GetInt64() : 0;
        var timestamp = node.TryGetProperty("timestamp_utc_ms", out var tsProp)
            ? tsProp.GetInt64()
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var riskLevelText = node.TryGetProperty("risk_level", out var levelProp) ? levelProp.GetString() ?? "info" : "info";
        var riskType = node.TryGetProperty("risk_type", out var typeProp) ? typeProp.GetString() ?? "unknown" : "unknown";
        var message = node.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? string.Empty : string.Empty;
        var score = node.TryGetProperty("score", out var scoreProp) ? scoreProp.GetSingle() : 0f;
        var source = node.TryGetProperty("source", out var sourceProp) ? sourceProp.GetString() ?? "visual" : "visual";

        var boxes = new List<DetectionBox>();
        if (node.TryGetProperty("boxes", out var boxesProp) && boxesProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var box in boxesProp.EnumerateArray())
            {
                boxes.Add(new DetectionBox(
                    X: box.TryGetProperty("x", out var x) ? x.GetSingle() : 0f,
                    Y: box.TryGetProperty("y", out var y) ? y.GetSingle() : 0f,
                    Width: box.TryGetProperty("width", out var w) ? w.GetSingle() : 0f,
                    Height: box.TryGetProperty("height", out var h) ? h.GetSingle() : 0f,
                    Label: box.TryGetProperty("label", out var l) ? l.GetString() ?? string.Empty : string.Empty,
                    Score: box.TryGetProperty("score", out var s) ? s.GetSingle() : 0f));
            }
        }

        return new DetectionResult(
            FrameId: frameId,
            TimestampUtcMs: timestamp,
            RiskLevel: MapRiskLevel(riskLevelText),
            RiskType: riskType,
            Message: message,
            Score: score,
            Source: source,
            Boxes: boxes);
    }

    private static RiskLevel MapRiskLevel(string text)
    {
        return text.Trim().ToLowerInvariant() switch
        {
            "low" or "risk_level_low" => RiskLevel.Low,
            "medium" or "risk_level_medium" => RiskLevel.Medium,
            "high" or "risk_level_high" => RiskLevel.High,
            "critical" or "risk_level_critical" => RiskLevel.Critical,
            _ => RiskLevel.Info
        };
    }

    private async Task TryPostSessionEventAsync(string path, object payload, string eventName, CancellationToken cancellationToken)
    {
        try
        {
            await PostAsync(path, payload, cancellationToken);
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound
            || ex.Message.Contains("HTTP 400", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "Backend rejected optional HTTP endpoint {EventName}; continue without it.", eventName);
        }
    }

    private HttpClient RequireHttpClient()
        => _httpClient ?? throw new InvalidOperationException("HTTP client is not initialized.");

    private static HttpClient CreateHttpClient(LinuxBackendOptions options, Uri baseUri)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(Math.Max(5, options.RequestTimeoutSeconds))
        };

        if (!string.IsNullOrWhiteSpace(options.AuthToken))
        {
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.AuthToken}");
        }

        return client;
    }
}
