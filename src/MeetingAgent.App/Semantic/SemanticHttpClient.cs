using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingAgent.App.Semantic;

/// <summary>
/// audio_server (:8003) 的轻量 HTTP 客户端：
///   · 启动 / 结束语义会话
///   · 上传 WAV 音频块
///   · 轮询当前会话的转录文本、说话人、风险事件
/// </summary>
public sealed class SemanticHttpClient : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private string _baseUrl = "http://127.0.0.1:8003";

    public string BaseUrl
    {
        get => _baseUrl;
        set => _baseUrl = (value ?? "http://127.0.0.1:8003").TrimEnd('/');
    }

    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<string?> StartSessionAsync(string title, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync(
                $"{_baseUrl}/api/session/start",
                new { title }, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var doc = await resp.Content.ReadFromJsonAsync<StartResp>(cancellationToken: ct);
            return doc?.SessionId;
        }
        catch { return null; }
    }

    public async Task StopSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            using var _ = await _http.PostAsJsonAsync(
                $"{_baseUrl}/api/session/stop",
                new { session_id = sessionId }, ct);
        }
        catch { /* swallow */ }
    }

    public async Task UploadChunkAsync(string sessionId, byte[] wavBytes, int sampleRate,
                                       string speakerId, CancellationToken ct = default)
    {
        try
        {
            using var _ = await _http.PostAsJsonAsync(
                $"{_baseUrl}/api/audio/chunk",
                new
                {
                    session_id  = sessionId,
                    audio       = Convert.ToBase64String(wavBytes),
                    sample_rate = sampleRate,
                    speaker_id  = speakerId,
                    format      = "wav",
                }, ct);
        }
        catch { /* swallow */ }
    }

    public async Task<PollResult?> PollAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(
                $"{_baseUrl}/api/results/poll?session_id={Uri.EscapeDataString(sessionId)}&limit=20", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<PollResult>(cancellationToken: ct);
        }
        catch { return null; }
    }

    public void Dispose() => _http.Dispose();

    // ─── DTO ────────────────────────────────────────────────────────

    private sealed class StartResp
    {
        [JsonPropertyName("session_id")] public string? SessionId { get; set; }
        [JsonPropertyName("ok")]         public bool Ok { get; set; }
    }

    public sealed class TranscriptItem
    {
        [JsonPropertyName("ts")]      public string Ts { get; set; } = "";
        [JsonPropertyName("speaker")] public string Speaker { get; set; } = "";
        [JsonPropertyName("text")]    public string Text { get; set; } = "";
    }

    public sealed class RiskItem
    {
        [JsonPropertyName("ts")]         public string Ts { get; set; } = "";
        [JsonPropertyName("speaker_id")] public string SpeakerId { get; set; } = "";
        [JsonPropertyName("text")]       public string Text { get; set; } = "";
        [JsonPropertyName("risk_level")] public string RiskLevel { get; set; } = "";
        [JsonPropertyName("risk_score")] public double RiskScore { get; set; }
        [JsonPropertyName("risk_type")]  public string RiskType { get; set; } = "";
        [JsonPropertyName("keyword")]    public string Keyword { get; set; } = "";
    }

    public sealed class SpeakerInfo
    {
        [JsonPropertyName("name")]        public string Name { get; set; } = "";
        [JsonPropertyName("risk_count")]  public int RiskCount { get; set; }
        [JsonPropertyName("chunk_count")] public int ChunkCount { get; set; }
    }

    public sealed class PollResult
    {
        [JsonPropertyName("results")]     public List<RiskItem> Results { get; set; } = new();
        [JsonPropertyName("speakers")]    public Dictionary<string, SpeakerInfo> Speakers { get; set; } = new();
        [JsonPropertyName("transcript")]  public List<TranscriptItem> Transcript { get; set; } = new();
        [JsonPropertyName("risk_events")] public List<RiskItem> RiskEvents { get; set; } = new();
    }

    // ─── 工具：16-bit PCM → WAV 字节数组 ────────────────────────────

    public static byte[] PcmToWav(byte[] pcm, int sampleRate, int channels, int bitsPerSample)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        int dataLen   = pcm.Length;
        int byteRate  = sampleRate * channels * (bitsPerSample / 8);
        int blockAlign = channels * (bitsPerSample / 8);

        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataLen);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);                              // fmt 长度
        bw.Write((short)1);                        // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataLen);
        bw.Write(pcm);
        bw.Flush();
        return ms.ToArray();
    }
}
