using MeetingAgent.Core;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingAgent.Audio;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMeetingAgentAudio(this IServiceCollection services)
    {
        services.AddSingleton<IAudioCaptureService, HybridAudioCaptureService>();
        return services;
    }
}
