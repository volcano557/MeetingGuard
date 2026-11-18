using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MeetingAgent.App;

/// <summary>
/// 与 Linux 侧 face_protect_server.py (FastAPI) 通信的 HTTP 客户端。
/// </summary>
public sealed class FaceProtectHttpClient : IDisposable
{
    private readonly HttpClient _http;

    public FaceProtectHttpClient()
    {
        _http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public void SetBaseUrl(string host, int port)
    {
        _http.BaseAddress = new Uri($"http://{host}:{port}/");
    }

    // ─── 健康检查 ────────────────────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<FpHealthResponse?> GetHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("health", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<FpHealthResponse>(ct);
        }
        catch
        {
            return null;
        }
    }

    // ─── 离线生成 ────────────────────────────────────────────────────────────

    public async Task<FpGenerateResponse> StartGenerationAsync(
        string userName,
        string imgsDir,
        float eps = 16f,
        int iters = 200,
        string device = "cuda:0",
        CancellationToken ct = default)
    {
        var body = new
        {
            user_name = userName,
            imgs_dir = imgsDir,
            eps,
            iters,
            device
        };
        using var resp = await _http.PostAsJsonAsync("api/face/offline/generate", body, ct);
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<FpGenerateResponse>(ct))!;
    }

    public async Task<FpGenerateStatus> GetGenerationStatusAsync(string userName, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(
            $"api/face/offline/status/{Uri.EscapeDataString(userName)}", ct);
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<FpGenerateStatus>(ct))!;
    }

    public async Task<FpUsersResponse> ListUsersAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("api/face/offline/users", ct);
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<FpUsersResponse>(ct))!;
    }

    /// <summary>
    /// 把 Windows 客户端的本地照片真实上传到 Linux 后端的 experiments/&lt;user_name&gt;/training_imgs 下，
    /// 返回后端实际可访问的绝对路径，供后续 StartGenerationAsync 的 imgs_dir 参数使用。
    /// </summary>
    public async Task<FpUploadPhotosResponse> UploadPhotosAsync(
        string userName,
        IEnumerable<string> filePaths,
        bool clearExisting = true,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        int count = 0;
        foreach (var p in filePaths)
        {
            if (!System.IO.File.Exists(p)) continue;
            var bytes = await System.IO.File.ReadAllBytesAsync(p, ct);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GuessMime(p));
            form.Add(content, "files", System.IO.Path.GetFileName(p));
            count++;
        }
        if (count == 0)
            throw new InvalidOperationException("没有可上传的本地照片文件。");

        var url = $"api/face/offline/upload_photos" +
                  $"?user_name={Uri.EscapeDataString(userName)}" +
                  $"&clear_existing={(clearExisting ? "true" : "false")}";
        using var resp = await _http.PostAsync(url, form, ct);
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<FpUploadPhotosResponse>(ct))!;
    }

    private static string GuessMime(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png"  => "image/png",
            ".bmp"  => "image/bmp",
            ".webp" => "image/webp",
            _       => "image/jpeg",
        };
    }

    // ─── 在线保护 ────────────────────────────────────────────────────────────

    public async Task<FpOnlineResponse> StartOnlineAsync(
        string userName,
        string device = "cuda:0",
        float eps = 16f,
        int uvRefreshInterval = 3,
        CancellationToken ct = default)
    {
        var body = new
        {
            user_name = userName,
            device,
            eps,
            uv_refresh_interval = uvRefreshInterval
        };
        using var resp = await _http.PostAsJsonAsync("api/face/online/start", body, ct);
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<FpOnlineResponse>(ct))!;
    }

    public async Task StopOnlineAsync(string userName, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(
            $"api/face/online/stop/{Uri.EscapeDataString(userName)}", null, ct);
        await EnsureSuccessAsync(resp);
    }

    public async Task<FpOnlineStatus> GetOnlineStatusAsync(string userName, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(
            $"api/face/online/status/{Uri.EscapeDataString(userName)}", ct);
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<FpOnlineStatus>(ct))!;
    }

    public async Task<FpProtectFrameResponse> ProtectFrameAsync(
        string userName,
        string frameBase64,
        CancellationToken ct = default)
    {
        var body = new { user_name = userName, frame_b64 = frameBase64 };
        using var resp = await _http.PostAsJsonAsync("api/face/online/protect_frame", body, ct);
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<FpProtectFrameResponse>(ct))!;
    }

    // ─── 内部工具 ─────────────────────────────────────────────────────────────

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp)
    {
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"FaceProtect API 错误 {(int)resp.StatusCode}: {body}");
        }
    }

    public void Dispose() => _http.Dispose();
}

// ─── DTO ──────────────────────────────────────────────────────────────────────

public sealed record FpHealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("cuda_available")] bool CudaAvailable,
    [property: JsonPropertyName("torch_ok")] bool TorchOk,
    [property: JsonPropertyName("active_sessions")] List<string> ActiveSessions);

public sealed record FpGenerateResponse(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("user_name")] string UserName);

public sealed record FpGenerateStatus(
    [property: JsonPropertyName("user_name")] string UserName,
    [property: JsonPropertyName("status")] string Status,       // idle / running / done / failed
    [property: JsonPropertyName("progress")] int Progress,
    [property: JsonPropertyName("message")] string Message);

public sealed record FpUsersResponse(
    [property: JsonPropertyName("users")] List<string> Users);

public sealed record FpUploadPhotosResponse(
    [property: JsonPropertyName("user_name")] string UserName,
    [property: JsonPropertyName("saved_count")] int SavedCount,
    [property: JsonPropertyName("imgs_dir")] string ImgsDir,
    [property: JsonPropertyName("files")] List<string> Files);

public sealed record FpOnlineResponse(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("user_name")] string UserName);

public sealed record FpOnlineStatus(
    [property: JsonPropertyName("user_name")] string UserName,
    [property: JsonPropertyName("status")] string Status,       // active / inactive
    [property: JsonPropertyName("frames_processed")] int FramesProcessed,
    [property: JsonPropertyName("last_latency_ms")] double LastLatencyMs);

public sealed record FpProtectFrameResponse(
    [property: JsonPropertyName("protected_frame_b64")] string ProtectedFrameBase64,
    [property: JsonPropertyName("latency_ms")] double LatencyMs);
