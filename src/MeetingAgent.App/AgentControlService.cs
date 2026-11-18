using MeetingAgent.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingAgent.App;

public sealed class AgentControlService : IAgentControlService
{
    private readonly IWindowDiscoveryService _windowDiscoveryService;
    private readonly IVideoCaptureService _videoCaptureService;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly IDetectionBackendAdapter _detectionBackendAdapter;
    private readonly ITransportClient _transportClient;
    private readonly IPipelineBufferControl _pipelineBufferControl;
    private readonly IOptionsMonitor<LinuxBackendOptions> _backendOptions;
    private readonly IOptions<AudioOptions> _audioOptions;
    private readonly IOptions<CaptureOptions> _captureOptions;
    private readonly IAgentStatusStore _statusStore;
    private readonly ILogger<AgentControlService> _logger;

    private string? _currentSessionId;
    private bool _sessionStarted;

    public AgentControlService(
        IWindowDiscoveryService windowDiscoveryService,
        IVideoCaptureService videoCaptureService,
        IAudioCaptureService audioCaptureService,
        IDetectionBackendAdapter detectionBackendAdapter,
        ITransportClient transportClient,
        IPipelineBufferControl pipelineBufferControl,
        IOptionsMonitor<LinuxBackendOptions> backendOptions,
        IOptions<AudioOptions> audioOptions,
        IOptions<CaptureOptions> captureOptions,
        IAgentStatusStore statusStore,
        ILogger<AgentControlService> logger)
    {
        _windowDiscoveryService = windowDiscoveryService;
        _videoCaptureService = videoCaptureService;
        _audioCaptureService = audioCaptureService;
        _detectionBackendAdapter = detectionBackendAdapter;
        _transportClient = transportClient;
        _pipelineBufferControl = pipelineBufferControl;
        _backendOptions = backendOptions;
        _audioOptions = audioOptions;
        _captureOptions = captureOptions;
        _statusStore = statusStore;
        _logger = logger;
    }

    public bool IsConnected => _transportClient.IsConnected;
    public WindowDescriptor? SelectedWindow { get; private set; }

    public Task<IReadOnlyList<WindowDescriptor>> GetWindowsAsync(CancellationToken cancellationToken)
        => _windowDiscoveryService.GetAvailableWindowsAsync(cancellationToken);

    public void SetSelectedWindow(WindowDescriptor? window)
    {
        SelectedWindow = window;
    }

    public async Task ConnectAsync(string host, int port, string protocol, string authToken, bool useGateway, CancellationToken cancellationToken)
    {
        var normalizedProtocol = string.IsNullOrWhiteSpace(protocol)
            ? "http"
            : protocol.Trim().ToLowerInvariant();
        if (normalizedProtocol is not ("grpc" or "http" or "https"))
        {
            throw new InvalidOperationException("仅支持 grpc / http / https 协议。");
        }

        if (_transportClient.IsConnected)
        {
            return;
        }

        OverrideBackendOptions(host, port, normalizedProtocol, authToken, useGateway);
        await _transportClient.ConnectAsync(cancellationToken);
        _statusStore.Update(s => s with
        {
            ConnectionState = AgentConnectionState.Connected
        });
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await StopCaptureAsync(cancellationToken);
        await StopRemoteSessionAsync(cancellationToken);
        await _transportClient.DisconnectAsync(cancellationToken);
        _statusStore.Update(s => s with
        {
            ConnectionState = AgentConnectionState.Disconnected
        });
    }

    public async Task TestConnectionAsync(string host, int port, string protocol, string authToken, bool useGateway, CancellationToken cancellationToken)
    {
        var wasConnected = _transportClient.IsConnected;
        OverrideBackendOptions(host, port, protocol, authToken, useGateway);

        if (!wasConnected)
        {
            await _transportClient.ConnectAsync(cancellationToken);
        }

        await _transportClient.SendHeartbeatAsync(cancellationToken);
        _logger.LogInformation("Connection test success for {Host}:{Port}", host, port);

        if (!wasConnected)
        {
            await _transportClient.DisconnectAsync(cancellationToken);
        }
    }

    public async Task StartCaptureAsync(CancellationToken cancellationToken)
    {
        if (SelectedWindow is null)
        {
            throw new InvalidOperationException("请先选择会议窗口。");
        }

        if (!_transportClient.IsConnected)
        {
            throw new InvalidOperationException("请先连接 Linux 检测服务。");
        }

        if (_videoCaptureService.IsRunning)
        {
            return;
        }

        if (!_sessionStarted)
        {
            _currentSessionId = Guid.NewGuid().ToString("N");
            await _detectionBackendAdapter.StartSessionAsync(_currentSessionId, cancellationToken);
            _sessionStarted = true;
        }
        _statusStore.Update(s => s with
        {
            CurrentWindowTitle = SelectedWindow.Title,
            CurrentFps = _captureOptions.Value.TargetFps,
            CaptureState = CaptureLifecycleState.Running
        });

        await _videoCaptureService.StartAsync(SelectedWindow, cancellationToken);
        if (_audioOptions.Value.Enabled)
        {
            await _audioCaptureService.StartAsync(cancellationToken);
        }
    }

    public async Task StopCaptureAsync(CancellationToken cancellationToken)
    {
        if (_videoCaptureService.IsRunning)
        {
            await _videoCaptureService.StopAsync(cancellationToken);
        }

        if (_audioCaptureService.IsRunning)
        {
            await _audioCaptureService.StopAsync(cancellationToken);
        }

        _pipelineBufferControl.ClearBuffers();

        _statusStore.Update(s => s with
        {
            CaptureState = CaptureLifecycleState.Stopped,
            AudioActive = false
        });
    }

    private async Task StopRemoteSessionAsync(CancellationToken cancellationToken)
    {
        if (!_sessionStarted || string.IsNullOrWhiteSpace(_currentSessionId))
        {
            return;
        }

        await _detectionBackendAdapter.StopSessionAsync(_currentSessionId, cancellationToken);
        _sessionStarted = false;
        _currentSessionId = null;
    }

    private void OverrideBackendOptions(string host, int port, string protocol, string authToken, bool useGateway)
    {
        var normalizedProtocol = (protocol ?? "grpc").Trim().ToLowerInvariant();
        var options = _backendOptions.CurrentValue;
        options.Host = host;
        options.Port = port;
        options.Protocol = normalizedProtocol;
        options.AuthToken = authToken;
        if (normalizedProtocol is "http" or "https")
        {
            options.Http.Scheme = normalizedProtocol;
            BackendUrlHelper.ApplyHttpPaths(options, useGateway);
        }
    }
}
