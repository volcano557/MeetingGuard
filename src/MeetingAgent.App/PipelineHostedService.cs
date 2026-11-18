using MeetingAgent.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Threading.Channels;

namespace MeetingAgent.App;

public sealed class PipelineHostedService : IHostedService, IDisposable, IPipelineBufferControl
{
    private readonly IVideoCaptureService _videoCaptureService;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly ITransportClient _transportClient;
    private readonly IDetectionBackendAdapter _backendAdapter;
    private readonly IAlertService _alertService;
    private readonly IAgentStatusStore _statusStore;
    private readonly ILogger<PipelineHostedService> _logger;

    private readonly Channel<FramePacket> _frameChannel = Channel.CreateBounded<FramePacket>(new BoundedChannelOptions(120)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly Channel<AudioPacket> _audioChannel = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(240)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    private CancellationTokenSource? _cts;
    private Task? _videoWorker;
    private Task? _audioWorker;

    public PipelineHostedService(
        IVideoCaptureService videoCaptureService,
        IAudioCaptureService audioCaptureService,
        ITransportClient transportClient,
        IDetectionBackendAdapter backendAdapter,
        IAlertService alertService,
        IAgentStatusStore statusStore,
        ILogger<PipelineHostedService> logger)
    {
        _videoCaptureService = videoCaptureService;
        _audioCaptureService = audioCaptureService;
        _transportClient = transportClient;
        _backendAdapter = backendAdapter;
        _alertService = alertService;
        _statusStore = statusStore;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _videoCaptureService.FrameReady += OnFrameReady;
        _videoCaptureService.StateChanged += OnCaptureStateChanged;
        _audioCaptureService.AudioReady += OnAudioReady;
        _audioCaptureService.CaptureError += OnAudioCaptureError;
        _transportClient.StateChanged += OnTransportStateChanged;
        _transportClient.DetectionReceived += OnDetectionReceived;
        _transportClient.MetricsUpdated += OnMetricsUpdated;
        _alertService.AlertRaised += OnAlertRaised;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _videoWorker = Task.Run(() => VideoSendLoopAsync(_cts.Token), _cts.Token);
        _audioWorker = Task.Run(() => AudioSendLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
        }

        if (_videoWorker is not null)
        {
            await Task.WhenAny(_videoWorker, Task.Delay(1000, cancellationToken));
        }

        if (_audioWorker is not null)
        {
            await Task.WhenAny(_audioWorker, Task.Delay(1000, cancellationToken));
        }

        _videoCaptureService.FrameReady -= OnFrameReady;
        _videoCaptureService.StateChanged -= OnCaptureStateChanged;
        _audioCaptureService.AudioReady -= OnAudioReady;
        _audioCaptureService.CaptureError -= OnAudioCaptureError;
        _transportClient.StateChanged -= OnTransportStateChanged;
        _transportClient.DetectionReceived -= OnDetectionReceived;
        _transportClient.MetricsUpdated -= OnMetricsUpdated;
        _alertService.AlertRaised -= OnAlertRaised;
    }

    private void OnFrameReady(object? sender, FramePacket e)
    {
        if (!_frameChannel.Writer.TryWrite(e))
        {
            _statusStore.Update(s => s with
            {
                DroppedFrames = s.DroppedFrames + 1
            });
        }
    }

    private void OnAudioReady(object? sender, AudioPacket e)
    {
        _ = _audioChannel.Writer.TryWrite(e);
        _statusStore.Update(s => s with { AudioActive = true });
    }

    private void OnAudioCaptureError(object? sender, string e)
    {
        _logger.LogWarning("Audio capture error: {Message}", e);
        _statusStore.Update(s => s with { AudioActive = false });
    }

    private void OnCaptureStateChanged(object? sender, CaptureStateChangedEventArgs e)
    {
        _statusStore.Update(s => s with { CaptureState = e.State });
    }

    private void OnTransportStateChanged(object? sender, TransportStateChangedEventArgs e)
    {
        _statusStore.Update(s => s with { ConnectionState = e.State });
    }

    private void OnDetectionReceived(object? sender, DetectionResult e)
    {
        // FakeSTormer realtime service returns audio passthrough events with score=0.
        // Ignore audio events here so UI authenticity reflects visual model inference only.
        if (!string.Equals(e.Source, "visual", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _alertService.HandleDetection(e);
        _statusStore.Update(s => s with
        {
            LastDetectionAt = DateTimeOffset.FromUnixTimeMilliseconds(e.TimestampUtcMs),
            CurrentRiskLevel = e.RiskLevel,
            TotalDetections = s.TotalDetections + 1,
            LastDetectionScore = e.Score,
            LastRiskType = e.RiskType,
            LastRiskMessage = e.Message
        });
    }

    private void OnMetricsUpdated(object? sender, TransportMetrics e)
    {
        var lastFrameAt = e.LastFrameSentUtcMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(e.LastFrameSentUtcMs)
            : (DateTimeOffset?)null;
        _statusStore.Update(s => s with
        {
            LastFrameSentAt = lastFrameAt ?? s.LastFrameSentAt,
            LastDetectionAt = e.LastDetectionUtcMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(e.LastDetectionUtcMs) : s.LastDetectionAt,
            FrameThroughputMbps = e.FrameThroughputMbps,
            DroppedFrames = e.DroppedFrames,
            LastRttMs = e.LastRttMs > 0 ? e.LastRttMs : s.LastRttMs
        });
    }

    private void OnAlertRaised(object? sender, AlertEvent e)
    {
        _statusStore.Update(s =>
        {
            var list = s.RecentAlerts.ToList();
            list.Insert(0, e);
            if (list.Count > 50)
            {
                list = list.Take(50).ToList();
            }

            return s with
            {
                CurrentRiskLevel = e.RiskLevel,
                RecentAlerts = new ReadOnlyCollection<AlertEvent>(list)
            };
        });
    }

    private async Task VideoSendLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var frame in _frameChannel.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await _backendAdapter.PushFrameAsync(frame, cancellationToken);
                _statusStore.Update(s => s with
                {
                    CurrentResolution = $"{frame.Width}x{frame.Height}",
                    LastFrameSentAt = DateTimeOffset.FromUnixTimeMilliseconds(frame.TimestampUtcMs)
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PushFrameAsync failed.");
            }
        }
    }

    private async Task AudioSendLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var audio in _audioChannel.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await _backendAdapter.PushAudioAsync(audio, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PushAudioAsync failed.");
            }
        }
    }

    public void ClearBuffers()
    {
        var clearedFrames = 0;
        var clearedAudio = 0;
        while (_frameChannel.Reader.TryRead(out _))
        {
            clearedFrames++;
        }

        while (_audioChannel.Reader.TryRead(out _))
        {
            clearedAudio++;
        }

        if (clearedFrames > 0 || clearedAudio > 0)
        {
            _logger.LogInformation("Cleared pipeline buffers. Frames={Frames}, Audio={Audio}", clearedFrames, clearedAudio);
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}
