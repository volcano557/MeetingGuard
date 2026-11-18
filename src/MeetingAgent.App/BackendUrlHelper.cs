using MeetingAgent.Core;

namespace MeetingAgent.App;

/// <summary>
/// 解析 / 构建后端服务 URL，并在「直连 FakeSTormer」与「经 Gateway :8000」两种模式间切换 API 路径。
/// </summary>
internal static class BackendUrlHelper
{
    public static string Build(string host, int port, string scheme = "http")
        => $"{scheme.Trim().ToLowerInvariant()}://{host.Trim()}:{port}";

    public static string BuildFromOptions(LinuxBackendOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.GatewayUrl))
            return options.GatewayUrl.Trim().TrimEnd('/');

        var scheme = options.Http.Scheme;
        if (string.IsNullOrWhiteSpace(scheme))
            scheme = options.Protocol is "https" ? "https" : "http";
        return Build(options.Host, options.Port, scheme);
    }

    public static bool TryParse(string? raw, out ParsedBackendUrl parsed)
    {
        parsed = default!;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var text = raw.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return false;

        if (uri.HostNameType == UriHostNameType.Unknown)
            return false;

        var port = uri.IsDefaultPort
            ? uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80
            : uri.Port;

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https"))
            return false;

        // 经统一网关 :8000，或 URL 路径含 /api/aigc
        var useGateway = port == 8000
            || uri.AbsolutePath.StartsWith("/api/aigc", StringComparison.OrdinalIgnoreCase);

        parsed = new ParsedBackendUrl(uri.Host, port, scheme, useGateway, text.TrimEnd('/'));
        return true;
    }

    /// <summary>将 LinuxBackendOptions 的 HTTP 路径设为直连 FakeSTormer (:50053) 或 Gateway 代理模式。</summary>
    public static void ApplyHttpPaths(LinuxBackendOptions options, bool useGateway)
    {
        options.UseGateway = useGateway;
        options.Http.Scheme = options.Protocol is "https" ? "https" : "http";

        if (useGateway)
        {
            options.Http.HealthPingPath     = "/api/health";
            options.Http.SessionStartPath   = "/api/aigc/session/start";
            options.Http.SessionStopPath    = "/api/aigc/session/stop";
            options.Http.VideoFramePath     = "/api/aigc/video/frame";
            options.Http.AudioChunkPath     = "/api/aigc/audio/chunk";
            options.Http.DetectionPollPath  = "/api/aigc/results/poll";
        }
        else
        {
            options.Http.HealthPingPath     = "/health";
            options.Http.SessionStartPath   = "/api/agent/session/start";
            options.Http.SessionStopPath    = "/api/agent/session/stop";
            options.Http.VideoFramePath     = "/api/agent/video/frame";
            options.Http.AudioChunkPath     = "/api/agent/audio/chunk";
            options.Http.DetectionPollPath  = "/api/agent/results/poll";
        }
    }

    /// <summary>给出更友好的连接失败提示。</summary>
    public static string ExplainConnectionFailure(string host, Exception ex)
    {
        var isLocal = host is "127.0.0.1" or "localhost" or "::1";

        if (!isLocal)
        {
            return "连接失败：请使用本地地址 http://127.0.0.1:<端口>（与 FaceProtect 相同）。\n"
                 + "appsettings.json 中所有 Url 应为 127.0.0.1，\n"
                 + "并以管理员运行 tools\\setup_localhost_ports.ps1。\n\n"
                 + $"原始错误：{ex.Message}";
        }

        if (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("refused", StringComparison.OrdinalIgnoreCase))
        {
            return "连接失败：127.0.0.1 端口无响应。\n"
                 + "请确认：\n"
                 + "  1) WSL 中后端已启动（FakeSTormer :50053 / FaceProtect :5001 / audio :8003）\n"
                 + "  2) 以管理员运行：tools\\setup_localhost_ports.ps1\n"
                 + "     （WSL 每次重启后需重新运行一次）\n"
                 + "  3) WSL 内自测：curl http://127.0.0.1:50053/health\n\n"
                 + $"原始错误：{ex.Message}";
        }

        return $"连接失败：{ex.Message}";
    }

    internal readonly record struct ParsedBackendUrl(
        string Host,
        int Port,
        string Scheme,
        bool UseGateway,
        string NormalizedUrl);
}
