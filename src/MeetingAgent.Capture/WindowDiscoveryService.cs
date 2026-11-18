using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MeetingAgent.Core;

namespace MeetingAgent.Capture;

public sealed class WindowDiscoveryService : IWindowDiscoveryService
{
    public Task<IReadOnlyList<WindowDescriptor>> GetAvailableWindowsAsync(CancellationToken cancellationToken = default)
    {
        var windows = new List<WindowDescriptor>();

        EnumWindows((hwnd, lParam) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            var sb = new StringBuilder(512);
            _ = GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var processId);
            string processName;
            try
            {
                processName = Process.GetProcessById((int)processId).ProcessName;
            }
            catch
            {
                processName = "unknown";
            }

            windows.Add(new WindowDescriptor(hwnd, title, processName, IsIconic(hwnd)));
            return true;
        }, IntPtr.Zero);

        var sorted = windows
            .OrderByDescending(w => w.ProcessName.Contains("Tencent", StringComparison.OrdinalIgnoreCase))
            .ThenBy(w => w.Title)
            .ToList();

        return Task.FromResult<IReadOnlyList<WindowDescriptor>>(sorted);
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
