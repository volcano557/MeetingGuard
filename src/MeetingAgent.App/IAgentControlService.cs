using MeetingAgent.Core;

namespace MeetingAgent.App;

public interface IAgentControlService
{
    bool IsConnected { get; }
    WindowDescriptor? SelectedWindow { get; }
    Task<IReadOnlyList<WindowDescriptor>> GetWindowsAsync(CancellationToken cancellationToken);
    void SetSelectedWindow(WindowDescriptor? window);
    Task ConnectAsync(string host, int port, string protocol, string authToken, bool useGateway, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task TestConnectionAsync(string host, int port, string protocol, string authToken, bool useGateway, CancellationToken cancellationToken);
    Task StartCaptureAsync(CancellationToken cancellationToken);
    Task StopCaptureAsync(CancellationToken cancellationToken);
}
