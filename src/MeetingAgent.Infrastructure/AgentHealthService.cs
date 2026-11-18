using MeetingAgent.Core;

namespace MeetingAgent.Infrastructure;

public sealed class AgentHealthService : IAgentHealthService
{
    private readonly ITransportClient _transportClient;
    private readonly IVideoCaptureService _videoCaptureService;

    public AgentHealthService(ITransportClient transportClient, IVideoCaptureService videoCaptureService)
    {
        _transportClient = transportClient;
        _videoCaptureService = videoCaptureService;
    }

    public HealthCheckResult Check()
    {
        var now = DateTimeOffset.UtcNow;
        if (_videoCaptureService.IsRunning && !_transportClient.IsConnected)
        {
            return new HealthCheckResult(false, now, "Capture is running while backend is disconnected.");
        }

        return new HealthCheckResult(true, now, "Agent is healthy.");
    }
}
