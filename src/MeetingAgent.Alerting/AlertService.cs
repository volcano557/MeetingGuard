using MeetingAgent.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingAgent.Alerting;

public sealed class AlertService : IAlertService
{
    private readonly AlertingOptions _options;
    private readonly ILogger<AlertService> _logger;
    private readonly LinkedList<AlertEvent> _history = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldownMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public AlertService(IOptions<AlertingOptions> options, ILogger<AlertService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public event EventHandler<AlertEvent>? AlertRaised;

    public IReadOnlyList<AlertEvent> GetRecentAlerts()
    {
        lock (_sync)
        {
            return _history.ToList();
        }
    }

    public void HandleDetection(DetectionResult detectionResult)
    {
        var now = DateTimeOffset.UtcNow;
        var key = $"{detectionResult.RiskType}:{detectionResult.Source}:{detectionResult.RiskLevel}";

        lock (_sync)
        {
            if (_cooldownMap.TryGetValue(key, out var last) &&
                (now - last).TotalSeconds < _options.CooldownSeconds)
            {
                return;
            }

            _cooldownMap[key] = now;
            var alert = new AlertEvent(
                Timestamp: now,
                RiskLevel: detectionResult.RiskLevel,
                RiskType: detectionResult.RiskType,
                Message: detectionResult.Message,
                Source: detectionResult.Source,
                FrameId: detectionResult.FrameId);

            _history.AddFirst(alert);
            while (_history.Count > _options.MaxHistory)
            {
                _history.RemoveLast();
            }

            _logger.LogWarning("Risk detected level={RiskLevel} type={RiskType} frame={FrameId}",
                alert.RiskLevel, alert.RiskType, alert.FrameId);

            AlertRaised?.Invoke(this, alert);
        }
    }
}
