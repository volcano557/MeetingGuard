using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace MeetingAgent.App;

/// <summary>
/// 独立保护预览窗口。
/// 仅显示人脸保护后的画面，方便 OBS 单独捕获此窗口作为虚拟摄像头输入。
/// </summary>
public partial class ProtectedPreviewWindow : Window
{
    private bool _isPinned;

    public ProtectedPreviewWindow()
    {
        InitializeComponent();

        // 放置在主窗口右侧
        var main = Application.Current.MainWindow;
        if (main != null)
        {
            Left = main.Left + main.Width + 10;
            Top  = main.Top;
        }
        else
        {
            Left = 100; Top = 100;
        }
    }

    /// <summary>更新显示的保护帧及状态信息。</summary>
    public void UpdateFrame(BitmapSource? frame, string latencyText, bool isActive)
    {
        ProtectedImage.Source = frame;

        PlaceholderPanel.Visibility = frame is null ? Visibility.Visible : Visibility.Collapsed;

        StatusDot.Fill  = isActive
            ? new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50))
            : new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
        StatusText.Text  = isActive ? "保护中" : "未保护";
        LatencyText.Text = isActive && !string.IsNullOrWhiteSpace(latencyText)
            ? $"· {latencyText}" : string.Empty;
    }

    private void FadeToolbar(double to, double durationSec)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromSeconds(durationSec));
        Toolbar.BeginAnimation(OpacityProperty, anim);
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        => FadeToolbar(1.0, 0.15);

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        => FadeToolbar(0.0, 0.4);

    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        Topmost = _isPinned;
        PinBtn.Content = _isPinned ? "取消置顶" : "置顶";
    }

    private void HintBtn_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "OBS 捕获此窗口步骤：\n\n" +
            "① 来源 → + → 窗口捕获\n" +
            "② 窗口选择：[MeetingAgent.App.exe]: MeetingGuard · 保护预览\n" +
            "③ 捕获方式：Windows 图形捕获（推荐）\n" +
            "④ 工具 → 虚拟摄像头 → 启动\n" +
            "⑤ 在视频会议软件摄像头中选择 OBS Virtual Camera",
            "OBS 捕获说明",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 只隐藏窗口，保持实例，方便下次快速显示
        e.Cancel = true;
        Hide();
    }
}
