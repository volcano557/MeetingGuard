using MeetingAgent.Core;
using Microsoft.Extensions.Options;

namespace MeetingAgent.Transport;

public sealed class ConfigurableTransportClient : ITransportClient
{
    private readonly GrpcTransportClient _grpcTransportClient;
    private readonly HttpTransportClient _httpTransportClient;
    private readonly IOptionsMonitor<LinuxBackendOptions> _optionsMonitor;
    private ITransportClient? _activeClient;

    public ConfigurableTransportClient(
        GrpcTransportClient grpcTransportClient,
        HttpTransportClient httpTransportClient,
        IOptionsMonitor<LinuxBackendOptions> optionsMonitor)
    {
        _grpcTransportClient = grpcTransportClient;
        _httpTransportClient = httpTransportClient;
        _optionsMonitor = optionsMonitor;

        _grpcTransportClient.StateChanged += (sender, args) =>
        {
            if (ReferenceEquals(_activeClient, _grpcTransportClient))
            {
                StateChanged?.Invoke(sender, args);
            }
        };
        _httpTransportClient.StateChanged += (sender, args) =>
        {
            if (ReferenceEquals(_activeClient, _httpTransportClient))
            {
                StateChanged?.Invoke(sender, args);
            }
        };
        _grpcTransportClient.DetectionReceived += (sender, args) =>
        {
            if (ReferenceEquals(_activeClient, _grpcTransportClient))
            {
                DetectionReceived?.Invoke(sender, args);
            }
        };
        _httpTransportClient.DetectionReceived += (sender, args) =>
        {
            if (ReferenceEquals(_activeClient, _httpTransportClient))
            {
                DetectionReceived?.Invoke(sender, args);
            }
        };
        _grpcTransportClient.MetricsUpdated += (sender, args) =>
        {
            if (ReferenceEquals(_activeClient, _grpcTransportClient))
            {
                MetricsUpdated?.Invoke(sender, args);
            }
        };
        _httpTransportClient.MetricsUpdated += (sender, args) =>
        {
            if (ReferenceEquals(_activeClient, _httpTransportClient))
            {
                MetricsUpdated?.Invoke(sender, args);
            }
        };
    }

    public event EventHandler<TransportStateChangedEventArgs>? StateChanged;
    public event EventHandler<DetectionResult>? DetectionReceived;
    public event EventHandler<TransportMetrics>? MetricsUpdated;

    public bool IsConnected => _activeClient?.IsConnected == true;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var client = ResolveClient();
        _activeClient = client;
        await client.ConnectAsync(cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (_activeClient is null)
        {
            return;
        }

        await _activeClient.DisconnectAsync(cancellationToken);
        _activeClient = null;
    }

    public Task SendSessionStartAsync(SessionStartMessage message, CancellationToken cancellationToken)
        => RequireActiveClient().SendSessionStartAsync(message, cancellationToken);

    public Task SendSessionStopAsync(SessionStopMessage message, CancellationToken cancellationToken)
        => RequireActiveClient().SendSessionStopAsync(message, cancellationToken);

    public Task SendVideoFrameAsync(FramePacket framePacket, CancellationToken cancellationToken)
        => RequireActiveClient().SendVideoFrameAsync(framePacket, cancellationToken);

    public Task SendAudioChunkAsync(AudioPacket audioPacket, CancellationToken cancellationToken)
        => RequireActiveClient().SendAudioChunkAsync(audioPacket, cancellationToken);

    public Task SendHeartbeatAsync(CancellationToken cancellationToken)
        => RequireActiveClient().SendHeartbeatAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_activeClient is not null)
        {
            await _activeClient.DisposeAsync();
        }

        await _grpcTransportClient.DisposeAsync();
        await _httpTransportClient.DisposeAsync();
    }

    private ITransportClient ResolveClient()
    {
        var protocol = (_optionsMonitor.CurrentValue.Protocol ?? "grpc").Trim().ToLowerInvariant();
        return protocol switch
        {
            "http" or "https" => _httpTransportClient,
            _ => _grpcTransportClient
        };
    }

    private ITransportClient RequireActiveClient()
        => _activeClient ?? throw new InvalidOperationException("Transport is not connected.");
}
