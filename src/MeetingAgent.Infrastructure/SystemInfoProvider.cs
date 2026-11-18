using MeetingAgent.Core;

namespace MeetingAgent.Infrastructure;

public sealed class SystemInfoProvider : ISystemInfoProvider
{
    public SystemInfoSnapshot Collect()
    {
        return new SystemInfoSnapshot(
            MachineName: Environment.MachineName,
            OsVersion: Environment.OSVersion.VersionString,
            RuntimeVersion: Environment.Version.ToString(),
            ProcessorCount: Environment.ProcessorCount,
            WorkingSetMb: Environment.WorkingSet / 1024 / 1024);
    }
}
