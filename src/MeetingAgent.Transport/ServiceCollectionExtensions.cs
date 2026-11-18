using MeetingAgent.Core;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingAgent.Transport;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMeetingAgentTransport(this IServiceCollection services)
    {
        services.AddSingleton<GrpcTransportClient>();
        services.AddSingleton<HttpTransportClient>();
        services.AddSingleton<ITransportClient, ConfigurableTransportClient>();
        services.AddSingleton<IFrameCompressionService, LosslessFrameCompressionService>();
        services.AddSingleton<IDetectionBackendAdapter, RawFrameDetectionBackendAdapter>();
        return services;
    }
}
