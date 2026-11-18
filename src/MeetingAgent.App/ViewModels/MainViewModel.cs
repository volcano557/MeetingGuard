using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetingAgent.App.Data;
using MeetingAgent.Core;
using MeetingAgent.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.ObjectModel;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MeetingAgent.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IAgentControlService _agentControlService;
    private readonly IAgentStatusStore _statusStore;
    private readonly IVideoCaptureService _videoCaptureService;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly IAlertService _alertService;
    private readonly InMemoryLogSink _logSink;
    private readonly IOptionsMonitor<LinuxBackendOptions> _backendOptions;
    private readonly IOptions<SemanticRiskOptions> _semanticOptions;
    private readonly IOptions<FaceProtectOptions> _faceProtectOptions;
    private readonly IOptions<CaptureOptions> _captureOptions;
    private readonly IOptions<AudioOptions> _audioOptions;
    private readonly IOptions<AlertingOptions> _alertingOptions;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Semantic.SemanticHttpClient _semanticHttp;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private long _lastProcessedDetectionCount;

    /// <summary>人脸隐私保护子 ViewModel（绑定至第二个标签页）。</summary>
    public FaceProtectViewModel FaceProtect { get; private set; } = null!;

    [ObservableProperty] private string aigcGatewayUrl = "http://127.0.0.1:50053";
    [ObservableProperty] private string aigcConnectionHint = "地址可在 appsettings.json 中配置，修改后重启生效";
    // 保留供内部逻辑使用（由 AigcGatewayUrl 解析同步）
    [ObservableProperty] private string host = "127.0.0.1";
    [ObservableProperty] private string port = "50053";
    [ObservableProperty] private string protocol = "http";
    [ObservableProperty] private string authToken = string.Empty;

    [ObservableProperty] private WindowItemViewModel? selectedWindow;
    [ObservableProperty] private string currentWindowTitle = "当前窗口：N/A";
    [ObservableProperty] private string currentResolution = "分辨率：N/A";
    [ObservableProperty] private string currentFpsText = "采集 FPS：0";

    [ObservableProperty] private string connectionStatusText = "连接状态：Disconnected";
    [ObservableProperty] private string captureStatusText = "采集状态：Idle";
    [ObservableProperty] private string lastFrameSentText = "最近帧发送：N/A";
    [ObservableProperty] private string lastDetectionText = "最近检测结果：N/A";
    [ObservableProperty] private string riskLevelText = "当前告警级别：Info";
    [ObservableProperty] private string throughputText = "帧吞吐：0 Mbps";
    [ObservableProperty] private string audioStatusText = "音频状态：Idle";
    [ObservableProperty] private string dropAndRttText = "丢帧：0 / RTT：0 ms";
    [ObservableProperty] private string currentHighRiskText = "当前高风险：无";
    [ObservableProperty] private string logPathText = "日志目录：logs";
    [ObservableProperty] private string detectionCountText = "检测计数：0";
    [ObservableProperty] private string lastScoreText = "最近分数：0.000";
    [ObservableProperty] private string lastRiskTypeText = "最近风险类型：N/A";
    [ObservableProperty] private string lastRiskMessageText = "最近风险说明：N/A";
    [ObservableProperty] private string riskBadgeText = "INFO";
    [ObservableProperty] private double riskProgress = 0;
    [ObservableProperty] private Brush riskBadgeBrush = Brushes.SeaGreen;
    [ObservableProperty] private string backendTargetText = "后端目标：N/A";
    [ObservableProperty] private string detectorEngineText = "检测引擎：FakeSTormer Realtime";
    [ObservableProperty] private string dashboardStatusText = "待机";
    [ObservableProperty] private Brush dashboardStatusBrush = Brushes.SlateGray;
    [ObservableProperty] private double videoAuthenticityPercent = 100;
    [ObservableProperty] private double fakeProbabilityPercent = 0;
    [ObservableProperty] private string authenticityText = "100%";
    [ObservableProperty] private string fakeProbabilityText = "0%";
    [ObservableProperty] private string detectorRuntimeText = "模型状态：等待检测";
    [ObservableProperty] private ImageSource? liveFrameImage;
    [ObservableProperty] private string liveFrameMetaText = "预览帧：等待采集开始";
    [ObservableProperty] private string frameFeedStatusText = "帧流状态：Idle";

    public MainViewModel(
        IAgentControlService agentControlService,
        IAgentStatusStore statusStore,
        IVideoCaptureService videoCaptureService,
        IAudioCaptureService audioCaptureService,
        IAlertService alertService,
        InMemoryLogSink logSink,
        IOptionsMonitor<LinuxBackendOptions> backendOptions,
        IOptions<SemanticRiskOptions> semanticOptions,
        IOptions<FaceProtectOptions> faceProtectOptions,
        IOptions<CaptureOptions> captureOptions,
        IOptions<AudioOptions> audioOptions,
        IOptions<AlertingOptions> alertingOptions,
        ILogger<MainViewModel> logger,
        FaceProtectViewModel faceProtect,
        DatabaseService databaseService,
        Semantic.SemanticHttpClient semanticHttp)
    {
        _agentControlService = agentControlService;
        _statusStore = statusStore;
        _videoCaptureService = videoCaptureService;
        _audioCaptureService = audioCaptureService;
        _alertService = alertService;
        _logSink = logSink;
        _backendOptions = backendOptions;
        _semanticOptions = semanticOptions;
        _faceProtectOptions = faceProtectOptions;
        _captureOptions = captureOptions;
        _audioOptions = audioOptions;
        _alertingOptions = alertingOptions;
        _logger = logger;
        _semanticHttp = semanticHttp;
        FaceProtect = faceProtect;
        InitDb(databaseService);
        InitSemanticAudioHook();
        InitAudioLevelHook();

        Windows = new ObservableCollection<WindowItemViewModel>();
        Alerts = new ObservableCollection<AlertEvent>();
        Logs = new ObservableCollection<string>();
        RecentDetections = new ObservableCollection<DetectionTileViewModel>();
        RecentFrames = new ObservableCollection<FrameThumbnailViewModel>();
    }

    public ObservableCollection<WindowItemViewModel> Windows { get; }
    public ObservableCollection<AlertEvent> Alerts { get; }
    public ObservableCollection<string> Logs { get; }
    public ObservableCollection<DetectionTileViewModel> RecentDetections { get; }
    public ObservableCollection<FrameThumbnailViewModel> RecentFrames { get; }

    public async Task OnLoadedAsync()
    {
        var options = _backendOptions.CurrentValue;
        AigcGatewayUrl = BackendUrlHelper.BuildFromOptions(options);
        Host = options.Host;
        Port = options.Port.ToString();
        Protocol = options.Protocol;
        AuthToken = options.AuthToken;
        LogPathText = $"日志目录：{Path.Combine(AppContext.BaseDirectory, "logs")}";
        CurrentFpsText = $"采集 FPS：{_captureOptions.Value.TargetFps}";
        BackendTargetText = $"后端目标：{AigcGatewayUrl}";
        AigcConnectionHint = "本地直连 http://127.0.0.1:50053 · WSL 重启后运行 tools\\setup_localhost_ports.ps1";

        // 同步其它模块默认地址（与 appsettings 一致，无需每次手改）
        var sem = _semanticOptions.Value;
        SemanticGatewayUrl = !string.IsNullOrWhiteSpace(sem.Url)
            ? sem.Url.Trim()
            : BackendUrlHelper.Build(sem.Host, sem.Port);
        _semanticHttp.BaseUrl = SemanticGatewayUrl;

        var fp = _faceProtectOptions.Value;
        FaceProtect.FaceProtectGatewayUrl = !string.IsNullOrWhiteSpace(fp.Url)
            ? fp.Url.Trim()
            : BackendUrlHelper.Build(fp.Host, fp.Port);

        _statusStore.StatusChanged += OnStatusChanged;
        _videoCaptureService.FrameReady += OnFrameReady;
        _alertService.AlertRaised += OnAlertRaised;
        _logSink.LogAdded += OnLogAdded;

        foreach (var line in _logSink.Entries.TakeLast(100))
        {
            Logs.Add(line);
        }

        await RefreshWindowsAsync();
    }

    public async Task OnClosingAsync()
    {
        _statusStore.StatusChanged -= OnStatusChanged;
        _videoCaptureService.FrameReady -= OnFrameReady;
        _alertService.AlertRaised -= OnAlertRaised;
        _logSink.LogAdded -= OnLogAdded;
        FaceProtect.Dispose();
        await _agentControlService.StopCaptureAsync(CancellationToken.None);
        await _agentControlService.DisconnectAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task RefreshWindowsAsync()
    {
        var windows = await _agentControlService.GetWindowsAsync(CancellationToken.None);
        await _dispatcher.InvokeAsync(() =>
        {
            Windows.Clear();
            foreach (var window in windows)
            {
                Windows.Add(new WindowItemViewModel
                {
                    Descriptor = window,
                    DisplayName = $"{window.ProcessName} | {window.Title}"
                });
            }
        });
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (!TryResolveAigcBackend(out var parsed, out var error))
        {
            MessageBox.Show(error, "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await _agentControlService.ConnectAsync(
                parsed.Host, parsed.Port, parsed.Scheme, AuthToken, parsed.UseGateway, CancellationToken.None);
            Host = parsed.Host;
            Port = parsed.Port.ToString();
            Protocol = parsed.Scheme;
            BackendTargetText = $"后端目标：{parsed.NormalizedUrl}";
            ConnectionStatusText = "连接状态：Connected · 健康检查通过";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connect failed");
            MessageBox.Show(
                BackendUrlHelper.ExplainConnectionFailure(parsed.Host, ex),
                "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _agentControlService.DisconnectAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (!TryResolveAigcBackend(out var parsed, out var error))
        {
            MessageBox.Show(error, "连接测试", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await _agentControlService.TestConnectionAsync(
                parsed.Host, parsed.Port, parsed.Scheme, AuthToken, parsed.UseGateway, CancellationToken.None);
            MessageBox.Show($"连接测试成功。\n{parsed.NormalizedUrl}", "连接测试",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                BackendUrlHelper.ExplainConnectionFailure(parsed.Host, ex),
                "连接测试", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>从 AigcGatewayUrl 解析连接参数；UI 输入会覆盖 appsettings 默认值。</summary>
    private bool TryResolveAigcBackend(out BackendUrlHelper.ParsedBackendUrl parsed, out string error)
    {
        parsed = default;
        error = string.Empty;

        if (!BackendUrlHelper.TryParse(AigcGatewayUrl, out parsed))
        {
            error = "AIGC 后端地址格式不正确。\n示例：http://127.0.0.1:50053";
            return false;
        }

        if (parsed.Scheme is not ("http" or "https"))
        {
            error = "AIGC 检测仅支持 http / https 协议。";
            return false;
        }

        return true;
    }

    [RelayCommand]
    private async Task StartCaptureAsync()
    {
        try
        {
            _agentControlService.SetSelectedWindow(SelectedWindow?.Descriptor);
            await _agentControlService.StartCaptureAsync(CancellationToken.None);
            CurrentWindowTitle = $"当前窗口：{SelectedWindow?.Descriptor.Title ?? "N/A"}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StartCapture failed");
            MessageBox.Show(ex.ToString(), "启动采集失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task StopCaptureAsync()
    {
        await _agentControlService.StopCaptureAsync(CancellationToken.None);
        LiveFrameImage = null;
        LiveFrameMetaText = "预览帧：缓存已清空";
        RecentFrames.Clear();
        RecentDetections.Clear();
        _lastProcessedDetectionCount = 0;
        FrameFeedStatusText = "帧流状态：Stopped";
        DetectorRuntimeText = "模型状态：检测已停止（后端连接保持）";
    }

    partial void OnSelectedWindowChanged(WindowItemViewModel? value)
    {
        _agentControlService.SetSelectedWindow(value?.Descriptor);
    }

    private void OnStatusChanged(object? sender, AgentStatusSnapshot status)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            ConnectionStatusText = $"连接状态：{status.ConnectionState}";
            CaptureStatusText = $"采集状态：{status.CaptureState}";
            CurrentWindowTitle = $"当前窗口：{status.CurrentWindowTitle}";
            CurrentResolution = $"分辨率：{status.CurrentResolution}";
            CurrentFpsText = $"采集 FPS：{status.CurrentFps}";
            LastFrameSentText = $"最近帧发送：{FormatDateTime(status.LastFrameSentAt)}";
            LastDetectionText = $"最近检测结果：{FormatDateTime(status.LastDetectionAt)}";
            RiskLevelText = $"当前告警级别：{status.CurrentRiskLevel}";
            ThroughputText = $"帧吞吐：{status.FrameThroughputMbps:F3} Mbps";
            AudioStatusText = $"音频状态：{(status.AudioActive ? "Running" : "Idle")}";
            DropAndRttText = $"丢帧：{status.DroppedFrames} / RTT：{status.LastRttMs:F1} ms";
            DetectionCountText = $"检测计数：{status.TotalDetections}";
            LastScoreText = $"最近分数：{status.LastDetectionScore:F3}";
            LastRiskTypeText = $"最近风险类型：{status.LastRiskType}";
            LastRiskMessageText = $"最近风险说明：{status.LastRiskMessage}";
            BackendTargetText = $"后端目标：{Protocol}://{Host}:{Port}";
            ApplyRiskVisual(status.CurrentRiskLevel, status.LastDetectionScore);
            ApplyDashboardVisual(status);
            int newCount = TryPushDetectionTile(status);
            PushScorePoint(status.LastDetectionScore);
            if (newCount > 0) IncrementSessionDetection(newCount);

            Alerts.Clear();
            foreach (var alert in status.RecentAlerts)
            {
                Alerts.Add(alert);
            }
        });
    }

    private void ApplyRiskVisual(RiskLevel level, float score)
    {
        RiskBadgeText = level.ToString().ToUpperInvariant();
        RiskProgress = Math.Max(0, Math.Min(100, score * 100));
        RiskBadgeBrush = level switch
        {
            RiskLevel.Critical => Brushes.DarkRed,
            RiskLevel.High => Brushes.OrangeRed,
            RiskLevel.Medium => Brushes.DarkOrange,
            RiskLevel.Low => Brushes.Goldenrod,
            _ => Brushes.SeaGreen
        };
    }

    private void ApplyDashboardVisual(AgentStatusSnapshot status)
    {
        var fakePercent = Math.Max(0, Math.Min(100, status.LastDetectionScore * 100));
        var authenticityPercent = 100 - fakePercent;

        VideoAuthenticityPercent = authenticityPercent;
        FakeProbabilityPercent = fakePercent;
        AuthenticityText = $"{authenticityPercent:F1}%";
        FakeProbabilityText = $"{fakePercent:F1}%";
        DetectorRuntimeText = status.LastRiskType switch
        {
            "fakestormer_model_inference" => "模型状态：FakeSTormer 实时推理中",
            "fakestormer_warmup" => "模型状态：预热中（需要积累时序帧）",
            "fakestormer_inference_error" => $"模型状态：推理异常（{status.LastRiskMessage}）",
            _ => $"模型状态：{status.LastRiskType}"
        };

        DashboardStatusText = status switch
        {
            { CaptureState: CaptureLifecycleState.Running } => "检测中",
            { ConnectionState: AgentConnectionState.Connected } => "已连接",
            _ => "待机"
        };
        DashboardStatusBrush = status.CurrentRiskLevel switch
        {
            RiskLevel.Critical => Brushes.DarkRed,
            RiskLevel.High => Brushes.OrangeRed,
            RiskLevel.Medium => Brushes.DarkOrange,
            RiskLevel.Low => Brushes.Goldenrod,
            _ => (status.CaptureState == CaptureLifecycleState.Running ? Brushes.DodgerBlue : Brushes.SlateGray)
        };
        FrameFeedStatusText = status.CaptureState == CaptureLifecycleState.Running
            ? "帧流状态：Running"
            : "帧流状态：Idle";
    }

    /// <summary>返回本次新增的检测条目数（&gt; 0 时表示有新检测）。</summary>
    private int TryPushDetectionTile(AgentStatusSnapshot status)
    {
        if (status.TotalDetections <= 0 || status.TotalDetections == _lastProcessedDetectionCount)
        {
            return 0;
        }

        var delta = (int)Math.Max(0, status.TotalDetections - _lastProcessedDetectionCount);
        _lastProcessedDetectionCount = status.TotalDetections;
        RecentDetections.Insert(0, new DetectionTileViewModel(
            IndexText: $"#{status.TotalDetections}",
            RiskText: status.CurrentRiskLevel.ToString().ToUpperInvariant(),
            ScoreText: $"{status.LastDetectionScore * 100:F0}%",
            TimeText: status.LastDetectionAt?.LocalDateTime.ToString("HH:mm:ss") ?? "--:--:--",
            RiskBrush: status.CurrentRiskLevel switch
            {
                RiskLevel.Critical => Brushes.DarkRed,
                RiskLevel.High => Brushes.OrangeRed,
                RiskLevel.Medium => Brushes.DarkOrange,
                RiskLevel.Low => Brushes.Goldenrod,
                _ => Brushes.SeaGreen
            }));

        while (RecentDetections.Count > 12)
        {
            RecentDetections.RemoveAt(RecentDetections.Count - 1);
        }
        return delta;
    }

    private void OnAlertRaised(object? sender, AlertEvent alert)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            CurrentHighRiskText = $"当前高风险：{alert.RiskLevel} / {alert.RiskType} / {alert.Message}";
            if (_alertingOptions.Value.EnableToast && alert.RiskLevel >= RiskLevel.High)
            {
                MessageBox.Show(
                    $"{alert.RiskLevel} - {alert.RiskType}\n{alert.Message}",
                    "会议风险告警",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            if (_alertingOptions.Value.EnableSound)
            {
                SystemSounds.Exclamation.Play();
            }
        });
    }

    private void OnLogAdded(object? sender, string line)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            Logs.Insert(0, line);
            while (Logs.Count > 300)
            {
                Logs.RemoveAt(Logs.Count - 1);
            }
        });
    }

    private static string FormatDateTime(DateTimeOffset? value)
        => value.HasValue ? value.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff") : "N/A";

    private void OnFrameReady(object? sender, FramePacket packet)
    {
        if (packet.FrameId % 3 != 0)
        {
            return;
        }

        BitmapSource? preview;
        try
        {
            preview = CreateFramePreview(packet);
        }
        catch
        {
            return;
        }

        if (preview is null)
        {
            return;
        }

        _ = _dispatcher.InvokeAsync(() =>
        {
            LiveFrameImage = preview;
            LiveFrameMetaText = $"预览帧：#{packet.FrameId}  {packet.Width}x{packet.Height}  {DateTime.Now:HH:mm:ss.fff}";

            if (packet.FrameId % 9 != 0)
            {
                return;
            }

            RecentFrames.Insert(0, new FrameThumbnailViewModel(
                Image: preview,
                FrameText: $"#{packet.FrameId}",
                TimeText: DateTime.Now.ToString("HH:mm:ss")));

            while (RecentFrames.Count > 12)
            {
                RecentFrames.RemoveAt(RecentFrames.Count - 1);
            }
        });
    }

    private static BitmapSource? CreateFramePreview(FramePacket packet)
    {
        if (packet.Width <= 0 || packet.Height <= 0 || packet.Data.Length == 0)
        {
            return null;
        }

        var stride = packet.Stride > 0 ? packet.Stride : packet.Width * 4;
        var minLength = stride * packet.Height;
        if (packet.Data.Length < minLength)
        {
            return null;
        }

        // 当前采集链路默认 BGRA8，直接转成 WPF 可渲染位图。
        var bitmap = BitmapSource.Create(
            pixelWidth: packet.Width,
            pixelHeight: packet.Height,
            dpiX: 96,
            dpiY: 96,
            pixelFormat: PixelFormats.Bgra32,
            palette: null,
            pixels: packet.Data,
            stride: stride);
        bitmap.Freeze();
        return bitmap;
    }
}

public sealed record DetectionTileViewModel(
    string IndexText,
    string RiskText,
    string ScoreText,
    string TimeText,
    Brush RiskBrush);

public sealed record FrameThumbnailViewModel(
    ImageSource Image,
    string FrameText,
    string TimeText);
