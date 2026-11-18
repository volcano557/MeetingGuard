using MeetingAgent.Core;

namespace MeetingAgent.App.ViewModels;

public sealed class WindowItemViewModel
{
    public required WindowDescriptor Descriptor { get; init; }
    public required string DisplayName { get; init; }
}
