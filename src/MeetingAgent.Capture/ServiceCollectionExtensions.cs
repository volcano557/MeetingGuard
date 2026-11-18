using MeetingAgent.Core;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingAgent.Capture;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMeetingAgentCapture(this IServiceCollection services)
    {
        services.AddSingleton<IWindowDiscoveryService, WindowDiscoveryService>();
        services.AddSingleton<IVideoCaptureService, HybridWindowsCaptureService>();
        return services;
    }
}
