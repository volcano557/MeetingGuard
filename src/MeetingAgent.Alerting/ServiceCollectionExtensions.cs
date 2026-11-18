using MeetingAgent.Core;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingAgent.Alerting;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMeetingAgentAlerting(this IServiceCollection services)
    {
        services.AddSingleton<IAlertService, AlertService>();
        return services;
    }
}
