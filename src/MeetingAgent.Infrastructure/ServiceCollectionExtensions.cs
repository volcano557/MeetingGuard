using MeetingAgent.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Extensions.Logging;

namespace MeetingAgent.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMeetingAgentInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        InMemoryLogSink logSink)
    {
        services.Configure<LinuxBackendOptions>(configuration.GetSection("LinuxBackend"));
        services.Configure<GatewayOptions>(configuration.GetSection("Gateway"));
        services.Configure<FaceProtectOptions>(configuration.GetSection("FaceProtect"));
        services.Configure<SemanticRiskOptions>(configuration.GetSection("SemanticRisk"));
        services.Configure<CaptureOptions>(configuration.GetSection("Capture"));
        services.Configure<AudioOptions>(configuration.GetSection("Audio"));
        services.Configure<AlertingOptions>(configuration.GetSection("Alerting"));
        services.Configure<LoggingOptions>(configuration.GetSection("Logging"));

        services.AddSingleton<IAgentStatusStore, AgentStatusStore>();
        services.AddSingleton<ISystemInfoProvider, SystemInfoProvider>();
        services.AddSingleton<IAgentHealthService, AgentHealthService>();
        services.AddSingleton(logSink);

        return services;
    }

    public static LoggerConfiguration AddMeetingAgentFileLogging(
        this LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        InMemoryLogSink logSink)
    {
        var logDirectory = configuration.GetSection("Logging").GetValue<string>("LogDirectory") ?? "logs";
        var logPath = Path.Combine(AppContext.BaseDirectory, logDirectory, "meeting-agent-.log");

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        return loggerConfiguration
            .MinimumLevel.Information()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .WriteTo.Sink(logSink);
    }
}
